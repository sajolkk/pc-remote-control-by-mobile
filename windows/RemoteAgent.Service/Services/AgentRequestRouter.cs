using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using RemoteAgent.Service.Network;
using FileOptions = RemoteAgent.Core.Configuration.FileOptions;

namespace RemoteAgent.Service.Services;

/// <summary>
/// Handles the requests that travel from the session agent to the service.
/// </summary>
/// <remarks>
/// <para>Most IPC traffic goes service → agent, forwarding commands to the desktop. This handles
/// the other direction, which exists because the two halves each hold something the other needs:
/// the service owns the identity fingerprint, the listening port and the pairing token, while the
/// agent owns the only desktop those things can be displayed on.</para>
///
/// <para><b>Opening pairing from the agent is not a security hole.</b> It grants nothing by itself
/// — it opens a five-minute window in which a device presenting the right single-use token and
/// receiving a human's approval may pair. The person able to trigger it is the person signed in at
/// the PC, who can already do anything to that machine. That is the same trust level the approval
/// dialog itself assumes.</para>
/// </remarks>
public sealed class AgentRequestRouter
{
    private readonly IPairingService _pairing;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IIdentityStore _identityStore;
    private readonly ListenerEndpointStatus _endpoint;
    private readonly ClientSessionManager _sessions;
    private readonly ISessionStateProvider _sessionState;
    private readonly AgentConfigurationWriter _configWriter;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly IPairingStore _pairingStore;
    private readonly ILogger<AgentRequestRouter> _logger;

    /// <summary>Creates the router.</summary>
    public AgentRequestRouter(
        IPairingService pairing,
        ISystemInfoProvider systemInfo,
        IIdentityStore identityStore,
        ListenerEndpointStatus endpoint,
        ClientSessionManager sessions,
        ISessionStateProvider sessionState,
        AgentConfigurationWriter configWriter,
        IOptionsMonitor<AgentOptions> options,
        IPairingStore pairingStore,
        ILogger<AgentRequestRouter> logger)
    {
        _pairingStore = pairingStore;
        _pairing = pairing;
        _systemInfo = systemInfo;
        _identityStore = identityStore;
        _endpoint = endpoint;
        _sessions = sessions;
        _sessionState = sessionState;
        _configWriter = configWriter;
        _options = options;
        _logger = logger;
    }

