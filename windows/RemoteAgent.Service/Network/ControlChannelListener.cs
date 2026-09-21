using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Security;

namespace RemoteAgent.Service.Network;

/// <summary>
/// The single remote entry point: accepts TLS connections and authenticates them (§3).
/// </summary>
/// <remarks>
/// <para>There is exactly one listener in the whole system, and it requires a client
/// certificate. There is no status port, no debug port and no unauthenticated endpoint of any
/// kind — a port scan of this PC finds one socket that refuses to talk to anything without a
/// paired key.</para>
///
/// <para><b>Accept path ordering.</b> The cheapest checks come first so that an attacker
/// cannot make the PC do expensive work: address blocklist, then connection count, then the
/// TLS handshake, then cipher validation, then the pairing lookup. A blocked address is
/// disconnected before a handshake is attempted.</para>
///
/// <para><b>Unpaired peers are accepted, briefly.</b> A connection whose certificate matches
/// no pairing record is not dropped, because pairing itself has to happen over some
/// connection. It is instead given the <c>Unpaired</c> stage, from which the command catalog
/// permits exactly one command — <c>pair.request</c> — and that command is separately gated by
/// pairing mode, a single-use token and local human approval (§8.1).</para>
///
/// <para><b>Port selection.</b> If the configured port is taken, the next few are tried and
/// the one that worked is what gets advertised. A phone never assumes a port: it learns it
/// from discovery or the QR code (§0).</para>
/// </remarks>
public sealed class ControlChannelListener : IAsyncDisposable
{
    private readonly IIdentityStore _identityStore;
    private readonly IPairingStore _pairingStore;
    private readonly ISessionTokenService _tokens;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ClientSessionManager _sessions;
    private readonly ListenerEndpointStatus _endpointStatus;
    private readonly AbuseLimiter _abuseLimiter;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<ControlChannelListener> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CancellationTokenSource _lifetime = new();

    // Disposal happens twice: once from the ordered shutdown in the host, and again when
    // the DI container disposes its singletons. The second pass must be a no-op.
    private bool _disposed;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _pendingHandshakes;

