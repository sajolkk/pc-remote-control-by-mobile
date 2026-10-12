import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/protocol/input_messages.dart';

void main() {
  group('InputArgs', () {
    // Every expectation below is the JSON the PC will parse, written out in full: the field names
    // are the contract, and a renamed field would otherwise fail silently as "no position given".
    String json(Map<String, dynamic> args) => jsonEncode(args);

    test('mouse_move, absolute', () {
      expect(json(InputArgs.moveTo(0.25, 0.75, monitorId: 'mon-a')), '{"x":0.25,"y":0.75,"monitorId":"mon-a"}');
      expect(json(InputArgs.moveTo(0.5, 0.5)), '{"x":0.5,"y":0.5}', reason: 'no monitor is omitted, not null');
    });

    test('mouse_move, relative, is integer pixels with no position', () {
      expect(json(InputArgs.moveBy(-3, 12)), '{"dx":-3,"dy":12}');
    });

    test('mouse_button', () {
      expect(
        json(InputArgs.button(MouseButton.left, down: true, x: 0.1, y: 0.2, monitorId: 'mon-a')),
        '{"button":"left","down":true,"x":0.1,"y":0.2,"monitorId":"mon-a"}',
      );
      expect(json(InputArgs.button(MouseButton.right, down: false)), '{"button":"right","down":false}');
      expect(json(InputArgs.button(MouseButton.middle, down: true)), '{"button":"middle","down":true}');
    });

    test('mouse_click', () {
      expect(
        json(InputArgs.click(MouseButton.left, x: 1, y: 0, monitorId: 'mon-a')),
        '{"button":"left","count":1,"x":1.0,"y":0.0,"monitorId":"mon-a"}',
      );
      expect(json(InputArgs.click(MouseButton.left, count: 2)), '{"button":"left","count":2}');
      expect(json(InputArgs.click(MouseButton.right)), '{"button":"right","count":1}');
      expect(InputArgs.click(MouseButton.left, count: 7)['count'], 2, reason: 'the PC accepts 1 or 2 only');
    });

    test('scroll', () {
      expect(
        json(InputArgs.scroll(0, 120, x: 0.5, y: 0.5, monitorId: 'mon-a')),
        '{"dx":0,"dy":120,"x":0.5,"y":0.5,"monitorId":"mon-a"}',
      );
      expect(json(InputArgs.scroll(-240, 0)), '{"dx":-240,"dy":0}');
    });

    test('key, text and shortcut', () {
      expect(json(InputArgs.key(VirtualKeys.back, down: true)), '{"vk":8,"down":true}');
      expect(json(InputArgs.key(VirtualKeys.back, down: false)), '{"vk":8,"down":false}');
      expect(json(InputArgs.text('héllo 👍')), '{"text":"héllo 👍"}');
      expect(json(InputArgs.shortcut([VirtualKeys.control, 0x43])), '{"keys":[17,67]}');
    });

    test('a position is sent whole or not at all', () {
      // The PC switches on "x and y present". Half a position must never reach it, and a monitor
      // without a position means nothing.
      expect(InputArgs.click(MouseButton.left, x: 0.5), isNot(contains('x')));
      expect(InputArgs.click(MouseButton.left, monitorId: 'mon-a'), isNot(contains('monitorId')));
    });

    test('coordinates are clamped to the monitor and never NaN', () {
      // The PC rejects NaN and infinity outright; a rejected move mid-drag would strand the button.
      expect(InputArgs.moveTo(-0.2, 1.7), {'x': 0.0, 'y': 1.0});
      expect(InputArgs.moveTo(double.nan, double.infinity), {'x': 0.0, 'y': 0.0});
    });

    test('long text is split within the limit without cutting an emoji in half', () {
      final text = '${'a' * 1999}👍${'b' * 10}';
      final chunks = InputArgs.chunkText(text);

      expect(chunks.join(), text);
      expect(chunks.every((chunk) => chunk.length <= InputArgs.maxTextUnits), isTrue);
      expect(chunks.first.length, 1999, reason: 'the pair moves whole into the next chunk');
      expect(chunks[1].startsWith('👍'), isTrue);
      expect(InputArgs.chunkText(''), isEmpty);
    });
  });

  group('VirtualKeys', () {
    test('are the Windows codes the contract lists', () {
      expect(
        [
          VirtualKeys.back,
          VirtualKeys.tab,
          VirtualKeys.enter,
          VirtualKeys.shift,
          VirtualKeys.control,
          VirtualKeys.alt,
          VirtualKeys.escape,
          VirtualKeys.space,
          VirtualKeys.pageUp,
          VirtualKeys.pageDown,
          VirtualKeys.end,
          VirtualKeys.home,
          VirtualKeys.left,
          VirtualKeys.up,
          VirtualKeys.right,
          VirtualKeys.down,
          VirtualKeys.printScreen,
          VirtualKeys.insert,
          VirtualKeys.delete,
          VirtualKeys.win,
        ],
        [8, 9, 13, 16, 17, 18, 27, 32, 33, 34, 35, 36, 37, 38, 39, 40, 44, 45, 46, 91],
      );
      expect(VirtualKeys.function(1), 112);
      expect(VirtualKeys.function(12), 123);
    });

    test('letters and digits map to their keys whatever their case', () {
      expect(VirtualKeys.forCharacter('c'), 67);
      expect(VirtualKeys.forCharacter('C'), 67);
      expect(VirtualKeys.forCharacter('z'), 90);
      expect(VirtualKeys.forCharacter('0'), 48);
      expect(VirtualKeys.forCharacter(' '), 32);
      expect(VirtualKeys.forCharacter('\n'), 13);
    });

    test('characters without a layout-independent key have none', () {
      expect(VirtualKeys.forCharacter('/'), isNull);
      expect(VirtualKeys.forCharacter('é'), isNull);
      expect(VirtualKeys.forCharacter('👍'), isNull);
      expect(VirtualKeys.forCharacter('ab'), isNull);
    });
  });

  group('wire names', () {
    test('the eight commands are spelled as in the contract', () {
      expect(
        [
          InputCommands.mouseMove,
          InputCommands.mouseButton,
          InputCommands.mouseClick,
          InputCommands.scroll,
          InputCommands.key,
          InputCommands.text,
          InputCommands.shortcut,
          InputCommands.releaseAll,
        ],
        [
          'input.mouse_move',
          'input.mouse_button',
          'input.mouse_click',
          'input.scroll',
          'input.key',
          'input.text',
          'input.shortcut',
          'input.release_all',
        ],
      );
      expect(InputCapabilities.mouse, 'input.mouse');
      expect(InputCapabilities.keyboard, 'input.keyboard');
    });

    test('and match the PC', () {
      // The C# source is the authority; when it is present (a full checkout), check against it.
      final csharp = File('../../windows/RemoteAgent.Protocol/CommandNames.cs');
      if (!csharp.existsSync()) {
        markTestSkipped('PC sources not present in this checkout.');
        return;
      }

      final source = csharp.readAsStringSync();
      for (final name in [
        InputCommands.mouseMove,
        InputCommands.mouseButton,
        InputCommands.mouseClick,
        InputCommands.scroll,
        InputCommands.key,
        InputCommands.text,
        InputCommands.shortcut,
        InputCommands.releaseAll,
      ]) {
        expect(source, contains('"$name"'), reason: '$name is not declared on the PC');
      }
    });
  });
}
