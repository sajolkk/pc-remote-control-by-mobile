import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/app/providers.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/domain/connection_controller.dart';
import 'package:remote_control_app/features/files/files_screen.dart';
import 'package:remote_control_app/protocol/protocol.dart';

const _documents = r'C:\Users\me\Documents';
const _projects = r'C:\Users\me\Documents\Projects';

/// A connection that is already up, answering from a script and recording what it was asked.
class _FakeConnection extends ConnectionController {
  _FakeConnection({
    List<String> permissions = const [Permissions.manageFiles],
    List<String> capabilities = const [Capabilities.files],
  })  : _status = ConnectionStatus(
          phase: ConnectionPhase.ready,
          permissions: permissions,
          capabilities: capabilities,
          sessionState: 'Active',
        ),
        super(identityStore: DeviceIdentityStore(), pairedStore: PairedPcStore());

  final ConnectionStatus _status;

  /// Every command sent, with its args, in order.
  final sent = <(String, Map<String, dynamic>?)>[];

  /// Answers by command. Anything not scripted succeeds with an empty result.
  final answers = <String, ResponseEnvelope Function(Map<String, dynamic> args)>{};

  @override
  ConnectionStatus get status => _status;

  @override
  Future<ResponseEnvelope?> send(String command, {Map<String, dynamic>? args}) async {
    sent.add((command, args));
    return answers[command]?.call(args ?? const {}) ?? _ok(const {});
  }

  List<Map<String, dynamic>?> argsOf(String command) =>
      [for (final (name, args) in sent) if (name == command) args];
}

ResponseEnvelope _ok(Map<String, dynamic> data) => ResponseEnvelope(id: 'r', ok: true, data: data);

ResponseEnvelope _refused(String code, String message) => ResponseEnvelope(
      id: 'r',
      ok: false,
      error: ProtocolError(code: code, message: message),
    );

/// A small PC: two roots, a Documents folder with a subfolder and a file.
ResponseEnvelope _list(Map<String, dynamic> args) {
  final path = args['path'] as String? ?? '';

  return switch (path) {
    '' => _ok({
        'path': '',
        'entries': [
          {'name': 'Documents', 'path': _documents, 'isDirectory': true, 'isRoot': true},
          {'name': 'Local Disk (C:)', 'path': r'C:\', 'isDirectory': true, 'isRoot': true},
        ],
        'truncated': false,
      }),
    _documents => _ok({
        'path': _documents,
        'entries': [
          {'name': 'Projects', 'path': _projects, 'isDirectory': true},
          {
            'name': 'notes.txt',
            'path': '$_documents\\notes.txt',
            'isDirectory': false,
            'size': 1536,
            'modified': '2026-03-09T14:05:00Z',
          },
        ],
        'truncated': false,
      }),
    _projects => _ok({
        'path': _projects,
        'parent': _documents,
        'entries': <dynamic>[],
        'truncated': false,
      }),
    _ => _refused(ErrorCodes.notFound, 'That folder does not exist.'),
  };
}

Future<_FakeConnection> _pump(WidgetTester tester, {_FakeConnection? connection}) async {
  final fake = connection ?? _FakeConnection();
  fake.answers.putIfAbsent(Commands.fileList, () => _list);

  // A small phone, so the layouts are exercised at the width they have to survive.
  tester.view.physicalSize = const Size(1080, 2340);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [connectionProvider.overrideWith((ref) => fake)],
      child: const MaterialApp(home: FilesScreen()),
    ),
  );
  await tester.pumpAndSettle();

  return fake;
}

Future<void> _openActions(WidgetTester tester, String name) async {
  await tester.longPress(find.text(name));
  await tester.pumpAndSettle();
}

