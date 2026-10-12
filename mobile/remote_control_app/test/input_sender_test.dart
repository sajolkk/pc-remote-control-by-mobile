import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/input/input_sender.dart';
import 'package:remote_control_app/protocol/input_messages.dart';
import 'package:remote_control_app/protocol/protocol.dart';

import 'input_testing.dart';

// testWidgets is used for its fake clock: the sender's rate limit is a timer, and these tests
// step that timer exactly.
void main() {
  const frame = Duration(milliseconds: 16);

  group('pointer moves', () {
    testWidgets('the first goes at once; the rest of the frame collapses into the latest', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.moveTo(0.1, 0.1);
      expect(transport.log, ['input.mouse_move {x: 0.1, y: 0.1}'], reason: 'no latency on the first move');

      sender.moveTo(0.2, 0.2);
      sender.moveTo(0.3, 0.3);
      sender.moveTo(0.4, 0.4, monitorId: 'mon-a');
      expect(transport.sent, hasLength(1), reason: 'still inside the 16 ms window');

      await tester.pump(frame);
      expect(transport.log.last, 'input.mouse_move {x: 0.4, y: 0.4, monitorId: mon-a}');
      expect(transport.sent, hasLength(2), reason: '0.2 and 0.3 were never sent');

      // Nothing pending: the timer stops instead of ticking forever.
      await tester.pump(frame);
      await tester.pump(frame);
      expect(transport.sent, hasLength(2));

      sender.dispose();
    });

    testWidgets('are capped at about sixty a second however fast they arrive', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      // A 240 Hz digitiser for one second.
      for (var i = 0; i < 240; i++) {
        sender.moveTo(i / 240, 0.5);
        await tester.pump(const Duration(microseconds: 4167));
      }

      expect(transport.sent.length, inInclusiveRange(55, 64));
      sender.dispose();
    });

    testWidgets('relative moves add up instead of being dropped', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.moveBy(1, 1);
      sender.moveBy(2, -5);
      sender.moveBy(3, 1);
      await tester.pump(frame);

      expect(transport.log, ['input.mouse_move {dx: 1, dy: 1}', 'input.mouse_move {dx: 5, dy: -4}']);
      sender.dispose();
    });

    testWidgets('never wait for the PC: a slow answer stalls nothing', (tester) async {
      final transport = RecordingTransport()..autoAnswer = false;
      final sender = InputSender(transport.send);

      for (var i = 0; i < 10; i++) {
        sender.moveTo(i / 10, 0);
        await tester.pump(frame);
      }
      sender.click(MouseButton.left);

      expect(transport.unanswered, 11, reason: 'ten moves and a click, all written, none answered');
      expect(transport.commands.last, InputCommands.mouseClick);

      transport.answerAll();
      sender.dispose();
    });
  });

  group('ordering', () {
    testWidgets('a pending move is flushed before a button, so the click lands where it was aimed',
        (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.moveTo(0.1, 0.1);
      sender.moveTo(0.9, 0.9); // pending
      sender.button(MouseButton.left, down: false);

      expect(transport.log, [
        'input.mouse_move {x: 0.1, y: 0.1}',
        'input.mouse_move {x: 0.9, y: 0.9}',
        'input.mouse_button {button: left, down: false}',
      ]);

      // The flushed move is not sent a second time when the timer fires.
      await tester.pump(frame);
      expect(transport.sent, hasLength(3));
      sender.dispose();
    });

    testWidgets('keys, text and shortcuts keep their place among moves too', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.moveBy(1, 0);
      sender.moveBy(4, 0); // pending
      sender.tapKey(VirtualKeys.enter);
      sender.moveBy(2, 2); // pending again
      sender.text('hi');
      sender.shortcut([VirtualKeys.control, 0x53]);

      expect(transport.log, [
        'input.mouse_move {dx: 1, dy: 0}',
        'input.mouse_move {dx: 4, dy: 0}',
        'input.key {vk: 13, down: true}',
        'input.key {vk: 13, down: false}',
        'input.mouse_move {dx: 2, dy: 2}',
        'input.text {text: hi}',
        'input.shortcut {keys: [17, 83]}',
      ]);
      await tester.pump(frame);
      sender.dispose();
    });

    testWidgets('a scroll after a move does not overtake it, and scrolls accumulate', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.moveTo(0.5, 0.5);
      sender.moveTo(0.6, 0.6); // pending
      sender.scroll(0, 120, x: 0.6, y: 0.6);
      sender.scroll(0, 240, x: 0.6, y: 0.6);
      await tester.pump(frame);

      expect(transport.log, [
        'input.mouse_move {x: 0.5, y: 0.5}',
        'input.mouse_move {x: 0.6, y: 0.6}',
        'input.scroll {dx: 0, dy: 360, x: 0.6, y: 0.6}',
      ]);
      sender.dispose();
    });
  });

  group('text', () {
    testWidgets('line endings are normalised and long text is split', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.text('a\r\nb');
      expect(transport.sent.single.$2, {'text': 'a\nb'});

      transport.clear();
      sender.text('x' * 4500);
      expect(transport.sent.map((entry) => (entry.$2!['text'] as String).length), [2000, 2000, 500]);
      sender.dispose();
    });
  });

  group('faults', () {
    testWidgets('a refusal repeated on every move of a drag is reported once', (tester) async {
      final transport = RecordingTransport()..failWith = ErrorCodes.desktopLocked;
      final faults = <InputFault>[];
      final sender = InputSender(transport.send, onFault: faults.add);

      for (var i = 0; i < 30; i++) {
        sender.moveTo(i / 30, 0);
        await tester.pump(frame);
      }

      expect(faults, hasLength(1));
      expect(faults.single.code, ErrorCodes.desktopLocked);
      expect(faults.single.message, contains('locked'));

      // Unlocked: input works again. Locked a second time: that is news, and is said again.
      transport.failWith = null;
      sender.click(MouseButton.left);
      await tester.pump();

      transport.failWith = ErrorCodes.desktopLocked;
      sender.click(MouseButton.left);
      await tester.pump();

      expect(faults, hasLength(2));
      sender.dispose();
    });

    testWidgets('each kind of refusal has its own explanation', (tester) async {
      final transport = RecordingTransport();
      final faults = <InputFault>[];
      final sender = InputSender(transport.send, onFault: faults.add);

      for (final code in [ErrorCodes.permissionDenied, ErrorCodes.secureDesktop, ErrorCodes.sessionUnavailable]) {
        transport.failWith = code;
        sender.click(MouseButton.left);
        await tester.pump();
      }

      expect(faults.map((fault) => fault.code), [
        ErrorCodes.permissionDenied,
        ErrorCodes.secureDesktop,
        ErrorCodes.sessionUnavailable,
      ]);
      expect(faults[0].message, contains('Enable it on the PC'));
      expect(faults[1].message, contains('secure prompt'));
      sender.dispose();
    });

    testWidgets('not being connected is not a fault', (tester) async {
      final faults = <InputFault>[];
      final sender = InputSender((command, {args}) async => null, onFault: faults.add);

      sender.click(MouseButton.left);
      await tester.pump();

      expect(faults, isEmpty, reason: 'the connection banner already says so');
      sender.dispose();
    });
  });

  group('release_all', () {
    testWidgets('is sent with no args, drops the pending move, and is skipped when nothing was sent',
        (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.releaseAll();
      expect(transport.sent, isEmpty, reason: 'nothing is held, so there is nothing to release');

      sender.button(MouseButton.left, down: true, x: 0.5, y: 0.5);
      sender.moveTo(0.6, 0.6);
      sender.moveTo(0.7, 0.7); // pending, and now pointless
      sender.releaseAll();

      expect(transport.commands, [InputCommands.mouseButton, InputCommands.mouseMove, InputCommands.releaseAll]);
      expect(transport.sent.last.$2, isNull);

      await tester.pump(frame);
      expect(transport.sent, hasLength(3), reason: 'the dropped move does not come back');

      sender.releaseAll();
      expect(transport.sent, hasLength(3), reason: 'already released');
    });

    testWidgets('dispose releases, and nothing is sent afterwards', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);

      sender.key(VirtualKeys.shift, down: true);
      sender.dispose();
      expect(transport.commands, [InputCommands.key, InputCommands.releaseAll]);

      sender.moveTo(0.5, 0.5);
      sender.click(MouseButton.left);
      sender.text('late');
      await tester.pump(frame);
      expect(transport.sent, hasLength(2));
    });

    testWidgets('a refused release is not reported: control is already over', (tester) async {
      final transport = RecordingTransport();
      final faults = <InputFault>[];
      final sender = InputSender(transport.send, onFault: faults.add);

      sender.click(MouseButton.left);
      await tester.pump();

      transport.failWith = ErrorCodes.permissionDenied;
      sender.releaseAll();
      await tester.pump();

      expect(faults, isEmpty);
    });
  });
}
