// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:async';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../protocol/protocol.dart';
import '../protocol/screen_messages.dart';
import 'block_list.dart';
import 'host_handlers.dart';
import 'host_session.dart';

/// The host's half of the screen stream: one capture, one peer connection per viewer.
///
/// **Signalling runs over the control channel**, exactly as with a PC, so DTLS only ever completes
/// between the viewer that authenticated and this phone. No STUN, no TURN: host candidates only.
///
/// **The block list acts on the track, not the viewer.** While a blocked app is in front the one
/// shared video track is disabled, which makes libwebrtc send black frames to every viewer at once;
/// nothing of the blocked app leaves the phone. Each viewer is told why with a `media.quality`
/// event in the `paused` state and the reason `blocked_by_owner`, which it draws as a placeholder.
/// When another app comes to the front the track is enabled again and a `streaming` event follows.
class ScreenShare extends ChangeNotifier implements HostScreenService {
  ScreenShare({
    required BlockListGate gate,
    required HostSession? Function(String connectionId) sessionOf,
  })  : _gate = gate,
        _sessionOf = sessionOf {
    _gate.addListener(_applyGate);
  }

  static const _gatherTimeout = Duration(milliseconds: 1500);
  static const _telemetryInterval = Duration(seconds: 2);

  final BlockListGate _gate;
  final HostSession? Function(String) _sessionOf;
  final _streams = <String, _ViewerStream>{};
  final _random = Random.secure();

  MediaStream? _display;
  MediaStreamTrack? _track;
  bool _disposed = false;

  bool get isCapturing => _track != null;

  int get viewerCount => _streams.length;

  /// Asks the owner for screen capture consent. Must be called before the foreground service
  /// starts: on Android 14 the service of type `mediaProjection` may only start after consent.
  Future<bool> requestConsent() async {
    try {
      return await Helper.requestCapturePermission(fullScreenOnly: true);
    } on Object {
      return false;
    }
  }

  /// Starts capturing the screen. Uses the consent just given; on Android 14 that consent is
  /// single-use, so sharing again means asking again.
  Future<void> startCapture() async {
    if (_track != null) return;

    final stream = await navigator.mediaDevices.getDisplayMedia({'video': true, 'audio': false});
    final tracks = stream.getVideoTracks();
    if (tracks.isEmpty) {
      await stream.dispose();
      throw StateError('The screen capture produced no video track.');
    }

    _display = stream;
    _track = tracks.first;
    _applyGate();
    notifyListeners();
  }

  @override
  Future<HostResult> offer(String connectionId, Map<String, dynamic> args) async {
    final track = _track;
    final display = _display;
    if (track == null || display == null) {
      return HostResult.notSupported('screen streaming right now: the capture is not running');
    }

    final sdp = args['sdp'] as String?;
    if (sdp == null || sdp.isEmpty) return HostResult.invalidArguments('An SDP offer is required.');

    final monitorId = args['monitorId'] as String?;
    if (monitorId != null && monitorId != phoneMonitorId) return HostResult.notFound('That monitor');

    // One stream per viewer: a new offer replaces the old one.
    await stop(connectionId, null);

    final pc = await createPeerConnection({
      'iceServers': <Map<String, dynamic>>[],
      'sdpSemantics': 'unified-plan',
      'bundlePolicy': 'max-bundle',
      'rtcpMuxPolicy': 'require',
    });

    final viewer = _ViewerStream(connectionId, _sessionId(), pc);
    _streams[connectionId] = viewer;

    try {
      viewer.sender = await pc.addTrack(track, display);

      pc.onConnectionState = (state) {
        switch (state) {
          case RTCPeerConnectionState.RTCPeerConnectionStateConnected:
            viewer.connected = true;
            _sendTelemetry(viewer);
          case RTCPeerConnectionState.RTCPeerConnectionStateFailed:
          case RTCPeerConnectionState.RTCPeerConnectionStateClosed:
            stop(connectionId, viewer.sessionId).ignore();
          default:
            break;
        }
      };

      await pc.setRemoteDescription(RTCSessionDescription(sdp, 'offer'));

      final answer = await pc.createAnswer({});
      await pc.setLocalDescription(answer);
      await _waitForGathering(pc);

      final local = await pc.getLocalDescription();

      final quality = args['quality'];
      if (quality is Map<String, dynamic>) await _applyQuality(viewer, quality);

      viewer.telemetry = Timer.periodic(_telemetryInterval, (_) => _sendTelemetry(viewer));
      notifyListeners();

      return HostResult.success({
        'sessionId': viewer.sessionId,
        'sdp': local?.sdp ?? answer.sdp ?? '',
        'monitorId': phoneMonitorId,
        'encoder': 'Android MediaCodec (libwebrtc)',
        'hardware': true,
      });
    } on Object {
      await stop(connectionId, viewer.sessionId);
      return const HostResult.fail(ErrorCodes.internal, 'The stream could not be started.');
    }
  }

