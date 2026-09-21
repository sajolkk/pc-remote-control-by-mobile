using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// One monitor, as returned by <see cref="CommandNames.ScreenMonitorList"/> (§9.6).
/// </summary>
/// <remarks>
/// <see cref="Id"/> is PC-generated and opaque. It is stable while the display topology is
/// unchanged, and a client must treat it as meaningless on any other PC — which is why
/// <c>streaming.defaultMonitorId</c> defaults to null rather than to a literal id (§0).
/// </remarks>
public sealed class MonitorInfo
{
    /// <summary>Opaque monitor handle, valid for <see cref="CommandNames.ScreenSelectMonitor"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display adapter the monitor is attached to.</summary>
    [JsonPropertyName("adapter")]
    public string Adapter { get; init; } = string.Empty;

    /// <summary>GDI device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Human-readable name, e.g. "Display 1 (primary)".</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Left edge in virtual-desktop coordinates, physical pixels.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Top edge in virtual-desktop coordinates, physical pixels.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Width in physical pixels, after rotation.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Height in physical pixels, after rotation.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>DPI scale factor, 1.0 at 96 DPI.</summary>
    [JsonPropertyName("scale")]
    public double Scale { get; init; } = 1.0;

    /// <summary>Refresh rate in Hz, or 0 when Windows does not report one.</summary>
    [JsonPropertyName("refreshHz")]
    public int RefreshHz { get; init; }

    /// <summary>Whether this is the primary monitor.</summary>
    [JsonPropertyName("primary")]
    public bool Primary { get; init; }

    /// <summary>Rotation in degrees clockwise: 0, 90, 180 or 270.</summary>
    [JsonPropertyName("rotation")]
    public int Rotation { get; init; }
}

/// <summary>Result of <see cref="CommandNames.ScreenMonitorList"/>.</summary>
public sealed class MonitorListResult
{
    /// <summary>Every monitor that can be captured, primary first.</summary>
    [JsonPropertyName("monitors")]
    public MonitorInfo[] Monitors { get; init; } = [];

