import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/app/providers.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/features/windows/windows_screen.dart';
import 'package:remote_control_app/protocol/protocol.dart';

/// A connection that is already up, answering from a script and recording what it was asked.
class _FakeConnection extends ConnectionController {
  _FakeConnection({
    List<String> permissions = const [Permissions.launchApps],
    List<String> capabilities = const [Capabilities.windows],
  })  : _status = ConnectionStatus(
          phase: ConnectionPhase.ready,
          permissions: permissions,
          capabilities: capabilities,
          sessionState: 'Active',
        ),
        super(identityStore: DeviceIdentityStore(), pairedStore: PairedPcStore());

  final ConnectionStatus _status;
  final sent = <(String, Map<String, dynamic>?)>[];

  ResponseEnvelope Function(Map<String, dynamic> args) onAction =
      (_) => const ResponseEnvelope(id: 'r', ok: true, data: {'succeeded': true});

  @override
  ConnectionStatus get status => _status;

  @override
  Future<ResponseEnvelope?> send(String command, {Map<String, dynamic>? args}) async {
    sent.add((command, args));

    if (command == Commands.windowAction) return onAction(args ?? const {});

    return const ResponseEnvelope(id: 'r', ok: true, data: {
      'windows': [
        {
          'id': 'w-word',
          'title': 'Report.docx - Word',
          'processName': 'WINWORD',
          'isMinimized': false,
          'isMaximized': true,
          'isForeground': true,
        },
        {
          'id': 'w-explorer',
          'title': 'Downloads',
          'processName': 'explorer',
          'isMinimized': true,
          'isMaximized': false,
          'isForeground': false,
        },
      ],
    });
  }

  List<Map<String, dynamic>?> get actions =>
      [for (final (name, args) in sent) if (name == Commands.windowAction) args];
}

Future<_FakeConnection> _pump(WidgetTester tester, {_FakeConnection? connection}) async {
  final fake = connection ?? _FakeConnection();

  // A small phone, so the layouts are exercised at the width they have to survive.
  tester.view.physicalSize = const Size(1080, 2340);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [connectionProvider.overrideWith((ref) => fake)],
      child: const MaterialApp(home: WindowsScreen()),
    ),
  );
  await tester.pumpAndSettle();

  return fake;
}

Future<void> _choose(WidgetTester tester, String window, String action) async {
  final tile = find.ancestor(of: find.text(window), matching: find.byType(ListTile));

  await tester.tap(find.descendant(of: tile, matching: find.byTooltip('Window actions')));
  await tester.pumpAndSettle();
  await tester.tap(find.text(action).last);
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('without the permission it explains where to enable it and asks nothing',
      (tester) async {
    final fake = await _pump(tester, connection: _FakeConnection(permissions: const []));

    expect(find.textContaining('"Open and close apps"'), findsOneWidget);
    expect(fake.sent, isEmpty);
  });

  testWidgets('without the capability it says the PC cannot, and asks nothing', (tester) async {
    final fake = await _pump(tester, connection: _FakeConnection(capabilities: const []));

    expect(find.textContaining('did not report that it can list open windows'), findsOneWidget);
    expect(fake.sent, isEmpty);
  });

  testWidgets('lists windows with their process and state, asked for without args', (tester) async {
    final fake = await _pump(tester);

    expect(fake.sent.single, ('window.list', null));

    expect(find.text('Report.docx - Word'), findsOneWidget);
    expect(find.text('WINWORD · In front · Maximized'), findsOneWidget);
    expect(find.text('Downloads'), findsOneWidget);
    expect(find.text('explorer · Minimized'), findsOneWidget);
  });

  testWidgets('tapping a window switches to it, then refreshes', (tester) async {
    final fake = await _pump(tester);

    await tester.tap(find.text('Downloads'));
    await tester.pumpAndSettle();

    expect(fake.actions.single, {'windowId': 'w-explorer', 'action': 'focus'});
    expect(fake.sent.last.$1, 'window.list');
    expect(fake.sent.where((call) => call.$1 == 'window.list'), hasLength(2));
  });

  testWidgets('each menu action sends its own word for that window', (tester) async {
    final fake = await _pump(tester);

    await _choose(tester, 'Report.docx - Word', 'Minimize');
    await _choose(tester, 'Report.docx - Word', 'Restore');
    await _choose(tester, 'Downloads', 'Maximize');
    await _choose(tester, 'Downloads', 'Switch to');
    await _choose(tester, 'Downloads', 'Close');

    expect(fake.actions, [
      {'windowId': 'w-word', 'action': 'minimize'},
      {'windowId': 'w-word', 'action': 'restore'},
      {'windowId': 'w-explorer', 'action': 'maximize'},
      {'windowId': 'w-explorer', 'action': 'focus'},
      {'windowId': 'w-explorer', 'action': 'close'},
    ]);
  });

  testWidgets('shows the reason the PC gave when an action did not take', (tester) async {
    final fake = _FakeConnection()
      ..onAction = (_) => const ResponseEnvelope(
            id: 'r',
            ok: true,
            data: {'succeeded': false, 'detail': 'Windows refused the focus change.'},
          );

    await _pump(tester, connection: fake);
    await tester.tap(find.text('Downloads'));
    await tester.pumpAndSettle();

    expect(find.text('Windows refused the focus change.'), findsOneWidget);
  });

  testWidgets('a window that has gone is reported and the list refreshed', (tester) async {
    final fake = _FakeConnection()
      ..onAction = (_) => const ResponseEnvelope(
            id: 'r',
            ok: false,
            error: ProtocolError(code: ErrorCodes.notFound, message: 'No such window.'),
          );

    await _pump(tester, connection: fake);
    await tester.tap(find.text('Downloads'));
    await tester.pumpAndSettle();

    expect(find.text('That window is no longer open.'), findsOneWidget);
    expect(fake.sent.last.$1, 'window.list');
  });
}
