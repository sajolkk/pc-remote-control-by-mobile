import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/app/providers.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/features/devices/devices_screen.dart';
import 'package:remote_control_app/protocol/protocol.dart';

/// A controller that is doing nothing, so the Devices screen renders its idle state.
class _IdleConnection extends ConnectionController {
  _IdleConnection(PairedPcStore store)
      : super(identityStore: DeviceIdentityStore(), pairedStore: store);

  @override
  ConnectionStatus get status => const ConnectionStatus();
}

Future<PairedPcStore> _pump(WidgetTester tester, {RemoteAddress? remote}) async {
  FlutterSecureStorage.setMockInitialValues({});

  final store = PairedPcStore();

  await tester.runAsync(() async {
    await store.save(
      PairedPc(
        deviceId: 'pc-1',
        deviceName: 'Office PC',
        fingerprint: 'fp-abc',
        pairedAt: DateTime(2026, 1, 5),
        lastAddress: '192.168.1.20',
        lastPort: 47800,
      ),
    );

    if (remote != null) await store.setRemoteAddress('pc-1', remote);
  });

  tester.view.physicalSize = const Size(1080, 2340);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        pairedStoreProvider.overrideWithValue(store),
        connectionProvider.overrideWith((ref) => _IdleConnection(store)),

        // No network in a test: the scan simply finds nothing.
        discoveredPcsProvider.overrideWith((ref) => Stream.value(const <DiscoveryBeacon>[])),
      ],
      child: const MaterialApp(home: DevicesScreen()),
    ),
  );

  await _settle(tester);
  return store;
}

/// Lets the secure-storage mock's real asynchrony finish, then the frames that follow it.
Future<void> _settle(WidgetTester tester) async {
  await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
  await tester.pumpAndSettle();
}

Future<void> _openDialog(WidgetTester tester, String menuItem) async {
  await tester.tap(find.byTooltip('More'));
  await tester.pumpAndSettle();
  await tester.tap(find.text(menuItem));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('a remote address can be added, and says what it is for', (tester) async {
    final store = await _pump(tester);

    expect(find.textContaining('Remote address:'), findsNothing);

    await _openDialog(tester, 'Add remote address…');

    expect(
      find.text(
        'Used when the PC cannot be found on this network, e.g. over a VPN such as Tailscale.',
      ),
      findsOneWidget,
    );

    await tester.enterText(find.byType(TextField), '100.64.0.7');
    await tester.tap(find.text('Save'));
    await _settle(tester);

    final stored = await tester.runAsync(() => store.find('pc-1'));
    expect(stored!.remoteAddress, '100.64.0.7');
    expect(stored.remotePort, isNull);
    expect(stored.lastAddress, '192.168.1.20');

    expect(find.textContaining('Remote address: 100.64.0.7'), findsOneWidget);
  });

  testWidgets('a port can be given, and nonsense is not saved', (tester) async {
    final store = await _pump(tester);

    await _openDialog(tester, 'Add remote address…');

    await tester.enterText(find.byType(TextField), 'http://my pc');
    await tester.tap(find.text('Save'));
    await _settle(tester);

    expect(find.textContaining('Enter a host name or IP address'), findsOneWidget);
    expect((await tester.runAsync(() => store.find('pc-1')))!.remoteAddress, isNull);

    await tester.enterText(find.byType(TextField), 'office-pc.tailnet.ts.net:50123');
    await tester.tap(find.text('Save'));
    await _settle(tester);

    final stored = await tester.runAsync(() => store.find('pc-1'));
    expect(stored!.remoteAddress, 'office-pc.tailnet.ts.net');
    expect(stored.remotePort, 50123);
  });

  testWidgets('an existing address is shown for editing and can be removed', (tester) async {
    final store = await _pump(tester, remote: const RemoteAddress(host: '100.64.0.7', port: 50123));

    expect(find.textContaining('Remote address: 100.64.0.7:50123'), findsOneWidget);

    await _openDialog(tester, 'Remote address…');

    expect(tester.widget<TextField>(find.byType(TextField)).controller!.text, '100.64.0.7:50123');

    await tester.tap(find.text('Remove'));
    await _settle(tester);

    final stored = await tester.runAsync(() => store.find('pc-1'));
    expect(stored!.remoteAddress, isNull);
    expect(stored.lastAddress, '192.168.1.20');
    expect(find.textContaining('Remote address:'), findsNothing);
  });

  testWidgets('cancelling changes nothing', (tester) async {
    final store = await _pump(tester, remote: const RemoteAddress(host: '100.64.0.7'));

    await _openDialog(tester, 'Remote address…');
    await tester.enterText(find.byType(TextField), 'somewhere-else');
    await tester.tap(find.text('Cancel'));
    await _settle(tester);

    expect((await tester.runAsync(() => store.find('pc-1')))!.remoteAddress, '100.64.0.7');
  });
}
