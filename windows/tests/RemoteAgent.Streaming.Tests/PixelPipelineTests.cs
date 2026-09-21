using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Media;
using RemoteAgent.Streaming.Video;
using Xunit;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// The pixel path: Annex-B inspection, BGRA → NV12, scaling and pointer compositing.
/// </summary>
/// <remarks>
/// Colour values are pinned exactly, because an error here does not crash anything — it just makes
/// every stream subtly the wrong colour on the phone, which is the kind of bug nobody files.
/// </remarks>
public sealed class PixelPipelineTests
{
    // --- H.264 Annex-B ---

    [Fact]
    public void FindNalUnits_HandlesThreeAndFourByteStartCodes()
    {
        byte[] stream =
        [
            0, 0, 0, 1, 0x67, 0xAA, 0xBB,   // SPS, four-byte start code
            0, 0, 1, 0x68, 0xCC,             // PPS, three-byte start code
            0, 0, 0, 1, 0x65, 0x01, 0x02, 0x03, // IDR
        ];

        List<(int Offset, int Length)> units = H264AnnexB.FindNalUnits(stream);

        Assert.Equal(3, units.Count);
        Assert.Equal([H264AnnexB.NalSps, H264AnnexB.NalPps, H264AnnexB.NalIdr], units.Select(u => H264AnnexB.NalType(stream, u.Offset)));
        Assert.Equal(3, units[0].Length);
        Assert.Equal(2, units[1].Length);
        Assert.Equal(4, units[2].Length);
    }

    [Fact]
    public void FindNalUnits_IgnoresLeadingGarbageAndTrailingZeros()
    {
        byte[] stream = [0x12, 0x34, 0, 0, 1, 0x41, 0x99, 0, 0];

        List<(int Offset, int Length)> units = H264AnnexB.FindNalUnits(stream);

        (int offset, int length) = Assert.Single(units);
        Assert.Equal(H264AnnexB.NalSlice, H264AnnexB.NalType(stream, offset));
        Assert.Equal(2, length);
    }

    [Fact]
    public void EnsureParameterSets_PrependsOnlyToKeyFramesWithoutSps()
    {
        byte[] parameterSets = [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2];
        byte[] idr = [0, 0, 0, 1, 0x65, 9];
        byte[] slice = [0, 0, 0, 1, 0x41, 9];
        byte[] idrWithSps = [.. parameterSets, .. idr];

        byte[] fixedIdr = H264AnnexB.EnsureParameterSets(idr, parameterSets);
        Assert.True(H264AnnexB.Contains(fixedIdr, H264AnnexB.NalSps));
        Assert.True(H264AnnexB.IsKeyFrame(fixedIdr));

        Assert.Same(slice, H264AnnexB.EnsureParameterSets(slice, parameterSets));
        Assert.Same(idrWithSps, H264AnnexB.EnsureParameterSets(idrWithSps, parameterSets));
    }

