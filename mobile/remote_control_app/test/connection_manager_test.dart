import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/domain/connection_manager.dart';

/// A connection that connects the moment it is asked to, without a network.
class _FakeConnection extends ConnectionController {
  _FakeConnection(PairedPcStore store)
      : super(identityStore: DeviceIdentityStore(), pairedStore: store);

  ConnectionStatus _fake = const ConnectionStatus();
  int connects = 0;

  @override
  ConnectionStatus get status => _fake;

  @override
  Future<void> connect(PairedPc pc) async {
    connects++;
    _fake = ConnectionStatus(phase: ConnectionPhase.ready, pc: pc);
    notifyListeners();
  }

  @override
  Future<void> disconnect() async {
    _fake = const ConnectionStatus();
    notifyListeners();
  }
}

PairedPc _pc(String id, {DateTime? seen}) => PairedPc(
      deviceId: id,
      deviceName: 'PC $id',
      fingerprint: 'fp-$id',
      pairedAt: DateTime(2026, 1, 1),
      lastAddress: '192.168.1.${id.hashCode % 200 + 2}',
      lastSeenAt: seen,
    );

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late PairedPcStore store;
  late ConnectionManager manager;

  setUp(() {
    FlutterSecureStorage.setMockInitialValues({});
    store = PairedPcStore();
    manager = ConnectionManager(store: store, create: () => _FakeConnection(store));
  });

  tearDown(() => manager.dispose());

  test('several PCs stay connected at once, each on its own connection', () async {
    await manager.connect(_pc('a'));
    await manager.connect(_pc('b'));

    expect(manager.statusOf('a').isConnected, isTrue);
    expect(manager.statusOf('b').isConnected, isTrue);
    expect(manager.of('a'), isNot(same(manager.of('b'))));
    expect(manager.connected, hasLength(2));
  });

  test('the PC connected last is the selected one, and selecting another switches', () async {
    await manager.connect(_pc('a'));
    await manager.connect(_pc('b'));

    expect(manager.activeId, 'b');
    expect(manager.active, same(manager.of('b')));
    expect(manager.connected.first, same(manager.of('b')), reason: 'The selected PC is listed first.');

    var notified = 0;
    manager.addListener(() => notified++);
    manager.select('a');

    expect(manager.active, same(manager.of('a')));
    expect(notified, 1);
  });

  test('connecting without selecting leaves the selection alone', () async {
    await manager.connect(_pc('a'));
    await manager.connect(_pc('b'), select: false);

    expect(manager.activeId, 'a');
  });

  test('disconnecting one PC leaves the others connected, and one of them selected', () async {
    await manager.connect(_pc('a'));
    await manager.connect(_pc('b'));

    await manager.disconnect('b');

    expect(manager.statusOf('a').isConnected, isTrue);
    expect(manager.statusOf('b').isConnected, isFalse);
    expect(manager.activeId, 'a');
  });

  test('with nothing selected the active connection is one that is not connected', () {
    expect(manager.activeId, isNull);
    expect(manager.active.status.isConnected, isFalse);
  });

  test('forgetting a PC drops its connection', () async {
    await manager.connect(_pc('a'));
    await manager.connect(_pc('b'));

    await manager.forget('b');

    expect(manager.of('b'), isNull);
    expect(manager.activeId, 'a');
  });

  group('at start-up', () {
    test('with nothing recorded, only the PC used most recently is connected', () async {
      final older = _pc('a', seen: DateTime(2026, 2, 1));
      final newer = _pc('b', seen: DateTime(2026, 3, 1));

      await manager.restore([older, newer]);
      await pumpEventQueue();

      expect(manager.statusOf('a').isConnected, isFalse);
      expect(manager.statusOf('b').isConnected, isTrue);
    });

    test('the PCs left connected are connected again, and a disconnected one is not', () async {
      final a = _pc('a', seen: DateTime(2026, 2, 1));
      final b = _pc('b', seen: DateTime(2026, 3, 1));
      final c = _pc('c', seen: DateTime(2026, 1, 1));

      await manager.connect(a);
      await manager.connect(b);
      await manager.connect(c);
      await manager.disconnect('c');
      await pumpEventQueue();

      final next = ConnectionManager(store: store, create: () => _FakeConnection(store));
      addTearDown(next.dispose);

      await next.restore([a, b, c]);
      await pumpEventQueue();

      expect(next.statusOf('a').isConnected, isTrue);
      expect(next.statusOf('b').isConnected, isTrue);
      expect(next.statusOf('c').isConnected, isFalse);
      expect(next.activeId, 'b', reason: 'The most recently used is selected.');
    });
  });

  test('saves from several connections at once all land', () async {
    // Each connection saves its PC as it comes up; with several connecting together the saves
    // overlap, and none may be lost.
    await Future.wait([for (final id in ['a', 'b', 'c', 'd']) store.save(_pc(id))]);

    final ids = (await store.getAll()).map((pc) => pc.deviceId).toSet();
    expect(ids, {'a', 'b', 'c', 'd'});
  });
}
