using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// State of the interactive Windows session, as far as remote control is concerned.
/// </summary>
public enum InteractiveSessionState
{
    /// <summary>State could not be determined.</summary>
    Unknown,

    /// <summary>Nobody is logged on; Windows is at the logon screen.</summary>
    LoggedOut,

    /// <summary>A user is logged on but the workstation is locked. Capture and input are blocked (§12.1).</summary>
    Locked,

    /// <summary>A user is logged on and the desktop is active.</summary>
    Active,
}

/// <summary>
/// Result of <see cref="CommandNames.DeviceInfo"/>: facts that change rarely.
/// </summary>
/// <remarks>
/// Every value here is read from the OS at runtime — nothing is compiled in (§0).
/// </remarks>
public sealed class DeviceInfoResult
{
    /// <summary>Stable device id, generated on first run.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Display name, defaulting to the computer name.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>The machine's NetBIOS/DNS host name.</summary>
    [JsonPropertyName("hostName")]
    public string HostName { get; init; } = string.Empty;

    /// <summary>Windows edition and build, e.g. <c>Windows 10 Pro 22H2 (19045)</c>.</summary>
    [JsonPropertyName("osVersion")]
    public string OsVersion { get; init; } = string.Empty;

    /// <summary>Process architecture, e.g. <c>X64</c> or <c>Arm64</c>.</summary>
    [JsonPropertyName("architecture")]
    public string Architecture { get; init; } = string.Empty;

    /// <summary>Agent build version.</summary>
    [JsonPropertyName("agentVersion")]
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>Logical processor count.</summary>
    [JsonPropertyName("cpuCount")]
    public int CpuCount { get; init; }

    /// <summary>Installed physical memory in megabytes, or 0 when unavailable.</summary>
    [JsonPropertyName("totalMemoryMb")]
    public long TotalMemoryMb { get; init; }

    /// <summary>LAN addresses currently bound, re-read on every network change.</summary>
    [JsonPropertyName("localAddresses")]
    public string[] LocalAddresses { get; init; } = [];

    /// <summary>The control port actually listening.</summary>
    [JsonPropertyName("controlPort")]
    public int ControlPort { get; init; }

    /// <summary>
    /// The TLS version negotiated for this connection, e.g. <c>Tls13</c> or
    /// <c>Tls12</c>. Surfaced because it varies by Windows build (§7.1).
    /// </summary>
    [JsonPropertyName("tlsVersion")]
    public string TlsVersion { get; init; } = string.Empty;

    /// <summary>Capabilities probed on this machine.</summary>
    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; init; } = [];
}

/// <summary>
/// Result of <see cref="CommandNames.SystemState"/>: facts that change constantly.
/// </summary>
public sealed class SystemStateResult
{
    /// <summary>Whether an interactive session is active, locked or absent.</summary>
    [JsonPropertyName("sessionState")]
    public InteractiveSessionState SessionState { get; init; } = InteractiveSessionState.Unknown;

    /// <summary>User name of the interactive session, or null when nobody is logged on.</summary>
    [JsonPropertyName("userName")]
    public string? UserName { get; init; }

    /// <summary>Whether the user-session agent is connected and able to serve session commands.</summary>
    [JsonPropertyName("sessionAgentConnected")]
    public bool SessionAgentConnected { get; init; }

    /// <summary>Seconds since Windows booted.</summary>
    [JsonPropertyName("uptimeSec")]
    public long UptimeSeconds { get; init; }

    /// <summary>Master volume 0-100, or null when no audio endpoint or no session agent.</summary>
    [JsonPropertyName("volume")]
    public int? Volume { get; init; }

    /// <summary>Whether audio is muted, or null when unknown.</summary>
    [JsonPropertyName("muted")]
    public bool? Muted { get; init; }

    /// <summary>Attached monitor count, or null when unknown.</summary>
    [JsonPropertyName("monitorCount")]
    public int? MonitorCount { get; init; }

    /// <summary>Whether a restart or shutdown is pending and still cancellable.</summary>
    [JsonPropertyName("shutdownPending")]
    public bool ShutdownPending { get; init; }

    /// <summary>Battery percentage 0-100, or null on a desktop.</summary>
    [JsonPropertyName("batteryPercent")]
    public int? BatteryPercent { get; init; }

    /// <summary>Whether the machine is on AC power, or null when unknown.</summary>
    [JsonPropertyName("onAcPower")]
    public bool? OnAcPower { get; init; }
}

/// <summary>
/// Arguments shared by restart and shutdown.
/// </summary>
public sealed class PowerActionArgs
{
    /// <summary>
    /// Grace period before the action executes, giving the user a chance to abort
    /// at the PC. Clamped to the configured maximum.
    /// </summary>
    [JsonPropertyName("delaySec")]
    public int DelaySeconds { get; init; }

    /// <summary>
    /// Whether to force applications closed rather than letting them block.
    /// Defaults to false: data loss must be an explicit choice.
    /// </summary>
    [JsonPropertyName("force")]
    public bool Force { get; init; }
}

/// <summary>
/// Result of a power command.
/// </summary>
public sealed class PowerActionResult
{
    /// <summary>Whether Windows accepted the request.</summary>
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    /// <summary>Seconds until execution, 0 when immediate.</summary>
    [JsonPropertyName("delaySec")]
    public int DelaySeconds { get; init; }

    /// <summary>Whether the action can still be cancelled with <c>system.abort_shutdown</c>.</summary>
    [JsonPropertyName("cancellable")]
    public bool Cancellable { get; init; }
}

/// <summary>
/// Result of <see cref="CommandNames.WolInfo"/>.
/// </summary>
/// <remarks>
/// The magic packet is sent by the phone, not the PC, so this command only reports
/// what the phone needs and whether it will actually work. The honesty matters:
/// with Fast Startup enabled, a "shut down" PC frequently cannot be woken, and
/// telling the user that up front beats a button that silently fails (§12.2).
/// </remarks>
public sealed class WolInfoResult
{
    /// <summary>Whether wake is expected to succeed from sleep.</summary>
    [JsonPropertyName("supported")]
    public bool Supported { get; init; }

    /// <summary>MAC addresses to target, colon-separated upper-case hex.</summary>
    [JsonPropertyName("macAddresses")]
    public string[] MacAddresses { get; init; } = [];

    /// <summary>Broadcast addresses appropriate for the current subnets.</summary>
    [JsonPropertyName("broadcastAddresses")]
    public string[] BroadcastAddresses { get; init; } = [];

    /// <summary>Port to send the magic packet to, conventionally 9.</summary>
    [JsonPropertyName("port")]
    public int Port { get; init; } = 9;

    /// <summary>Whether Fast Startup is on, which usually breaks wake-from-shutdown.</summary>
    [JsonPropertyName("fastStartupEnabled")]
    public bool FastStartupEnabled { get; init; }

    /// <summary>Plain-language explanation of any limitation, for display.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}
