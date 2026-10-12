import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/app/providers.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/features/apps/manage_screen.dart';
import 'package:remote_control_app/protocol/protocol.dart';

/// A connected PC with one app, one window and one shared folder.
class _FakeConnection extends ConnectionController {
  _FakeConnection({this.phase = ConnectionPhase.ready})
      : super(identityStore: DeviceIdentityStore(), pairedStore: PairedPcStore());

  final ConnectionPhase phase;
  final sent = <String>[];

  @override
  ConnectionStatus get status => ConnectionStatus(
        phase: phase,
        permissions: const [Permissions.launchApps, Permissions.manageFiles],
        capabilities: const [Capabilities.apps, Capabilities.windows, Capabilities.files],
        sessionState: 'Active',
      );

  @override
  Future<ResponseEnvelope?> send(String command, {Map<String, dynamic>? args}) async {
    sent.add(command);

    final data = switch (command) {
      Commands.appList => {
          'apps': [
            {'id': 'app-1', 'name': 'Notepad', 'running': false, 'hasWindow': false},
          ],
        },
      Commands.windowList => {
          'windows': [
            {'id': 'w-1', 'title': 'Inbox - Mail', 'processName': 'mail'},
          ],
        },
      Commands.fileList => (args?['path'] as String? ?? '').isEmpty
          ? {
              'path': '',
              'entries': [
                {'name': 'Desktop', 'path': r'C:\Users\me\Desktop', 'isDirectory': true, 'isRoot': true},
              ],
              'truncated': false,
            }
          : {'path': args!['path'], 'entries': <dynamic>[], 'truncated': false},
      _ => <String, dynamic>{},
    };

    return ResponseEnvelope(id: 'r', ok: true, data: data);
  }
}

Future<_FakeConnection> _pump(WidgetTester tester, {_FakeConnection? connection}) async {
  final fake = connection ?? _FakeConnection();

  // A small phone: the three-way switch has to fit here.
  tester.view.physicalSize = const Size(1080, 2340);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [connectionProvider.overrideWith((ref) => fake)],
      child: const MaterialApp(home: Scaffold(body: ManageScreen())),
    ),
  );
  await tester.pumpAndSettle();

  return fake;
}

/// Whether the file browser is letting the back button through to the app.
bool _backPassesThrough(WidgetTester tester) => tester
    .widget<PopScope>(find.byWidgetPredicate((widget) => widget is PopScope, skipOffstage: false))
    .canPop;

void main() {
  testWidgets('opens on Apps, which works as before, and asks for nothing else', (tester) async {
    final fake = await _pump(tester);

    expect(find.text('Notepad'), findsOneWidget);
    expect(find.text('Search installed apps'), findsOneWidget);

    // Windows and Files have not been visited, so the PC has not been asked about them.
    expect(fake.sent, isNot(contains(Commands.windowList)));
    expect(fake.sent, isNot(contains(Commands.fileList)));
  });

  testWidgets('the switch at the top reaches Windows and Files', (tester) async {
    final fake = await _pump(tester);

    await tester.tap(find.text('Windows').first);
    await tester.pumpAndSettle();

    expect(fake.sent, contains(Commands.windowList));
    expect(find.text('Inbox - Mail'), findsOneWidget);

    await tester.tap(find.text('Files').first);
    await tester.pumpAndSettle();

    expect(fake.sent, contains(Commands.fileList));
    expect(find.text('Desktop'), findsOneWidget);
  });

  testWidgets('the folder being browsed survives a look at another section', (tester) async {
    final fake = await _pump(tester);

    await tester.tap(find.text('Files').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Desktop'));
    await tester.pumpAndSettle();

    expect(find.text('This folder is empty.'), findsOneWidget);

    await tester.tap(find.text('Windows').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Files').first);
    await tester.pumpAndSettle();

    expect(find.text('This folder is empty.'), findsOneWidget);

    // And coming back did not start over from the roots.
    expect(fake.sent.where((command) => command == Commands.fileList), hasLength(2));
  });

  testWidgets('back climbs out of a folder only while Files is the section on show',
      (tester) async {
    await _pump(tester);

    await tester.tap(find.text('Files').first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Desktop'));
    await tester.pumpAndSettle();

    // On another section, the hidden file browser must not swallow the back button.
    await tester.tap(find.text('Windows').first);
    await tester.pumpAndSettle();
    expect(_backPassesThrough(tester), isTrue);

    await tester.tap(find.text('Files').first);
    await tester.pumpAndSettle();
    expect(_backPassesThrough(tester), isFalse);

    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();

    expect(find.text('Desktop'), findsOneWidget);
  });

  testWidgets('without a connection it shows the usual explanation and no switch', (tester) async {
    final fake = await _pump(tester, connection: _FakeConnection(phase: ConnectionPhase.idle));

    expect(find.text('Not connected'), findsOneWidget);
    expect(find.text('Windows'), findsNothing);
    expect(fake.sent, isEmpty);
  });
}
