import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';
import '../../protocol/screen_messages.dart';

/// Where the stream is in its life.
enum MediaPhase {
  /// Not streaming.
  idle,

  /// Building the offer and waiting for the PC's answer.
  negotiating,

  /// Answer applied; ICE and DTLS in progress.
  connecting,

  /// Frames are arriving.
  streaming,

  /// Connected, but the PC has stopped producing frames and said why (usually: locked).
  paused,

  /// It did not work, and [MediaViewState.message] says why.
  failed,
}

/// What the viewer renders.
@immutable
class MediaViewState {
  const MediaViewState({
    this.phase = MediaPhase.idle,
    this.message,
    this.sessionId,
    this.monitorId,
    this.quality,
    this.decodedFps,
    this.jitterMs,
    this.framesDropped,
  });

  final MediaPhase phase;
  final String? message;
  final String? sessionId;
  final String? monitorId;

  /// The PC's telemetry (§9.5): bitrate, loss, RTT, encoder, quality level.
  final MediaQuality? quality;

  /// What this phone is actually decoding and showing, from WebRTC's own stats. Can differ from
  /// the PC's send rate when this device's decoder is the bottleneck.
  final double? decodedFps;
  final double? jitterMs;
  final int? framesDropped;

  bool get isActive =>
      phase == MediaPhase.negotiating ||
      phase == MediaPhase.connecting ||
      phase == MediaPhase.streaming ||
      phase == MediaPhase.paused;

  MediaViewState copyWith({
    MediaPhase? phase,
    String? message,
    String? sessionId,
    String? monitorId,
    MediaQuality? quality,
    double? decodedFps,
    double? jitterMs,
    int? framesDropped,
    bool clearMessage = false,
  }) =>
      MediaViewState(
        phase: phase ?? this.phase,
        message: clearMessage ? null : (message ?? this.message),
        sessionId: sessionId ?? this.sessionId,
        monitorId: monitorId ?? this.monitorId,
        quality: quality ?? this.quality,
        decodedFps: decodedFps ?? this.decodedFps,
        jitterMs: jitterMs ?? this.jitterMs,
        framesDropped: framesDropped ?? this.framesDropped,
      );
}

/// Keeps only the PC's candidates on [host], the address the control connection reached it at.
/// Returns [sdp] unchanged when none of them is on it, rather than leaving nothing to try.
///
/// The PC sends the screen only to the address that authenticated, and closes a stream that
/// connects anywhere else. A phone and PC that share a Wi-Fi but are connected over a VPN such as
/// Tailscale have two routes to each other, and ICE, left to itself, prefers the LAN one — which
/// the PC then refuses. Checking only the PC's address on the control connection's route makes
/// ICE take that route, whichever it is.
String restrictCandidatesToHost(String sdp, String host) {
  final wanted = _normalizeAddress(host);
  final lines = sdp.split('\n');
  final kept = <String>[];
  var matched = 0;

  for (final line in lines) {
    final text = line.trimRight();

    if (text.startsWith('a=candidate:')) {
      // foundation component transport priority address port "typ" type ...
      final fields = text.split(' ');
      if (fields.length < 5 || _normalizeAddress(fields[4]) != wanted) continue;
      matched++;
    }

    kept.add(line);
  }

  return matched == 0 ? sdp : kept.join('\n');
}

String _normalizeAddress(String address) {
  var value = address.trim().toLowerCase();
  if (value.startsWith('[') && value.endsWith(']')) value = value.substring(1, value.length - 1);

  final zone = value.indexOf('%');
  if (zone >= 0) value = value.substring(0, zone);

  // An IPv4 address seen through an IPv6 socket.
  if (value.startsWith('::ffff:') && value.contains('.')) value = value.substring(7);

  return value;
}

/// Removes the congestion-control feedback (`goog-remb`, `transport-cc` and the transport-wide
/// sequence number extension) from this phone's offer, so the PC's answer does not take it up.
///
/// The PC's RTCP stack keeps one feedback message per compound packet. libwebrtc puts REMB in the
/// same compound as a keyframe request (PLI), so the PLI was overwritten and never honoured: after
/// any lost packet the picture stayed frozen — while input still worked — until the PC happened
/// to send a keyframe on its own. The PC adapts from receiver reports, not from REMB or
/// transport-cc, so nothing is lost by not asking for them.
String withoutCongestionFeedback(String sdp) {
  final lines = sdp.split('\n');

  bool drop(String line) {
    final text = line.trimRight();
    if (text.startsWith('a=rtcp-fb:')) {
      return text.endsWith(' goog-remb') || text.endsWith(' transport-cc');
    }
    return text.startsWith('a=extmap:') && text.contains('transport-wide-cc');
  }

  return lines.where((line) => !drop(line)).join('\n');
}

