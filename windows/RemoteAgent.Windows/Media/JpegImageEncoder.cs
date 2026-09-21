using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteAgent.Core.Abstractions;

namespace RemoteAgent.Windows.Media;

/// <summary>
/// JPEG encoding for screenshots, through GDI+.
/// </summary>
/// <remarks>
/// GDI+ is already a dependency of this project (icon extraction), ships with every Windows
/// installation, and a screenshot is an occasional request rather than a per-frame cost.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class JpegImageEncoder : IImageEncoder
{
    private static readonly ImageCodecInfo? JpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(static c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <inheritdoc />
    public unsafe byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);

        if (bgra.Length < stride * height)
        {
            throw new ArgumentException("The pixel buffer is smaller than stride × height.", nameof(bgra));
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        BitmapData locked = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);

        try
        {
            for (int y = 0; y < height; y++)
            {
                bgra.Slice(y * stride, width * 4).CopyTo(
                    new Span<byte>((byte*)locked.Scan0 + ((long)y * locked.Stride), width * 4));
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        using var stream = new MemoryStream();

        if (JpegCodec is null)
        {
            bitmap.Save(stream, ImageFormat.Jpeg);
        }
        else
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            bitmap.Save(stream, JpegCodec, parameters);
        }

        return stream.ToArray();
    }
}
