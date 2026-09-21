using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteAgent.Protocol;

namespace RemoteAgent.Service.Network;

/// <summary>
/// Tracks live control-channel connections and fans events out to them.
/// </summary>
/// <remarks>
/// <para>Also the lookup that lets a command handler reach its own connection: the handshake
/// handler needs to bind the token it issues to the connection that asked, and the dispatcher
/// deliberately passes only an opaque connection id rather than the session object. Routing
/// through this registry keeps handlers free of transport details while still allowing the
/// one case that genuinely needs it.</para>
///
/// <para>Event fan-out is fire-and-forget per connection and never fails the caller. A
/// wedged client must not be able to stall a state change for everyone else, so a failed push
/// closes that one connection and the rest proceed.</para>
/// </remarks>
public sealed class ClientSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly ILogger<ClientSessionManager> _logger;

    // See the other components: disposal runs both from the ordered shutdown and from the
    // DI container, so it has to be idempotent.
    private bool _disposed;

    /// <summary>Creates the manager.</summary>
    public ClientSessionManager(ILogger<ClientSessionManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Number of live connections.</summary>
    public int Count => _sessions.Count;

    /// <summary>Live connections, for the UI's status view.</summary>
    public IReadOnlyCollection<ClientSession> All => _sessions.Values.ToArray();

    /// <summary>Raised whenever a connection is added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with the connection id when a connection closes.</summary>
    public event EventHandler<string>? ConnectionEnded;

    /// <summary>Raised when a live connection's device permissions change at the PC.</summary>
    public event EventHandler<(string ConnectionId, Permission Permissions)>? PermissionsChanged;

    /// <summary>Registers a session and wires its removal.</summary>
    public void Add(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _sessions[session.ConnectionId] = session;
        session.Closed += (_, _) => Remove(session.ConnectionId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a session by connection id.</summary>
    public void Remove(string connectionId)
    {
        if (_sessions.TryRemove(connectionId, out _))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            ConnectionEnded?.Invoke(this, connectionId);
        }
    }

    /// <summary>Finds a session by connection id.</summary>
    public bool TryGet(string connectionId, out ClientSession session) =>
        _sessions.TryGetValue(connectionId, out session!);

    /// <summary>Whether any connection currently holds an authenticated session.</summary>
    public bool HasAuthenticatedClient => _sessions.Values.Any(static s => s.IsAuthenticated);

    /// <summary>Pushes an event to every authenticated connection.</summary>
    public async Task BroadcastAsync(EventEnvelope ipcEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ipcEvent);

        foreach (ClientSession session in _sessions.Values)
        {
            if (!session.IsAuthenticated)
            {
                continue;
            }

            try
            {
                await session.SendEventAsync(ipcEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not deliver {Topic} to connection {ConnectionId}.",
                    ipcEvent.Topic,
                    session.ConnectionId);
            }
        }
    }

    /// <summary>
    /// Re-reads every session's pairing record, closing any whose pairing was revoked.
    /// </summary>
    /// <remarks>
    /// Called when the pairing store changes. This is what turns "Revoke" in the UI into an
    /// immediately dropped connection rather than access that persists until a token expires.
    /// </remarks>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (ClientSession session in _sessions.Values)
        {
            try
            {
                Permission? before = session.Device?.Permissions;
                await session.RefreshDeviceAsync(cancellationToken).ConfigureAwait(false);
                Permission? after = session.Device?.Permissions;

                // A revoked pairing closes the connection, which raises ConnectionEnded; only a
                // surviving connection whose grants changed needs this.
                if (before is not null && after is not null && before != after &&
                    _sessions.ContainsKey(session.ConnectionId))
                {
                    PermissionsChanged?.Invoke(this, (session.ConnectionId, after.Value));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not refresh the pairing record for connection {ConnectionId}.",
                    session.ConnectionId);
            }
        }
    }

    /// <summary>Closes every connection for one device.</summary>
    public async Task CloseDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        foreach (ClientSession session in _sessions.Values)
        {
            if (string.Equals(session.Device?.DeviceId, deviceId, StringComparison.Ordinal))
            {
                await session.CloseAsync(EventNames.PairingRevoked, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (ClientSession session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