/// Notices a picture that has stopped while the PC is still sending: the decoder is waiting for a
/// keyframe that is not coming. Restarting the stream always starts with one.
class FreezeDetector {
  FreezeDetector({
    this.stalledFor = const Duration(seconds: 3),
    this.minRestartGap = const Duration(seconds: 8),
  });

  /// How long the picture may stand still while frames arrive before it counts as frozen.
  final Duration stalledFor;

  /// Restarts are no closer together than this, so a PC that cannot be decoded at all is not
  /// restarted in a tight loop.
  final Duration minRestartGap;

  DateTime? _stalledSince;
  DateTime? _lastRestart;

  /// One stats sample. [sentFps] is what the PC reports sending, [shownFps] what this phone
  /// decoded. True when the stream should be restarted now.
  bool sample(DateTime now, {required double? sentFps, required double? shownFps}) {
    // A still screen sends next to nothing, and showing nothing then is correct.
    final stalled = sentFps != null && sentFps >= 2 && shownFps != null && shownFps < 0.5;

    if (!stalled) {
      _stalledSince = null;
      return false;
    }

    final since = _stalledSince ??= now;
    if (now.difference(since) < stalledFor) return false;

    final last = _lastRestart;
    if (last != null && now.difference(last) < minRestartGap) return false;

    _lastRestart = now;
    _stalledSince = null;
    return true;
  }

  /// Forgets a stall in progress, e.g. when the stream is restarted for another reason.
  void reset() => _stalledSince = null;
}

/// The phone's half of the screen stream.
///
/// **Signalling runs over the control channel**, which is already mutually authenticated and
/// pinned. That is the whole security story of the media plane: the offer carries this phone's
/// DTLS fingerprint to the PC and the answer carries the PC's back, over a channel an attacker on
/// the network cannot read or alter, so DTLS will only ever complete between these two peers.
///
/// **No STUN, no TURN** (§9.4). The peer connection is built with an empty ICE server list, so
/// the only candidates are this phone's own LAN addresses, and the PC additionally refuses any
/// candidate that is not on the address this phone's control connection came from.
///
/// **Receive only.** No camera or microphone is ever opened, so the app needs neither permission.
class MediaClient extends ValueNotifier<MediaViewState> {
  MediaClient(this._connection) : super(const MediaViewState());

  /// How long to let ICE gather host candidates before sending the offer. Host-only gathering is
  /// near-instant; the wait just avoids trickling what could have gone in the offer.
  static const _gatherTimeout = Duration(milliseconds: 1500);

  final ConnectionController _connection;
  final RTCVideoRenderer renderer = RTCVideoRenderer();

  RTCPeerConnection? _pc;
  StreamSubscription<EventEnvelope>? _events;
  Timer? _statsTimer;
  final List<RTCIceCandidate> _pendingCandidates = [];
  bool _rendererReady = false;
  bool _disposed = false;
  int _generation = 0;

  // For decoded-fps computation between stats samples.
  int? _lastFramesDecoded;
  DateTime? _lastStatsAt;

  // For restarting a frozen picture with what the user last chose.
  final _freeze = FreezeDetector();
  QualityPreset? _quality;

  /// Must be called once before [start].
  Future<void> initialize() async {
    if (_rendererReady) return;
    await renderer.initialize();
    _rendererReady = true;
  }

