import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';
import 'dart:ui';

import 'package:remote_control_app/host/block_list.dart';
import 'package:remote_control_app/host/host_handlers.dart';
import 'package:remote_control_app/host/host_input.dart';
import 'package:remote_control_app/host/host_pairing.dart';
import 'package:remote_control_app/host/host_session.dart';
import 'package:remote_control_app/host/input_mapper.dart';
import 'package:remote_control_app/host/paired_viewer_store.dart';
import 'package:remote_control_app/host/session_security.dart';
import 'package:remote_control_app/protocol/frame_codec.dart';
import 'package:remote_control_app/protocol/protocol.dart';

/// A clock the test moves by hand.
class FakeClock {
  DateTime now = DateTime.utc(2026, 11, 7, 12);

  DateTime call() => now;

  void advance(Duration by) => now = now.add(by);
}

/// Records what the phone was asked to do, instead of doing it.
class FakePlatform implements HostPlatformServices {
  final log = <String>[];
  Size screen = const Size(1080, 2400);
  int rotation = 0;
  bool accessibility = true;
  bool locked = false;
  bool perform = true;
  List<InstalledApp> apps = const [];
  Uint8List? screenshot;

  @override
  Future<Size> screenSize() async => screen;

  @override
  Future<int> screenRotation() async => rotation;

  @override
  Future<bool> isAccessibilityEnabled() async => accessibility;

  @override
  Future<bool> isLocked() async => locked;

  @override
  Future<bool> tap(Offset at, {required Duration duration}) async {
    log.add('tap ${_p(at)} ${duration.inMilliseconds}ms');
    return perform;
  }

  @override
  Future<bool> swipe(Swipe swipe) async {
    log.add('swipe ${_p(swipe.from)}->${_p(swipe.to)}');
    return perform;
  }

  @override
  Future<bool> dragStart(Offset at) async {
    log.add('dragStart ${_p(at)}');
    return perform;
  }

  @override
  Future<bool> dragMove(Swipe segment) async {
    log.add('dragMove ${_p(segment.from)}->${_p(segment.to)}');
    return perform;
  }

  @override
  Future<bool> dragEnd() async {
    log.add('dragEnd');
    return perform;
  }

  @override
  Future<bool> typeText(String text) async {
    log.add('text $text');
    return perform;
  }

  @override
  Future<bool> keyAction(PhoneKeyAction action) async {
    log.add('key ${action.name}');
    return perform;
  }

  @override
  Future<bool> navAction(String action) async {
    log.add('nav $action');
    return perform;
  }

  @override
  Future<List<InstalledApp>> listApps({required bool includeIcons}) async => apps;

  @override
  Future<bool> launchApp(String package) async {
    log.add('launch $package');
    return perform;
  }

  @override
  Future<Uint8List?> screenshotJpeg({required int maxWidth}) async {
    log.add('screenshot $maxWidth');
    return screenshot;
  }

  static String _p(Offset o) => '${o.dx.round()},${o.dy.round()}';
}

/// A screen service that remembers the offers it was given.
class FakeScreen implements HostScreenService {
  final offers = <String>[];
  final stopped = <String>[];

  @override
  Future<HostResult> offer(String connectionId, Map<String, dynamic> args) async {
    offers.add(connectionId);
    return HostResult.success({'sessionId': 'ms-$connectionId', 'sdp': 'answer', 'monitorId': 'screen'});
  }

  @override
  Future<HostResult> addCandidate(String connectionId, Map<String, dynamic> args) async => HostResult.empty;

  @override
  Future<void> stop(String connectionId, String? sessionId) async => stopped.add(connectionId);

  @override
  Future<HostResult> setQuality(String connectionId, Map<String, dynamic> args) async =>
      HostResult.success({'maxHeight': 1080, 'maxFps': 30, 'maxBitrateKbps': 6000});

  @override
  String? sessionIdOf(String connectionId) => offers.contains(connectionId) ? 'ms-$connectionId' : null;
}

/// Answers every pairing prompt the same way, and remembers what it was asked.
class FakeApprover {
  FakeApprover(this.decision);

  PairingDecision decision;
  final asked = <PairingApprovalRequest>[];

  /// When set, the prompt waits for this instead of answering at once.
  Completer<PairingDecision>? hold;

  Future<PairingDecision> call(PairingApprovalRequest request, Duration timeout) async {
    asked.add(request);
    final pending = hold;
    return pending == null ? decision : pending.future;
  }
}

/// A whole host, built from fakes.
class HostRig {
  HostRig({PairingDecision decision = const PairingDecision(approved: true)}) {
    approver = FakeApprover(decision);
    tokens = SessionTokenService(clock: clock.call);
    pairing = HostPairingService(
      store: store,
      approver: approver.call,
      hostFingerprint: hostFingerprint,
      clock: clock.call,
      approvalTimeout: const Duration(seconds: 5),
    );
    gate = BlockListGate();
    input = InputGateway(platform: platform, gate: gate);
    env = HostEnvironment(
      info: const HostInfo(
        deviceId: 'phone-a',
        deviceName: 'Pixel A',
        fingerprint: hostFingerprint,
        controlPort: 47800,
        localAddresses: ['192.168.1.10'],
        appVersion: 'test',
      ),
      store: store,
      tokens: tokens,
      pairing: pairing,
      gate: gate,
      sessions: sessions,
      platform: platform,
      screen: screen,
      input: input,
      clock: clock.call,
    );
    router = buildHostRouter(env);
  }

  static const hostFingerprint = 'host-fingerprint';