    /// <summary>
    /// The monitor this caller's stream shows, or would show if one were started now.
    /// </summary>
    [JsonPropertyName("selectedId")]
    public string? SelectedId { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.ScreenSelectMonitor"/>.</summary>
public sealed class SelectMonitorArgs
{
    /// <summary>A monitor id from <see cref="MonitorListResult"/>.</summary>
    [JsonPropertyName("monitorId")]
    public string MonitorId { get; init; } = string.Empty;
}

/// <summary>Arguments for <see cref="CommandNames.ScreenScreenshot"/>.</summary>
public sealed class ScreenshotArgs
{
    /// <summary>Monitor to capture. Null means the caller's selected monitor.</summary>
    [JsonPropertyName("monitorId")]
    public string? MonitorId { get; init; }

    /// <summary>
    /// Largest width to return. The image is downscaled to fit, and the PC applies its own
    /// ceiling as well, because the response has to fit in one control frame (§6.2).
    /// </summary>
    [JsonPropertyName("maxWidth")]
    public int? MaxWidth { get; init; }
}

/// <summary>Result of <see cref="CommandNames.ScreenScreenshot"/>.</summary>
public sealed class ScreenshotResult
{
    /// <summary>The monitor that was captured.</summary>
    [JsonPropertyName("monitorId")]
    public string MonitorId { get; init; } = string.Empty;

    /// <summary>Image MIME type. Always <c>image/jpeg</c> in this version.</summary>
    [JsonPropertyName("mime")]
    public string Mime { get; init; } = "image/jpeg";

    /// <summary>Image width in pixels, after any downscaling.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Image height in pixels, after any downscaling.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>The image, base64-encoded.</summary>
    [JsonPropertyName("data")]
    public string Data { get; init; } = string.Empty;
}

/// <summary>
/// Arguments for <see cref="CommandNames.MediaOffer"/>: the phone's SDP offer (§1.4 step 6).
/// </summary>
/// <remarks>
/// The offer travels over the already-authenticated control channel, and that is what secures the
/// media plane: the DTLS fingerprint inside it is the only certificate the PC's peer will accept,
/// so a device on the network that did not see this message cannot complete the DTLS handshake.
/// </remarks>
public sealed class MediaOfferArgs
{
    /// <summary>The SDP offer, verbatim from the client's WebRTC stack.</summary>
    [JsonPropertyName("sdp")]
    public string Sdp { get; init; } = string.Empty;

    /// <summary>Monitor to stream. Null means the caller's selected monitor.</summary>
    [JsonPropertyName("monitorId")]
    public string? MonitorId { get; init; }

    /// <summary>Initial quality ceiling. Null fields mean "the PC's configured maximum".</summary>
    [JsonPropertyName("quality")]
    public MediaQualityArgs? Quality { get; init; }
}

/// <summary>Result of <see cref="CommandNames.MediaOffer"/>: the PC's SDP answer.</summary>
/// <remarks>
/// The answer carries the PC's ICE candidates inline. They are host candidates only — there is no
/// STUN or TURN (§9.4) — so gathering is immediate and no candidate ever needs to be trickled in
/// this direction.
/// </remarks>
public sealed class MediaAnswerResult
{
    /// <summary>Identifies this media session in later <c>media.*</c> commands and events.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The SDP answer.</summary>
    [JsonPropertyName("sdp")]
    public string Sdp { get; init; } = string.Empty;

    /// <summary>The monitor being streamed.</summary>
    [JsonPropertyName("monitorId")]
    public string MonitorId { get; init; } = string.Empty;

    /// <summary>Encoder in use, e.g. "NVIDIA H.264 Encoder MFT", for the telemetry overlay.</summary>
    [JsonPropertyName("encoder")]
    public string Encoder { get; init; } = string.Empty;

    /// <summary>Whether the encoder is hardware-accelerated.</summary>
    [JsonPropertyName("hardware")]
    public bool Hardware { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.MediaIce"/>: one trickled ICE candidate.</summary>
public sealed class MediaIceArgs
{
    /// <summary>The media session from <see cref="MediaAnswerResult.SessionId"/>.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The candidate line, e.g. <c>candidate:1 1 udp 2122260223 192.168.1.20 50000 typ host</c>.</summary>
    [JsonPropertyName("candidate")]
    public string Candidate { get; init; } = string.Empty;

    /// <summary>Media stream id the candidate belongs to.</summary>
    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }

    /// <summary>Index of the m-line the candidate belongs to.</summary>
    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.MediaStop"/>.</summary>
public sealed class MediaStopArgs
{
    /// <summary>The session to stop. Null stops whatever this connection is streaming.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.MediaSetQuality"/>, and the initial quality in an offer.
/// </summary>
/// <remarks>
/// These are ceilings, not targets: the adaptive controller still steps down beneath them when the
/// network or the encoder cannot keep up (§9.5), and the PC's own configured limits cap them from
/// above. A client asking for 4K at 240 fps gets the PC's maximum, not an error.
/// </remarks>
public sealed class MediaQualityArgs
{
    /// <summary>The session to adjust. Null means this connection's session.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>Largest streamed height in pixels. Null keeps the current ceiling.</summary>
    [JsonPropertyName("maxHeight")]
    public int? MaxHeight { get; init; }

    /// <summary>Highest frame rate. Null keeps the current ceiling.</summary>
    [JsonPropertyName("maxFps")]
    public int? MaxFps { get; init; }

    /// <summary>Highest bitrate in kbit/s. Null keeps the current ceiling.</summary>
    [JsonPropertyName("maxBitrateKbps")]
    public int? MaxBitrateKbps { get; init; }
}

/// <summary>Result of <see cref="CommandNames.MediaSetQuality"/>: the ceilings now in force.</summary>
public sealed class MediaQualityResult
{
    /// <summary>Height ceiling after clamping to the PC's limits.</summary>
    [JsonPropertyName("maxHeight")]
    public int MaxHeight { get; init; }

    /// <summary>Frame-rate ceiling after clamping.</summary>
    [JsonPropertyName("maxFps")]
    public int MaxFps { get; init; }

    /// <summary>Bitrate ceiling after clamping.</summary>
    [JsonPropertyName("maxBitrateKbps")]
    public int MaxBitrateKbps { get; init; }
}

/// <summary>
/// Why a media session is not currently producing frames.
/// </summary>
public static class MediaPauseReasons
{
    /// <summary>The desktop is locked, or the secure desktop (UAC, Ctrl+Alt+Del) is showing (§12.1).</summary>
    public const string DesktopUnavailable = "desktop_unavailable";

    /// <summary>The monitor being streamed was disconnected.</summary>
    public const string MonitorLost = "monitor_lost";

    /// <summary>Capture failed and is being rebuilt, e.g. after a driver reset or mode change.</summary>
    public const string Recovering = "recovering";
}

/// <summary>
/// Payload of <see cref="EventNames.MediaQuality"/>: streaming telemetry, pushed every few
/// seconds to the connection that owns the stream and to no one else.
/// </summary>
public sealed class MediaQualityEvent
{
    /// <summary>The media session this describes.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary><c>connecting</c>, <c>streaming</c>, <c>paused</c> or <c>closed</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>When <see cref="State"/> is <c>paused</c>, one of <see cref="MediaPauseReasons"/>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Frames sent per second over the last interval. Low on a static desktop by design.</summary>
    [JsonPropertyName("fps")]
    public double Fps { get; init; }

    /// <summary>Current frame-rate ceiling from the adaptive controller.</summary>
    [JsonPropertyName("targetFps")]
    public int TargetFps { get; init; }

    /// <summary>Measured send bitrate in kbit/s.</summary>
    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; init; }

    /// <summary>Encoder target bitrate in kbit/s.</summary>
    [JsonPropertyName("targetBitrateKbps")]
    public int TargetBitrateKbps { get; init; }

    /// <summary>Streamed width.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Streamed height.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>Packet loss reported by the receiver, percent.</summary>
    [JsonPropertyName("lossPercent")]
    public double LossPercent { get; init; }

    /// <summary>Round-trip time from RTCP, milliseconds, or null before the first report.</summary>
    [JsonPropertyName("rttMs")]
    public double? RttMs { get; init; }

    /// <summary>Mean capture-to-send time on the PC, milliseconds.</summary>
    [JsonPropertyName("pipelineMs")]
    public double PipelineMs { get; init; }

    /// <summary>Always <c>H264</c> in this version.</summary>
    [JsonPropertyName("codec")]
    public string Codec { get; init; } = "H264";

    /// <summary>Encoder name.</summary>
    [JsonPropertyName("encoder")]
    public string Encoder { get; init; } = string.Empty;

    /// <summary>Whether the encoder is hardware-accelerated.</summary>
    [JsonPropertyName("hardware")]
    public bool Hardware { get; init; }

    /// <summary>
    /// Position on the degradation ladder, 0 being full quality (§9.5). Anything at or above
    /// <see cref="DegradedLevel"/> is the explicitly-labelled degraded mode.
    /// </summary>
    [JsonPropertyName("level")]
    public int Level { get; init; }

    /// <summary>The ladder level from which the client should label the stream as degraded.</summary>
    [JsonPropertyName("degradedLevel")]
    public int DegradedLevel { get; init; }

    /// <summary>A single summary for the UI: <c>good</c>, <c>fair</c> or <c>poor</c>.</summary>
    [JsonPropertyName("indicator")]
    public string Indicator { get; init; } = "good";
}
