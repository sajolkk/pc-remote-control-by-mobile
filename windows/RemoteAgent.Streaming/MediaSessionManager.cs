using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Streaming;

/// <summary>Why a media request could not be satisfied.</summary>
public enum MediaFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The arguments were unusable.</summary>
    InvalidArguments,

    /// <summary>The referenced session or monitor does not exist.</summary>
    NotFound,

    /// <summary>The PC's stream limit has been reached.</summary>
    Busy,

    /// <summary>This PC cannot stream at all.</summary>
    NotSupported,
}

/// <summary>
/// Owns every media session in the agent, keyed by the control connection that started it.
/// </summary>
/// <remarks>
/// <para><b>One stream per connection.</b> A new offer from a connection that is already streaming
/// replaces the old stream rather than adding a second one: that is what a phone does when it
/// reconnects its media after a Wi-Fi blip, and holding both would double the encode cost for a
/// stream nobody is watching.</para>
///
/// <para><b>Streams die with their connection.</b> The service tells the agent when a control
/// connection closes or loses <c>ViewScreen</c>, and <see cref="EndConnectionAsync"/> stops its stream
/// immediately. The media plane never outlives the authorization that created it.</para>
///
/// <para><b>Monitor selection is per connection,</b> so two phones can watch different screens.</para>
/// </remarks>
public sealed class MediaSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MediaSession> _byConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _selectedMonitor = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Func<MediaSessionOptions> _options;
    private readonly Func<int> _maxStreams;
    private readonly Func<string?> _defaultMonitorId;
    private readonly IMonitorProvider _monitors;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly IVideoEncoderFactory _encoderFactory;
    private readonly ILogger<MediaSessionManager> _logger;
    private readonly ILogger<MediaSession> _sessionLogger;

    /// <summary>Creates the manager.</summary>
    /// <param name="options">Current session options, read when each stream starts.</param>
    /// <param name="maxStreams">Current limit on simultaneous streams.</param>
    /// <param name="defaultMonitorId">The configured default monitor, or null for the primary.</param>
    /// <param name="monitors">Monitor enumeration.</param>
    /// <param name="captureFactory">Capture.</param>
    /// <param name="encoderFactory">Encoding.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="sessionLogger">Diagnostics for individual sessions.</param>
    public MediaSessionManager(
        Func<MediaSessionOptions> options,
        Func<int> maxStreams,
        Func<string?> defaultMonitorId,
        IMonitorProvider monitors,
        IScreenCaptureFactory captureFactory,
        IVideoEncoderFactory encoderFactory,
        ILogger<MediaSessionManager> logger,
        ILogger<MediaSession> sessionLogger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _maxStreams = maxStreams ?? throw new ArgumentNullException(nameof(maxStreams));
        _defaultMonitorId = defaultMonitorId ?? throw new ArgumentNullException(nameof(defaultMonitorId));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionLogger = sessionLogger ?? throw new ArgumentNullException(nameof(sessionLogger));
    }

    /// <summary>
    /// Raised with telemetry for one connection's stream. The host delivers it to that connection
    /// only — never broadcast, since it identifies what another device is watching.
    /// </summary>
    public event Action<string, MediaQualityEvent>? Telemetry;

    /// <summary>Number of live streams.</summary>
    public int ActiveCount => _byConnection.Count;

    /// <summary>The monitor a connection is streaming, or would stream if it started now.</summary>
    public string? GetSelectedMonitor(string connectionId, IReadOnlyList<MonitorInfo> monitors)
    {
        if (_byConnection.TryGetValue(connectionId, out MediaSession? session) && !session.IsClosed)
        {
            return session.MonitorId;
        }

        return Resolve(connectionId, null, monitors)?.Id;
    }

    /// <summary>Starts a stream from an offer, replacing any stream this connection already has.</summary>
    public async Task<(MediaAnswerResult? Answer, MediaFailure Failure, string? Detail)> StartAsync(
        string connectionId,
        string remoteAddress,
        MediaOfferArgs args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (string.IsNullOrWhiteSpace(args.Sdp))
        {
            return (null, MediaFailure.InvalidArguments, "An SDP offer is required.");
        }

        if (!IPAddress.TryParse(remoteAddress, out IPAddress? peer))
        {
            // The address is what media is restricted to. Without one there is nothing safe to
            // send to, so the request is refused rather than allowed to go anywhere.
            return (null, MediaFailure.InvalidArguments, "The connection has no usable peer address.");
        }

        IReadOnlyList<VideoEncoderDescriptor> encoders = _encoderFactory.Probe();
        if (encoders.Count == 0)
        {
            return (null, MediaFailure.NotSupported, "This PC has no H.264 encoder.");
        }

        IReadOnlyList<MonitorInfo> monitors = _monitors.GetMonitors();
        MonitorInfo? monitor = Resolve(connectionId, args.MonitorId, monitors);
        if (monitor is null)
        {
            return args.MonitorId is null
                ? (null, MediaFailure.NotSupported, "No monitor is available to capture.")
                : (null, MediaFailure.NotFound, "That monitor is not connected.");
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byConnection.TryRemove(connectionId, out MediaSession? previous))
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            if (_byConnection.Count >= Math.Max(1, _maxStreams()))
            {
                return (null, MediaFailure.Busy, "The PC is already streaming to the maximum number of devices.");
            }

            MediaSessionOptions options = _options();

            (MediaSession session, string answer) = await MediaSession.CreateAsync(
                connectionId,
                args.Sdp,
                monitor.Id,
                peer,
                options,
                _captureFactory,
                _encoderFactory,
                _sessionLogger).ConfigureAwait(false);

            if (args.Quality is { } quality)
            {
                session.SetQuality(quality.MaxHeight, quality.MaxFps, quality.MaxBitrateKbps);
            }

            session.Telemetry += (s, report) => Telemetry?.Invoke(s.ConnectionId, report);
            session.Closed += (s, reason) =>
            {
                // Remove only this exact session: a replacement for the same connection may
                // already be registered under the key.
                _byConnection.TryRemove(new KeyValuePair<string, MediaSession>(s.ConnectionId, s));
                _ = s.DisposeAsync().AsTask();
            };

            _byConnection[connectionId] = session;
            _selectedMonitor[connectionId] = monitor.Id;

            VideoEncoderDescriptor preferred = options.PreferHardwareEncode
                ? encoders.OrderByDescending(static e => e.IsHardware).First()
                : encoders.OrderBy(static e => e.IsHardware).First();

            _logger.LogInformation(
                "Media session {Session} started for connection {Connection} on {Monitor}.",
                session.Id,
                connectionId,
                monitor.Name);

            return (new MediaAnswerResult
            {
                SessionId = session.Id,
                Sdp = answer,
                MonitorId = monitor.Id,
                Encoder = preferred.Name,
                Hardware = preferred.IsHardware,
            }, MediaFailure.None, null);
        }
        catch (ArgumentException ex)
        {
            return (null, MediaFailure.InvalidArguments, ex.Message);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Adds a trickled candidate to a connection's stream.</summary>
    public MediaFailure AddCandidate(string connectionId, MediaIceArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!TryGetSession(connectionId, args.SessionId, out MediaSession? session))
        {
            return MediaFailure.NotFound;
        }

        // A refused candidate is not an error to report: the phone offers every interface it has,
        // and most of them are supposed to be refused.
        session!.AddRemoteCandidate(args.Candidate, args.SdpMid, args.SdpMLineIndex);
        return MediaFailure.None;
    }

    /// <summary>Applies a quality ceiling to a connection's stream.</summary>
    public (MediaQualityResult? Result, MediaFailure Failure) SetQuality(string connectionId, MediaQualityArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!TryGetSession(connectionId, args.SessionId, out MediaSession? session))
        {
            return (null, MediaFailure.NotFound);
        }

        return (session!.SetQuality(args.MaxHeight, args.MaxFps, args.MaxBitrateKbps), MediaFailure.None);
    }

    /// <summary>Selects the monitor for a connection, switching its live stream if it has one.</summary>
    public MediaFailure SelectMonitor(string connectionId, string monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return MediaFailure.InvalidArguments;
        }

        if (!_monitors.GetMonitors().Any(m => string.Equals(m.Id, monitorId, StringComparison.Ordinal)))
        {
            return MediaFailure.NotFound;
        }

        _selectedMonitor[connectionId] = monitorId;

        if (_byConnection.TryGetValue(connectionId, out MediaSession? session))
        {
            session.SelectMonitor(monitorId);
        }

        return MediaFailure.None;
    }

    /// <summary>Stops a connection's stream. Stopping a stream that does not exist succeeds.</summary>
    public async Task StopAsync(string connectionId, string? sessionId)
    {
        if (_byConnection.TryGetValue(connectionId, out MediaSession? session) &&
            (sessionId is null || string.Equals(session.Id, sessionId, StringComparison.Ordinal)) &&
            _byConnection.TryRemove(new KeyValuePair<string, MediaSession>(connectionId, session)))
        {
            session.Close("stopped by the phone");
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends everything belonging to a connection: its stream and its monitor selection. Called when
    /// the connection closes, or when the device loses permission to view the screen.
    /// </summary>
    public async Task EndConnectionAsync(string connectionId, string reason)
    {
        _selectedMonitor.TryRemove(connectionId, out _);

        if (_byConnection.TryRemove(connectionId, out MediaSession? session))
        {
            session.Close(reason);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Ends every stream, e.g. when the service that authorized them has gone away.</summary>
    public async Task EndAllAsync(string reason)
    {
        foreach (string connectionId in _byConnection.Keys.ToArray())
        {
            await EndConnectionAsync(connectionId, reason).ConfigureAwait(false);
        }

        _selectedMonitor.Clear();
    }

    private bool TryGetSession(string connectionId, string? sessionId, out MediaSession? session)
    {
        if (_byConnection.TryGetValue(connectionId, out session) &&
            !session.IsClosed &&
            (string.IsNullOrEmpty(sessionId) || string.Equals(session.Id, sessionId, StringComparison.Ordinal)))
        {
            return true;
        }

        // A session id belonging to another connection is reported as not found, not as
        // forbidden: one phone must not be able to probe for another's session ids.
        session = null;
        return false;
    }

    private MonitorInfo? Resolve(string connectionId, string? requested, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (requested is not null)
        {
            return monitors.FirstOrDefault(m => string.Equals(m.Id, requested, StringComparison.Ordinal));
        }

        // Fall back through the connection's own choice, then configuration, then the primary. A
        // stale choice (the monitor was unplugged) is skipped rather than failing the stream.
        foreach (string? candidate in new[]
                 {
                     _selectedMonitor.TryGetValue(connectionId, out string? selected) ? selected : null,
                     _defaultMonitorId(),
                 })
        {
            MonitorInfo? match = candidate is null
                ? null
                : monitors.FirstOrDefault(m => string.Equals(m.Id, candidate, StringComparison.Ordinal));

            if (match is not null)
            {
                return match;
            }
        }

        return monitors.FirstOrDefault(static m => m.Primary) ?? monitors[0];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await EndAllAsync("the agent is stopping").ConfigureAwait(false);
        _startLock.Dispose();
    }
}
