using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;

namespace RemoteAgent.Security;

/// <summary>
/// File-backed pairing store (§3).
/// </summary>
/// <remarks>
/// A JSON file rather than a database: the record count is in the single digits, the
/// contents are worth inspecting by hand when diagnosing a pairing problem, and a
/// plain file is trivial to back up and to reason about. It is loaded once and kept
/// in memory, so authentication never waits on disk.
/// <para>
/// The file holds no secret — a device's public certificate fingerprint is its
/// credential, and proof of possession happens in the TLS handshake — so it is
/// stored in the clear and protected by ACLs (SYSTEM and Administrators only,
/// applied by the Windows layer) rather than by encryption. Writing it encrypted
/// would imply a confidentiality guarantee that is not actually needed and would
/// make manual recovery harder.
/// </para>
/// <para>
/// Writes are atomic (temp file then move) so an interrupted save cannot destroy
/// every existing pairing.
/// </para>
/// </remarks>
public sealed class JsonPairingStore : IPairingStore
{
    private const int FileVersion = 1;

    private readonly AgentPaths _paths;
    private readonly IClock _clock;
    private readonly ILogger<JsonPairingStore> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private Dictionary<string, PairedDevice>? _byDeviceId;

    /// <summary>Creates the store.</summary>
    public JsonPairingStore(AgentPaths paths, IClock clock, ILogger<JsonPairingStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised after any change, so the UI and connection manager can react.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PairedDevice>> GetAllAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, PairedDevice> devices = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return devices.Values.OrderBy(static d => d.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <inheritdoc />
    public async Task<PairedDevice?> FindByFingerprintAsync(
        string certificateFingerprint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(certificateFingerprint))
        {
            return null;
        }

        Dictionary<string, PairedDevice> devices = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        // Linear scan over a handful of records, using a constant-time comparison so
        // the lookup does not reveal how much of a fingerprint matched.
        foreach (PairedDevice device in devices.Values)
        {
            if (CertificateFingerprint.Equal(device.CertificateFingerprint, certificateFingerprint))
            {
                return device;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<PairedDevice?> FindByDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        Dictionary<string, PairedDevice> devices = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return devices.GetValueOrDefault(deviceId);
    }

    /// <inheritdoc />
    public Task SaveAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        return MutateAsync(devices => devices[device.DeviceId] = device, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(string deviceId, CancellationToken cancellationToken)
    {
        bool removed = false;
        await MutateAsync(devices => removed = devices.Remove(deviceId), cancellationToken).ConfigureAwait(false);

        if (removed)
        {
            _logger.LogInformation("Pairing revoked for device {DeviceId}.", deviceId);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<bool> SetPermissionsAsync(
        string deviceId,
        Permission permissions,
        CancellationToken cancellationToken)
    {
        bool updated = false;
        await MutateAsync(
            devices =>
            {
                if (devices.TryGetValue(deviceId, out PairedDevice? existing))
                {
                    devices[deviceId] = existing with { Permissions = permissions };
                    updated = true;
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (updated)
        {
            _logger.LogInformation(
                "Permissions changed for device {DeviceId}: {Permissions}",
                deviceId,
                string.Join(",", PermissionSet.ToNames(permissions)));
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> RenameAsync(string deviceId, string deviceName, CancellationToken cancellationToken)
    {
        bool updated = false;
        string trimmed = (deviceName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        await MutateAsync(
            devices =>
            {
                if (devices.TryGetValue(deviceId, out PairedDevice? existing))
                {
                    devices[deviceId] = existing with { DeviceName = trimmed };
                    updated = true;
                }
            },
            cancellationToken).ConfigureAwait(false);

        return updated;
    }

    /// <inheritdoc />
    public Task RecordConnectionAsync(string deviceId, string remoteAddress, CancellationToken cancellationToken) =>
        MutateAsync(
            devices =>
            {
                if (devices.TryGetValue(deviceId, out PairedDevice? existing))
                {
                    devices[deviceId] = existing with
                    {
                        LastSeenAt = _clock.UtcNow,
                        LastAddress = remoteAddress,
                        ConnectionCount = existing.ConnectionCount + 1,
                    };
                }
            },
            cancellationToken);

    private async Task<Dictionary<string, PairedDevice>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_byDeviceId is not null)
        {
            return _byDeviceId;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _byDeviceId ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _byDeviceId;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, PairedDevice>> LoadAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PairedDevice>(StringComparer.Ordinal);

        if (!File.Exists(_paths.PairingStoreFile))
        {
            _logger.LogInformation("No pairing store found; starting with no paired devices.");
            return result;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_paths.PairingStoreFile);
            PairingFile? file = await JsonSerializer
                .DeserializeAsync<PairingFile>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (file?.Devices is null)
            {
                _logger.LogWarning("The pairing store is empty or unreadable; treating it as no pairings.");
                return result;
            }

            foreach (PairedDeviceRecord record in file.Devices)
            {
                if (string.IsNullOrWhiteSpace(record.DeviceId) ||
                    string.IsNullOrWhiteSpace(record.CertificateFingerprint))
                {
                    // A record without an id or a credential cannot authenticate
                    // anything. Skipping it is safer than guessing what was meant.
                    _logger.LogWarning("Skipping a pairing record with no device id or fingerprint.");
                    continue;
                }

                result[record.DeviceId] = new PairedDevice
                {
                    DeviceId = record.DeviceId,
                    DeviceName = string.IsNullOrWhiteSpace(record.DeviceName) ? "Mobile device" : record.DeviceName,
                    Platform = record.Platform ?? string.Empty,
                    Model = record.Model ?? string.Empty,
                    CertificateFingerprint = record.CertificateFingerprint,
                    PairedAt = record.PairedAt,
                    LastSeenAt = record.LastSeenAt,
                    LastAddress = record.LastAddress,
                    Permissions = PermissionSet.FromNames(record.Permissions),
                    ConnectionCount = record.ConnectionCount,
                };
            }

            _logger.LogInformation("Loaded {Count} paired device(s).", result.Count);
            return result;
        }
        catch (JsonException ex)
        {
            // Refusing to start would leave the user with no way in. Starting with no
            // pairings is recoverable: they pair again, and the corrupt file is kept
            // for inspection rather than silently overwritten.
            _logger.LogError(
                ex,
                "The pairing store could not be parsed. Starting with no paired devices; the existing file " +
                "is preserved and will be replaced on the next successful pairing.");
            return result;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "The pairing store could not be read.");
            return result;
        }
    }

    private async Task MutateAsync(Action<Dictionary<string, PairedDevice>> mutation, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _byDeviceId ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
            mutation(_byDeviceId);
            await PersistAsync(_byDeviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task PersistAsync(
        Dictionary<string, PairedDevice> devices,
        CancellationToken cancellationToken)
    {
        var file = new PairingFile
        {
            Version = FileVersion,
            UpdatedUtc = _clock.UtcNow,
            Devices = devices.Values.Select(static d => new PairedDeviceRecord
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                Platform = d.Platform,
                Model = d.Model,
                CertificateFingerprint = d.CertificateFingerprint,
                PairedAt = d.PairedAt,
                LastSeenAt = d.LastSeenAt,
                LastAddress = d.LastAddress,
                Permissions = PermissionSet.ToNames(d.Permissions),
                ConnectionCount = d.ConnectionCount,
            }).ToArray(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.PairingStoreFile)!);

        string temp = _paths.PairingStoreFile + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(file, SerializerOptions);
        await File.WriteAllBytesAsync(temp, payload, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _paths.PairingStoreFile, overwrite: true);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class PairingFile
    {
        public int Version { get; init; }

        public DateTimeOffset UpdatedUtc { get; init; }

        public PairedDeviceRecord[] Devices { get; init; } = [];
    }

    private sealed class PairedDeviceRecord
    {
        public string DeviceId { get; init; } = string.Empty;

        public string DeviceName { get; init; } = string.Empty;

        public string? Platform { get; init; }

        public string? Model { get; init; }

        public string CertificateFingerprint { get; init; } = string.Empty;

        public DateTimeOffset PairedAt { get; init; }

        public DateTimeOffset? LastSeenAt { get; init; }

        public string? LastAddress { get; init; }

        public string[] Permissions { get; init; } = [];

        public int ConnectionCount { get; init; }
    }
}
