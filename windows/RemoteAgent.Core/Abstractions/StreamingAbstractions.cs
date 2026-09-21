using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// Enumerates the monitors that can be captured (§9.6).
/// </summary>
public interface IMonitorProvider
{
    /// <summary>
    /// Every monitor attached to the desktop, primary first. Re-enumerated on each call: the
    /// topology changes on hot-plug, and a cached list is how a stream ends up pointed at a
    /// monitor that no longer exists.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetMonitors();
}

/// <summary>
/// Opens capture sessions on individual monitors (§9.1).
/// </summary>
public interface IScreenCaptureFactory
{
    /// <summary>
    /// Starts capturing <paramref name="monitorId"/>.
    /// </summary>
    /// <exception cref="ScreenCaptureUnavailableException">
    /// The monitor does not exist, or the desktop cannot be captured right now — typically
    /// because the secure desktop is showing (§12.1).
    /// </exception>
    IScreenCapture Open(string monitorId);
}

/// <summary>
/// Raised when capture cannot start or cannot continue.
/// </summary>
public sealed class ScreenCaptureUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ScreenCaptureUnavailableException(string message, bool transient, Exception? inner = null)
        : base(message, inner)
    {
        Transient = transient;
    }

    /// <summary>Creates the exception.</summary>
    public ScreenCaptureUnavailableException()
        : this("Screen capture is unavailable.", transient: true)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ScreenCaptureUnavailableException(string message)
        : this(message, transient: true)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ScreenCaptureUnavailableException(string message, Exception innerException)
        : this(message, transient: true, innerException)
    {
    }

    /// <summary>
    /// Whether retrying later may succeed. True for the secure desktop and driver resets; false
    /// for a monitor id that does not exist.
    /// </summary>
    public bool Transient { get; }
}

/// <summary>What one call to <see cref="IScreenCapture.Acquire"/> produced.</summary>
public enum CaptureStatus
{
    /// <summary>
    /// Something visible changed: the desktop image, the pointer position, or the pointer shape.
    /// <see cref="IScreenCapture.Frame"/> is current.
    /// </summary>
    Updated,

    /// <summary>
    /// Nothing changed within the timeout. The previous frame is still valid, and a static desktop
    /// costs nothing to stream because no frame is encoded (§9.5).
    /// </summary>
    Unchanged,

    /// <summary>
    /// Capture was lost — a mode change, a driver reset, or a switch to the secure desktop. The
    /// session must be disposed and a new one opened.
    /// </summary>
    Lost,
}

/// <summary>
/// A live capture of one monitor.
/// </summary>
/// <remarks>
/// Not thread-safe: one pump thread owns it. The frame buffer is reused between calls to avoid
/// allocating tens of megabytes per second, so a consumer must finish with
/// <see cref="Frame"/> before calling <see cref="Acquire"/> again.
/// </remarks>
public interface IScreenCapture : IDisposable
{
    /// <summary>The monitor being captured.</summary>
    MonitorInfo Monitor { get; }

    /// <summary>The most recent desktop image and pointer state.</summary>
    CapturedFrame Frame { get; }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for the desktop or pointer to change.</summary>
    CaptureStatus Acquire(int timeoutMs);
}

/// <summary>
/// A desktop image in 32-bit BGRA, top-down, plus the pointer to draw over it.
/// </summary>
/// <remarks>
/// The pointer is kept separate from the pixels because that is how Desktop Duplication delivers
/// it, and because keeping them apart lets a pointer-only movement be encoded without re-reading
/// the desktop from the GPU.
/// </remarks>
public sealed class CapturedFrame
{
    /// <summary>Pixel data, <see cref="Stride"/> × <see cref="Height"/> bytes.</summary>
    public byte[] Pixels { get; set; } = [];

    /// <summary>Width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Bytes per row. At least <see cref="Width"/> × 4.</summary>
    public int Stride { get; set; }

    /// <summary>Increments whenever <see cref="Pixels"/> changes, so a consumer can tell desktop updates from pointer-only updates.</summary>
    public long ContentVersion { get; set; }

    /// <summary>Whether the pointer is over this monitor and visible.</summary>
    public bool PointerVisible { get; set; }

    /// <summary>Pointer's top-left position in frame coordinates (the hotspot is already subtracted).</summary>
    public int PointerX { get; set; }

