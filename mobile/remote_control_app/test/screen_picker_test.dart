import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/app/providers.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/domain/connection_manager.dart';
import 'package:remote_control_app/features/remote_desktop/screen_picker_screen.dart';

class _FakeConnection extends ConnectionController {
  _FakeConnection(PairedPcStore store)
      : super(identityStore: DeviceIdentityStore(), pairedStore: store);

  ConnectionStatus _fake = const ConnectionStatus();

  @override
  ConnectionStatus get status => _fake;

  @override
  Future<void> connect(PairedPc pc) async {
    _fake = ConnectionStatus(phase: ConnectionPhase.ready, pc: pc, userName: 'me');
    notifyListeners();
  }

  @override
  Future<void> disconnect() async {
    _fake = const ConnectionStatus();
    notifyListeners();
  }
}

PairedPc _pc(String id, String name) => PairedPc(
      deviceId: id,
      deviceName: name,
      fingerprint: 'fp-$id',
      pairedAt: DateTime(2026, 1, 1),
    );

void main() {
  testWidgets('lists every paired PC, and connects one that is not connected', (tester) async {
    FlutterSecureStorage.setMockInitialValues({});
    final store = PairedPcStore();
    // Disposed by the ProviderScope it is handed to.
    final manager = ConnectionManager(create: () => _FakeConnection(store));

    final office = _pc('a', 'Office PC');
    final home = _pc('b', 'Home PC');
    await tester.runAsync(() => manager.connect(office));

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          connectionManagerProvider.overrideWith((ref) => manager),
          pairedPcsProvider.overrideWith((ref) async => [home, office]),
        ],
        child: const MaterialApp(home: ScreenPickerScreen()),
      ),
    );
    await tester.pumpAndSettle();

    // Not straight into a stream: a list to choose from, the connected PC first.
    expect(find.text('Office PC'), findsOneWidget);
    expect(find.text('Home PC'), findsOneWidget);
    expect(find.text('Open'), findsOneWidget);
    expect(find.text('Connect'), findsOneWidget);
    expect(
      tester.getTopLeft(find.text('Office PC')).dy,
      lessThan(tester.getTopLeft(find.text('Home PC')).dy),
    );

    await tester.tap(find.text('Connect'));
    await tester.pumpAndSettle();

    expect(manager.statusOf('b').isConnected, isTrue);
    expect(manager.statusOf('a').isConnected, isTrue, reason: 'Connecting one keeps the other.');
    expect(find.text('Open'), findsNWidgets(2));
  });
}
