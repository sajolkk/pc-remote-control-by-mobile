namespace RemoteAgent.Streaming.Video;

/// <summary>
/// Box-filter downscaling of a BGRA image, for screenshots.
/// </summary>
/// <remarks>
/// The same filter as <see cref="Nv12Converter"/> and for the same reason: averaging every source
/// pixel in each output pixel's footprint keeps small text legible, where sampling would drop it.
/// </remarks>
public static class BgraScaler
{
    /// <summary>
    /// Returns a tightly packed (stride = width × 4) copy of <paramref name="source"/> scaled to
    /// <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public static byte[] Downscale(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > sourceWidth || height > sourceHeight)
        {
            throw new ArgumentException("Output dimensions must be positive and no larger than the source.");
        }

        if (sourceStride < sourceWidth * 4 || source.Length < sourceStride * sourceHeight)
        {
            throw new ArgumentException("The source buffer is smaller than its dimensions.", nameof(source));
        }

        byte[] output = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int sy0 = (int)((long)y * sourceHeight / height);
            int sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * sourceHeight / height));

            for (int x = 0; x < width; x++)
            {
                int sx0 = (int)((long)x * sourceWidth / width);
                int sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * sourceWidth / width));

                int b = 0, g = 0, r = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    ReadOnlySpan<byte> row = source.Slice(sy * sourceStride);
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        b += row[sx * 4];
                        g += row[(sx * 4) + 1];
                        r += row[(sx * 4) + 2];
                    }
                }

                int count = (sx1 - sx0) * (sy1 - sy0);
                int half = count / 2;
                int o = ((y * width) + x) * 4;
                output[o] = (byte)((b + half) / count);
                output[o + 1] = (byte)((g + half) / count);
                output[o + 2] = (byte)((r + half) / count);
                output[o + 3] = 255;
            }
        }

        return output;
    }
}
