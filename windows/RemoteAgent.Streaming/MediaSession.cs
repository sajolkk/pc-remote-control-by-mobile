using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Media;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Streaming.Adaptation;
using RemoteAgent.Streaming.Transport;
using RemoteAgent.Streaming.Video;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;
using IVideoEncoder = RemoteAgent.Core.Abstractions.IVideoEncoder;

namespace RemoteAgent.Streaming;

/// <summary>Settings shared by every media session.</summary>
public sealed class MediaSessionOptions
{
    /// <summary>The PC's streaming limits.</summary>
    public required StreamingLimits Limits { get; init; }

    /// <summary>Whether to draw the pointer into the stream.</summary>
    public bool CaptureCursor { get; init; } = true;

    /// <summary>Whether to prefer a hardware encoder.</summary>
    public bool PreferHardwareEncode { get; init; } = true;

    /// <summary>Lowest UDP port for media.</summary>
    public int PortRangeStart { get; init; } = 47810;

    /// <summary>Highest UDP port for media.</summary>
    public int PortRangeEnd { get; init; } = 47850;

    /// <summary>Seconds to wait for ICE and DTLS to complete before giving up.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 20;
}

/// <summary>
/// One screen stream to one phone: a WebRTC peer plus the capture-encode-send pump that feeds it.
/// </summary>
/// <remarks>
/// <para><b>Pipeline.</b> A dedicated thread runs capture → pointer compositing → NV12 conversion →
/// H.264 encode → RTP. It is a thread rather than a task because it blocks in the capture API
/// by design and must not starve the thread pool. Frames are never queued: if the encoder falls
/// behind, the next capture simply supersedes the frame that was waiting, which is what keeps
/// latency flat under load (§9.2).</para>
///
/// <para><b>Nothing is sent while nothing changes.</b> A static desktop produces no captures and
/// therefore no frames. One refresh frame is sent shortly after motion stops, so the encoder's
/// last, bitrate-starved frame of a scroll is replaced by a sharp one.</para>
///
/// <para><b>Recovery.</b> Capture is rebuilt after a mode change or driver reset, and paused with
/// an explanation while the secure desktop is showing (§6.4, §12.1). An encoder that fails is
/// replaced; one that keeps failing ends the session with an error rather than looping.</para>
///
/// <para><b>Security of the media plane.</b> Everything that could steer this peer arrives over the
/// authenticated control channel: the offer (carrying the phone's DTLS fingerprint, which is the
/// only certificate the DTLS handshake will accept) and any trickled candidates, which are
/// filtered to the phone's authenticated address. ICE uses host candidates only, bound to the
/// configured port range. No STUN, no TURN, no external server of any kind (§9.4).</para>
/// </remarks>
public sealed class MediaSession : IAsyncDisposable
{
    /// <summary>Largest offer accepted. Real offers are a few kilobytes.</summary>
    public const int MaxSdpLength = 64 * 1024;

    private const int TelemetryIntervalMs = 2_000;
    private const int RefreshAfterIdleMs = 300;
    private const int MinKeyFrameIntervalMs = 500;
    private const int CaptureRetryMs = 1_000;
    private const int MaxConsecutiveEncoderFailures = 3;
    private const int IdleAcquireTimeoutMs = 100;

