import 'dart:async';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/control/control_client.dart';
import 'package:remote_control_app/data/discovery/discovery_service.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/protocol/protocol.dart';

const _fingerprint = 'pinned-fingerprint';
const _lan = '192.168.1.20';
const _remote = '100.64.0.7';

/// What a fake address does when connected to.
enum _Answer {
  /// Nothing listens there.
  unreachable,

  /// The paired PC, presenting the pinned certificate.
  thePc,

  /// Something else, presenting a different certificate.
  impostor,
}

/// The network as the tests script it: which addresses answer, and every connection tried.
class _Network {
  final answers = <String, _Answer>{};
  final attempts = <({String host, int port, String? fingerprint, Duration timeout})>[];
  final scans = <Duration>[];
  var beacons = <DiscoveryBeacon>[];

  List<String> get hostsTried => attempts.map((attempt) => attempt.host).toList();
}

class _FakeClient extends ControlClient {
  _FakeClient(this._network, DeviceIdentity identity) : super(identity: identity);

  final _Network _network;
  final _events = StreamController<EventEnvelope>.broadcast();
  final _closed = StreamController<String>.broadcast();
  bool _connected = false;

  @override
  Future<void> connect({
    required String host,
    required int port,
    String? expectedFingerprint,
    Duration timeout = const Duration(seconds: 8),
  }) async {
    _network.attempts
        .add((host: host, port: port, fingerprint: expectedFingerprint, timeout: timeout));

    switch (_network.answers[host] ?? _Answer.unreachable) {
      case _Answer.unreachable:
        throw ControlChannelException('Could not reach the PC at $host:$port.');

      case _Answer.impostor:
        // Mirrors the real client: with a pin supplied, a different certificate is refused.
        // Without one it would be accepted — which is why the tests also assert that the pin
        // is always supplied.
        if (expectedFingerprint != null) {
          throw CertificatePinMismatch(expectedFingerprint, 'someone-else');
        }
        _connected = true;

      case _Answer.thePc:
        _connected = true;
    }
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
  }) async =>
      const HelloResult(
        version: 1,
        deviceId: 'pc-1',
        deviceName: 'Office PC',
        token: 'token',
        tokenTtlSeconds: 600,
        permissions: [Permissions.launchApps, Permissions.manageFiles],
        capabilities: [Capabilities.files, Capabilities.windows],
        keepaliveTimeoutSeconds: 15,
      );

  @override
  Future<ResponseEnvelope> send(
    String command, {
    Map<String, dynamic>? args,
    bool includeToken = true,
    Duration timeout = const Duration(seconds: 20),
  }) async =>
      const ResponseEnvelope(id: 'r', ok: true, data: {'sessionState': 'Active'});

  @override
  Future<void> close() async => _connected = false;

  @override
  Future<void> dispose() async {
    _connected = false;
    await _events.close();
    await _closed.close();
  }
}

class _FakeDiscovery extends DiscoveryService {
  _FakeDiscovery(this._network);

  final _Network _network;

  @override
  Stream<DiscoveryBeacon> scan({Duration duration = const Duration(seconds: 5)}) {
    _network.scans.add(duration);
    return Stream.fromIterable(_network.beacons);
  }
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

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late _Network network;
  late PairedPcStore store;
  late ConnectionController controller;

  PairedPc pc({String? remoteAddress, int? remotePort}) {
    final base = PairedPc(
      deviceId: 'pc-1',
      deviceName: 'Office PC',
      fingerprint: _fingerprint,
      pairedAt: DateTime(2026, 1, 5),
      lastAddress: _lan,
      lastPort: 47800,
    );

    return remoteAddress == null
        ? base
        : base.withRemoteAddress(RemoteAddress(host: remoteAddress, port: remotePort));
  }

  setUp(() {
    FlutterSecureStorage.setMockInitialValues({});

    network = _Network();
    store = PairedPcStore();

    controller = ConnectionController(
      identityStore: _FakeIdentityStore(),
      pairedStore: store,
      discovery: _FakeDiscovery(network),
      clientFactory: (identity) => _FakeClient(network, identity),
    );
  });