    /// <summary>Creates the listener.</summary>
    public ControlChannelListener(
        IIdentityStore identityStore,
        IPairingStore pairingStore,
        ISessionTokenService tokens,
        ICommandDispatcher dispatcher,
        ClientSessionManager sessions,
        ListenerEndpointStatus endpointStatus,
        AbuseLimiter abuseLimiter,
        IClock clock,
        IOptionsMonitor<AgentOptions> options,
        ILogger<ControlChannelListener> logger,
        ILoggerFactory loggerFactory)
    {
        _identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _endpointStatus = endpointStatus ?? throw new ArgumentNullException(nameof(endpointStatus));
        _abuseLimiter = abuseLimiter ?? throw new ArgumentNullException(nameof(abuseLimiter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>The port actually bound, or 0 before the listener starts.</summary>
    public int BoundPort { get; private set; }

    /// <summary>Whether the listener is accepting connections.</summary>
    public bool IsListening => _listener is not null;

    /// <summary>Binds a port and begins accepting.</summary>
    /// <exception cref="IOException">No port in the configured range could be bound.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        NetworkOptions network = _options.CurrentValue.Network;

        // The identity must exist before the first connection: generating it lazily inside
        // the accept path would make the first pairing attempt fail while keys are created.
        DeviceIdentity identity = await _identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);

        _listener = BindListener(network, out int port);
        BoundPort = port;
        _endpointStatus.SetListening(port);

        _logger.LogInformation(
            "Control channel listening on port {Port}. Identity fingerprint {Fingerprint}.",
            port,
            identity.FingerprintShort);

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <summary>
    /// Binds the configured port, walking forward through the fallback range if it is taken.
    /// </summary>
    private TcpListener BindListener(NetworkOptions network, out int boundPort)
    {
        int firstPort = network.ControlPort;
        int lastPort = Math.Min(65535, firstPort + Math.Max(0, network.ControlPortFallbackRange));

        for (int port = firstPort; port <= lastPort; port++)
        {
            // IPAddress.Any, not a specific interface: the machine's addresses change with
            // DHCP, docking and Wi-Fi roaming, and binding to one of them would mean the
            // listener silently stops being reachable (§0, §6.4).
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);
                listener.Start(network.MaxPendingConnections);

                if (port != firstPort)
                {
                    _logger.LogWarning(
                        "Port {Preferred} was unavailable; listening on {Actual} instead. " +
                        "Discovery advertises the actual port, so clients need no change.",
                        firstPort,
                        port);
                }

                boundPort = port;
                return listener;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Dispose();
            }
        }

        throw new IOException(
            $"No free port between {firstPort} and {lastPort} could be bound for the control channel.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        TcpListener listener = _listener!;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

                // Handshake on its own task: a slow or malicious peer must not delay the next
                // legitimate connection.
                TcpClient accepted = client;
                client = null;
                _ = Task.Run(() => HandleClientAsync(accepted, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept failed; continuing to listen.");
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            finally
            {
                client?.Dispose();
            }
        }

        _logger.LogInformation("Control channel stopped accepting connections.");
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string remoteAddress = DescribeRemote(client);
        AgentOptions options = _options.CurrentValue;
        SslStream? tls = null;

        try
        {
            client.NoDelay = true;

            // --- Cheap refusals first, before any cryptography ---

            if (_abuseLimiter.IsBlocked(remoteAddress))
            {
                _logger.LogWarning("Refused a connection from blocked address {Address}.", remoteAddress);
                return;
            }

            if (!IsAddressAllowed(remoteAddress, options.Network))
            {
                _logger.LogWarning(
                    "Refused a connection from {Address}: not in the configured subnet allowlist.",
                    remoteAddress);
                return;
            }

            if (_sessions.Count >= options.Network.MaxConnections)
            {
                _logger.LogWarning(
                    "Refused a connection from {Address}: the {Max} connection limit is reached.",
                    remoteAddress,
                    options.Network.MaxConnections);
                return;
            }

            if (Interlocked.Increment(ref _pendingHandshakes) > options.Network.MaxPendingConnections)
            {
                _logger.LogWarning(
                    "Refused a connection from {Address}: too many handshakes in progress.",
                    remoteAddress);
                return;
            }

            try
            {
                DeviceIdentity identity = await _identityStore.GetOrCreateAsync(cancellationToken)
                    .ConfigureAwait(false);

                tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(options.Network.HandshakeTimeoutSeconds));

                await tls.AuthenticateAsServerAsync(
                    TlsTransport.CreateServerOptions(identity.Certificate),
                    handshakeTimeout.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingHandshakes);
            }

            // --- Post-handshake validation: protocol floor and cipher allowlist ---

            TlsRejectionReason rejection = TlsTransport.Validate(tls, _clock.UtcNow);
            if (rejection != TlsRejectionReason.None)
            {
                _abuseLimiter.RecordFailure(remoteAddress, TlsTransport.Describe(rejection));
                _logger.LogWarning(
                    "Rejected a TLS session from {Address}: {Reason}.",
                    remoteAddress,
                    TlsTransport.Describe(rejection));
                return;
            }

            string? fingerprint = TlsTransport.GetPeerFingerprint(tls);
            if (fingerprint is null)
            {
                _abuseLimiter.RecordFailure(remoteAddress, "no client certificate");
                return;
            }

            // --- Identity resolution: fingerprint to pairing record ---

            PairedDevice? device = await _pairingStore
                .FindByFingerprintAsync(fingerprint, cancellationToken)
                .ConfigureAwait(false);

            string connectionId = Guid.NewGuid().ToString("n")[..12];
            string tlsDescription = TlsTransport.DescribeSession(tls);

            if (device is null)
            {
                // Not paired. Allowed to proceed only far enough to attempt pairing, which is
                // itself gated three ways. Counted as an auth failure so that a device
                // hammering the port without a valid pairing eventually gets blocked.
                _abuseLimiter.RecordFailure(remoteAddress, "unknown client certificate");

                _logger.LogInformation(
                    "Unpaired client connected from {Address} ({Tls}). Fingerprint {Fingerprint}. " +
                    "Only pairing is permitted on this connection.",
                    remoteAddress,
                    tlsDescription,
                    CertificateFingerprint.ToShortForm(fingerprint));
            }
            else
            {
                _abuseLimiter.RecordSuccess(remoteAddress);

                _logger.LogInformation(
                    "Paired device connected. Device={DeviceId} Name={DeviceName} Address={Address} " +
                    "Connection={ConnectionId} Tls={Tls}",
                    device.DeviceId,
                    device.DeviceName,
                    remoteAddress,
                    connectionId,
                    tlsDescription);

                await _pairingStore.RecordConnectionAsync(device.DeviceId, remoteAddress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var session = new ClientSession(
                tls,
                connectionId,
                remoteAddress,
                fingerprint,
                device,
                tlsDescription,
                _dispatcher,
                _tokens,
                _pairingStore,
                _clock,
                _loggerFactory.CreateLogger<ClientSession>(),
                options.Security,
                options.Network);

            tls = null; // Ownership transferred to the session.
            _sessions.Add(session);

            try
            {
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sessions.Remove(session.ConnectionId);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (AuthenticationException ex)
        {
            // A failed TLS handshake is the normal shape of "something connected that is not
            // our client" — a port scanner, a browser, an old app version.
            _abuseLimiter.RecordFailure(remoteAddress, "TLS handshake failed");
            _logger.LogInformation(
                "TLS handshake from {Address} failed: {Reason}",
                remoteAddress,
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Handshake from {Address} timed out or was cancelled.", remoteAddress);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Connection from {Address} ended during setup.", remoteAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure handling a connection from {Address}.", remoteAddress);
        }
        finally
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }

            client.Dispose();
        }
    }

    /// <summary>
    /// Applies the optional subnet allowlist.
    /// </summary>
    /// <remarks>
    /// Empty means "no restriction", which is the portable default: hardcoding a subnet would
    /// break the moment the PC joins a different network (§0). When configured, this is a
    /// convenience boundary, not a security one — addresses are forgeable on a LAN, and
    /// authentication is what actually protects the PC.
    /// </remarks>
    private bool IsAddressAllowed(string remoteAddress, NetworkOptions network)
    {
        if (network.SubnetAllowlist.Length == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(remoteAddress, out IPAddress? address))
        {
            return false;
        }

        foreach (string cidr in network.SubnetAllowlist)
        {
            if (TryMatchCidr(address, cidr))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryMatchCidr(IPAddress address, string cidr)
    {
        try
        {
            string[] parts = cidr.Split('/', 2);
            if (parts.Length != 2 ||
                !IPAddress.TryParse(parts[0], out IPAddress? network) ||
                !int.TryParse(parts[1], out int prefix))
            {
                return false;
            }

            byte[] addressBytes = address.GetAddressBytes();
            byte[] networkBytes = network.GetAddressBytes();

            if (addressBytes.Length != networkBytes.Length || prefix < 0 || prefix > addressBytes.Length * 8)
            {
                return false;
            }

            int fullBytes = prefix / 8;
            int remainingBits = prefix % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                if (addressBytes[i] != networkBytes[i])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            int mask = 0xFF << (8 - remainingBits);
            return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogWarning("Ignoring a malformed entry in the subnet allowlist: {Entry}", cidr);
            return false;
        }
    }

    private static string DescribeRemote(TcpClient client)
    {
        try
        {
            if (client.Client.RemoteEndPoint is IPEndPoint endpoint)
            {
                // IPv4-mapped IPv6 addresses print as ::ffff:192.168.1.5, which makes the
                // allowlist and the logs inconsistent with what users expect to see.
                IPAddress address = endpoint.Address.IsIPv4MappedToIPv6
                    ? endpoint.Address.MapToIPv4()
                    : endpoint.Address;

                return address.ToString();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // Connection already gone.
        }

        return "unknown";
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
        _endpointStatus.SetStopped();

        _listener?.Stop();
        _listener?.Dispose();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Accept loop unblocks when the listener is disposed.
            }
        }

        _lifetime.Dispose();
    }
}
