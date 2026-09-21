using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// A device that has completed pairing and may connect.
/// </summary>
/// <remarks>
/// The record holds no secret: the device's public certificate fingerprint is the
/// credential, and possession of the matching private key is proven by the TLS
/// handshake itself. There is nothing here worth stealing, which is deliberate —
/// compromise of the pairing file alone does not let an attacker impersonate a
/// paired phone.
/// </remarks>
public sealed record PairedDevice
{
    /// <summary>The device's self-generated stable id.</summary>
    public required string DeviceId { get; init; }

    /// <summary>User-editable display name.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Platform string, e.g. <c>android</c>. Display only.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Model string, e.g. <c>Pixel 8</c>. Display only.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Base64url SHA-256 of the device's DER certificate. This is the credential
    /// the TLS layer matches against, and the lookup key for authentication.
    /// </summary>
    public required string CertificateFingerprint { get; init; }

    /// <summary>When pairing was approved.</summary>
    public required DateTimeOffset PairedAt { get; init; }

    /// <summary>When the device last completed a handshake, or null if never.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Last peer address seen. Audit only — never used for authorization.</summary>
    public string? LastAddress { get; init; }

    /// <summary>Permission groups granted to this device.</summary>
    public Permission Permissions { get; init; } = PermissionSet.Default;

    /// <summary>Total successful connections, for the device's history view.</summary>
    public int ConnectionCount { get; init; }
}

/// <summary>
/// Persistent store of paired devices (§3).
/// </summary>
/// <remarks>
/// Owned exclusively by the Windows service. The file lives under
/// <c>%ProgramData%</c> with inherited ACLs removed so that only SYSTEM and
/// Administrators can read or write it.
/// </remarks>
public interface IPairingStore
{
    /// <summary>Every paired device, for the Paired Devices page.</summary>
    Task<IReadOnlyList<PairedDevice>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a TLS peer's certificate fingerprint to its pairing record. This is
    /// the authentication lookup, and returning null is what produces
    /// <see cref="ErrorCodes.NotPaired"/>.
    /// </summary>
    Task<PairedDevice?> FindByFingerprintAsync(string certificateFingerprint, CancellationToken cancellationToken);

    /// <summary>Finds a device by its id.</summary>
    Task<PairedDevice?> FindByDeviceIdAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Adds or replaces a pairing record.</summary>
    Task SaveAsync(PairedDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a pairing. Live connections for that device must be dropped by the
    /// caller immediately afterwards, and its session tokens invalidated (§8.3).
    /// </summary>
    Task<bool> RevokeAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Replaces a device's permission set.</summary>
    Task<bool> SetPermissionsAsync(string deviceId, Permission permissions, CancellationToken cancellationToken);

    /// <summary>Renames a device.</summary>
    Task<bool> RenameAsync(string deviceId, string deviceName, CancellationToken cancellationToken);

    /// <summary>Records a successful connection: last-seen time, address and counter.</summary>
    Task RecordConnectionAsync(string deviceId, string remoteAddress, CancellationToken cancellationToken);
}