    /// <summary>
    /// The H.264 format offered back to the phone: packetization mode 1 (FU-A, needed for any frame
    /// bigger than one packet) and constrained baseline, which every mobile hardware decoder takes.
    /// </summary>
    private static readonly VideoFormat H264 = new(
        VideoCodecsEnum.H264,
        96,
        90_000,
        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");

    private readonly MediaSessionOptions _options;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly IVideoEncoderFactory _encoderFactory;
    private readonly IPAddress _peer;
    private readonly ILogger _logger;
    private readonly RTCPeerConnection _pc;
    private readonly Lock _controllerLock = new();
    private readonly ManualResetEventSlim _connected = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Nv12Converter _converter = new();
    private readonly CursorCompositor _cursor = new();

    private Thread? _pump;
    private AdaptiveController? _controller;
    private (int? Height, int? Fps, int? Bitrate) _pendingCeiling;
    private string _monitorId;
    private volatile string? _requestedMonitorId;
    private volatile bool _keyFrameRequested = true;
    private long _lastKeyFrameMs = long.MinValue / 2;
    private int _closed;

    // Telemetry, written by the pump thread and read when a report is assembled.
    private string _state = "connecting";
    private string? _pauseReason;
    private int _framesSinceReport;
    private long _bytesSinceReport;
    private long _lastReportMs;
    private int _width;
    private int _height;
    private VideoEncoderDescriptor? _encoderInUse;

    private MediaSession(
        string connectionId,
        string monitorId,
        IPAddress peer,
        MediaSessionOptions options,
        IScreenCaptureFactory captureFactory,
        IVideoEncoderFactory encoderFactory,
        ILogger logger)
    {
        Id = "ms-" + Guid.NewGuid().ToString("n")[..12];
        ConnectionId = connectionId;
        _monitorId = monitorId;
        _peer = peer;
        _options = options;
        _captureFactory = captureFactory;
        _encoderFactory = encoderFactory;
        _logger = logger;

        var configuration = new RTCConfiguration
        {
            // Host candidates only: no STUN, no TURN (§9.4).
            iceServers = [],
            X_ICEIncludeAllInterfaceAddresses = false,
        };

        var ports = new PortRange(options.PortRangeStart, options.PortRangeEnd, shuffle: false, randomSeed: null);
        _pc = new RTCPeerConnection(configuration, 0, ports, videoAsPrimary: true);
        _pc.addTrack(new MediaStreamTrack(new List<VideoFormat> { H264 }, MediaStreamStatusEnum.SendOnly));

        _pc.onconnectionstatechange += OnConnectionStateChange;
        _pc.OnReceiveReport += OnReceiveReport;
        _pc.OnRtcpBye += reason => Close($"the phone ended the stream ({reason})");
    }

    /// <summary>Identifies this session in <c>media.*</c> commands and events.</summary>
    public string Id { get; }

    /// <summary>The control connection this stream belongs to.</summary>
    public string ConnectionId { get; }

    /// <summary>The monitor currently being streamed.</summary>
    public string MonitorId => _requestedMonitorId ?? _monitorId;

    /// <summary>Whether the session has ended.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Raised every couple of seconds, and on state changes, with streaming telemetry.</summary>
    public event Action<MediaSession, MediaQualityEvent>? Telemetry;

    /// <summary>Raised once when the session ends, with the reason.</summary>
    public event Action<MediaSession, string>? Closed;

    /// <summary>
    /// Creates a session from the phone's offer and returns it with the SDP answer.
    /// </summary>
    /// <exception cref="ArgumentException">The offer is unusable: malformed, oversized, or it offers no H.264.</exception>
    public static async Task<(MediaSession Session, string AnswerSdp)> CreateAsync(
        string connectionId,
        string offerSdp,
        string monitorId,
        IPAddress peer,
        MediaSessionOptions options,
        IScreenCaptureFactory captureFactory,
        IVideoEncoderFactory encoderFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(offerSdp);
        ArgumentNullException.ThrowIfNull(peer);

        if (offerSdp.Length == 0 || offerSdp.Length > MaxSdpLength)
        {
            throw new ArgumentException("The offer is empty or too large.", nameof(offerSdp));
        }

        string filtered = IceCandidatePolicy.FilterSdp(offerSdp, peer, out int removed);
        if (removed > 0)
        {
            logger.LogDebug(
                "Dropped {Count} ICE candidate(s) from the offer that were not host/UDP on {Peer}.",
                removed,
                peer);
        }

        var session = new MediaSession(connectionId, monitorId, peer, options, captureFactory, encoderFactory, logger);

        try
        {
            SetDescriptionResultEnum result = session._pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = filtered,
            });

            if (result != SetDescriptionResultEnum.OK)
            {
                throw new ArgumentException(Describe(result), nameof(offerSdp));
            }

            RTCSessionDescriptionInit answer = session._pc.createAnswer(null);
            await session._pc.setLocalDescription(answer).ConfigureAwait(false);

            session.Start();
            return (session, answer.sdp);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Adds a candidate the phone trickled after its offer.</summary>
    /// <returns>False when the candidate was refused by <see cref="IceCandidatePolicy"/>.</returns>
    public bool AddRemoteCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (IsClosed)
        {
            return false;
        }

        if (!IceCandidatePolicy.IsAllowed(candidate, _peer, out string reason))
        {
            _logger.LogDebug("Ignored a trickled ICE candidate for {Session}: {Reason}.", Id, reason);
            return false;
        }

        string line = candidate.Trim();
        if (line.StartsWith("a=", StringComparison.Ordinal))
        {
            line = line[2..];
        }

        _pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = line,
            sdpMid = sdpMid ?? "0",
            sdpMLineIndex = (ushort)Math.Max(0, sdpMLineIndex ?? 0),
        });

        return true;
    }

    /// <summary>Applies a quality ceiling. Returns the ceilings in force after clamping.</summary>
    public MediaQualityResult SetQuality(int? maxHeight, int? maxFps, int? maxBitrateKbps)
    {
        lock (_controllerLock)
        {
            if (_controller is null)
            {
                // Capture has not opened yet, so the source size is unknown. Remember the request
                // and apply it when the controller is created.
                _pendingCeiling = (maxHeight ?? _pendingCeiling.Height, maxFps ?? _pendingCeiling.Fps, maxBitrateKbps ?? _pendingCeiling.Bitrate);
                StreamingLimits limits = _options.Limits;
                return new MediaQualityResult
                {
                    MaxHeight = Math.Clamp(_pendingCeiling.Height ?? limits.MaxHeight, 240, limits.MaxHeight),
                    MaxFps = Math.Clamp(_pendingCeiling.Fps ?? limits.FpsLimit, 1, limits.FpsLimit),
                    MaxBitrateKbps = Math.Clamp(_pendingCeiling.Bitrate ?? limits.MaxBitrateKbps, limits.MinBitrateKbps, limits.MaxBitrateKbps),
                };
            }

            (int height, int fps, int bitrate) = _controller.SetCeiling(maxHeight, maxFps, maxBitrateKbps);
            return new MediaQualityResult { MaxHeight = height, MaxFps = fps, MaxBitrateKbps = bitrate };
        }
    }

    /// <summary>Switches the stream to another monitor without renegotiating.</summary>
    /// <remarks>
    /// No renegotiation is needed: H.264 carries resolution in-band, so the new monitor starts with
    /// a keyframe whose SPS describes it and the phone's decoder follows.
    /// </remarks>
    public void SelectMonitor(string monitorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);
        _requestedMonitorId = monitorId;
    }

    /// <summary>Ends the session.</summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _logger.LogInformation("Media session {Session} closed: {Reason}.", Id, reason);

        _stop.Cancel();
        _connected.Set();

        try
        {
            _pc.Close(reason);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing the peer connection failed.");
        }

        _state = "closed";
        RaiseTelemetry(NowMs);
        Closed?.Invoke(this, reason);
    }

    private long NowMs => _clock.ElapsedMilliseconds;

    private void Start()
    {
        _pump = new Thread(Pump)
        {
            Name = "media-pump " + Id,
            IsBackground = true,

            // Above normal, not higher: the pump must beat background work, but a runaway encoder
            // at a real-time priority could make the user's own desktop unresponsive.
            Priority = ThreadPriority.AboveNormal,
        };

        _pump.Start();

        // Fail the session if ICE and DTLS never complete — a phone that sent an offer and then
        // vanished must not hold a port and a capture slot forever.
        _ = Task.Delay(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds), _stop.Token).ContinueWith(
            _ =>
            {
                if (!_connected.IsSet)
                {
                    Close("the phone did not complete the media connection in time");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        _logger.LogDebug("Media session {Session} peer state: {State}.", Id, state);

        switch (state)
        {
            case RTCPeerConnectionState.connected:
                // ICE learns peer-reflexive candidates from whoever sends it a valid connectivity
                // check, so filtering the offer's candidate lines is not sufficient on its own: the
                // pair ICE actually selected is checked here, before a single frame is sent. The
                // check needs the ICE credentials from the authenticated offer, so this is
                // reachable only by a peer that saw that offer — but "saw the offer" is not the
                // same as "is the phone that authenticated", and the address is.
                IPEndPoint? selected = _pc.VideoDestinationEndPoint;
                if (selected is null || !SameHost(selected.Address, _peer))
                {
                    _logger.LogWarning(
                        "Media session {Session} connected to {Selected}, not the authenticated address {Peer}. Closing.",
                        Id,
                        selected?.Address,
                        _peer);
                    Close("the media connection came from an unexpected address");
                    break;
                }

                _logger.LogInformation(
                    "Media session {Session} connected to {Peer} (DTLS complete).",
                    Id,
                    _peer);
                _keyFrameRequested = true;
                _connected.Set();
                break;

            case RTCPeerConnectionState.failed:
                Close("the media connection failed");
                break;

            case RTCPeerConnectionState.closed:
                Close("the media connection closed");
                break;

            case RTCPeerConnectionState.disconnected:
                // ICE may recover on its own after a brief Wi-Fi drop. Give it a few seconds.
                _ = Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ContinueWith(
                    _ =>
                    {
                        if (_pc.connectionState == RTCPeerConnectionState.disconnected)
                        {
                            Close("the media connection was lost");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
                break;
        }
    }

    /// <summary>
    /// RTCP from the phone: keyframe requests and receiver reports.
    /// </summary>
    private void OnReceiveReport(IPEndPoint endpoint, SDPMediaTypesEnum mediaType, RTCPCompoundPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.video || packet is null)
        {
            return;
        }

        if (packet.Feedback is { } feedback &&
            feedback.Header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
        {
            // The decoder lost its reference (a lost keyframe, or it has just started). Nothing it
            // receives is decodable until the next IDR, so this is the one request always honoured.
            _keyFrameRequested = true;
        }

        List<ReceptionReportSample>? reports =
            packet.ReceiverReport?.ReceptionReports ?? packet.SenderReport?.ReceptionReports;

        if (reports is null || reports.Count == 0)
        {
            return;
        }

        uint ourSsrc = _pc.VideoRtcpSession?.Ssrc ?? 0;
        ReceptionReportSample sample = reports.FirstOrDefault(r => r.SSRC == ourSsrc) ?? reports[0];

        double loss = sample.FractionLost / 256.0;
        double? rtt = ComputeRttMs(sample.LastSenderReportTimestamp, sample.DelaySinceLastSenderReport);

        lock (_controllerLock)
        {
            _controller?.OnReceiverReport(NowMs, loss, rtt);
        }
    }

    /// <summary>
    /// RTT from a reception report, per RFC 3550 §6.4.1: now − LSR − DLSR, in units of 1/65536 s.
    /// </summary>
    internal static double? ComputeRttMs(uint lastSenderReport, uint delaySinceLastSenderReport)
    {
        if (lastSenderReport == 0)
        {
            return null;
        }

        uint now = NtpCompact(DateTime.UtcNow);
        uint rtt = now - lastSenderReport - delaySinceLastSenderReport;

        double ms = rtt * 1000.0 / 65536.0;

        // A wrapped or skewed result is discarded rather than fed to the controller as a
        // multi-hour round trip.
        return ms is >= 0 and < 10_000 ? ms : null;
    }

    private static bool SameHost(IPAddress a, IPAddress b) =>
        (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

    private static uint NtpCompact(DateTime utc)
    {
        var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double seconds = (utc - epoch).TotalSeconds;
        ulong ntp = ((ulong)Math.Floor(seconds) << 32) | (uint)((seconds - Math.Floor(seconds)) * 4294967296.0);
        return (uint)(ntp >> 16);
    }

    /// <summary>The pump thread.</summary>
    private void Pump()
    {
        IScreenCapture? capture = null;
        IVideoEncoder? encoder = null;
        byte[] nv12 = [];
        int encoderFailures = 0;
        long lastSendMs = -1;
        long lastChangeMs = 0;
        bool pending = false;
        bool refreshed = true;
        int appliedVersion = -1;
        long lastContentVersion = -1;

        try
        {
            // Nothing is captured until DTLS completes: frames sent before then would be dropped.
            _connected.Wait(_stop.Token);

            while (!_stop.IsCancellationRequested)
            {
                // --- Monitor switch ---
                string? requested = _requestedMonitorId;
                if (requested is not null && !string.Equals(requested, _monitorId, StringComparison.Ordinal))
                {
                    _monitorId = requested;
                    capture?.Dispose();
                    capture = null;
                }

                _requestedMonitorId = null;

                // --- Capture (re)open ---
                if (capture is null)
                {
                    capture = TryOpenCapture();
                    if (capture is null)
                    {
                        _stop.Token.WaitHandle.WaitOne(CaptureRetryMs);
                        MaybeReport();
                        continue;
                    }

                    CreateOrResizeController(capture.Monitor);
                    _keyFrameRequested = true;
                    lastContentVersion = -1;
                    SetState("streaming", null);
                }

                // --- Wait for a change, or for the next frame slot ---
                int fps;
                lock (_controllerLock)
                {
                    fps = _controller!.Fps;
                }

                long intervalMs = Math.Max(1, 1000 / Math.Max(1, fps));
                long now = NowMs;
                long deadline = lastSendMs < 0 ? now : lastSendMs + intervalMs;

                int timeout = pending ? (int)Math.Clamp(deadline - now, 0, intervalMs) : IdleAcquireTimeoutMs;
                CaptureStatus status = capture.Acquire(timeout);

                if (status == CaptureStatus.Lost)
                {
                    capture.Dispose();
                    capture = null;
                    SetState("paused", MediaPauseReasons.Recovering);
                    continue;
                }

                now = NowMs;
                if (status == CaptureStatus.Updated)
                {
                    pending = true;
                    refreshed = false;
                    lastChangeMs = now;
                }

                if (_keyFrameRequested)
                {
                    pending = true;
                }

                if (!pending && !refreshed && lastSendMs >= 0 && now - lastChangeMs >= RefreshAfterIdleMs)
                {
                    pending = true;
                    refreshed = true;
                }

                if (!pending || (lastSendMs >= 0 && now < lastSendMs + intervalMs))
                {
                    MaybeReport();
                    continue;
                }

                // --- Compose, convert, encode, send ---
                long started = NowMs;
                CapturedFrame frame = capture.Frame;

                (int width, int height, int bitrate, int version) target;
                lock (_controllerLock)
                {
                    (int w, int h) = _controller!.OutputSize;
                    target = (w, h, _controller.BitrateKbps, _controller.Version);
                }

                if (encoder is null || encoder.Width != target.width || encoder.Height != target.height)
                {
                    encoder?.Dispose();
                    encoder = null;

                    try
                    {
                        encoder = _encoderFactory.Create(new VideoEncoderSettings(
                            target.width,
                            target.height,
                            fps,
                            target.bitrate,
                            _options.PreferHardwareEncode));
                        _encoderInUse = encoder.Descriptor;
                        appliedVersion = target.version;
                    }
                    catch (NotSupportedException ex)
                    {
                        _logger.LogError(ex, "No H.264 encoder could be started for {Session}.", Id);
                        Close("no video encoder is available on this PC");
                        return;
                    }

                    nv12 = new byte[Nv12Converter.FrameSize(target.width, target.height)];
                    _width = target.width;
                    _height = target.height;
                    _keyFrameRequested = true;
                }
                else if (appliedVersion != target.version)
                {
                    encoder.SetBitrate(target.bitrate);
                    appliedVersion = target.version;
                }

                if (_options.CaptureCursor)
                {
                    _cursor.Draw(frame);
                }

                try
                {
                    _converter.Convert(frame.Pixels, frame.Width, frame.Height, frame.Stride, nv12, target.width, target.height);
                }
                finally
                {
                    _cursor.Restore(frame);
                }

                bool wantKey = _keyFrameRequested && now - _lastKeyFrameMs >= MinKeyFrameIntervalMs;
                if (_keyFrameRequested && lastContentVersion < 0)
                {
                    // The first frame after (re)opening capture must be a keyframe regardless of
                    // throttling: it is the only thing the decoder can start from.
                    wantKey = true;
                }

                byte[]? accessUnit;
                try
                {
                    accessUnit = encoder.Encode(nv12, now * 10_000, wantKey);
                    encoderFailures = 0;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    encoderFailures++;
                    _logger.LogWarning(
                        ex,
                        "Encoder {Encoder} failed ({Count} in a row) for {Session}; replacing it.",
                        encoder.Descriptor.Name,
                        encoderFailures,
                        Id);

                    encoder.Dispose();
                    encoder = null;

                    if (encoderFailures >= MaxConsecutiveEncoderFailures)
                    {
                        Close("the video encoder kept failing");
                        return;
                    }

                    continue;
                }

                if (accessUnit is null || accessUnit.Length == 0)
                {
                    continue;
                }

                bool isKey = H264AnnexB.IsKeyFrame(accessUnit);
                if (isKey)
                {
                    _keyFrameRequested = false;
                    _lastKeyFrameMs = now;
                }

                uint durationRtp = lastSendMs < 0 ? 0 : (uint)((now - lastSendMs) * 90);
                _pc.SendVideo(durationRtp, accessUnit);

                lastSendMs = now;
                lastContentVersion = frame.ContentVersion;
                pending = false;

                _framesSinceReport++;
                _bytesSinceReport += accessUnit.Length;

                lock (_controllerLock)
                {
                    _controller!.OnFrameProcessed(NowMs, NowMs - started);
                }

                MaybeReport();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The media pump for {Session} failed.", Id);
            Close("an internal error stopped the stream");
        }
        finally
        {
            encoder?.Dispose();
            capture?.Dispose();
        }
    }

    private IScreenCapture? TryOpenCapture()
    {
        try
        {
            return _captureFactory.Open(_monitorId);
        }
        catch (ScreenCaptureUnavailableException ex) when (ex.Transient)
        {
            SetState("paused", MediaPauseReasons.DesktopUnavailable);
            _logger.LogDebug("Capture unavailable for {Session}: {Reason}", Id, ex.Message);
            return null;
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            SetState("paused", MediaPauseReasons.MonitorLost);
            _logger.LogInformation("Monitor for {Session} is gone: {Reason}", Id, ex.Message);
            return null;
        }
    }

    private void CreateOrResizeController(MonitorInfo monitor)
    {
        lock (_controllerLock)
        {
            // A new monitor may have a different size, so the ladder is rebuilt; the client's
            // ceilings carry over.
            _controller = new AdaptiveController(_options.Limits, monitor.Width, monitor.Height, NowMs);

            (int? height, int? fps, int? bitrate) = _pendingCeiling;
            if (height is not null || fps is not null || bitrate is not null)
            {
                (int h, int f, int b) = _controller.SetCeiling(height, fps, bitrate);
                _pendingCeiling = (h, f, b);
            }
        }
    }

    private void SetState(string state, string? reason)
    {
        if (_state == state && _pauseReason == reason)
        {
            return;
        }

        _state = state;
        _pauseReason = reason;

        // State changes are reported immediately rather than at the next interval, so the phone can
        // say "the PC is locked" the moment the stream stops rather than two seconds later.
        RaiseTelemetry(NowMs);
    }

    private void MaybeReport()
    {
        long now = NowMs;
        if (now - _lastReportMs >= TelemetryIntervalMs)
        {
            RaiseTelemetry(now);
        }
    }

    private void RaiseTelemetry(long now)
    {
        double seconds = Math.Max(0.001, (now - _lastReportMs) / 1000.0);
        MediaQualityEvent report;

        lock (_controllerLock)
        {
            AdaptiveController? c = _controller;
            report = new MediaQualityEvent
            {
                SessionId = Id,
                State = _state,
                Reason = _pauseReason,
                Fps = Math.Round(_framesSinceReport / seconds, 1),
                TargetFps = c?.Fps ?? 0,
                BitrateKbps = (int)(_bytesSinceReport * 8 / 1000 / seconds),
                TargetBitrateKbps = c?.BitrateKbps ?? 0,
                Width = _width,
                Height = _height,
                LossPercent = Math.Round((c?.LastLoss ?? 0) * 100, 1),
                RttMs = c?.LastRttMs is { } rtt ? Math.Round(rtt, 1) : null,
                PipelineMs = Math.Round(c?.PipelineMs ?? 0, 1),
                Encoder = _encoderInUse?.Name ?? string.Empty,
                Hardware = _encoderInUse?.IsHardware ?? false,
                Level = c?.Level ?? 0,
                DegradedLevel = c?.DegradedLevel ?? int.MaxValue,
                Indicator = _state == "streaming" ? c?.Indicator ?? "good" : "poor",
            };
        }

        _lastReportMs = now;
        _framesSinceReport = 0;
        _bytesSinceReport = 0;

        try
        {
            Telemetry?.Invoke(this, report);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A telemetry subscriber failed.");
        }
    }

    private static string Describe(SetDescriptionResultEnum result) => result switch
    {
        SetDescriptionResultEnum.VideoIncompatible or SetDescriptionResultEnum.NoMatchingMediaType =>
            "The offer does not include H.264 video with packetization mode 1.",
        SetDescriptionResultEnum.DtlsFingerprintMissing or SetDescriptionResultEnum.DtlsFingerprintInvalid =>
            "The offer has no usable DTLS fingerprint.",
        SetDescriptionResultEnum.NoRemoteMedia => "The offer contains no media.",
        _ => $"The offer was not accepted ({result}).",
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Close("disposed");

        Thread? pump = _pump;
        if (pump is not null && pump != Thread.CurrentThread)
        {
            // The pump notices the stop within one capture timeout. Joined off the caller's thread
            // so disposing from an async path does not block a pool thread for that long.
            await Task.Run(() => pump.Join(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        _pc.Dispose();
        _connected.Dispose();
        _stop.Dispose();
    }
}
