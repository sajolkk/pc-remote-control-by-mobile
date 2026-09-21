using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Service.Network;

namespace RemoteAgent.Service.Discovery;

/// <summary>
/// Answers LAN discovery probes over UDP (§6.1).
/// </summary>
/// <remarks>
/// <para><b>Why UDP broadcast is the primary mechanism, not mDNS.</b> The architecture called
/// for mDNS with UDP as a fallback; implementation reversed the priority, for practical
/// reasons. On Android, multicast reception is unreliable in a way that is outside the app's
/// control — several Wi-Fi chipsets drop multicast in power-save, battery optimisation can
/// suspend the responder, and some routers filter it between wireless clients. A directed UDP
/// probe to a known port has none of those failure modes and is under our control end to end.
/// mDNS remains worth adding as an <em>additional</em> path, because it makes the PC visible to
/// generic service browsers, but making it the only path would mean discovery that works on a
/// laptop and fails on a phone.</para>
///
/// <para><b>The beacon is deliberately sparse.</b> Anything answered here is readable by every
/// device on the network, hostile ones included, so it carries only what a client needs to
/// attempt a connection: device id, display name, port and protocol version. No user name, no
/// Windows build, no MAC address, no capability list, and nothing that indicates whether any
/// device is paired. The device id is a random GUID unrelated to any hardware identifier, so
/// publishing it reveals only that an agent exists here — which is unavoidable for anything
/// discoverable.</para>
///
/// <para><b>Probes are not authenticated, and that is intentional.</b> A signed probe would
/// require the client to already hold a key, which is precisely what a device looking for a PC
/// to pair with does not have. Authentication belongs on the control channel, where it actually
/// protects something. What this responder does instead is refuse to be useful for
/// amplification: a fixed magic prefix, a hard size cap, and a per-address rate limit.</para>
/// </remarks>
public sealed class UdpDiscoveryResponder : IAsyncDisposable
{
    private static readonly byte[] ProbeMagic = Encoding.ASCII.GetBytes(DiscoveryConstants.UdpProbeMagic);
    private static readonly byte[] ResponseMagic = Encoding.ASCII.GetBytes(DiscoveryConstants.UdpResponseMagic);
    private static readonly TimeSpan MinReplyInterval = TimeSpan.FromMilliseconds(250);

    private readonly ListenerEndpointStatus _endpoint;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<UdpDiscoveryResponder> _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastReply = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    // Disposal happens twice: once from the ordered shutdown in the host, and again when
    // the DI container disposes its singletons. The second pass must be a no-op.
    private bool _disposed;

    private UdpClient? _socket;
    private Task? _loop;

    /// <summary>Creates the responder.</summary>
    public UdpDiscoveryResponder(
        ListenerEndpointStatus endpoint,
        IOptionsMonitor<AgentOptions> options,
        IClock clock,
        ILogger<UdpDiscoveryResponder> logger)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the responder is listening.</summary>
    public bool IsRunning => _socket is not null;

    /// <summary>Starts answering probes, if discovery is enabled.</summary>
    public void Start()
    {
        DiscoveryOptions discovery = _options.CurrentValue.Discovery;

        if (!discovery.Enabled || !discovery.UdpFallback)
        {
            _logger.LogInformation("LAN discovery is disabled by configuration; this PC will not advertise itself.");
            return;
        }

        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);

            // Allow address reuse so a stale socket from a crashed previous run cannot keep
            // discovery down until a reboot.
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, discovery.UdpFallbackPort));
            _socket.EnableBroadcast = true;

            _logger.LogInformation(
                "Discovery responder listening on UDP {Port}.",
                discovery.UdpFallbackPort);

            _loop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
        }
        catch (SocketException ex)
        {
            // Discovery is a convenience: a phone can still connect by an address it already
            // knows. Failing to bind must not stop the agent from serving paired devices.
            _logger.LogError(
                ex,
                "Could not bind the discovery port {Port}. Paired devices can still connect directly, " +
                "but automatic discovery will not work.",
                discovery.UdpFallbackPort);

            _socket?.Dispose();
            _socket = null;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        UdpClient socket = _socket!;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult received = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                await HandleProbeAsync(socket, received, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // A single bad datagram or a transient network change must not end discovery.
                _logger.LogDebug(ex, "Discovery receive failed; continuing.");
            }
        }
    }

    private async Task HandleProbeAsync(
        UdpClient socket,
        UdpReceiveResult received,
        CancellationToken cancellationToken)
    {
        // Cheapest checks first: a malformed or oversized datagram costs almost nothing.
        if (received.Buffer.Length is 0 or > DiscoveryConstants.MaxUdpDatagramBytes)
        {
            return;
        }

        if (!StartsWithMagic(received.Buffer))
        {
            // Unrelated broadcast traffic — DHCP, SSDP, game servers. Silently ignored so the
            // log does not fill with other people's protocols.
            return;
        }

        string address = received.RemoteEndPoint.Address.ToString();
        if (!AllowReply(address))
        {
            return;
        }

        AgentOptions options = _options.CurrentValue;

        var beacon = new DiscoveryBeacon
        {
            DeviceId = options.Device.Id,
            DeviceName = options.Device.Name,

            // The port actually bound, which may differ from the configured preference if it
            // was occupied. A client must never have to guess (§0).
            Port = _endpoint.Port,
            ProtocolVersion = ProtocolVersion.Current,
        };

        byte[] payload = ProtocolJson.SerializeToUtf8(beacon);
        byte[] datagram = new byte[ResponseMagic.Length + payload.Length];
        ResponseMagic.CopyTo(datagram, 0);
        payload.CopyTo(datagram, ResponseMagic.Length);

        try
        {
            await socket.SendAsync(datagram, received.RemoteEndPoint, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Answered a discovery probe from {Address}.", address);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Could not answer a discovery probe from {Address}.", address);
        }
    }

    private static bool StartsWithMagic(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= ProbeMagic.Length &&
        datagram[..ProbeMagic.Length].SequenceEqual(ProbeMagic);

    /// <summary>
    /// Throttles replies per source address.
    /// </summary>
    /// <remarks>
    /// Keeps the responder from being useful as a reflector: a spoofed-source flood gets at most
    /// one small reply every quarter second per claimed address, which is not an amplification
    /// anyone would bother with.
    /// </remarks>
    private bool AllowReply(string address)
    {
        DateTimeOffset now = _clock.UtcNow;

        lock (_lastReply)
        {
            if (_lastReply.TryGetValue(address, out DateTimeOffset last) && now - last < MinReplyInterval)
            {
                return false;
            }

            _lastReply[address] = now;

            // Bound the table so a spoofed-source flood cannot grow it without limit.
            if (_lastReply.Count > 512)
            {
                DateTimeOffset cutoff = now - TimeSpan.FromMinutes(5);
                foreach (string stale in _lastReply.Where(e => e.Value < cutoff).Select(e => e.Key).ToArray())
                {
                    _lastReply.Remove(stale);
                }
            }

            return true;
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

        await _lifetime.CancelAsync().ConfigureAwait(false);

        _socket?.Dispose();
        _socket = null;

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Loop ends when the socket is disposed.
            }
        }

        _lifetime.Dispose();
    }
}
