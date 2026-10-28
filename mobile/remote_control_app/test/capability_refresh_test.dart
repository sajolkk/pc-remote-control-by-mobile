import 'dart:async';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/control/control_client.dart';
import 'package:remote_control_app/data/discovery/discovery_service.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/protocol/protocol.dart';

const _lan = '192.168.1.20';

/// A PC that has just booted: the phone connects before its screen agent has registered, so the
/// handshake says it cannot stream, and a moment later it can.
class _BootingPc {
  /// What the handshake reports.
  var helloCapabilities = [Capabilities.files, Capabilities.windows];

  /// What `device.info` reports from now on.
  var currentCapabilities = [Capabilities.files, Capabilities.windows];

  /// Called as the handshake is answered, the moment an agent registering on the PC would
  /// broadcast that it did.
  void Function(_FakeClient client)? onHello;

  final commands = <String>[];
}

class _FakeClient extends ControlClient {
  _FakeClient(this.pc, DeviceIdentity identity) : super(identity: identity);

  final _BootingPc pc;
  final _events = StreamController<EventEnvelope>.broadcast();
  final _closed = StreamController<String>.broadcast();
  bool _connected = false;

  void emit(EventEnvelope event) => _events.add(event);

  @override
  Future<void> connect({
    required String host,
    required int port,
    String? expectedFingerprint,
    Duration timeout = const Duration(seconds: 8),
  }) async {
    if (host != _lan) throw ControlChannelException('Could not reach the PC at $host:$port.');
    _connected = true;
  }

  @override
  bool get isConnected => _connected;

  @override
  Stream<EventEnvelope> get events => _events.stream;

  @override
  Stream<String> get onClosed => _closed.stream;

  @override
  Future<HelloResult> hello({
    required String deviceName,
    required String platform,
    required String appVersion,
  }) async {
    final result = HelloResult(
      version: 1,
      deviceId: 'pc-1',
      deviceName: 'Office PC',
      token: 'token',
      tokenTtlSeconds: 600,
      permissions: const [Permissions.viewScreen, Permissions.controlInput],
      capabilities: List.of(pc.helloCapabilities),
      keepaliveTimeoutSeconds: 15,
    );

    pc.onHello?.call(this);
    return result;
  }

  @override
  Future<ResponseEnvelope> send(
    String command, {
    Map<String, dynamic>? args,
    bool includeToken = true,
    Duration timeout = const Duration(seconds: 20),
  }) async {
    pc.commands.add(command);

    return switch (command) {
      Commands.deviceInfo => ResponseEnvelope(
          id: 'r',
          ok: true,
          data: {'capabilities': List.of(pc.currentCapabilities)},
        ),
      Commands.systemState => const ResponseEnvelope(
          id: 'r',
          ok: true,
          data: {'sessionState': 'Active', 'sessionAgentConnected': true},
        ),
      _ => const ResponseEnvelope(id: 'r', ok: true, data: {}),
    };
  }

  @override
  Future<void> close() async => _connected = false;

  @override
  Future<void> dispose() async {
    _connected = false;
    await _events.close();
    await _closed.close();
  }
}

class _NoDiscovery extends DiscoveryService {
  @override
  Stream<DiscoveryBeacon> scan({Duration duration = const Duration(seconds: 5)}) =>
      const Stream.empty();
}

class _FakeIdentityStore extends DeviceIdentityStore {
  @override
  Future<DeviceIdentity> getOrCreate() async => const DeviceIdentity(
        deviceId: 'phone-1',
        certificatePem: 'unused',
        privateKeyPem: 'unused',
        fingerprint: 'phone-fingerprint',
      );
}

const _screen = [
  Capabilities.files,
  Capabilities.windows,
  Capabilities.screenCapture,
  Capabilities.secureDesktop,
];

EventEnvelope _agentConnected() => const EventEnvelope(
      topic: Events.sessionStateChanged,
      timestampMs: 0,
      data: {'sessionState': 'Active', 'sessionAgentConnected': true},
    );

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late _BootingPc pc;
  late PairedPcStore store;
  late ConnectionController controller;
  late List<_FakeClient> clients;

  final paired = PairedPc(
    deviceId: 'pc-1',
    deviceName: 'Office PC',
    fingerprint: 'pinned',
    pairedAt: DateTime(2026, 1, 5),
    lastAddress: _lan,
    lastPort: 47800,
  );

  setUp(() async {
    FlutterSecureStorage.setMockInitialValues({});

    pc = _BootingPc();
    store = PairedPcStore();
    clients = [];
    await store.save(paired);

    controller = ConnectionController(
      identityStore: _FakeIdentityStore(),
      pairedStore: store,
      discovery: _NoDiscovery(),
      clientFactory: (identity) {
        final client = _FakeClient(pc, identity);
        clients.add(client);
        return client;
      },
    );
  });

  tearDown(() => controller.disconnect());

  group('after the PC restarts', () {
    test('the screen agent registering just after the handshake is seen without reconnecting',
        () async {
      // The handshake was answered before the agent registered; by the time anyone asks, it has.
      pc.currentCapabilities = _screen;

      await controller.connect(paired);

      expect(controller.status.phase, ConnectionPhase.ready);
      expect(controller.status.has(Capabilities.screenCapture), isTrue);
      expect(controller.status.sessionAgentConnected, isTrue);
      expect(clients, hasLength(1), reason: 'No reconnect was needed.');
    });

    test('the "agent connected" event sent with the handshake is not needed', () async {
      // The PC broadcasts as the agent registers, which can be before this phone listens. A
      // broadcast stream drops it; the phone must not depend on it.
      pc.onHello = (client) {
        pc.currentCapabilities = _screen;
        client.emit(_agentConnected());
      };

      await controller.connect(paired);

      expect(controller.status.has(Capabilities.screenCapture), isTrue);
    });

    test('a refresh triggered during the handshake is not overwritten by the handshake\'s list',
        () async {
      // The event arrives after the phone listens but before it has stored the handshake's
      // list; the refresh it starts must not be undone by that list.
      pc.onHello = (client) {
        pc.currentCapabilities = _screen;
        scheduleMicrotask(() => client.emit(_agentConnected()));
      };

      await controller.connect(paired);
      await pumpEventQueue();

      expect(controller.status.has(Capabilities.screenCapture), isTrue);
    });

    test('an agent that finds the screen later is picked up from its event', () async {
      await controller.connect(paired);
      expect(controller.status.has(Capabilities.screenCapture), isFalse);

      // The desktop agent re-probed and the service told every phone.
      pc.currentCapabilities = _screen;
      clients.single.emit(_agentConnected());
      await pumpEventQueue();

      expect(controller.status.has(Capabilities.screenCapture), isTrue);
    });

    test('refreshing the PC\'s state reads its capabilities again', () async {
      await controller.connect(paired);
      expect(controller.status.has(Capabilities.screenCapture), isFalse);

      pc.currentCapabilities = _screen;
      await controller.refreshState();

      expect(controller.status.has(Capabilities.screenCapture), isTrue);
    });

    test('a PC that genuinely cannot stream still says so', () async {
      await controller.connect(paired);
      await controller.refreshState();

      expect(controller.status.has(Capabilities.screenCapture), isFalse);
      expect(controller.status.has(Capabilities.files), isTrue);
    });
  });
}
