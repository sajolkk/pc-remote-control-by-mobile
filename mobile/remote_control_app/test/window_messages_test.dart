import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/protocol/protocol.dart';
import 'package:remote_control_app/protocol/window_messages.dart';

void main() {
  group('window commands', () {
    test('use the names the PC registers', () {
      // Copied from the wire contract. A typo here does not fail loudly: the PC just answers
      // E_UNKNOWN_COMMAND and the screen looks broken.
      expect(Commands.windowList, 'window.list');
      expect(Commands.windowAction, 'window.action');
      expect(Capabilities.windows, 'windows');
    });
  });

  group('WindowInfo', () {
    test('parses window.list exactly as the PC serializes it', () {
      final json = jsonDecode('''
        {"windows":[
          {"id":"0x000A04C2","title":"Report.docx - Word","processName":"WINWORD",
           "isMinimized":false,"isMaximized":true,"isForeground":true},
          {"id":"0x00120B10","title":"Downloads","processName":"explorer",
           "isMinimized":true,"isMaximized":false,"isForeground":false}
        ]}
      ''') as Map<String, dynamic>;

      final windows = WindowInfo.listFromJson(json);

      expect(windows, hasLength(2));

      expect(windows[0].id, '0x000A04C2');
      expect(windows[0].title, 'Report.docx - Word');
      expect(windows[0].processName, 'WINWORD');
      expect(windows[0].isMinimized, isFalse);
      expect(windows[0].isMaximized, isTrue);
      expect(windows[0].isForeground, isTrue);

      expect(windows[1].id, '0x00120B10');
      expect(windows[1].isMinimized, isTrue);
      expect(windows[1].isForeground, isFalse);
    });

    test('keeps the order the PC sent, which puts the foreground window first', () {
      final windows = WindowInfo.listFromJson({
        'windows': [
          {'id': 'b', 'title': 'Zebra', 'processName': 'z', 'isForeground': true},
          {'id': 'a', 'title': 'Apple', 'processName': 'a'},
        ],
      });

      expect(windows.map((window) => window.id), ['b', 'a']);
    });

    test('survives missing fields and drops entries that cannot be acted on', () {
      final windows = WindowInfo.listFromJson({
        'windows': [
          {'id': 'w1'},
          {'title': 'No id, so nothing could be done with it'},
          'not an object',
        ],
      });

      expect(windows, hasLength(1));
      expect(windows.single.title, isEmpty);
      expect(windows.single.isMinimized, isFalse);
      expect(windows.single.isMaximized, isFalse);
      expect(windows.single.isForeground, isFalse);
    });

    test('an empty or malformed result is an empty list, not a crash', () {
      expect(WindowInfo.listFromJson(const {}), isEmpty);
      expect(WindowInfo.listFromJson(const {'windows': 'nope'}), isEmpty);
    });

    test('a window without a title is called by its process', () {
      expect(
        const WindowInfo(id: '1', title: '  ', processName: 'notepad').displayTitle,
        'notepad',
      );
      expect(const WindowInfo(id: '1', title: '', processName: '').displayTitle, isNotEmpty);
      expect(const WindowInfo(id: '1', title: 'Notes', processName: 'notepad').displayTitle, 'Notes');
    });
  });

  group('WindowAction', () {
    test('sends the action words the PC accepts', () {
      expect(
        WindowAction.values.map((action) => action.wireName),
        ['focus', 'minimize', 'maximize', 'restore', 'close'],
      );
    });

    test('builds window.action args with the contract field names', () {
      for (final action in WindowAction.values) {
        expect(
          jsonEncode(action.argsFor('0x000A04C2')),
          '{"windowId":"0x000A04C2","action":"${action.wireName}"}',
        );
      }
    });
  });

  group('WindowActionResult', () {
    test('parses a refusal with the reason the PC gave', () {
      final result = WindowActionResult.fromJson(
        jsonDecode('{"succeeded":false,"detail":"Windows refused the focus change."}')
            as Map<String, dynamic>,
      );

      expect(result.succeeded, isFalse);
      expect(result.detail, 'Windows refused the focus change.');
    });

    test('detail is optional', () {
      final result = WindowActionResult.fromJson(const {'succeeded': true});

      expect(result.succeeded, isTrue);
      expect(result.detail, isNull);
    });
  });
}