void main() {
  group('gating', () {
    testWidgets('without the permission it explains where to enable it and asks nothing',
        (tester) async {
      final fake = await _pump(tester, connection: _FakeConnection(permissions: const []));

      expect(find.textContaining('"Manage files"'), findsOneWidget);
      expect(find.textContaining('on the PC'), findsWidgets);
      expect(fake.sent, isEmpty);
    });

    testWidgets('without the capability it says the PC cannot, and asks nothing', (tester) async {
      final fake = await _pump(tester, connection: _FakeConnection(capabilities: const []));

      expect(find.textContaining('did not report that it can share its files'), findsOneWidget);
      expect(fake.sent, isEmpty);
    });
  });

  group('browsing', () {
    testWidgets('starts at the roots, asked for with an empty object', (tester) async {
      final fake = await _pump(tester);

      expect(fake.sent.single.$1, 'file.list');
      expect(fake.sent.single.$2, isEmpty);

      expect(find.text('Documents'), findsOneWidget);
      expect(find.text('Local Disk (C:)'), findsOneWidget);
      expect(find.text('Files'), findsOneWidget);
    });

    testWidgets('opens a folder by the path the PC gave, and shows size and date', (tester) async {
      final fake = await _pump(tester);

      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      expect(fake.argsOf('file.list').last, {'path': _documents});
      expect(find.text('Projects'), findsOneWidget);
      expect(find.text('notes.txt'), findsOneWidget);
      expect(find.textContaining('1.50 KB'), findsOneWidget);

      // The trail: the roots, then the folder on show.
      expect(find.widgetWithText(TextButton, 'PC'), findsOneWidget);
      expect(find.widgetWithText(TextButton, 'Documents'), findsOneWidget);
    });

    testWidgets('the back button climbs out one folder at a time', (tester) async {
      final fake = await _pump(tester);

      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Projects'));
      await tester.pumpAndSettle();

      expect(find.text('This folder is empty.'), findsOneWidget);

      await tester.binding.handlePopRoute();
      await tester.pumpAndSettle();
      expect(fake.argsOf('file.list').last, {'path': _documents});
      expect(find.text('notes.txt'), findsOneWidget);

      // Documents is a root: the PC reports no parent, so up from it is the roots view.
      await tester.binding.handlePopRoute();
      await tester.pumpAndSettle();
      expect(fake.argsOf('file.list').last, isEmpty);
      expect(find.text('Local Disk (C:)'), findsOneWidget);
    });

    testWidgets('a crumb jumps straight back', (tester) async {
      final fake = await _pump(tester);

      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Projects'));
      await tester.pumpAndSettle();

      await tester.tap(find.widgetWithText(TextButton, 'PC'));
      await tester.pumpAndSettle();

      expect(fake.argsOf('file.list').last, isEmpty);
      expect(find.text('Local Disk (C:)'), findsOneWidget);
    });

    testWidgets('a refused folder shows the PC\'s reason and a way out', (tester) async {
      final fake = _FakeConnection();
      fake.answers[Commands.fileList] = (args) => args['path'] == r'C:\'
          ? _refused(ErrorCodes.accessDenied, 'Windows denied access to this folder.')
          : _list(args);

      await _pump(tester, connection: fake);

      await tester.tap(find.text('Local Disk (C:)'));
      await tester.pumpAndSettle();

      expect(find.text('Windows denied access to this folder.'), findsOneWidget);
      expect(find.text('Try again'), findsOneWidget);

      await tester.tap(find.text('Go up'));
      await tester.pumpAndSettle();

      expect(find.text('Documents'), findsOneWidget);
    });

    testWidgets('a truncated listing says so', (tester) async {
      final fake = _FakeConnection();
      fake.answers[Commands.fileList] = (args) => args['path'] == _documents
          ? _ok({
              'path': _documents,
              'entries': [
                {'name': 'a.txt', 'path': '$_documents\\a.txt', 'isDirectory': false, 'size': 1},
              ],
              'truncated': true,
            })
          : _list(args);

      await _pump(tester, connection: fake);

      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      expect(find.textContaining('holds more than can be shown'), findsOneWidget);
    });
  });

  group('actions', () {
    testWidgets('a root offers only opening it on the PC', (tester) async {
      final fake = await _pump(tester);

      await _openActions(tester, 'Documents');

      expect(find.text('Open on PC'), findsOneWidget);
      expect(find.text('Rename'), findsNothing);
      expect(find.text('Delete'), findsNothing);

      await tester.tap(find.text('Open on PC'));
      await tester.pumpAndSettle();

      expect(fake.argsOf('file.open').single, {'path': _documents});
    });

    testWidgets('there is no upload or download', (tester) async {
      await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();
      await _openActions(tester, 'notes.txt');

      expect(find.textContaining('ownload'), findsNothing);
      expect(find.textContaining('pload'), findsNothing);
    });

    testWidgets('delete goes to the Recycle Bin by default', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'notes.txt');
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      expect(find.textContaining('Recycle Bin'), findsWidgets);

      await tester.tap(find.widgetWithText(FilledButton, 'Move to Recycle Bin'));
      await tester.pumpAndSettle();

      // No "permanent" on the wire at all.
      expect(fake.argsOf('file.delete').single, {'path': '$_documents\\notes.txt'});
    });

    testWidgets('a permanent delete takes a deliberate extra step', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'notes.txt');
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      expect(find.widgetWithText(FilledButton, 'Delete permanently'), findsNothing);

      await tester.tap(find.text('Delete permanently instead'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Delete permanently'));
      await tester.pumpAndSettle();

      expect(
        fake.argsOf('file.delete').single,
        {'path': '$_documents\\notes.txt', 'permanent': true},
      );
    });

    testWidgets('cancelling a delete sends nothing', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'notes.txt');
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(fake.argsOf('file.delete'), isEmpty);
    });

    testWidgets('new folder validates the name before asking the PC', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('New'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('New folder'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), r'bad\name');
      await tester.pumpAndSettle();

      expect(find.textContaining('cannot contain'), findsOneWidget);
      expect(tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Create')).onPressed, isNull);

      await tester.enterText(find.byType(TextField), 'Reports ');
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Create'));
      await tester.pumpAndSettle();

      expect(
        fake.argsOf('file.create').single,
        {'parent': _documents, 'name': 'Reports', 'isDirectory': true},
      );
    });

    testWidgets('new file sends isDirectory false', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('New'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('New file'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), 'todo.txt');
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Create'));
      await tester.pumpAndSettle();

      expect(
        fake.argsOf('file.create').single,
        {'parent': _documents, 'name': 'todo.txt', 'isDirectory': false},
      );
    });

    testWidgets('rename sends the path and the new name, and shows a refusal', (tester) async {
      final fake = _FakeConnection();
      fake.answers[Commands.fileRename] =
          (_) => _refused(ErrorCodes.accessDenied, 'The file is open in another program.');

      await _pump(tester, connection: fake);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'notes.txt');
      await tester.tap(find.text('Rename'));
      await tester.pumpAndSettle();

      await tester.enterText(find.byType(TextField), 'ideas.txt');
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Rename'));
      await tester.pumpAndSettle();

      expect(
        fake.argsOf('file.rename').single,
        {'path': '$_documents\\notes.txt', 'newName': 'ideas.txt'},
      );
      expect(find.text('The file is open in another program.'), findsOneWidget);
    });

    testWidgets('copy is finished by browsing to a folder and pasting there', (tester) async {
      final fake = _FakeConnection();
      fake.answers[Commands.fileCopy] = (_) => _ok({
            'entry': {'name': 'notes (2).txt', 'path': '$_projects\\notes (2).txt', 'isDirectory': false},
          });

      await _pump(tester, connection: fake);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'notes.txt');
      await tester.tap(find.text('Copy to…'));
      await tester.pumpAndSettle();

      expect(find.text('Copying notes.txt'), findsOneWidget);

      await tester.tap(find.text('Projects'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Paste here'));
      await tester.pumpAndSettle();

      expect(
        fake.argsOf('file.copy').single,
        {'path': '$_documents\\notes.txt', 'destination': _projects},
      );

      // The PC chose another name, and the user is told which.
      expect(find.textContaining('notes (2).txt'), findsOneWidget);
      expect(find.text('Copying notes.txt'), findsNothing);
    });

    testWidgets('move will not drop a file back where it already is', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'notes.txt');
      await tester.tap(find.text('Move to…'));
      await tester.pumpAndSettle();

      FilledButton moveHere() =>
          tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Move here'));

      expect(moveHere().onPressed, isNull);
      expect(find.text('It is already in this folder.'), findsOneWidget);

      await tester.tap(find.text('Projects'));
      await tester.pumpAndSettle();

      expect(moveHere().onPressed, isNotNull);

      await tester.tap(find.widgetWithText(FilledButton, 'Move here'));
      await tester.pumpAndSettle();

      expect(
        fake.argsOf('file.move').single,
        {'path': '$_documents\\notes.txt', 'destination': _projects},
      );
    });

    testWidgets('a folder cannot be moved into itself', (tester) async {
      final fake = await _pump(tester);
      await tester.tap(find.text('Documents'));
      await tester.pumpAndSettle();

      await _openActions(tester, 'Projects');
      await tester.tap(find.text('Move to…'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Projects'));
      await tester.pumpAndSettle();

      expect(
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Move here')).onPressed,
        isNull,
      );
      expect(find.text('A folder cannot be put inside itself.'), findsOneWidget);
      expect(fake.argsOf('file.move'), isEmpty);
    });
  });
}
