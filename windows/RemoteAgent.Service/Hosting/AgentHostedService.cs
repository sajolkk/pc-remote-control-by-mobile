using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using RemoteAgent.Service.Configuration;
using RemoteAgent.Service.Discovery;
using RemoteAgent.Service.Handlers;
using RemoteAgent.Service.Network;
using RemoteAgent.Service.Services;
using RemoteAgent.Windows.Security;
using RemoteAgent.Windows.Session;

namespace RemoteAgent.Service.Hosting;

/// <summary>
/// Orchestrates the service's moving parts and keeps them wired together.
/// </summary>
/// <remarks>
/// <para><b>Startup order is load-bearing.</b> Data directory and ACLs first, so nothing is
/// ever written to an unprotected location. Then the identity, because the listener cannot
/// accept a TLS connection without a certificate and generating one lazily would make the first
/// pairing attempt fail. Then the IPC registry before the supervisor, so an agent that starts
/// quickly finds somewhere to connect. The listener and discovery come last, because accepting
/// a client before the rest is ready would mean answering commands that cannot yet be served.</para>
///
/// <para><b>Event relay.</b> Two independent sources of state changes — the session agent over
/// IPC, and the service's own session watcher — both fan out to connected clients here. Keeping
/// the relay in one place means a client's view of the PC has one code path, rather than each
/// component inventing its own way to notify.</para>
///
/// <para><b>Session polling.</b> Logon and logoff are detected by polling the console session
/// every few seconds rather than only from service notifications, because the same binary also
/// runs in console mode where no service notifications exist. Lock and unlock are reported by
/// the session agent, which is the only component that can actually observe them.</para>
/// </remarks>
public sealed class AgentHostedService : BackgroundService
{
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);

    private readonly AgentPaths _paths;
    private readonly AgentConfigurationStore _configStore;
    private readonly DpapiSecretProtector _protector;
    private readonly IIdentityStore _identityStore;
    private readonly IPairingStore _pairingStore;
    private readonly ISessionTokenService _tokens;
    private readonly AbuseLimiter _abuseLimiter;
    private readonly ControlChannelListener _listener;
    private readonly UdpDiscoveryResponder _discovery;
    private readonly SessionAgentRegistry _agents;
    private readonly AgentRequestRouter _agentRequests;
    private readonly SessionAgentSupervisor _supervisor;
    private readonly ClientSessionManager _sessions;
    private readonly WtsSessionStateProvider _sessionState;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<AgentHostedService> _logger;

    /// <summary>Creates the orchestrator.</summary>
    public AgentHostedService(
        AgentPaths paths,
        AgentConfigurationStore configStore,
        DpapiSecretProtector protector,
        IIdentityStore identityStore,
        IPairingStore pairingStore,
        ISessionTokenService tokens,
        AbuseLimiter abuseLimiter,
        ControlChannelListener listener,
        UdpDiscoveryResponder discovery,
        SessionAgentRegistry agents,
        AgentRequestRouter agentRequests,
        SessionAgentSupervisor supervisor,
        ClientSessionManager sessions,
        WtsSessionStateProvider sessionState,
        ISystemInfoProvider systemInfo,
        IClock clock,
        IOptionsMonitor<AgentOptions> options,
        ILogger<AgentHostedService> logger)
    {
        _paths = paths;
        _configStore = configStore;
        _protector = protector;
        _identityStore = identityStore;
        _pairingStore = pairingStore;
        _tokens = tokens;
        _abuseLimiter = abuseLimiter;
        _listener = listener;
        _discovery = discovery;
        _agents = agents;
        _agentRequests = agentRequests;
        _supervisor = supervisor;
        _sessions = sessions;
        _sessionState = sessionState;
        _systemInfo = systemInfo;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartComponentsAsync(stoppingToken).ConfigureAwait(false);
            await RunMaintenanceLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A failure here means the service cannot do its job, so it is logged as critical
            // and the host is allowed to stop. The SCM's restart policy then retries, which is
            // the right response to a transient cause such as a port briefly in use.
            _logger.LogCritical(ex, "The agent failed to start. The service will stop and be restarted.");
            throw;
        }
        finally
        {
            await ShutdownComponentsAsync().ConfigureAwait(false);
        }
    }

    private async Task StartComponentsAsync(CancellationToken cancellationToken)
    {
        // 1. Storage, with restrictive ACLs applied before anything sensitive is written.
        _paths.EnsureCreated();
        _protector.HardenAll(_paths);

        (string deviceId, string deviceName) = _configStore.EnsureInitialized();

        _logger.LogInformation(
            "PC-Remote agent starting. Device={DeviceName} ({DeviceId}) Version={Version} " +
            "DataDirectory={Path} Portable={Portable}",
            deviceName,
            deviceId,
            AgentVersion.Current,
            _paths.Root,
            _paths.IsPortable);

        // 2. Cryptographic identity, before any connection can be accepted.
        DeviceIdentity identity = await _identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        PairingIdentity.Initialize(deviceId, deviceName, identity.Fingerprint);

        // 3. IPC, before the supervisor, so a fast-starting agent has somewhere to connect.
        _agents.AgentEvent += OnAgentEvent;
        _agents.AgentConnected += OnAgentConnected;
        _agents.AgentDisconnected += OnAgentDisconnectedRelay;
        _sessions.ConnectionEnded += OnConnectionEnded;
        _sessions.PermissionsChanged += OnConnectionPermissionsChanged;

        // Requests travelling agent -> service: opening pairing, reporting status. Wired before
        // Start so an agent that connects immediately does not find the handler missing.
        _agents.AgentRequestHandler = _agentRequests.HandleAsync;

        _agents.Start();

        // 4. Session watching and the agent supervisor.
        _sessionState.StateChanged += OnSessionStateChanged;
        _agents.SetActiveSession(_sessionState.ActiveSessionId);
        _supervisor.Start();

        // 5. Revocation must take effect on live connections, not at token expiry.
        if (_pairingStore is JsonPairingStore jsonStore)
        {
            jsonStore.Changed += OnPairingStoreChanged;
        }

        // 6. Accept clients, then advertise. Advertising first would invite a connection to a
        //    port that is not yet listening.
        await _listener.StartAsync(cancellationToken).ConfigureAwait(false);
        _discovery.Start();

        IReadOnlyList<string> addresses = _systemInfo.GetLocalAddresses();
        _logger.LogInformation(
            "Agent ready. Fingerprint={Fingerprint} Port={Port} Addresses={Addresses}",
            identity.FingerprintShort,
            _listener.BoundPort,
            addresses.Count == 0 ? "(none)" : string.Join(", ", addresses));
    }

    /// <summary>
    /// Periodic housekeeping: session polling, token expiry, abuse-table decay.
    /// </summary>
    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nextMaintenance = _clock.UtcNow.Add(MaintenanceInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(SessionPollInterval, cancellationToken).ConfigureAwait(false);

            // Polling covers console mode, where no service session notifications arrive, and
            // acts as a safety net in service mode if a notification is missed.
            _sessionState.Refresh();

            if (_clock.UtcNow >= nextMaintenance)
            {
                nextMaintenance = _clock.UtcNow.Add(MaintenanceInterval);

                int purgedTokens = _tokens.PurgeExpired();
                int purgedAddresses = _abuseLimiter.Purge();

                if (purgedTokens > 0 || purgedAddresses > 0)
                {
                    _logger.LogDebug(
                        "Maintenance: purged {Tokens} expired token(s) and {Addresses} stale address entry(ies).",
                        purgedTokens,
                        purgedAddresses);
                }
            }
        }
    }

    private void OnSessionStateChanged(object? sender, InteractiveSessionState state)
    {
        _agents.SetActiveSession(_sessionState.ActiveSessionId);

        // Fire and forget: a client that cannot be reached must not delay the state change for
        // the rest of the system. Failures inside the broadcast close that one connection.
        _ = BroadcastAsync(EventEnvelope.Create(
            EventNames.SessionStateChanged,
            new
            {
                sessionState = state.ToString(),
                userName = _sessionState.UserName,
                sessionAgentConnected = _agents.IsConnected,
            },
            _clock.UnixTimeMilliseconds));
    }

    private void OnAgentConnected(object? sender, ConnectedAgent agent) =>
        _ = BroadcastAsync(EventEnvelope.Create(
            EventNames.SessionStateChanged,
            new
            {
                sessionState = _sessionState.State.ToString(),
                userName = _sessionState.UserName,
                sessionAgentConnected = true,
            },
            _clock.UnixTimeMilliseconds));

    private void OnAgentDisconnectedRelay(object? sender, int sessionId) =>
        _ = BroadcastAsync(EventEnvelope.Create(
            EventNames.SessionStateChanged,
            new
            {
                sessionState = _sessionState.State.ToString(),
                userName = _sessionState.UserName,
                sessionAgentConnected = _agents.IsConnected,
            },
            _clock.UnixTimeMilliseconds));

    /// <summary>
    /// Relays an event the session agent pushed to every connected client.
    /// </summary>
    /// <remarks>
    /// The agent's topics are the wire topics, so no translation is needed — but the lock state
    /// is also folded into the service's own view, because the agent is the only component that
    /// can observe a lock and the service is what reports state to clients (§3).
    /// </remarks>
    private void OnAgentEvent(object? sender, IpcEvent ipcEvent)
    {
        if (string.Equals(ipcEvent.Topic, EventNames.SessionStateChanged, StringComparison.Ordinal))
        {
            bool? locked = ipcEvent.Data?["locked"]?.GetValue<bool>();
            if (locked is not null)
            {
                _sessionState.SetLocked(locked.Value);
                return; // SetLocked raises StateChanged, which broadcasts.
            }
        }

        var envelope = new EventEnvelope
        {
            Topic = ipcEvent.Topic,
            TimestampMs = ipcEvent.TimestampMs,
            Data = ipcEvent.Data,
        };

        if (ipcEvent.ConnectionId is { } target)
        {
            // Targeted: media telemetry for one device's stream. Delivered to that connection only,
            // and only while it is authenticated; a closed or unknown connection drops it.
            if (_sessions.TryGet(target, out ClientSession session) && session.IsAuthenticated)
            {
                _ = session.SendEventAsync(envelope, CancellationToken.None);
            }

            return;
        }

        _ = BroadcastAsync(envelope);
    }

    private void OnConnectionEnded(object? sender, string connectionId) =>
        _ = NotifyAgentsAsync(new IpcConnectionChangedArgs { ConnectionId = connectionId, Closed = true });

    private void OnConnectionPermissionsChanged(object? sender, (string ConnectionId, Permission Permissions) change) =>
        _ = NotifyAgentsAsync(new IpcConnectionChangedArgs
        {
            ConnectionId = change.ConnectionId,
            Permissions = PermissionSet.ToNames(change.Permissions),
        });

    /// <summary>
    /// Tells the session agent about a connection change, so a stream never outlives the
    /// connection or the permission that started it.
    /// </summary>
    private async Task NotifyAgentsAsync(IpcConnectionChangedArgs change)
    {
        try
        {
            await _agents.NotifyConnectionChangedAsync(change, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not notify the session agent about connection {ConnectionId}.", change.ConnectionId);
        }
    }

    private void OnPairingStoreChanged(object? sender, EventArgs e) =>
        _ = RefreshSessionsAsync();

    private async Task RefreshSessionsAsync()
    {
        try
        {
            await _sessions.RefreshAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh client sessions after a pairing change.");
        }
    }

    private async Task BroadcastAsync(EventEnvelope envelope)
    {
        try
        {
            await _sessions.BroadcastAsync(envelope, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not broadcast {Topic}.", envelope.Topic);
        }
    }

    private async Task ShutdownComponentsAsync()
    {
        _logger.LogInformation("PC-Remote agent stopping.");

        _sessionState.StateChanged -= OnSessionStateChanged;
        _agents.AgentEvent -= OnAgentEvent;
        _agents.AgentConnected -= OnAgentConnected;
        _agents.AgentDisconnected -= OnAgentDisconnectedRelay;
        _sessions.ConnectionEnded -= OnConnectionEnded;
        _sessions.PermissionsChanged -= OnConnectionPermissionsChanged;

        if (_pairingStore is JsonPairingStore jsonStore)
        {
            jsonStore.Changed -= OnPairingStoreChanged;
        }

        await _discovery.DisposeAsync().ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        await _sessions.DisposeAsync().ConfigureAwait(false);
        await _agents.DisposeAsync().ConfigureAwait(false);
    }
}