    /// <summary>Routes one request from the agent.</summary>
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            IpcCommands.OpenPairing => await OpenPairingAsync(request, cancellationToken).ConfigureAwait(false),
            IpcCommands.ClosePairing => ClosePairing(request),
            IpcCommands.Status => await StatusAsync(request, cancellationToken).ConfigureAwait(false),
            IpcCommands.ListDevices => await ListDevicesAsync(request, cancellationToken).ConfigureAwait(false),
            IpcCommands.SetDeviceBlocked => await SetDeviceBlockedAsync(request, cancellationToken).ConfigureAwait(false),
            IpcCommands.RemoveDevice => await RemoveDeviceAsync(request, cancellationToken).ConfigureAwait(false),
            IpcCommands.SetDevicePermissions => await SetDevicePermissionsAsync(request, cancellationToken).ConfigureAwait(false),
            IpcCommands.GetFilePolicy => GetFilePolicy(request),
            _ => IpcResponse.Failure(
                request.Id,
                ErrorCodes.UnknownCommand,
                $"The service does not handle '{request.Command}'."),
        };
    }

    /// <summary>
    /// Opens a pairing window and returns everything the QR code needs.
    /// </summary>
    private async Task<IpcResponse> OpenPairingAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        DeviceIdentity identity = await _identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        AgentOptions options = _options.CurrentValue;

        // Pairing mode is a persisted setting as well as in-memory state, so that the service's
        // own checks agree with what the user asked for. Written before the window opens: a token
        // issued while the setting still said "closed" would be refused on use.
        await _configWriter.SetAllowPairingAsync(true, cancellationToken).ConfigureAwait(false);
        options.Pairing.AllowPairing = true;

        PairingWindow window = _pairing.OpenWindow();

        IReadOnlyList<string> addresses = _systemInfo.GetLocalAddresses();

        var payload = new PairingQrPayload
        {
            DeviceId = options.Device.Id,
            DeviceName = options.Device.Name,
            CertificateFingerprint = identity.Fingerprint,
            Token = window.Token,
            Port = _endpoint.Port,
            Hosts = addresses.ToArray(),
        };

        _logger.LogInformation(
            "Pairing opened from the desktop. Window closes at {ExpiresAt}. Advertising {Count} address(es) " +
            "on port {Port}.",
            window.ExpiresAt,
            addresses.Count,
            _endpoint.Port);

        // The QR payload is returned, never logged: it contains a live single-use token, and §7.5
        // excludes tokens from logs.
        return IpcResponse.Success(
            request.Id,
            new JsonObject
            {
                ["qr"] = JsonSerializer.Serialize(payload, ProtocolJson.Options),
                ["expiresAtUtc"] = window.ExpiresAt.UtcDateTime.ToString("O"),
                ["fingerprintShort"] = identity.FingerprintShort,
                ["port"] = _endpoint.Port,
                ["addresses"] = new JsonArray([.. addresses.Select(a => JsonValue.Create(a))]),
                ["deviceName"] = options.Device.Name,
            });
    }

    private IpcResponse ClosePairing(IpcRequest request)
    {
        _pairing.CloseWindow();
        _configWriter.SetAllowPairingAsync(false, CancellationToken.None).ContinueWith(
            task =>
            {
                if (task.Exception is not null)
                {
                    _logger.LogWarning(task.Exception, "Could not persist the closed pairing state.");
                }
            },
            TaskScheduler.Default);

        _logger.LogInformation("Pairing closed from the desktop.");
        return IpcResponse.Success(request.Id);
    }

    private async Task<IpcResponse> StatusAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        DeviceIdentity identity = await _identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        AgentOptions options = _options.CurrentValue;

        var connected = new JsonArray();
        foreach (ClientSession session in _sessions.All)
        {
            if (session.Device is null)
            {
                continue;
            }

            connected.Add(new JsonObject
            {
                ["deviceName"] = session.Device.DeviceName,
                ["address"] = session.RemoteAddress,
                ["authenticated"] = session.IsAuthenticated,
            });
        }

        return IpcResponse.Success(
            request.Id,
            new JsonObject
            {
                ["deviceName"] = options.Device.Name,
                ["port"] = _endpoint.Port,
                ["listening"] = _endpoint.IsListening,
                ["fingerprintShort"] = identity.FingerprintShort,
                ["pairingOpen"] = _pairing.IsOpen,
                ["sessionState"] = _sessionState.State.ToString(),
                ["addresses"] = new JsonArray([.. _systemInfo.GetLocalAddresses().Select(a => JsonValue.Create(a))]),
                ["connections"] = connected,
            });
    }

    /// <summary>Every paired device, with whether it has an authenticated connection right now.</summary>
    private async Task<IpcResponse> ListDevicesAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<PairedDevice> devices = await _pairingStore.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var list = new JsonArray();
        foreach (PairedDevice device in devices)
        {
            ClientSession? live = _sessions.All.FirstOrDefault(s =>
                s.IsAuthenticated &&
                string.Equals(s.Device?.DeviceId, device.DeviceId, StringComparison.Ordinal));

            list.Add(new JsonObject
            {
                ["deviceId"] = device.DeviceId,
                ["deviceName"] = device.DeviceName,
                ["platform"] = device.Platform,
                ["model"] = device.Model,
                ["pairedAt"] = device.PairedAt.UtcDateTime.ToString("O"),
                ["lastSeenAt"] = device.LastSeenAt?.UtcDateTime.ToString("O"),
                ["lastAddress"] = device.LastAddress,
                ["blocked"] = device.Blocked,
                ["permissions"] = new JsonArray(
                    [.. PermissionSet.ToNames(device.Permissions).Select(name => JsonValue.Create(name))]),
                ["connected"] = live is not null,
                ["address"] = live?.RemoteAddress,
            });
        }

        return IpcResponse.Success(request.Id, new JsonObject { ["devices"] = list });
    }

    /// <summary>
    /// Blocks or allows a device. Blocking drops its live connections through the store's change
    /// notification (<c>ClientSession.RefreshDeviceAsync</c>); allowing lets its next retry in.
    /// </summary>
    private async Task<IpcResponse> SetDeviceBlockedAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (ReadDeviceId(request) is not { } deviceId ||
            request.Args is not { } args ||
            !args.TryGetProperty("blocked", out JsonElement blockedElement) ||
            blockedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidArguments, "deviceId and blocked are required.");
        }

        bool updated = await _pairingStore
            .SetBlockedAsync(deviceId, blockedElement.GetBoolean(), cancellationToken)
            .ConfigureAwait(false);

        return updated
            ? IpcResponse.Success(request.Id)
            : IpcResponse.Failure(request.Id, ErrorCodes.NotFound, "That device is not paired.");
    }

    /// <summary>Removes a pairing; the store's change notification drops its live connections.</summary>
    private async Task<IpcResponse> RemoveDeviceAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (ReadDeviceId(request) is not { } deviceId)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidArguments, "deviceId is required.");
        }

        bool removed = await _pairingStore.RevokeAsync(deviceId, cancellationToken).ConfigureAwait(false);

        return removed
            ? IpcResponse.Success(request.Id)
            : IpcResponse.Failure(request.Id, ErrorCodes.NotFound, "That device is not paired.");
    }

    /// <summary>
    /// Replaces a device's permission groups. A live connection picks the change up through the
    /// store's change notification, so it applies to that device's very next command.
    /// </summary>
    /// <remarks>
    /// The list must be an array of strings and every name must be one this build knows. An
    /// unrecognised name fails the whole request rather than being dropped, because the caller is
    /// this product's own UI: a name it sends and the service does not know is a version mismatch
    /// worth surfacing, not something to half-apply.
    /// </remarks>
    private async Task<IpcResponse> SetDevicePermissionsAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (ReadDeviceId(request) is not { } deviceId ||
            request.Args is not { } args ||
            !args.TryGetProperty("permissions", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidArguments, "deviceId and permissions are required.");
        }

        var names = new List<string>();
        foreach (JsonElement element in list.EnumerateArray())
        {
            string? name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;

            if (name is null || !PermissionSet.AllNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return IpcResponse.Failure(request.Id, ErrorCodes.InvalidArguments, "Unknown permission name.");
            }

            names.Add(name);
        }

        Permission permissions = PermissionSet.FromNames(names);

        bool updated = await _pairingStore
            .SetPermissionsAsync(deviceId, permissions, cancellationToken)
            .ConfigureAwait(false);

        if (!updated)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.NotFound, "That device is not paired.");
        }

        _logger.LogInformation(
            "Permissions for device {DeviceId} were set at the PC: {Permissions}",
            deviceId,
            permissions == Permission.None ? "(none)" : string.Join(",", PermissionSet.ToNames(permissions)));

        return IpcResponse.Success(request.Id);
    }

    /// <summary>
    /// Tells the agent which directories file management may touch.
    /// </summary>
    /// <remarks>
    /// Read from the live options on every call, so an edit to the configuration file applies to
    /// the next file command without restarting anything. See
    /// <see cref="IpcCommands.GetFilePolicy"/> for why the agent asks instead of reading the file.
    /// </remarks>
    private IpcResponse GetFilePolicy(IpcRequest request)
    {
        FileOptions files = _options.CurrentValue.Files;

        // The list an earlier version wrote into config.json by itself is not a choice the owner
        // made; the configuration store removes it on upgrade, and until that write has been
        // picked up it is ignored here for the same reason.
        AllowedRoot[] configured = files.HasLegacyGeneratedRoots() ? [] : files.AllowedRoots ?? [];

        var policy = new IpcFilePolicy
        {
            UseDefaultRoots = files.UseDefaultRoots,
            MaxFileSizeMb = files.MaxFileSizeMb,
            AllowedRoots =
            [
                .. configured
                    .Where(static root => root is not null && !string.IsNullOrWhiteSpace(root.Path))
                    .Select(static root => new IpcFileRoot
                    {
                        Alias = root.Alias ?? string.Empty,
                        Path = root.Path,
                        Write = root.Write,
                    }),
            ],
        };

        return IpcResponse.Success(request.Id, JsonSerializer.SerializeToNode(policy, ProtocolJson.Options));
    }

    private static string? ReadDeviceId(IpcRequest request) =>
        request.Args is { ValueKind: JsonValueKind.Object } args &&
        args.TryGetProperty("deviceId", out JsonElement id) &&
        id.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(id.GetString())
            ? id.GetString()
            : null;
}

/// <summary>
/// Persists settings the user changes at runtime.
/// </summary>
/// <remarks>
/// Separate from <c>AgentConfigurationStore</c>'s read path because writing has a failure mode that
/// reading does not: the file lives in a directory the service can write but a portable instance
/// might not. A failed write is logged and the in-memory setting still takes effect, so turning on
/// pairing works even on a read-only installation — it simply does not survive a restart.
/// </remarks>
public sealed class AgentConfigurationWriter
{
    private readonly Configuration.AgentConfigurationStore _store;
    private readonly ILogger<AgentConfigurationWriter> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>Creates the writer.</summary>
    public AgentConfigurationWriter(
        Configuration.AgentConfigurationStore store,
        ILogger<AgentConfigurationWriter> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Turns pairing mode on or off and persists it.</summary>
    public async Task SetAllowPairingAsync(bool allow, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Update(options => options.Pairing.AllowPairing = allow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not persist the pairing setting. It applies to this run but will not survive a restart.");
        }
        finally
        {
            _mutex.Release();
        }
    }
}