  // Also cancels the retry timer a failed attempt leaves behind.
  tearDown(() => controller.disconnect());

  group('where the controller looks for the PC', () {
    test('the last address is tried first, and nothing else when it answers', () async {
      network.answers[_lan] = _Answer.thePc;
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      expect(network.hostsTried, [_lan]);
      expect(network.scans, isEmpty);
      expect(controller.status.phase, ConnectionPhase.ready);
      expect(controller.status.viaRemoteAddress, isFalse);
    });

    test('then a scan by device id, which finds a PC whose address changed', () async {
      network.answers['192.168.1.55'] = _Answer.thePc;
      network.beacons = [
        _beacon('someone-elses-pc', '192.168.1.30'),
        _beacon('pc-1', '192.168.1.55', port: 47811),
      ];
      await store.save(pc());

      await controller.connect(pc());

      expect(network.hostsTried, [_lan, '192.168.1.55']);
      expect(network.attempts.last.port, 47811);
      expect(controller.status.phase, ConnectionPhase.ready);

      // The new LAN address is remembered for next time.
      final stored = await store.find('pc-1');
      expect(stored!.lastAddress, '192.168.1.55');
      expect(stored.lastPort, 47811);
    });

    test('then the remote address, last', () async {
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      expect(network.hostsTried, [_lan, _remote]);
      expect(network.scans, hasLength(1), reason: 'The scan comes before the remote address.');
      expect(controller.status.phase, ConnectionPhase.ready);
      expect(controller.status.viaRemoteAddress, isTrue);
    });

    test('the remote address is tried after a PC the scan found but could not reach', () async {
      network.beacons = [_beacon('pc-1', '192.168.1.55')];
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      expect(network.hostsTried, [_lan, '192.168.1.55', _remote]);
      expect(controller.status.phase, ConnectionPhase.ready);
    });

    test('the remote address uses the PC\'s own port unless one was given', () async {
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));
      expect(network.attempts.last.port, 47800);

      await controller.disconnect();
      network.attempts.clear();

      await store.setRemoteAddress('pc-1', const RemoteAddress(host: _remote, port: 50123));
      await controller.connect((await store.find('pc-1'))!);

      expect(network.attempts.last.host, _remote);
      expect(network.attempts.last.port, 50123);
    });

    test('without a remote address nothing beyond the local network is tried', () async {
      await store.save(pc());

      await controller.connect(pc());

      // The second try of the last address is the existing "it was only slow" last resort.
      expect(network.hostsTried, [_lan, _lan]);
      expect(controller.status.phase, ConnectionPhase.reconnecting);
      expect(controller.status.isConnected, isFalse);
    });

    test('a remote address that does not answer either leaves the app retrying', () async {
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      expect(network.hostsTried, [_lan, _remote]);
      expect(controller.status.phase, ConnectionPhase.reconnecting);
      expect(controller.status.message, contains(_remote));
    });

