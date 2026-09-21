using RemoteAgent.Core.Abstractions;

namespace RemoteAgent.Streaming.Video;

/// <summary>
/// Draws the mouse pointer into a captured frame, and takes it out again.
/// </summary>
/// <remarks>
/// <para>Desktop Duplication delivers the pointer separately from the desktop image (§9.1), so it
/// has to be composited before encoding or the phone would see a desktop with no cursor.</para>
///
/// <para><b>Draw, convert, restore.</b> The capture keeps one desktop image and reuses it when only
/// the pointer moves. Drawing the pointer into that image permanently would leave a trail of
/// cursors across it, so <see cref="Draw"/> saves the pixels it covers and <see cref="Restore"/>
/// puts them back. The saved area is at most the pointer's size — a few kilobytes — where copying
/// the whole frame instead would be eight megabytes per frame at 1080p.</para>
/// </remarks>
public sealed class CursorCompositor
{
    private byte[] _saved = [];
    private (int X, int Y, int Width, int Height) _savedRect;
    private bool _hasSaved;

    /// <summary>
    /// Composites the frame's pointer into its pixels, if it is visible. Must be followed by
    /// <see cref="Restore"/> before the frame is next updated.
    /// </summary>
    public void Draw(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        _hasSaved = false;

        PointerShape? shape = frame.Pointer;
        if (!frame.PointerVisible || shape is null || shape.Width <= 0 || shape.Height <= 0)
        {
            return;
        }

        // Clip the pointer rectangle to the frame. A pointer at the screen edge is partly off it.
        int left = Math.Max(0, frame.PointerX);
        int top = Math.Max(0, frame.PointerY);
        int right = Math.Min(frame.Width, frame.PointerX + shape.Width);
        int bottom = Math.Min(frame.Height, frame.PointerY + shape.Height);

        if (right <= left || bottom <= top)
        {
            return;
        }

        Save(frame, left, top, right - left, bottom - top);

        for (int y = top; y < bottom; y++)
        {
            int shapeY = y - frame.PointerY;
            Span<byte> row = frame.Pixels.AsSpan(y * frame.Stride, frame.Width * 4);

            for (int x = left; x < right; x++)
            {
                int shapeX = x - frame.PointerX;
                Span<byte> pixel = row.Slice(x * 4, 4);

                switch (shape.Kind)
                {
                    case PointerShapeKind.Color:
                        BlendColor(shape, shapeX, shapeY, pixel);
                        break;
                    case PointerShapeKind.MaskedColor:
                        BlendMaskedColor(shape, shapeX, shapeY, pixel);
                        break;
                    default:
                        BlendMonochrome(shape, shapeX, shapeY, pixel);
                        break;
                }
            }
        }
    }

    /// <summary>Puts back the pixels the last <see cref="Draw"/> covered.</summary>
    public void Restore(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!_hasSaved)
        {
            return;
        }

        (int x, int y, int width, int height) = _savedRect;
        int rowBytes = width * 4;

        for (int row = 0; row < height; row++)
        {
            _saved.AsSpan(row * rowBytes, rowBytes)
                .CopyTo(frame.Pixels.AsSpan(((y + row) * frame.Stride) + (x * 4), rowBytes));
        }

        _hasSaved = false;
    }

    private void Save(CapturedFrame frame, int x, int y, int width, int height)
    {
        int rowBytes = width * 4;
        if (_saved.Length < rowBytes * height)
        {
            _saved = new byte[rowBytes * height];
        }

        for (int row = 0; row < height; row++)
        {
            frame.Pixels.AsSpan(((y + row) * frame.Stride) + (x * 4), rowBytes)
                .CopyTo(_saved.AsSpan(row * rowBytes, rowBytes));
        }

        _savedRect = (x, y, width, height);
        _hasSaved = true;
    }

    /// <summary>32-bit BGRA with straight alpha: ordinary alpha blending.</summary>
    private static void BlendColor(PointerShape shape, int x, int y, Span<byte> pixel)
    {
        int offset = (y * shape.Pitch) + (x * 4);
        int alpha = shape.Data[offset + 3];

        if (alpha == 0)
        {
            return;
        }

        if (alpha == 255)
        {
            pixel[0] = shape.Data[offset];
            pixel[1] = shape.Data[offset + 1];
            pixel[2] = shape.Data[offset + 2];
            return;
        }

        int inverse = 255 - alpha;
        for (int c = 0; c < 3; c++)
        {
            pixel[c] = (byte)(((shape.Data[offset + c] * alpha) + (pixel[c] * inverse) + 127) / 255);
        }
    }

    /// <summary>
    /// 32-bit colour whose alpha byte is a mask: 0 replaces the screen pixel, 0xFF XORs with it.
    /// </summary>
    private static void BlendMaskedColor(PointerShape shape, int x, int y, Span<byte> pixel)
    {
        int offset = (y * shape.Pitch) + (x * 4);
        bool xor = shape.Data[offset + 3] != 0;

        for (int c = 0; c < 3; c++)
        {
            pixel[c] = xor ? (byte)(pixel[c] ^ shape.Data[offset + c]) : shape.Data[offset + c];
        }
    }

    /// <summary>
    /// Two 1-bpp masks stacked vertically: AND then XOR. The classic Windows cursor model — the
    /// I-beam text cursor is the one users meet most, and its XOR half is what makes it visible on
    /// both light and dark backgrounds.
    /// </summary>
    private static void BlendMonochrome(PointerShape shape, int x, int y, Span<byte> pixel)
    {
        int bit = 0x80 >> (x % 8);
        int andOffset = (y * shape.Pitch) + (x / 8);
        int xorOffset = ((y + shape.Height) * shape.Pitch) + (x / 8);

        if (xorOffset >= shape.Data.Length)
        {
            return;
        }

        bool and = (shape.Data[andOffset] & bit) != 0;
        bool xor = (shape.Data[xorOffset] & bit) != 0;

        for (int c = 0; c < 3; c++)
        {
            byte value = and ? pixel[c] : (byte)0;
            pixel[c] = xor ? (byte)(value ^ 0xFF) : value;
        }
    }
}
