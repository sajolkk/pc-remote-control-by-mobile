using System.ComponentModel.DataAnnotations;

namespace RemoteAgent.Core.Configuration;

/// <summary>
/// The agent's complete configuration, bound from <c>config.json</c> (Appendix B).
/// </summary>
/// <remarks>
/// Every environment-specific value lives here rather than in code (§0). The
/// defaults on these properties are chosen to be correct on an arbitrary PC: the
/// device name falls back to the machine name at runtime, ports are defaults that
/// self-heal when occupied, and file roots are expressed as environment variables
/// rather than literal paths.
/// </remarks>
public sealed class AgentOptions
{
    /// <summary>Configuration section name in <c>config.json</c>.</summary>
    public const string SectionName = "agent";

    /// <summary>Identity of this PC.</summary>
    public DeviceOptions Device { get; set; } = new();

    /// <summary>Listener and media port settings.</summary>
    public NetworkOptions Network { get; set; } = new();

    /// <summary>LAN advertisement settings.</summary>
    public DiscoveryOptions Discovery { get; set; } = new();

    /// <summary>Pairing window and anti-brute-force settings.</summary>
    public PairingOptions Pairing { get; set; } = new();

    /// <summary>Session token lifetime and replay window.</summary>
    public SecurityOptions Security { get; set; } = new();

    /// <summary>Capture and encode limits.</summary>
    public StreamingOptions Streaming { get; set; } = new();

    /// <summary>Allowed file-transfer roots and limits.</summary>
    public FileOptions Files { get; set; } = new();

    /// <summary>Which power actions remote callers may request at all.</summary>
    public PowerOptions Power { get; set; } = new();

    /// <summary>Process startup and layout settings.</summary>
    public StartupOptions Startup { get; set; } = new();
}

/// <summary>Process startup settings.</summary>
public sealed class StartupOptions
{
    /// <summary>
    /// Full path to the session agent executable. Empty means "the one beside this executable",
    /// which is correct for every real installation and needs no configuration (§0). It exists as a
    /// setting because a development build puts the two hosts in separate output directories.
    /// </summary>
    public string SessionAgentPath { get; set; } = string.Empty;

    /// <summary>Whether the tray UI should start at logon.</summary>
    public bool TrayUiAtLogon { get; set; } = true;
}

/// <summary>Identity of this PC.</summary>
public sealed class DeviceOptions
{
    /// <summary>
    /// Display name. Empty means "use the machine name", resolved at startup — the
    /// config file is never written with a hardcoded name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Stable device id. Empty means "generate one on first run", after which it is
    /// persisted and never changes.
    /// </summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Listener and media port settings.</summary>
public sealed class NetworkOptions
{
    /// <summary>Preferred TCP port for the control channel.</summary>
    [Range(1, 65535)]
    public int ControlPort { get; set; } = 47800;

    /// <summary>
    /// How many consecutive ports to try if the preferred one is occupied. The
    /// actual port is advertised via discovery, so clients never assume it (§0).
    /// </summary>
    [Range(1, 64)]
    public int ControlPortFallbackRange { get; set; } = 16;

    /// <summary>
    /// Restricts which peers may connect, as CIDR ranges. Empty means "any address
    /// on a local interface's subnet", which is the portable default.
    /// </summary>
    public string[] SubnetAllowlist { get; set; } = [];

    /// <summary>Lowest UDP port used for WebRTC media.</summary>
    [Range(1024, 65535)]
    public int MediaPortRangeStart { get; set; } = 47810;

    /// <summary>Highest UDP port used for WebRTC media.</summary>
    [Range(1024, 65535)]
    public int MediaPortRangeEnd { get; set; } = 47850;

    /// <summary>Maximum simultaneous authenticated connections.</summary>
    [Range(1, 64)]
    public int MaxConnections { get; set; } = 4;

    /// <summary>Maximum simultaneous half-open connections, to bound handshake cost.</summary>
    [Range(1, 128)]
    public int MaxPendingConnections { get; set; } = 8;

    /// <summary>Seconds a connection may stay idle before it is closed.</summary>
    [Range(5, 600)]
    public int KeepaliveTimeoutSeconds { get; set; } = 15;

