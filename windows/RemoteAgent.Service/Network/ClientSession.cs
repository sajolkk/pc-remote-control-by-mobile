using System.Net.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;
using RemoteAgent.Security;

namespace RemoteAgent.Service.Network;

/// <summary>
/// One authenticated control-channel connection.
/// </summary>
/// <remarks>
/// <para>Owns everything that is per-connection: the TLS stream, the replay guard, the rate
/// limiter and the session token binding. Keeping these per-connection rather than
/// per-device is what makes a captured token useless elsewhere and a replayed frame
/// detectable (§7.2, §6.2).</para>
///
/// <para><b>Request ordering.</b> Requests are processed strictly one at a time on the read
/// loop. That is a deliberate constraint rather than a limitation: the sequence-number check
/// requires ordering to mean something, and the alternative — dispatching concurrently —
/// would let a fast input command overtake the <c>release_all</c> that was meant to precede
/// it. Long-running commands are the dispatcher's problem to bound, not a reason to
/// interleave.</para>
///
/// <para><b>Permission freshness.</b> The device record is re-read when the pairing store
/// changes rather than trusted from the token's snapshot, so revoking a permission at the PC
/// takes effect on the next command rather than when the token happens to expire.</para>
/// </remarks>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly SslStream _stream;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ISessionTokenService _tokens;
    private readonly IPairingStore _pairingStore;
    private readonly IClock _clock;
    private readonly ILogger<ClientSession> _logger;
    private readonly SecurityOptions _security;
    private readonly NetworkOptions _network;
    private readonly ReplayGuard _replayGuard;
    private readonly RequestRateLimiter _rateLimiter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private PairedDevice? _device;
    private SessionToken? _token;
    private DateTimeOffset _lastActivity;
    private bool _disposed;

    /// <summary>Creates a session around an authenticated TLS stream.</summary>
    /// <param name="stream">The established, validated TLS stream.</param>
    /// <param name="connectionId">Unique id for this connection.</param>
    /// <param name="remoteAddress">Peer address, for audit.</param>
    /// <param name="peerFingerprint">The peer certificate's pinning fingerprint.</param>
    /// <param name="device">The matched pairing record, or null if the peer is unpaired.</param>
    /// <param name="tlsDescription">Negotiated protocol and cipher, for reporting.</param>
    /// <param name="dispatcher">Command pipeline.</param>
    /// <param name="tokens">Session token service.</param>
    /// <param name="pairingStore">Pairing records, for permission refresh.</param>
    /// <param name="clock">Injected clock.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="security">Token and replay settings.</param>
    /// <param name="network">Keepalive settings.</param>
    public ClientSession(
        SslStream stream,
        string connectionId,
        string remoteAddress,
        string peerFingerprint,
        PairedDevice? device,
        string tlsDescription,
        ICommandDispatcher dispatcher,
        ISessionTokenService tokens,
        IPairingStore pairingStore,
        IClock clock,
        ILogger<ClientSession> logger,
        SecurityOptions security,
        NetworkOptions network)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _network = network ?? throw new ArgumentNullException(nameof(network));

        ConnectionId = connectionId;
        RemoteAddress = remoteAddress;
        PeerFingerprint = peerFingerprint;
        TlsDescription = tlsDescription;
        _device = device;

        _replayGuard = new ReplayGuard(security.MaxClockSkewSeconds);
        _rateLimiter = new RequestRateLimiter(clock, security.MaxRequestsPerMinute);
        _lastActivity = clock.UtcNow;
        ConnectedAt = clock.UtcNow;
    }

    /// <summary>Unique id for this connection.</summary>
    public string ConnectionId { get; }

    /// <summary>Peer address. Audit only — never an authorization input.</summary>
    public string RemoteAddress { get; }

    /// <summary>The peer certificate's pinning fingerprint.</summary>
    public string PeerFingerprint { get; }

    /// <summary>Negotiated TLS protocol and cipher suite.</summary>
    public string TlsDescription { get; }

    /// <summary>When the connection was established.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>The paired device, or null while the peer is unpaired.</summary>
    public PairedDevice? Device => _device;

    /// <summary>Whether the handshake has completed and a token is in force.</summary>
    public bool IsAuthenticated => _token is not null;

    /// <summary>Raised when the read loop ends.</summary>
    public event EventHandler? Closed;

    /// <summary>Runs the read loop until the peer disconnects or the session is closed.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);

        using var keepalive = new Timer(
            static state => ((ClientSession)state!).CheckKeepalive(),
            this,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                Frame? frame = await FrameCodec.ReadAsync(_stream, linked.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    _logger.LogInformation(
                        "Client disconnected cleanly. Connection={ConnectionId} Device={DeviceId}",
                        ConnectionId,
                        _device?.DeviceId ?? "(unpaired)");
                    break;
                }

                _lastActivity = _clock.UtcNow;
                await HandleFrameAsync(frame.Value, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or keepalive expired.
        }
        catch (InvalidDataException ex)
        {
            // Malformed framing from a peer that has already authenticated at the TLS layer.
            // Treated as fatal for the connection: the stream position is unknown, so nothing
            // after this point could be trusted to be a complete frame.
            _logger.LogWarning(
                "Dropping connection {ConnectionId} from {Address}: {Reason}",
                ConnectionId,
                RemoteAddress,
                ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogInformation(
                "Connection {ConnectionId} lost: {Reason}",
                ConnectionId,
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure on connection {ConnectionId}.", ConnectionId);
        }
        finally
        {
            _tokens.RevokeConnection(ConnectionId);
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task HandleFrameAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case FrameType.Request:
                await HandleRequestAsync(frame, cancellationToken).ConfigureAwait(false);
                return;

            case FrameType.Ping:
                // Echoed verbatim so the client can measure round-trip latency against its
                // own timestamp rather than trusting ours.
                await WriteFrameAsync(FrameType.Pong, frame.Payload, cancellationToken).ConfigureAwait(false);
                return;

            case FrameType.Pong:
                return;

            case FrameType.Response:
            case FrameType.Event:
                // A client has no business sending these. Ignored rather than fatal: a
                // future protocol version might, and an old PC should not drop the
                // connection over a frame it simply does not need.
                _logger.LogDebug(
                    "Ignored a {Type} frame from client {ConnectionId}.",
                    frame.Type,
                    ConnectionId);
                return;

            default:
                return;
        }
    }

    private async Task HandleRequestAsync(Frame frame, CancellationToken cancellationToken)
    {
        if (!ProtocolJson.TryDeserialize(frame.Payload.Span, out RequestEnvelope? request) || request is null)
        {
            await RespondAsync(
                ResponseEnvelope.Failure(string.Empty, ErrorCodes.MalformedFrame, "The request could not be parsed."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string requestId = request.Id ?? string.Empty;

        if (!ProtocolVersion.IsSupported(request.Version))
        {
            await RespondAsync(
                ResponseEnvelope.Failure(
                    requestId,
                    ErrorCodes.VersionUnsupported,
                    $"This PC speaks protocol versions {ProtocolVersion.MinSupported}-{ProtocolVersion.Current}."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_rateLimiter.TryAcquire())
        {
            // Not fatal: throttling a burst is better than dropping a connection that is
            // merely enthusiastic. A client that keeps hitting this will see the failures.
            await RespondAsync(
                ResponseEnvelope.Failure(
                    requestId,
                    ErrorCodes.RateLimited,
                    "Too many requests. Slow down.",
                    retryable: true),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        ReplayVerdict verdict = _replayGuard.Evaluate(
            request.Sequence,
            request.TimestampMs,
            _clock.UnixTimeMilliseconds);

        if (verdict != ReplayVerdict.Accepted)
        {
            // Fatal by design. A sequence anomaly on an ordered, reliable, encrypted
            // transport is either an attack or a broken client, and continuing to serve
            // either is worse than making them reconnect.
            _logger.LogWarning(
                "Closing connection {ConnectionId} from {Address}: {Reason} (seq={Sequence}).",
                ConnectionId,
                RemoteAddress,
                ReplayGuard.Describe(verdict),
                request.Sequence);

            await RespondAsync(
                ResponseEnvelope.Failure(
                    requestId,
                    ErrorCodes.ReplayDetected,
                    ReplayGuard.Describe(verdict)),
                cancellationToken).ConfigureAwait(false);

            await _lifetime.CancelAsync().ConfigureAwait(false);
            return;
        }

        CallerIdentity caller = BuildCaller(request.Token);

        CommandResult result = await _dispatcher
            .DispatchAsync(request.Command, request.Args, requestId, caller, cancellationToken)
            .ConfigureAwait(false);

        await RespondAsync(result.ToResponse(requestId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines who is calling, from the pairing record and the presented token.
    /// </summary>
    /// <remarks>
    /// Permissions come from the live pairing record rather than the token's snapshot, so a
    /// permission revoked at the PC applies to the very next command.
    /// </remarks>
    private CallerIdentity BuildCaller(string? presentedToken)
    {
        PairedDevice? device = _device;

        if (device is null)
        {
            return CallerIdentity.Unpaired(ConnectionId, RemoteAddress);
        }

        if (_tokens.TryValidate(presentedToken, ConnectionId, out SessionToken? session, out string? failure) &&
            session is not null)
        {
            return new CallerIdentity(
                device.DeviceId,
                device.DeviceName,
                device.Permissions,
                CommandStage.Authenticated,
                ConnectionId,
                RemoteAddress);
        }

        if (!string.IsNullOrEmpty(presentedToken))
        {
            _logger.LogDebug(
                "Token rejected on connection {ConnectionId}: {Reason}.",
                ConnectionId,
                failure);
        }

        return new CallerIdentity(
            device.DeviceId,
            device.DeviceName,
            device.Permissions,
            CommandStage.Paired,
            ConnectionId,
            RemoteAddress);
    }

    /// <summary>
    /// Records the session token issued by the handshake, binding it to this connection.
    /// </summary>
    public void OnAuthenticated(SessionToken token, PairedDevice device)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>Refreshes the cached pairing record, or closes the session if it is gone.</summary>
    public async Task RefreshDeviceAsync(CancellationToken cancellationToken)
    {
        if (_device is null)
        {
            return;
        }

        PairedDevice? refreshed = await _pairingStore
            .FindByDeviceIdAsync(_device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (refreshed is null)
        {
            // The pairing was revoked while this connection was live. Closing immediately is
            // the point of revocation (§8.3).
            _logger.LogInformation(
                "Closing connection {ConnectionId}: the pairing for device {DeviceId} was revoked.",
                ConnectionId,
                _device.DeviceId);

            _tokens.RevokeDevice(_device.DeviceId);
            await CloseAsync(EventNames.PairingRevoked, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool permissionsChanged = refreshed.Permissions != _device.Permissions;
        _device = refreshed;

        if (permissionsChanged)
        {
            await SendEventAsync(
                EventEnvelope.Create(
                    EventNames.PermissionsChanged,
                    new { permissions = PermissionSet.ToNames(refreshed.Permissions) },
                    _clock.UnixTimeMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pushes an event to this client. Failures close the session rather than throw.</summary>
    public async Task SendEventAsync(EventEnvelope ipcEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ipcEvent);

        try
        {
            await WriteMessageAsync(FrameType.Event, ipcEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not push an event to connection {ConnectionId}.", ConnectionId);
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Sends a final event and then closes the connection.</summary>
    public async Task CloseAsync(string? finalEventTopic, CancellationToken cancellationToken)
    {
        if (finalEventTopic is not null)
        {
            await SendEventAsync(
                EventEnvelope.Create(finalEventTopic, new { }, _clock.UnixTimeMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
    }

    private Task RespondAsync(ResponseEnvelope response, CancellationToken cancellationToken) =>
        WriteMessageAsync(FrameType.Response, response, cancellationToken);

    private async Task WriteMessageAsync<T>(FrameType type, T message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteMessageAsync(_stream, type, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteFrameAsync(
        FrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, type, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Closes the connection when the peer has gone quiet.
    /// </summary>
    /// <remarks>
    /// A phone that loses Wi-Fi mid-session leaves a TCP connection that looks alive for
    /// minutes. Without this, the connection slot stays occupied and — more importantly —
    /// any held mouse button or key stays held on the user's desktop. The timeout is what
    /// triggers the input release (§6.4).
    /// </remarks>
    private void CheckKeepalive()
    {
        TimeSpan idle = _clock.UtcNow - _lastActivity;
        if (idle <= TimeSpan.FromSeconds(_network.KeepaliveTimeoutSeconds))
        {
            return;
        }

        _logger.LogInformation(
            "Connection {ConnectionId} timed out after {IdleSeconds:F0}s of inactivity.",
            ConnectionId,
            idle.TotalSeconds);

        _lifetime.Cancel();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _tokens.RevokeConnection(ConnectionId);

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone.
        }

        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