  /// Starts (or restarts) the stream.
  Future<void> start({String? monitorId, QualityPreset? quality}) async {
    _quality = quality;
    _freeze.reset();

    await initialize();
    await _close(notifyPc: true);

    final generation = ++_generation;
    _set(MediaViewState(phase: MediaPhase.negotiating, message: 'Starting the stream…', monitorId: monitorId));

    _events = _connection.events.listen(_onEvent);

    try {
      final pc = await createPeerConnection({
        // Deliberately empty: host candidates only (§9.4).
        'iceServers': <Map<String, dynamic>>[],
        'sdpSemantics': 'unified-plan',
        'bundlePolicy': 'max-bundle',
        'rtcpMuxPolicy': 'require',
      });

      if (generation != _generation) {
        await pc.close();
        return;
      }

      _pc = pc;

      pc.onTrack = (event) {
        if (event.track.kind == 'video' && event.streams.isNotEmpty) {
          renderer.srcObject = event.streams.first;
        }
      };

      pc.onIceCandidate = (candidate) {
        if (candidate.candidate == null || candidate.candidate!.isEmpty) return;

        final sessionId = value.sessionId;
        if (sessionId == null) {
          _pendingCandidates.add(candidate);
        } else {
          _sendCandidate(sessionId, candidate);
        }
      };

      pc.onConnectionState = (state) {
        if (generation != _generation) return;

        switch (state) {
          case RTCPeerConnectionState.RTCPeerConnectionStateConnected:
            if (value.phase == MediaPhase.connecting || value.phase == MediaPhase.negotiating) {
              _set(value.copyWith(phase: MediaPhase.streaming, clearMessage: true));
            }
          case RTCPeerConnectionState.RTCPeerConnectionStateFailed:
            _fail('The video connection could not be established. The PC and this phone must be '
                'on the same network, and the PC\'s firewall must allow PC-Remote.');
          case RTCPeerConnectionState.RTCPeerConnectionStateDisconnected:
            _set(value.copyWith(message: 'The video connection is unstable…'));
          default:
            break;
        }
      };

      await pc.addTransceiver(
        kind: RTCRtpMediaType.RTCRtpMediaTypeVideo,
        init: RTCRtpTransceiverInit(direction: TransceiverDirection.RecvOnly),
      );

      final offer = await pc.createOffer({'offerToReceiveVideo': true, 'offerToReceiveAudio': false});
      await pc.setLocalDescription(offer);
      await _waitForGathering(pc);

      final local = await pc.getLocalDescription();
      if (generation != _generation) return;

      final response = await _connection.send(Commands.mediaOffer, args: {
        'sdp': withoutCongestionFeedback(local?.sdp ?? offer.sdp ?? ''),
        if (monitorId != null) 'monitorId': monitorId,
        if (quality != null) 'quality': quality.toJson(),
      });

      if (generation != _generation) return;

      if (response == null) {
        _fail('Not connected to the PC.');
        return;
      }

      if (!response.ok) {
        _fail(_describeError(response.error));
        return;
      }

      final data = response.map;
      final sessionId = data['sessionId'] as String? ?? '';
      final peer = _connection.peerAddress;
      final answer = peer == null
          ? data['sdp'] as String? ?? ''
          : restrictCandidatesToHost(data['sdp'] as String? ?? '', peer);

      await pc.setRemoteDescription(RTCSessionDescription(answer, 'answer'));

      _set(value.copyWith(
        phase: MediaPhase.connecting,
        sessionId: sessionId,
        monitorId: data['monitorId'] as String?,
        message: 'Connecting video…',
      ));

      for (final candidate in _pendingCandidates) {
        _sendCandidate(sessionId, candidate);
      }
      _pendingCandidates.clear();

      _statsTimer = Timer.periodic(const Duration(seconds: 1), (_) => _sampleStats());
    } on Object catch (error) {
      if (generation == _generation) {
        _fail('Could not start the stream: $error');
      }
    }
  }

  /// Stops the stream and releases the PC's capture and encoder.
  Future<void> stop() async {
    _generation++;
    await _close(notifyPc: true);
    _set(const MediaViewState());
  }

  /// Changes the quality ceiling of the live stream.
  Future<String?> setQuality(QualityPreset preset) async {
    final sessionId = value.sessionId;
    if (sessionId == null) return null;

    final response = await _connection.send(Commands.mediaSetQuality, args: {
      'sessionId': sessionId,
      ...preset.toJson(),
    });

    if (response == null) return 'Not connected to the PC.';
    if (response.ok) _quality = preset;
    return response.ok ? null : _describeError(response.error);
  }

  /// Switches the live stream (and future ones) to another monitor, without renegotiating.
  Future<String?> selectMonitor(String monitorId) async {
    final response = await _connection.send(Commands.screenSelectMonitor, args: {'monitorId': monitorId});
    if (response == null) return 'Not connected to the PC.';

    if (response.ok) {
      _set(value.copyWith(monitorId: monitorId));
      return null;
    }

    return _describeError(response.error);
  }

  Future<void> _waitForGathering(RTCPeerConnection pc) async {
    if (pc.iceGatheringState == RTCIceGatheringState.RTCIceGatheringStateComplete) return;

    final done = Completer<void>();
    pc.onIceGatheringState = (state) {
      if (state == RTCIceGatheringState.RTCIceGatheringStateComplete && !done.isCompleted) {
        done.complete();
      }
    };

    // Anything gathered after the timeout is trickled with media.ice instead.
    await done.future.timeout(_gatherTimeout, onTimeout: () {});
  }

  void _sendCandidate(String sessionId, RTCIceCandidate candidate) {
    _connection.send(Commands.mediaIce, args: {
      'sessionId': sessionId,
      'candidate': candidate.candidate,
      if (candidate.sdpMid != null) 'sdpMid': candidate.sdpMid,
      if (candidate.sdpMLineIndex != null) 'sdpMLineIndex': candidate.sdpMLineIndex,
    }).ignore();
  }

