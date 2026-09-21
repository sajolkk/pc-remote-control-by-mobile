using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// Arguments for <see cref="CommandNames.Hello"/>: the client's supported
/// protocol range and its own identification.
/// </summary>
public sealed class HelloArgs
{
    /// <summary>Oldest protocol version the client can speak.</summary>
    [JsonPropertyName("minVersion")]
    public int MinVersion { get; init; } = ProtocolVersion.MinSupported;

    /// <summary>Newest protocol version the client can speak.</summary>
    [JsonPropertyName("maxVersion")]
    public int MaxVersion { get; init; } = ProtocolVersion.Current;

    /// <summary>Human-readable device name, shown in the PC's Paired Devices page.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Client platform, e.g. <c>android</c> or <c>ios</c>. Informational only.</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    /// <summary>Client app version, for diagnostics.</summary>
    [JsonPropertyName("appVersion")]
    public string AppVersion { get; init; } = string.Empty;
}

/// <summary>
/// Result of <see cref="CommandNames.Hello"/>.
/// </summary>
/// <remarks>
/// This is the message that makes one mobile build work against any PC (§0): the
/// client renders its UI from <see cref="Capabilities"/> and
/// <see cref="Permissions"/> rather than from assumptions about what a PC can do.
/// </remarks>
public sealed class HelloResult
{
    /// <summary>The protocol version both sides will use for this session.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = ProtocolVersion.Current;

    /// <summary>The PC's stable device id.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>The PC's display name.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Session token to present on every subsequent request (§7.2).</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Seconds until <see cref="Token"/> expires. The client renews before then.</summary>
    [JsonPropertyName("tokenTtlSec")]
    public int TokenTtlSeconds { get; init; }

    /// <summary>Permission groups granted to this device, by name.</summary>
    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];

    /// <summary>
    /// Capabilities this PC actually has right now, from
    /// <see cref="CapabilityNames"/>. Absent capability means the feature is
    /// hidden in the client, not that it errors.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; init; } = [];

    /// <summary>Seconds of inactivity after which the PC closes the connection.</summary>
    [JsonPropertyName("keepaliveTimeoutSec")]
    public int KeepaliveTimeoutSeconds { get; init; }

    /// <summary>Accepted clock skew for request timestamps, in seconds.</summary>
    [JsonPropertyName("maxClockSkewSec")]
    public int MaxClockSkewSeconds { get; init; }
}

/// <summary>
/// Result of <see cref="CommandNames.SessionRenew"/>.
/// </summary>
public sealed class SessionRenewResult
{
    /// <summary>The replacement token. The previous one is invalid immediately.</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Seconds until the new token expires.</summary>
    [JsonPropertyName("tokenTtlSec")]
    public int TokenTtlSeconds { get; init; }

    /// <summary>
    /// The device's permissions as of now. Re-sent on every renewal so a grant or
    /// revocation made at the PC reaches the client without a reconnect.
    /// </summary>
    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];
}

/// <summary>
/// Result of <see cref="CommandNames.PermissionsList"/>.
/// </summary>
public sealed class PermissionsResult
{
    /// <summary>Granted groups.</summary>
    [JsonPropertyName("granted")]
    public string[] Granted { get; init; } = [];

    /// <summary>Every group that exists, so the client can show what it is missing.</summary>
    [JsonPropertyName("available")]
    public string[] Available { get; init; } = [];
}

/// <summary>
/// Capability identifiers advertised in <see cref="HelloResult.Capabilities"/>.
/// </summary>
/// <remarks>
/// A capability is present only when the PC has probed it successfully — the
/// feature-detection half of the portability rule in §0. For example
/// <see cref="ScreenCapture"/> is absent on a machine whose only display adapter
/// refuses duplication, and <see cref="WakeOnLan"/> is absent when no adapter has
/// magic-packet wake enabled.
/// </remarks>
public static class CapabilityNames
{
    /// <summary>Desktop capture works on at least one monitor.</summary>
    public const string ScreenCapture = "screen.capture";

    /// <summary>Hardware-accelerated H.264 encoding is available.</summary>
    public const string HardwareEncodeH264 = "encode.h264.hardware";

    /// <summary>Software H.264 encoding is available as a fallback.</summary>
    public const string SoftwareEncodeH264 = "encode.h264.software";

    /// <summary>More than one monitor is attached.</summary>
    public const string MultiMonitor = "screen.multi_monitor";

    /// <summary>Remote input injection is available.</summary>
    public const string Input = "input";

    /// <summary>Application enumeration and launching are available.</summary>
    public const string Apps = "apps";

    /// <summary>At least one browser was detected.</summary>
    public const string Browser = "browser";

    /// <summary>Clipboard read/write is available.</summary>
    public const string Clipboard = "clipboard";

    /// <summary>File transfer is configured with at least one allowed root.</summary>
    public const string Files = "files";

    /// <summary>Volume control is available (an active audio endpoint exists).</summary>
    public const string Volume = "volume";

    /// <summary>The PC reports sleep as a supported power state.</summary>
    public const string PowerSleep = "power.sleep";

    /// <summary>Shutdown and restart are permitted by configuration and privilege.</summary>
    public const string PowerShutdown = "power.shutdown";

    /// <summary>Sign-out is permitted.</summary>
    public const string PowerSignOut = "power.signout";

    /// <summary>Wake-on-LAN is expected to work, and MAC addresses are available.</summary>
    public const string WakeOnLan = "wol";

    /// <summary>An interactive session agent is currently connected.</summary>
    public const string SessionAgent = "session.agent";
}