  @override
  Future<HostResult> addCandidate(String connectionId, Map<String, dynamic> args) async {
    final viewer = _streams[connectionId];
    if (viewer == null || viewer.sessionId != args['sessionId']) {
      return HostResult.notFound('That media session');
    }

    final candidate = args['candidate'] as String?;
    if (candidate == null || candidate.isEmpty) return HostResult.invalidArguments('A candidate is required.');

    try {
      await viewer.pc.addCandidate(RTCIceCandidate(
        candidate,
        args['sdpMid'] as String?,
        (args['sdpMLineIndex'] as num?)?.toInt(),
      ));
      return HostResult.empty;
    } on Object {
      return HostResult.invalidArguments('The candidate was not accepted.');
    }
  }

  @override
  Future<void> stop(String connectionId, String? sessionId) async {
    final viewer = _streams[connectionId];
    if (viewer == null) return;
    if (sessionId != null && sessionId != viewer.sessionId) return;

    _streams.remove(connectionId);
    await viewer.dispose();
    if (!_disposed) notifyListeners();
  }

  @override
  Future<HostResult> setQuality(String connectionId, Map<String, dynamic> args) async {
    final viewer = _streams[connectionId];
    if (viewer == null) return HostResult.notFound('That media session');

    final sessionId = args['sessionId'] as String?;
    if (sessionId != null && sessionId != viewer.sessionId) return HostResult.notFound('That media session');

    return HostResult.success(await _applyQuality(viewer, args));
  }

  @override
  String? sessionIdOf(String connectionId) => _streams[connectionId]?.sessionId;

  /// Applies ceilings through the sender's encoding parameters. They are ceilings: libwebrtc's
  /// own congestion control still steps down beneath them.
  Future<Map<String, dynamic>> _applyQuality(_ViewerStream viewer, Map<String, dynamic> args) async {
    final maxHeight = (args['maxHeight'] as num?)?.toInt();
    final maxFps = (args['maxFps'] as num?)?.toInt();
    final maxBitrateKbps = (args['maxBitrateKbps'] as num?)?.toInt();

    if (maxHeight != null) viewer.maxHeight = maxHeight.clamp(240, 4320);
    if (maxFps != null) viewer.maxFps = maxFps.clamp(5, 60);
    if (maxBitrateKbps != null) viewer.maxBitrateKbps = maxBitrateKbps.clamp(300, 40000);

    final sender = viewer.sender;
    if (sender != null) {
      try {
        final parameters = sender.parameters;
        final encodings = parameters.encodings ?? <RTCRtpEncoding>[];
        if (encodings.isEmpty) encodings.add(RTCRtpEncoding());

        final screenHeight = await _captureHeight();
        for (final encoding in encodings) {
          encoding.maxFramerate = viewer.maxFps;
          encoding.maxBitrate = viewer.maxBitrateKbps * 1000;
          encoding.scaleResolutionDownBy =
              screenHeight > 0 ? max(1.0, screenHeight / viewer.maxHeight) : 1.0;
        }
        parameters.encodings = encodings;
        await sender.setParameters(parameters);
      } on Object {
        // Encoding parameters are best effort; the stream runs at the default otherwise.
      }
    }

    return {
      'maxHeight': viewer.maxHeight,
      'maxFps': viewer.maxFps,
      'maxBitrateKbps': viewer.maxBitrateKbps,
    };
  }

  Future<double> _captureHeight() async {
    final track = _track;
    if (track == null) return 0;
    final settings = track.getSettings();
    return ((settings['height'] as num?) ?? 0).toDouble();
  }

  /// Enables or disables the one shared track as the block list says, and tells every viewer.
  void _applyGate() {
    final track = _track;
    if (track == null) return;

    final blocked = _gate.isBlocked;
    if (track.enabled == !blocked && _lastBlocked == blocked) return;
    _lastBlocked = blocked;

    try {
      track.enabled = !blocked;
    } on Object {
      // The track may be ending.
    }

    for (final viewer in _streams.values) {
      _sendTelemetry(viewer);
    }
    notifyListeners();
  }

  bool? _lastBlocked;

  /// Whether viewers see the placeholder right now.
  bool get isShowingPlaceholder => _lastBlocked ?? false;