  final clock = FakeClock();
  final store = InMemoryPairedViewerStore();
  final platform = FakePlatform();
  final screen = FakeScreen();
  final sessions = HostSessionManager();
  late final FakeApprover approver;
  late final SessionTokenService tokens;
  late final HostPairingService pairing;
  late final BlockListGate gate;
  late final InputGateway input;
  late final HostEnvironment env;
  late final HostRouter router;

  int _connections = 0;

  /// A viewer with [fingerprint] connects. Paired or not depends on the store.
  Future<TestViewer> connect({required String fingerprint, String address = '192.168.1.20'}) async {
    final viewer = TestViewer(fingerprint: fingerprint, address: address);
    final paired = await store.findByFingerprint(fingerprint);

    final session = HostSession(
      transport: viewer.transport,
      connectionId: 'conn-${++_connections}',
      viewer: paired,
      router: router,
      tokens: tokens,
      store: store,
      clock: clock.call,
      keepaliveTimeout: const Duration(seconds: 15),
    );

    sessions.add(session);
    session.run();
    viewer.session = session;
    return viewer;
  }

  Future<void> dispose() async {
    await sessions.dispose();
    input.dispose();
  }
}

/// The two ends of an in-memory connection, as the host sees one of them.
class MemoryTransport implements HostTransport {
  MemoryTransport({required this.peerFingerprint, required this.remoteAddress});

  final toHost = StreamController<Uint8List>();
  final fromHost = StreamController<Uint8List>.broadcast();
  bool closedByHost = false;

  @override
  final String peerFingerprint;

  @override
  final String remoteAddress;

  @override
  String get tlsDescription => 'TLSv1.3 TLS_AES_128_GCM_SHA256';

  @override
  Stream<Uint8List> get incoming => toHost.stream;

  @override
  void write(List<int> bytes) {
    if (!fromHost.isClosed) fromHost.add(Uint8List.fromList(bytes));
  }

  @override
  Future<void> close() async {
    closedByHost = true;
    if (!toHost.isClosed) await toHost.close();
    if (!fromHost.isClosed) await fromHost.close();
  }
}

/// A viewer speaking the wire protocol by hand, so the host is tested exactly as the app's own
/// client would exercise it.
class TestViewer {
  TestViewer({required String fingerprint, required String address})
      : transport = MemoryTransport(peerFingerprint: fingerprint, remoteAddress: address) {
    transport.fromHost.stream.listen((chunk) {
      for (final frame in _codec.decode(chunk)) {
        if (frame.type == FrameType.response) {
          final json = jsonDecode(utf8.decode(frame.payload)) as Map<String, dynamic>;
          _responses.remove(json['id'])?.complete(ResponseEnvelope.fromJson(json));
        } else if (frame.type == FrameType.event) {
          events.add(EventEnvelope.fromJson(jsonDecode(utf8.decode(frame.payload)) as Map<String, dynamic>));
        } else if (frame.type == FrameType.pong) {
          pongs++;
        }
      }
    });
  }

  final MemoryTransport transport;
  final _codec = FrameCodec();
  final _responses = <String, Completer<ResponseEnvelope>>{};
  final events = <EventEnvelope>[];
  int pongs = 0;
  int sequence = 0;
  String? token;
  HostSession? session;

  /// Sends a request and waits for the answer. [seq] and [ts] override the defaults for the
  /// replay tests.
  Future<ResponseEnvelope> send(
    String command, {
    Map<String, dynamic>? args,
    int? seq,
    int? ts,
    int version = ProtocolVersion.current,
    bool includeToken = true,
    DateTime? now,
  }) {
    final id = 'r${++_counter}';
    final completer = Completer<ResponseEnvelope>();
    _responses[id] = completer;

    sequence = seq ?? sequence + 1;
    final payload = {
      'v': version,
      'id': id,
      'ts': ts ?? (now ?? DateTime.utc(2026, 11, 7, 12)).millisecondsSinceEpoch,
      'seq': sequence,
      'cmd': command,
      if (args != null) 'args': args,
      if (includeToken && token != null) 'tok': token,
    };

    transport.toHost.add(_codec.encode(FrameType.request, utf8.encode(jsonEncode(payload))));
    return completer.future.timeout(const Duration(seconds: 5));
  }

  /// Sends raw bytes, for malformed-frame tests.
  void sendRaw(List<int> bytes) => transport.toHost.add(Uint8List.fromList(bytes));

  void ping() => transport.toHost.add(_codec.encode(FrameType.ping, utf8.encode('{"nonce":"n"}')));

  /// Completes the handshake as the app does, storing the token.
  Future<ResponseEnvelope> hello({String name = 'Pixel B'}) async {
    final response = await send(Commands.hello, includeToken: false, args: {
      'minVersion': 1,
      'maxVersion': 1,
      'deviceName': name,
      'platform': 'android',
      'appVersion': 'test',
    });
    if (response.ok) token = response.map['token'] as String?;
    return response;
  }

  Future<ResponseEnvelope> pair({
    required String token,
    String deviceId = 'viewer-b',
    String name = 'Pixel B',
    String? claimedFingerprint,
  }) =>
      send(Commands.pairRequest, includeToken: false, args: {
        'deviceId': deviceId,
        'deviceName': name,
        'platform': 'android',
        'model': 'Pixel',
        'token': token,
        'clientFingerprint': claimedFingerprint ?? transport.peerFingerprint,
      });

  Future<void> disconnect() => transport.toHost.close();

  int _counter = 0;
}

PairedViewer pairedViewer(String fingerprint, {List<String>? permissions, bool blocked = false}) =>
    PairedViewer(
      deviceId: 'viewer-$fingerprint',
      deviceName: 'Viewer $fingerprint',
      fingerprint: fingerprint,
      pairedAt: DateTime.utc(2026, 1, 1),
      permissions: permissions ?? PairedViewer.defaultPermissions,
      blocked: blocked,
    );