    test('a retry uses the remote address as stored now, not as first asked for', () async {
      // Connect was called before the user typed the address in. The record handed to
      // connect() is stale; the stored one is the truth.
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc());

      expect(network.hostsTried, [_lan, _remote]);
      expect(controller.status.phase, ConnectionPhase.ready);
    });
  });

  group('the pinned certificate', () {
    test('is demanded of every address, the remote one included', () async {
      network.beacons = [_beacon('pc-1', '192.168.1.55')];
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      expect(network.attempts, hasLength(3));
      for (final attempt in network.attempts) {
        expect(
          attempt.fingerprint,
          _fingerprint,
          reason: 'The connection to ${attempt.host} was not pinned.',
        );
      }
    });

    test('a remote address answered by something else is refused', () async {
      network.answers[_remote] = _Answer.impostor;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      expect(network.hostsTried, [_lan, _remote]);
      expect(network.attempts.last.fingerprint, _fingerprint);

      expect(controller.status.isConnected, isFalse);
      expect(controller.client, isNull);
      expect(controller.status.phase, ConnectionPhase.reconnecting);
      expect(controller.status.message, contains('certificate does not match'));

      // Nothing was learned from the impostor.
      final stored = await store.find('pc-1');
      expect(stored!.lastAddress, _lan);
      expect(stored.lastSeenAt, isNull);
    });

    test('a mismatch from the PC the scan found is still final', () async {
      network.beacons = [_beacon('pc-1', '192.168.1.55')];
      network.answers['192.168.1.55'] = _Answer.impostor;
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      // It does not go on to the remote address: something claiming to be this PC on the
      // local network is a reason to stop, not to look elsewhere.
      expect(network.hostsTried, [_lan, '192.168.1.55']);
      expect(controller.status.phase, ConnectionPhase.failed);
      expect(controller.status.requiresRepair, isTrue);
      expect(controller.status.isConnected, isFalse);
    });
  });

  group('what a connection leaves stored', () {
    test('connecting through the remote address keeps the LAN address', () async {
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote, remotePort: 50123));

      await controller.connect(pc(remoteAddress: _remote, remotePort: 50123));
      expect(controller.status.viaRemoteAddress, isTrue);

      final stored = await store.find('pc-1');
      expect(stored!.lastAddress, _lan, reason: 'The LAN address must survive a remote session.');
      expect(stored.lastPort, 47800);
      expect(stored.remoteAddress, _remote);
      expect(stored.remotePort, 50123);
      expect(stored.lastSeenAt, isNotNull);
      expect(stored.permissions, [Permissions.launchApps, Permissions.manageFiles]);

      // And the status the UI renders agrees with what was stored.
      expect(controller.status.pc!.lastAddress, _lan);
      expect(controller.status.pc!.remoteAddress, _remote);
    });

    test('connecting on the LAN keeps the remote address', () async {
      network.beacons = [_beacon('pc-1', '192.168.1.55')];
      network.answers['192.168.1.55'] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote, remotePort: 50123));

      await controller.connect(pc(remoteAddress: _remote, remotePort: 50123));

      final stored = await store.find('pc-1');
      expect(stored!.lastAddress, '192.168.1.55');
      expect(stored.remoteAddress, _remote);
      expect(stored.remotePort, 50123);
    });

    test('back on the LAN after a remote session, the LAN address still works first', () async {
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));
      await controller.disconnect();

      network.attempts.clear();
      network.answers[_lan] = _Answer.thePc;

      await controller.connect((await store.find('pc-1'))!);

      expect(network.hostsTried, [_lan]);
      expect(controller.status.viaRemoteAddress, isFalse);
    });
  });

  group('timeouts', () {
    test('the local steps are shortened when a remote address is waiting', () async {
      network.beacons = [_beacon('pc-1', '192.168.1.55')];
      network.answers[_remote] = _Answer.thePc;
      await store.save(pc(remoteAddress: _remote));

      await controller.connect(pc(remoteAddress: _remote));

      final direct = network.attempts[0].timeout;
      final found = network.attempts[1].timeout;
      final scan = network.scans.single;

      // Worst case before the remote address is even tried.
      expect(direct + scan + found, lessThanOrEqualTo(const Duration(seconds: 10)));
      expect(direct + scan, lessThanOrEqualTo(const Duration(seconds: 6)));
    });

    test('and left as they were when there is none', () async {
      await store.save(pc());

      await controller.connect(pc());

      expect(network.attempts[0].timeout, const Duration(seconds: 3));
      expect(network.scans.single, const Duration(seconds: 4));
      expect(network.attempts[1].timeout, const Duration(seconds: 8));
    });
  });
}

DiscoveryBeacon _beacon(String deviceId, String address, {int port = 47800}) => DiscoveryBeacon(
      deviceId: deviceId,
      deviceName: 'Office PC',
      port: port,
      protocolVersion: 1,
      address: address,
    );