  Future<void> _sendTelemetry(_ViewerStream viewer) async {
    final session = _sessionOf(viewer.connectionId);
    if (session == null || session.isClosed) return;

    double fps = 0;
    int bitrateKbps = 0;
    int width = 0;
    int height = 0;
    double? rttMs;

    try {
      final now = DateTime.now();
      final reports = await viewer.pc.getStats();
      for (final report in reports) {
        final values = report.values;
        if (report.type == 'outbound-rtp' && (values['kind'] == 'video' || values['mediaType'] == 'video')) {
          final frames = (values['framesEncoded'] as num?)?.toInt();
          final bytes = (values['bytesSent'] as num?)?.toInt();
          final elapsed = viewer.lastStatsAt == null
              ? 0.0
              : now.difference(viewer.lastStatsAt!).inMilliseconds / 1000.0;

          fps = (values['framesPerSecond'] as num?)?.toDouble() ??
              (elapsed > 0 && frames != null && viewer.lastFrames != null
                  ? (frames - viewer.lastFrames!) / elapsed
                  : 0);
          if (elapsed > 0 && bytes != null && viewer.lastBytes != null) {
            bitrateKbps = ((bytes - viewer.lastBytes!) * 8 / 1000 / elapsed).round();
          }

          viewer.lastFrames = frames;
          viewer.lastBytes = bytes;
          viewer.lastStatsAt = now;
          width = (values['frameWidth'] as num?)?.toInt() ?? 0;
          height = (values['frameHeight'] as num?)?.toInt() ?? 0;
        }

        if (report.type == 'candidate-pair' && values['state'] == 'succeeded') {
          final rtt = (values['currentRoundTripTime'] as num?)?.toDouble();
          if (rtt != null) rttMs = rtt * 1000;
        }
      }
    } on Object {
      // Stats are best effort.
    }

    final blocked = _gate.isBlocked;
    session.sendEvent(Events.mediaQuality, {
      'sessionId': viewer.sessionId,
      'state': blocked ? 'paused' : (viewer.connected ? 'streaming' : 'connecting'),
      if (blocked) 'reason': MediaPauseReasons.blockedByOwner,
      'fps': fps,
      'targetFps': viewer.maxFps,
      'bitrateKbps': bitrateKbps,
      'targetBitrateKbps': viewer.maxBitrateKbps,
      'width': width,
      'height': height,
      'lossPercent': 0,
      if (rttMs != null) 'rttMs': rttMs,
      'pipelineMs': 0,
      'codec': 'H264',
      'encoder': 'Android MediaCodec (libwebrtc)',
      'hardware': true,
      'level': 0,
      'degradedLevel': 1 << 30,
      'indicator': 'good',
    });
  }

  Future<void> _waitForGathering(RTCPeerConnection pc) async {
    if (pc.iceGatheringState == RTCIceGatheringState.RTCIceGatheringStateComplete) return;

    final done = Completer<void>();
    pc.onIceGatheringState = (state) {
      if (state == RTCIceGatheringState.RTCIceGatheringStateComplete && !done.isCompleted) {
        done.complete();
      }
    };

    await done.future.timeout(_gatherTimeout, onTimeout: () {});
  }

  String _sessionId() {
    const alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789';
    return String.fromCharCodes(
      List<int>.generate(8, (_) => alphabet.codeUnitAt(_random.nextInt(alphabet.length))),
    );
  }

  /// Ends every stream and the capture.
  Future<void> stopCapture() async {
    for (final id in _streams.keys.toList()) {
      await stop(id, null);
    }

    final track = _track;
    final display = _display;
    _track = null;
    _display = null;
    _lastBlocked = null;

    try {
      await track?.stop();
      await display?.dispose();
    } on Object {
      // Already gone.
    }

    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _gate.removeListener(_applyGate);
    stopCapture().ignore();
    super.dispose();
  }
}

class _ViewerStream {
  _ViewerStream(this.connectionId, this.sessionId, this.pc);

  final String connectionId;
  final String sessionId;
  final RTCPeerConnection pc;

  RTCRtpSender? sender;
  Timer? telemetry;
  bool connected = false;

  int maxHeight = 1080;
  int maxFps = 30;
  int maxBitrateKbps = 6000;

  int? lastFrames;
  int? lastBytes;
  DateTime? lastStatsAt;

  Future<void> dispose() async {
    telemetry?.cancel();
    try {
      await pc.close();
      await pc.dispose();
    } on Object {
      // Already torn down.
    }
  }
}