    /// <summary>Seconds allowed for the TLS handshake and <c>hello</c>.</summary>
    [Range(1, 120)]
    public int HandshakeTimeoutSeconds { get; set; } = 10;
}

/// <summary>LAN advertisement settings.</summary>
public sealed class DiscoveryOptions
{
    /// <summary>Whether the PC advertises itself at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether to advertise over mDNS/DNS-SD.</summary>
    public bool Mdns { get; set; } = true;

    /// <summary>Whether to answer the UDP broadcast fallback probe.</summary>
    public bool UdpFallback { get; set; } = true;

    /// <summary>UDP port for the fallback probe.</summary>
    [Range(1, 65535)]
    public int UdpFallbackPort { get; set; } = 47801;

    /// <summary>Advertisement TTL in seconds.</summary>
    [Range(15, 3600)]
    public int TtlSeconds { get; set; } = 120;
}

/// <summary>Pairing window and anti-brute-force settings (§7.3).</summary>
public sealed class PairingOptions
{
    /// <summary>
    /// Whether pairing is currently open. Defaults to false and is turned on
    /// deliberately by the user for a short window; it auto-closes afterwards.
    /// </summary>
    public bool AllowPairing { get; set; }

    /// <summary>Minutes a pairing window stays open before closing itself.</summary>
    [Range(1, 60)]
    public int WindowMinutes { get; set; } = 5;

    /// <summary>Failed token attempts tolerated in one window before pairing closes.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Whether a human must approve each pairing at the PC. Configurable in shape
    /// only: the service refuses to run with this disabled, because unattended
    /// pairing would make a leaked QR code sufficient for access (§7.3).
    /// </summary>
    public bool RequireLocalApproval { get; set; } = true;

    /// <summary>Seconds the approval dialog waits before treating silence as refusal.</summary>
    [Range(10, 600)]
    public int ApprovalTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whether a phone that found this PC on the network may ask to pair without a QR code. The
    /// user then compares a six-digit code on both screens and approves at the PC. Independent of
    /// <see cref="AllowPairing"/>: it needs no pairing window, only the person at the PC.
    /// </summary>
    public bool AllowCodePairing { get; set; } = true;
}

/// <summary>Session token lifetime and replay defences (§7.2).</summary>
public sealed class SecurityOptions
{
    /// <summary>Session token lifetime in seconds.</summary>
    [Range(60, 3600)]
    public int SessionTokenTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Accepted clock difference for request timestamps. Outside this window a
    /// request is refused as a replay (§6.2).
    /// </summary>
    [Range(5, 300)]
    public int MaxClockSkewSeconds { get; set; } = 30;

    /// <summary>Requests per minute allowed per connection before throttling.</summary>
    [Range(10, 100000)]
    public int MaxRequestsPerMinute { get; set; } = 6000;

    /// <summary>Failed authentication attempts from one address before it is blocked.</summary>
    [Range(1, 100)]
    public int MaxAuthFailuresPerAddress { get; set; } = 10;

    /// <summary>Minutes an address stays blocked after exceeding the failure limit.</summary>
    [Range(1, 1440)]
    public int AuthBlockMinutes { get; set; } = 15;

    /// <summary>Years of validity for the generated identity certificate.</summary>
    [Range(1, 30)]
    public int CertificateLifetimeYears { get; set; } = 10;
}

/// <summary>Capture and encode limits (Phase 3).</summary>
public sealed class StreamingOptions
{
    /// <summary>Maximum streamed width. Capture is downscaled to fit.</summary>
    [Range(320, 7680)]
    public int MaxWidth { get; set; } = 1920;

    /// <summary>Maximum streamed height.</summary>
    [Range(240, 4320)]
    public int MaxHeight { get; set; } = 1080;

    /// <summary>Upper frame-rate bound.</summary>
    [Range(1, 240)]
    public int FpsLimit { get; set; } = 60;

    /// <summary>Lower frame-rate bound before the adaptive controller drops resolution instead.</summary>
    [Range(1, 60)]
    public int MinFps { get; set; } = 15;

    /// <summary>Minimum bitrate in kbit/s.</summary>
    [Range(200, 200000)]
    public int MinBitrateKbps { get; set; } = 2000;

    /// <summary>Maximum bitrate in kbit/s.</summary>
    [Range(200, 200000)]
    public int MaxBitrateKbps { get; set; } = 40000;

