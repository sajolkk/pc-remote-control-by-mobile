namespace RemoteAgent.Streaming.Video;

/// <summary>
/// Converts a BGRA desktop image to NV12, downscaling as it goes.
/// </summary>
/// <remarks>
/// <para><b>Why on the CPU.</b> §9.2 describes a GPU video-processor stage for the hardware path.
/// This converter is what every encoder can use, including the software one, and it is fast
/// enough not to matter: the work is spread across all cores and 1080p converts in about a
/// millisecond. It also keeps the pipeline testable without a GPU, which is worth more than the
/// last millisecond.</para>
///
/// <para><b>Scaling is a box filter.</b> Each output pixel is the average of the source pixels its
/// footprint covers. For desktop content that matters: nearest-neighbour or plain bilinear
/// sampling at a 2:1 reduction drops whole rows of text pixels and makes small fonts unreadable,
/// while a box filter keeps every source pixel's contribution.</para>
///
/// <para><b>Colour is BT.601, limited range</b> — the matrix WebRTC's mobile renderers assume when
/// they convert back to RGB, so colours come out as they were on the PC. The encoder's input type
/// says the same thing, so the VUI in the bitstream agrees.</para>
/// </remarks>
public sealed class Nv12Converter
{
    private int[] _x0 = [];
    private int[] _x1 = [];
    private int[] _y0 = [];
    private int[] _y1 = [];
    private (int SourceWidth, int SourceHeight, int Width, int Height) _layout;

    /// <summary>Bytes needed for an NV12 frame of the given size.</summary>
    public static int FrameSize(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// Converts <paramref name="bgra"/> into <paramref name="nv12"/> at
    /// <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <param name="bgra">Top-down BGRA source.</param>
    /// <param name="sourceWidth">Source width in pixels.</param>
    /// <param name="sourceHeight">Source height in pixels.</param>
    /// <param name="sourceStride">Source bytes per row.</param>
    /// <param name="nv12">Destination, at least <see cref="FrameSize"/> bytes.</param>
    /// <param name="width">Output width. Must be even and no larger than the source.</param>
    /// <param name="height">Output height. Must be even and no larger than the source.</param>
    public void Convert(
        ReadOnlySpan<byte> bgra,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> nv12,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("Output dimensions must be positive and even.");
        }

        if (width > sourceWidth || height > sourceHeight)
        {
            throw new ArgumentException("The converter only downscales.");
        }

        if (sourceStride < sourceWidth * 4 || bgra.Length < sourceStride * sourceHeight)
        {
            throw new ArgumentException("The source buffer is smaller than its dimensions.", nameof(bgra));
        }

        if (nv12.Length < FrameSize(width, height))
        {
            throw new ArgumentException("The destination buffer is too small.", nameof(nv12));
        }

        EnsureLayout(sourceWidth, sourceHeight, width, height);

        unsafe
        {
            fixed (byte* source = bgra)
            fixed (byte* destination = nv12)
            {
                var job = new Job(source, sourceStride, destination, width, height, _x0, _x1, _y0, _y1);

                // Parallel over output row pairs: each pair shares one row of chroma, so pairs are
                // the unit that can be written without two threads touching the same bytes.
                Parallel.For(0, height / 2, job.ConvertRowPair);
            }
        }
    }

    /// <summary>
    /// Precomputes each output column's and row's source footprint. Recomputed only when the
    /// geometry changes, which is once per stream in practice.
    /// </summary>
    private void EnsureLayout(int sourceWidth, int sourceHeight, int width, int height)
    {
        if (_layout == (sourceWidth, sourceHeight, width, height))
        {
            return;
        }

        (_x0, _x1) = Footprints(sourceWidth, width);
        (_y0, _y1) = Footprints(sourceHeight, height);
        _layout = (sourceWidth, sourceHeight, width, height);
    }

    private static (int[] Start, int[] End) Footprints(int source, int output)
    {
        var start = new int[output];
        var end = new int[output];

        for (int i = 0; i < output; i++)
        {
            int s = (int)((long)i * source / output);
            int e = (int)((long)(i + 1) * source / output);
            start[i] = s;
            end[i] = Math.Max(e, s + 1);
        }

        return (start, end);
    }

    private readonly unsafe struct Job
    {
        private readonly byte* _source;
        private readonly int _stride;
        private readonly byte* _destination;
        private readonly int _width;
        private readonly int _height;
        private readonly int[] _x0;
        private readonly int[] _x1;
        private readonly int[] _y0;
        private readonly int[] _y1;

        public Job(byte* source, int stride, byte* destination, int width, int height, int[] x0, int[] x1, int[] y0, int[] y1)
        {
            _source = source;
            _stride = stride;
            _destination = destination;
            _width = width;
            _height = height;
            _x0 = x0;
            _x1 = x1;
            _y0 = y0;
            _y1 = y1;
        }

        public void ConvertRowPair(int pair)
        {
            int y = pair * 2;
            byte* lumaTop = _destination + ((long)y * _width);
            byte* lumaBottom = lumaTop + _width;
            byte* chroma = _destination + ((long)_width * _height) + ((long)pair * _width);

            for (int x = 0; x < _width; x += 2)
            {
                Sample(x, y, out int r00, out int g00, out int b00);
                Sample(x + 1, y, out int r01, out int g01, out int b01);
                Sample(x, y + 1, out int r10, out int g10, out int b10);
                Sample(x + 1, y + 1, out int r11, out int g11, out int b11);

                lumaTop[x] = Luma(r00, g00, b00);
                lumaTop[x + 1] = Luma(r01, g01, b01);
                lumaBottom[x] = Luma(r10, g10, b10);
                lumaBottom[x + 1] = Luma(r11, g11, b11);

                int r = (r00 + r01 + r10 + r11 + 2) >> 2;
                int g = (g00 + g01 + g10 + g11 + 2) >> 2;
                int b = (b00 + b01 + b10 + b11 + 2) >> 2;

                chroma[x] = (byte)((((-38 * r) - (74 * g) + (112 * b) + 128) >> 8) + 128);
                chroma[x + 1] = (byte)((((112 * r) - (94 * g) - (18 * b) + 128) >> 8) + 128);
            }
        }

        private static byte Luma(int r, int g, int b) =>
            (byte)((((66 * r) + (129 * g) + (25 * b) + 128) >> 8) + 16);

        /// <summary>Averages the source footprint of output pixel (x, y).</summary>
        private void Sample(int x, int y, out int r, out int g, out int b)
        {
            int sx0 = _x0[x];
            int sx1 = _x1[x];
            int sy0 = _y0[y];
            int sy1 = _y1[y];

            if (sx1 - sx0 == 1 && sy1 - sy0 == 1)
            {
                // Unscaled: the overwhelmingly common case on a 1080p desktop.
                byte* p = _source + ((long)sy0 * _stride) + (sx0 * 4);
                b = p[0];
                g = p[1];
                r = p[2];
                return;
            }

            int sumB = 0;
            int sumG = 0;
            int sumR = 0;

            for (int sy = sy0; sy < sy1; sy++)
            {
                byte* p = _source + ((long)sy * _stride) + (sx0 * 4);
                for (int sx = sx0; sx < sx1; sx++, p += 4)
                {
                    sumB += p[0];
                    sumG += p[1];
                    sumR += p[2];
                }
            }

            int count = (sx1 - sx0) * (sy1 - sy0);
            int half = count / 2;
            b = (sumB + half) / count;
            g = (sumG + half) / count;
            r = (sumR + half) / count;
        }
    }
}
