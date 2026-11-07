import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/host/input_mapper.dart';
import 'package:remote_control_app/host/paired_viewer_store.dart';
import 'package:remote_control_app/protocol/input_messages.dart';
import 'package:remote_control_app/protocol/protocol.dart';

import 'host_testing.dart';

void main() {
  const mapper = PhoneInputMapper();
  const screen = Size(1080, 2400);

  group('coordinates', () {
    test('a viewer\'s 0..1 point lands on the pixel it names', () {
      expect(mapper.toPixels(0, 0, screen), Offset.zero);
      expect(mapper.toPixels(1, 1, screen), const Offset(1079, 2399));
      expect(mapper.toPixels(0.5, 0.25, screen), const Offset(540, 600));
    });

    test('out-of-range values are clamped, as on the PC; non-finite ones are refused', () {
      expect(mapper.toPixels(1.5, -0.2, screen), const Offset(1079, 0));
      expect(mapper.toPixels(double.nan, 0.5, screen), isNull);
      expect(mapper.toPixels(0.5, double.infinity, screen), isNull);
      expect(mapper.toPixels(null, 0.5, screen), isNull);
    });

    test('the landscape screen maps with its own size', () {
      expect(mapper.toPixels(1, 1, const Size(2400, 1080)), const Offset(2399, 1079));
    });
  });

  group('scrolling', () {
    test('content follows the finger: a downward drag (dy > 0) swipes down', () {
      final swipe = mapper.scrollToSwipe(0, 240, const Offset(540, 1200), screen)!;
      expect(swipe.from, const Offset(540, 1200));
      expect(swipe.to.dy, greaterThan(1200));
      expect(swipe.to.dx, 540);
    });

    test('a rightward drag arrives as negative dx and swipes right', () {
      final swipe = mapper.scrollToSwipe(-120, 0, const Offset(540, 1200), screen)!;
      expect(swipe.to.dx, greaterThan(540));
    });

    test('a swipe never leaves the screen, and nothing is nothing', () {
      final swipe = mapper.scrollToSwipe(0, 120000, const Offset(540, 2390), screen)!;
      expect(swipe.to.dy, 2399);
      expect(mapper.scrollToSwipe(0, 0, const Offset(540, 1200), screen), isNull);
    });
  });

  group('keys', () {
    test('the keys a phone has a meaning for', () {
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.escape), PhoneKeyAction.back);
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.back), PhoneKeyAction.deleteBackward);
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.enter), PhoneKeyAction.enter);
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.win), PhoneKeyAction.home);
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.left), PhoneKeyAction.moveLeft);
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.volumeUp), PhoneKeyAction.volumeUp);
    });

    test('modifiers and function keys mean nothing, and are not errors', () {
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.control), isNull);
      expect(PhoneInputMapper.forVirtualKey(VirtualKeys.function(5)), isNull);
    });

    test('space types a space', () {
      expect(PhoneInputMapper.textForVirtualKey(VirtualKeys.space), ' ');
      expect(PhoneInputMapper.textForVirtualKey(VirtualKeys.enter), isNull);
    });
  });

  group('the drag tracker', () {
    test('a press, moves and a release make one continuous stroke', () {
      final drag = DragTracker();

      expect(drag.press(const Offset(100, 100)), const Offset(100, 100));
      final a = drag.move(const Offset(120, 110))!;
      final b = drag.move(const Offset(150, 130))!;

      expect(a.from, const Offset(100, 100));
      expect(a.to, const Offset(120, 110));
      expect(b.from, const Offset(120, 110), reason: 'Each segment starts where the last ended.');
      expect(drag.release(), isTrue);
      expect(drag.release(), isFalse);
    });

    test('moves with the button up only remember where the pointer is', () {
      final drag = DragTracker();
      expect(drag.move(const Offset(10, 10)), isNull);
      expect(drag.press(null), const Offset(10, 10));
    });

    test('a press with nowhere to land is refused', () {
      expect(DragTracker().press(null), isNull);
    });
  });

  group('the gateway', () {
    late HostRig rig;
    late TestViewer viewer;

    setUp(() async {
      rig = HostRig();
      await rig.store.save(pairedViewer('fp-a', permissions: PairedViewer.allPermissions));
      viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();
    });

    tearDown(() => rig.dispose());

    test('a click is a tap where the viewer touched', () async {
      final response = await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, x: 0.5, y: 0.5));
      expect(response.ok, isTrue);
      expect(rig.platform.log, ['tap 540,1200 40ms']);
    });

    test('a double click is two taps', () async {
      await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, count: 2, x: 0, y: 0));
      expect(rig.platform.log, ['tap 0,0 40ms', 'tap 0,0 40ms']);
    });

    test('a right click is a long press', () async {
      await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.right, x: 0.5, y: 0.5));
      expect(rig.platform.log, ['tap 540,1200 600ms']);
    });

    test('button down, moves and button up are one drag', () async {
      await viewer.send(InputCommands.mouseButton, args: InputArgs.button(MouseButton.left, down: true, x: 0.1, y: 0.1));
      await viewer.send(InputCommands.mouseMove, args: InputArgs.moveTo(0.2, 0.1));
      await viewer.send(InputCommands.mouseMove, args: InputArgs.moveTo(0.3, 0.1));
      await viewer.send(InputCommands.mouseButton, args: InputArgs.button(MouseButton.left, down: false));

      expect(rig.platform.log, [
        'dragStart 108,240',
        'dragMove 108,240->216,240',
        'dragMove 216,240->324,240',
        'dragEnd',
      ]);
    });

    test('moves with nothing held touch nothing', () async {
      await viewer.send(InputCommands.mouseMove, args: InputArgs.moveTo(0.2, 0.1));
      expect(rig.platform.log, isEmpty);
    });

    test('a scroll becomes a swipe at the touch point', () async {
      await viewer.send(InputCommands.scroll, args: InputArgs.scroll(0, 120, x: 0.5, y: 0.5));
      expect(rig.platform.log.single, startsWith('swipe 540,1200->540,'));
    });

    test('text is typed; keys act on release only', () async {
      await viewer.send(InputCommands.text, args: InputArgs.text('hi'));
      await viewer.send(InputCommands.key, args: InputArgs.key(VirtualKeys.back, down: true));
      await viewer.send(InputCommands.key, args: InputArgs.key(VirtualKeys.back, down: false));
      await viewer.send(InputCommands.key, args: InputArgs.key(VirtualKeys.control, down: false));

      expect(rig.platform.log, ['text hi', 'key deleteBackward']);
    });

    test('phone navigation goes straight through', () async {
      final response = await viewer.send(Commands.phoneNav, args: InputArgs.phoneNav(PhoneNavActions.home));
      expect(response.ok, isTrue);
      expect(rig.platform.log, ['nav home']);

      final bad = await viewer.send(Commands.phoneNav, args: InputArgs.phoneNav('reboot'));
      expect(bad.error!.code, ErrorCodes.invalidArguments);
    });

    test('release_all lifts a held finger, and so does disconnecting', () async {
      await viewer.send(InputCommands.mouseButton, args: InputArgs.button(MouseButton.left, down: true, x: 0.1, y: 0.1));
      await viewer.send(InputCommands.releaseAll);
      expect(rig.platform.log, ['dragStart 108,240', 'dragEnd']);

      rig.platform.log.clear();
      await viewer.send(InputCommands.mouseButton, args: InputArgs.button(MouseButton.left, down: true, x: 0.1, y: 0.1));
      await viewer.disconnect();
      await pumpEventQueue();
      await rig.input.releaseAll(viewer.session!.connectionId);
      expect(rig.platform.log, ['dragStart 108,240', 'dragEnd']);
    });

    test('with the accessibility service off, input is refused and explained', () async {
      rig.platform.accessibility = false;
      final response = await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, x: 0.5, y: 0.5));
      expect(response.ok, isFalse);
      expect(response.error!.code, ErrorCodes.sessionUnavailable);
      expect(rig.platform.log, isEmpty);
    });

    test('too much text is refused', () async {
      final response = await viewer.send(InputCommands.text, args: {'text': 'x' * 2001});
      expect(response.error!.code, ErrorCodes.invalidArguments);
    });
  });
}