  void _onEvent(EventEnvelope event) {
    if (event.topic != Events.mediaQuality || event.data == null) return;

    final quality = MediaQuality.fromJson(event.data!);
    if (quality.sessionId != value.sessionId) return;

    if (quality.isClosed) {
      _generation++;
      _close(notifyPc: false).ignore();
      _set(MediaViewState(phase: MediaPhase.idle, message: 'The PC ended the stream.', quality: quality));
      return;
    }

    final phase = switch (quality.state) {
      'paused' => MediaPhase.paused,
      'streaming' => value.phase == MediaPhase.connecting ? MediaPhase.connecting : MediaPhase.streaming,
      _ => value.phase,
    };

    _set(value.copyWith(
      phase: phase,
      quality: quality,
      message: quality.isPaused ? MediaPauseReasons.describe(quality.reason) : null,
      clearMessage: !quality.isPaused && phase == MediaPhase.streaming,
    ));
  }

  /// Reads WebRTC's inbound-rtp stats: what this phone is decoding, not what the PC is sending.
  Future<void> _sampleStats() async {
    final pc = _pc;
    if (pc == null) return;

    try {
      final reports = await pc.getStats();
      for (final report in reports) {
        if (report.type != 'inbound-rtp') continue;
        final values = report.values;
        if (values['kind'] != 'video' && values['mediaType'] != 'video') continue;

        final framesDecoded = (values['framesDecoded'] as num?)?.toInt();
        final now = DateTime.now();
        double? fps = (values['framesPerSecond'] as num?)?.toDouble();

        if (fps == null && framesDecoded != null && _lastFramesDecoded != null && _lastStatsAt != null) {
          final seconds = now.difference(_lastStatsAt!).inMilliseconds / 1000.0;
          if (seconds > 0) fps = (framesDecoded - _lastFramesDecoded!) / seconds;
        }

        _lastFramesDecoded = framesDecoded;
        _lastStatsAt = now;

        final jitter = (values['jitter'] as num?)?.toDouble();
        _set(value.copyWith(
          decodedFps: fps,
          jitterMs: jitter == null ? null : jitter * 1000,
          framesDropped: (values['framesDropped'] as num?)?.toInt(),
        ));

        final frozen = value.phase == MediaPhase.streaming &&
            _freeze.sample(now, sentFps: value.quality?.fps, shownFps: fps);

        if (frozen) {
          // The PC is sending and nothing is being shown. A new stream starts with a keyframe.
          start(monitorId: value.monitorId, quality: _quality).ignore();
          return;
        }
      }
    } on Object {
      // Stats are best-effort; a failed sample leaves the last values on screen.
    }
  }

  String _describeError(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.permissionDenied => 'This device is not allowed to view the screen. Grant "View screen" on the PC.',
        ErrorCodes.sessionUnavailable => 'Nobody is signed in on the PC, so there is no screen to show.',
        ErrorCodes.notSupported => error?.message ?? 'This PC cannot stream its screen.',
        ErrorCodes.busy => 'The PC is already streaming to the maximum number of devices.',
        ErrorCodes.notFound => 'That monitor is no longer connected.',
        _ => error?.message ?? 'The PC refused the stream.',
      };

  void _fail(String message) {
    _generation++;
    _close(notifyPc: true).ignore();
    _set(MediaViewState(phase: MediaPhase.failed, message: message));
  }

  Future<void> _close({required bool notifyPc}) async {
    _statsTimer?.cancel();
    _statsTimer = null;
    _pendingCandidates.clear();
    _lastFramesDecoded = null;
    _lastStatsAt = null;

    await _events?.cancel();
    _events = null;

    final sessionId = value.sessionId;
    if (notifyPc && sessionId != null) {
      // Best effort: if the control channel is down, the PC has already ended the stream.
      _connection.send(Commands.mediaStop, args: {'sessionId': sessionId}).ignore();
    }

    final pc = _pc;
    _pc = null;

    if (_rendererReady) {
      renderer.srcObject = null;
    }

    if (pc != null) {
      try {
        await pc.close();
        await pc.dispose();
      } on Object {
        // Already torn down.
      }
    }
  }

  void _set(MediaViewState state) {
    if (_disposed) return;
    value = state;
  }

  @override
  void dispose() {
    _disposed = true;
    _generation++;
    _close(notifyPc: true).whenComplete(() {
      if (_rendererReady) renderer.dispose();
    }).ignore();
    super.dispose();
  }
}
