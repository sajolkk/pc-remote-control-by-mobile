using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// The payload encoded in the PC's pairing QR code (§8.1).
/// </summary>
/// <remarks>
/// Deliberately contains no secret beyond the single-use, short-lived
/// <see cref="Token"/>: no password, no private key, and no permission grant. The
/// fingerprint is public information whose only job is to close the
/// trust-on-first-use window, and the token is useless without also passing the
/// approval dialog at the PC.
/// <para>
/// Kept compact because QR density matters: short property names, and hosts
/// limited to the addresses actually reachable on the current LAN.
/// </para>
/// </remarks>
public sealed class PairingQrPayload
{
    /// <summary>Payload schema version.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>The PC's stable device id.</summary>
    [JsonPropertyName("id")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>The PC's display name, shown to the user before they confirm.</summary>
    [JsonPropertyName("n")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>
    /// Base64url SHA-256 of the PC's DER-encoded TLS certificate. The client pins
    /// this on its very first connection, so there is no moment at which a LAN
    /// attacker can impersonate the PC (§7.1).
    /// </summary>
    [JsonPropertyName("fp")]
    public string CertificateFingerprint { get; init; } = string.Empty;

    /// <summary>Single-use pairing token, 128 bits of CSPRNG output, base64url.</summary>
    [JsonPropertyName("t")]
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// The control port actually in use. Never assumed by the client, because the
    /// service may have moved off its default when the port was occupied (§0).
    /// </summary>
    [JsonPropertyName("p")]
    public int Port { get; init; }

    /// <summary>
    /// Candidate LAN addresses, discovered at runtime. The client tries these in
    /// order and falls back to mDNS if none answer.
    /// </summary>
    [JsonPropertyName("h")]
    public string[] Hosts { get; init; } = [];
}

/// <summary>
/// Arguments for <see cref="CommandNames.PairRequest"/>.
/// </summary>
public sealed class PairRequestArgs
{
    /// <summary>The mobile device's stable, self-generated id.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Name to display in the approval dialog and Paired Devices page.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Platform string, e.g. <c>android</c>, for display only.</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    /// <summary>Model string, e.g. <c>Pixel 8</c>, for display only.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// The token from the QR code. Verified as valid, unexpired and unused before
    /// the user is even prompted, so a wrong token never costs the user a dialog.
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// The client's own certificate fingerprint. Must equal the fingerprint of the
    /// certificate presented on this TLS connection; a mismatch means the request
    /// was relayed and is rejected.
    /// </summary>
    [JsonPropertyName("clientFingerprint")]
    public string ClientFingerprint { get; init; } = string.Empty;
}

/// <summary>
/// Result of a successful <see cref="CommandNames.PairRequest"/>.
/// </summary>
public sealed class PairResult
{
    /// <summary>The PC's device id, stored by the client as the reconnect key.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>The PC's display name.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>
    /// The PC's certificate fingerprint, echoed so the client can confirm it
    /// matches what the QR code claimed before it commits the pairing.
    /// </summary>
    [JsonPropertyName("fingerprint")]
    public string CertificateFingerprint { get; init; } = string.Empty;

    /// <summary>Permission groups granted at pairing time (defaults, per §7.4).</summary>
    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];

    /// <summary>Protocol version the PC speaks.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = Protocol.ProtocolVersion.Current;
}