    /// <summary>
    /// Whether to use hardware encoding when a suitable encoder is present.
    /// Software encoding is the automatic fallback, not an error (§0).
    /// </summary>
    public bool PreferHardwareEncode { get; set; } = true;

    /// <summary>Whether to composite the mouse cursor into the stream.</summary>
    public bool CaptureCursor { get; set; } = true;

    /// <summary>
    /// Monitor to stream by default. Null means "the primary monitor as reported at
    /// runtime", because a monitor id from one PC is meaningless on another.
    /// </summary>
    public string? DefaultMonitorId { get; set; }

    /// <summary>
    /// Simultaneous streams. Each costs a capture and an encoder, and consumer GPUs cap
    /// concurrent hardware encode sessions, so the default is deliberately small.
    /// </summary>
    [Range(1, 8)]
    public int MaxConcurrentStreams { get; set; } = 2;

    /// <summary>Widest screenshot returned. Larger requests are clamped to this.</summary>
    [Range(320, 3840)]
    public int ScreenshotMaxWidth { get; set; } = 1920;
}

/// <summary>One directory exposed to file transfer.</summary>
public sealed class AllowedRoot
{
    /// <summary>
    /// Name shown to the client. The client only ever sees aliases and relative
    /// paths, never absolute ones, so the PC's directory layout is not disclosed.
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// Directory path, expanded from environment variables at load time. Expressed
    /// as <c>%USERPROFILE%\Downloads</c> rather than a literal path so the same
    /// config works for any user on any machine (§0).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Whether the client may write into this root.</summary>
    public bool Write { get; set; }
}

/// <summary>Allowed file-transfer roots and limits (§7.3).</summary>
public sealed class FileOptions
{
    /// <summary>
    /// Directories exposed to file transfer. Empty means file transfer is entirely
    /// unavailable, which is the safe default for a feature nobody has configured.
    /// </summary>
    public AllowedRoot[] AllowedRoots { get; set; } =
    [
        new() { Alias = "Downloads", Path = "%USERPROFILE%\\Downloads", Write = true },
        new() { Alias = "Documents", Path = "%USERPROFILE%\\Documents", Write = false },
        new() { Alias = "Desktop", Path = "%USERPROFILE%\\Desktop", Write = false },
    ];

    /// <summary>Largest single file accepted or served, in megabytes.</summary>
    [Range(1, 1024 * 64)]
    public int MaxFileSizeMb { get; set; } = 2048;

    /// <summary>
    /// Extensions that may never be written to the PC. Blocking upload of things
    /// Windows will execute on double-click limits the damage a compromised phone
    /// can do, without pretending to be a complete defence.
    /// </summary>
    public string[] BlockedUploadExtensions { get; set; } =
        [".ps1", ".bat", ".cmd", ".scr", ".com", ".pif", ".hta", ".msi", ".reg", ".vbs", ".js", ".jse", ".wsf"];

    /// <summary>Simultaneous transfers allowed per device.</summary>
    [Range(1, 16)]
    public int MaxConcurrentTransfers { get; set; } = 2;
}

/// <summary>Which power actions remote callers may request (§3).</summary>
/// <remarks>
/// A second gate behind the permission model: even a device holding
/// <c>PowerControls</c> cannot shut the PC down if the machine's owner disabled
/// shutdown here.
/// </remarks>
public sealed class PowerOptions
{
    /// <summary>Whether remote shutdown is permitted at all.</summary>
    public bool AllowShutdown { get; set; } = true;

    /// <summary>Whether remote restart is permitted at all.</summary>
    public bool AllowRestart { get; set; } = true;

    /// <summary>Whether remote sleep is permitted at all.</summary>
    public bool AllowSleep { get; set; } = true;

    /// <summary>Whether remote sign-out is permitted at all.</summary>
    public bool AllowSignOut { get; set; } = true;

    /// <summary>Whether remote lock is permitted at all.</summary>
    public bool AllowLock { get; set; } = true;

    /// <summary>Default grace period applied when the client asks for none.</summary>
    [Range(0, 600)]
    public int DefaultDelaySeconds { get; set; } = 5;

    /// <summary>Largest grace period a client may request.</summary>
    [Range(0, 3600)]
    public int MaxDelaySeconds { get; set; } = 600;
}