    // --- NV12 conversion ---

    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)] // white → nominal peak
    [InlineData(0, 0, 0, 16, 128, 128)]        // black → nominal floor
    [InlineData(255, 0, 0, 82, 90, 240)]       // red, BT.601 limited
    [InlineData(0, 255, 0, 144, 54, 34)]       // green
    [InlineData(0, 0, 255, 41, 240, 110)]      // blue
    public void Convert_SolidColour_UsesBt601LimitedRange(int r, int g, int b, int y, int u, int v)
    {
        byte[] bgra = Solid(4, 4, (byte)b, (byte)g, (byte)r);
        byte[] nv12 = new byte[Nv12Converter.FrameSize(4, 4)];

        new Nv12Converter().Convert(bgra, 4, 4, 16, nv12, 4, 4);

        Assert.All(nv12.AsSpan(0, 16).ToArray(), luma => Assert.Equal(y, luma));
        for (int i = 16; i < nv12.Length; i += 2)
        {
            Assert.Equal(u, nv12[i]);
            Assert.Equal(v, nv12[i + 1]);
        }
    }

    [Fact]
    public void Convert_TwoToOne_AveragesTheFootprint()
    {
        // A one-pixel black/white checkerboard: point sampling would return solid black or solid
        // white depending on phase. A box filter must return mid-grey everywhere.
        const int size = 8;
        byte[] bgra = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte value = (byte)((x + y) % 2 == 0 ? 255 : 0);
                int o = ((y * size) + x) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = value;
                bgra[o + 3] = 255;
            }
        }

        byte[] nv12 = new byte[Nv12Converter.FrameSize(4, 4)];
        new Nv12Converter().Convert(bgra, size, size, size * 4, nv12, 4, 4);

        // Grey 128 → Y = ((66+129+25)*128 + 128 >> 8) + 16 = 126.
        Assert.All(nv12.AsSpan(0, 16).ToArray(), luma => Assert.InRange(luma, 125, 127));
    }

    [Fact]
    public void Convert_RespectsSourceStride()
    {
        // Stride padding must never be read as pixels. Pad with bright red and convert black.
        const int width = 2;
        const int height = 2;
        const int stride = 16;
        byte[] bgra = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = width * 4; x < stride; x += 4)
            {
                bgra[(y * stride) + x + 2] = 255;
            }
        }

        byte[] nv12 = new byte[Nv12Converter.FrameSize(width, height)];
        new Nv12Converter().Convert(bgra, width, height, stride, nv12, width, height);

        Assert.All(nv12.AsSpan(0, 4).ToArray(), luma => Assert.Equal(16, luma));
        Assert.Equal(128, nv12[4]);
        Assert.Equal(128, nv12[5]);
    }

    [Theory]
    [InlineData(3, 4)]   // odd width
    [InlineData(4, 3)]   // odd height
    [InlineData(8, 4)]   // upscale
    public void Convert_RejectsInvalidGeometry(int width, int height)
    {
        byte[] bgra = Solid(4, 4, 0, 0, 0);
        byte[] nv12 = new byte[1024];

        Assert.Throws<ArgumentException>(() => new Nv12Converter().Convert(bgra, 4, 4, 16, nv12, width, height));
    }

    [Fact]
    public void BgraScaler_AveragesAndPacks()
    {
        byte[] bgra = Solid(4, 2, 0, 0, 0);
        // Right half white.
        for (int y = 0; y < 2; y++)
        {
            for (int x = 2; x < 4; x++)
            {
                int o = ((y * 4) + x) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = 255;
            }
        }

        byte[] scaled = BgraScaler.Downscale(bgra, 4, 2, 16, 2, 1);

        Assert.Equal(8, scaled.Length);
        Assert.Equal([0, 0, 0, 255, 255, 255, 255, 255], scaled);
    }

    // --- Pointer compositing ---

    [Fact]
    public void Cursor_DrawThenRestore_LeavesTheFrameUntouched()
    {
        CapturedFrame frame = Frame(8, 8, 10);
        byte[] original = frame.Pixels.ToArray();

        frame.Pointer = ColourShape(4, 4, b: 0, g: 0, r: 255, a: 255);
        frame.PointerVisible = true;
        frame.PointerX = 2;
        frame.PointerY = 3;

        var compositor = new CursorCompositor();
        compositor.Draw(frame);

        Assert.Equal(255, frame.Pixels[((3 * 8) + 2) * 4 + 2]);
        Assert.NotEqual(original, frame.Pixels);

        compositor.Restore(frame);
        Assert.Equal(original, frame.Pixels);
    }

    [Fact]
    public void Cursor_BlendsStraightAlpha()
    {
        CapturedFrame frame = Frame(2, 2, 0);
        frame.Pointer = ColourShape(1, 1, b: 200, g: 100, r: 0, a: 128);
        frame.PointerVisible = true;

        new CursorCompositor().Draw(frame);

        // 200 * 128/255 ≈ 100, 100 * 128/255 ≈ 50.
        Assert.InRange(frame.Pixels[0], 99, 101);
        Assert.InRange(frame.Pixels[1], 49, 51);
        Assert.Equal(0, frame.Pixels[2]);
    }

    [Fact]
    public void Cursor_IsClippedAtTheFrameEdge()
    {
        CapturedFrame frame = Frame(4, 4, 0);
        frame.Pointer = ColourShape(4, 4, b: 255, g: 255, r: 255, a: 255);
        frame.PointerVisible = true;
        frame.PointerX = 2;
        frame.PointerY = -2;

        var compositor = new CursorCompositor();
        compositor.Draw(frame);

        // Only the visible 2×2 corner is drawn, and nothing outside the buffer is touched.
        Assert.Equal(255, frame.Pixels[((0 * 4) + 3) * 4]);
        Assert.Equal(255, frame.Pixels[((1 * 4) + 2) * 4]);
        Assert.Equal(0, frame.Pixels[((2 * 4) + 2) * 4]);
        Assert.Equal(0, frame.Pixels[((0 * 4) + 1) * 4]);

        compositor.Restore(frame);
        Assert.All(frame.Pixels.Where((_, i) => i % 4 != 3), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Cursor_MonochromeXorInvertsTheScreen()
    {
        // 1×1 pointer: AND=1, XOR=1 → invert the screen pixel. This is how the I-beam stays
        // visible on any background.
        CapturedFrame frame = Frame(1, 1, 40);
        frame.Pointer = new PointerShape
        {
            Kind = PointerShapeKind.Monochrome,
            Width = 1,
            Height = 1,
            Pitch = 1,
            Data = [0x80, 0x80],
        };
        frame.PointerVisible = true;

        new CursorCompositor().Draw(frame);

        Assert.Equal(255 - 40, frame.Pixels[0]);
        Assert.Equal(255 - 40, frame.Pixels[1]);
        Assert.Equal(255 - 40, frame.Pixels[2]);
    }

    [Fact]
    public void Cursor_HiddenPointerDrawsNothing()
    {
        CapturedFrame frame = Frame(4, 4, 7);
        byte[] original = frame.Pixels.ToArray();
        frame.Pointer = ColourShape(2, 2, 255, 255, 255, 255);
        frame.PointerVisible = false;

        var compositor = new CursorCompositor();
        compositor.Draw(frame);
        compositor.Restore(frame);

        Assert.Equal(original, frame.Pixels);
    }

    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static CapturedFrame Frame(int width, int height, byte fill)
    {
        byte[] pixels = new byte[width * height * 4];
        pixels.AsSpan().Fill(fill);
        return new CapturedFrame { Pixels = pixels, Width = width, Height = height, Stride = width * 4 };
    }

    private static PointerShape ColourShape(int width, int height, byte b, byte g, byte r, byte a)
    {
        byte[] data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
            data[i + 3] = a;
        }

        return new PointerShape { Kind = PointerShapeKind.Color, Width = width, Height = height, Pitch = width * 4, Data = data };
    }
}
