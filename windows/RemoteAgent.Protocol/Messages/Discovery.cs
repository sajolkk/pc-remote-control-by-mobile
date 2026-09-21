using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// What a PC advertises on the LAN (§6.1).
/// </summary>
/// <remarks>
/// Discovery is unauthenticated by nature — anything here is readable by every
/// device on the network, including hostile ones. So it carries only what a client
/// needs in order to attempt a connection, and nothing that helps an attacker:
/// no user name, no OS build, no capability list, no MAC address, no pairing state,
/// and no indication of whether any device is currently paired.
/// <para>
/// The device id is a random GUID with no relationship to hardware serials, so
/// publishing it reveals nothing beyond "a PC-Remote agent exists here", which is
/// unavoidable for any discoverable service.
/// </para>
/// </remarks>
public sealed class DiscoveryBeacon
{
    /// <summary>Beacon schema version, so a newer PC can still be understood.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Stable device id, used as the reconnect key.</summary>
    [JsonPropertyName("id")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// Display name. Defaults to the computer name, and is user-editable precisely
    /// so someone who considers their machine name identifying can change it.
    /// </summary>
    [JsonPropertyName("name")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>The control port actually listening, which may differ from the default.</summary>
    [JsonPropertyName("port")]
    public int Port { get; init; }

    /// <summary>Protocol version the PC speaks, so an incompatible client can say so early.</summary>
    [JsonPropertyName("proto")]
    public int ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;
}

/// <summary>
/// DNS-SD service naming and TXT record keys.
/// </summary>
public static class DiscoveryConstants
{
    /// <summary>The DNS-SD service type advertised and browsed for.</summary>
    public const string ServiceType = "_pcremote._tcp";

    /// <summary>TXT key for the beacon's schema version.</summary>
    public const string TxtVersion = "v";

    /// <summary>TXT key for the device id.</summary>
    public const string TxtDeviceId = "id";

    /// <summary>TXT key for the display name.</summary>
    public const string TxtDeviceName = "name";

    /// <summary>TXT key for the protocol version.</summary>
    public const string TxtProtocol = "proto";

    /// <summary>
    /// Magic prefix for the UDP discovery fallback, used where multicast DNS is
    /// filtered. A probe must start with these bytes or it is ignored, which keeps
    /// unrelated broadcast traffic from reaching the parser.
    /// </summary>
    public const string UdpProbeMagic = "PCRQ1";

    /// <summary>Magic prefix of a UDP discovery response.</summary>
    public const string UdpResponseMagic = "PCRS1";

    /// <summary>Maximum accepted size of a UDP discovery datagram.</summary>
    public const int MaxUdpDatagramBytes = 512;
}