    /// <summary>Pointer's top-left position in frame coordinates.</summary>
    public int PointerY { get; set; }

    /// <summary>The current pointer image, or null if none has been reported yet.</summary>
    public PointerShape? Pointer { get; set; }
}

/// <summary>How a <see cref="PointerShape"/> is encoded, matching the three DXGI pointer formats.</summary>
public enum PointerShapeKind
{
    /// <summary>1 bpp AND mask followed by 1 bpp XOR mask; <see cref="PointerShape.Height"/> is the height of one mask.</summary>
    Monochrome,

    /// <summary>32 bpp BGRA with straight alpha.</summary>
    Color,

    /// <summary>32 bpp BGR where the alpha byte is a mask: 0 means replace, 0xFF means XOR.</summary>
    MaskedColor,
}

/// <summary>A pointer image as reported by the capture API.</summary>
public sealed class PointerShape
{
    /// <summary>Encoding of <see cref="Data"/>.</summary>
    public PointerShapeKind Kind { get; init; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels, of the visible image (not of both monochrome masks).</summary>
    public int Height { get; init; }

    /// <summary>Bytes per row of <see cref="Data"/>.</summary>
    public int Pitch { get; init; }

    /// <summary>Raw shape data.</summary>
    public byte[] Data { get; init; } = [];
}

/// <summary>What an encoder factory found on this PC.</summary>
/// <param name="Name">Encoder name, for telemetry and logs.</param>
/// <param name="IsHardware">Whether it runs on the GPU or a dedicated media block.</param>
public sealed record VideoEncoderDescriptor(string Name, bool IsHardware);

/// <summary>Parameters for a new encoder instance.</summary>
/// <param name="Width">Frame width. Must be even.</param>
/// <param name="Height">Frame height. Must be even.</param>
/// <param name="Fps">Nominal frame rate, which sets rate-control timing.</param>
/// <param name="BitrateKbps">Initial target bitrate.</param>
/// <param name="PreferHardware">Whether to try a hardware encoder before the software one.</param>
public sealed record VideoEncoderSettings(int Width, int Height, int Fps, int BitrateKbps, bool PreferHardware);

/// <summary>
/// Creates H.264 encoders (§9.2).
/// </summary>
public interface IVideoEncoderFactory
{
    /// <summary>
    /// Encoders usable on this PC, preferred first. Probed rather than assumed: a registered
    /// hardware encoder may still refuse to start on a GPU without the right encode block.
    /// </summary>
    IReadOnlyList<VideoEncoderDescriptor> Probe();

    /// <summary>
    /// Creates an encoder, falling back from hardware to software automatically (§0).
    /// </summary>
    /// <exception cref="NotSupportedException">No H.264 encoder at all could be started.</exception>
    IVideoEncoder Create(VideoEncoderSettings settings);
}

/// <summary>
/// One H.264 encoder instance, configured for low-latency streaming: constrained baseline, no
/// B-frames, keyframes only on demand.
/// </summary>
/// <remarks>Not thread-safe: owned by one pump thread.</remarks>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Which encoder this is.</summary>
    VideoEncoderDescriptor Descriptor { get; }

    /// <summary>Frame width this instance was created for.</summary>
    int Width { get; }

    /// <summary>Frame height this instance was created for.</summary>
    int Height { get; }

    /// <summary>
    /// Encodes one NV12 frame (<see cref="Width"/> × <see cref="Height"/> luma, then interleaved
    /// half-resolution chroma).
    /// </summary>
    /// <param name="nv12">The frame. Length must be Width × Height × 3 / 2.</param>
    /// <param name="timestamp100ns">Presentation time in 100 ns units.</param>
    /// <param name="keyFrame">Whether to force an IDR frame, e.g. in answer to a PLI.</param>
    /// <returns>
    /// The Annex-B access unit, or null if the encoder buffered this frame. A low-latency
    /// configuration should never buffer, but a caller must tolerate it.
    /// </returns>
    byte[]? Encode(ReadOnlySpan<byte> nv12, long timestamp100ns, bool keyFrame);

    /// <summary>Changes the target bitrate without restarting the encoder.</summary>
    void SetBitrate(int kbps);
}

/// <summary>
/// Encodes still images for <c>screen.screenshot</c>.
/// </summary>
public interface IImageEncoder
{
    /// <summary>Encodes a top-down BGRA image as JPEG.</summary>
    byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality);
}
