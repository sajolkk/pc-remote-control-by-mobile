import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/input/input_sender.dart';
import 'package:remote_control_app/data/input/keyboard_translator.dart';
import 'package:remote_control_app/features/remote_desktop/control_gestures.dart';
import 'package:remote_control_app/features/remote_desktop/control_surface.dart';
import 'package:remote_control_app/features/remote_desktop/pointer_mapping.dart';
import 'package:remote_control_app/features/remote_desktop/remote_keyboard.dart';
import 'package:remote_control_app/protocol/input_messages.dart';

import 'input_testing.dart';

void main() {
  group('ControlSurface', () {
    // A 400 × 800 viewport showing a 16:9 picture: the picture is 400 × 225, from y = 287.5.
    const viewport = Size(400, 800);
    const video = Size(1920, 1080);

    Future<void> pumpSurface(
      WidgetTester tester, {
      required InputSender sender,
      required TransformationController transform,
      required bool enabled,
      PointerStyle style = PointerStyle.direct,
    }) {
      // A phone-shaped screen, one logical pixel to the physical pixel.
      tester.view.physicalSize = viewport;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      return tester.pumpWidget(
          MaterialApp(
            home: Align(
              alignment: Alignment.topLeft,
              child: SizedBox.fromSize(
                size: viewport,
                child: ControlSurface(
                  transform: transform,
                  sender: sender,
                  enabled: enabled,
                  style: style,
                  videoSize: () => video,
                  monitorId: 'mon-a',
                  child: const ColoredBox(color: Colors.black),
                ),
              ),
            ),
          ),
        );
    }

    testWidgets('a real tap on the picture clicks the matching point of the monitor', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);
      final transform = TransformationController();
      await pumpSurface(tester, sender: sender, transform: transform, enabled: true);

      await tester.tapAt(const Offset(100, 287.5 + 56.25));
      await tester.pump(const Duration(milliseconds: 400));

      expect(transport.sent.single.$1, InputCommands.mouseClick);
      expect(transport.sent.single.$2, {'button': 'left', 'count': 1, 'x': 0.25, 'y': 0.25, 'monitorId': 'mon-a'});

      sender.dispose();
    });

    testWidgets('a tap on the bars clicks nothing', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);
      await pumpSurface(tester, sender: sender, transform: TransformationController(), enabled: true);

      await tester.tapAt(const Offset(200, 100));
      await tester.pump(const Duration(milliseconds: 400));

      expect(transport.sent, isEmpty);
    });

    testWidgets('a tap goes through the zoom to the right place', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);
      // Zoomed 2× onto the picture's bottom-right quarter (see the mapping tests).
      final transform = TransformationController(const ViewTransform(2, Offset(-400, -800)).toMatrix());
      await pumpSurface(tester, sender: sender, transform: transform, enabled: true);

      await tester.tapAt(const Offset(200, 112.5));
      await tester.pump(const Duration(milliseconds: 400));

      expect(transport.sent.single.$2, containsPair('x', 0.75));
      expect(transport.sent.single.$2, containsPair('y', 0.75));
      sender.dispose();
    });

    testWidgets('view-only: touches send nothing, pinch-zoom works and a double tap resets it', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);
      final transform = TransformationController();
      await pumpSurface(tester, sender: sender, transform: transform, enabled: false);

      // Pinch out with two fingers.
      final one = await tester.startGesture(const Offset(180, 400));
      final two = await tester.startGesture(const Offset(220, 400));
      await tester.pump();
      await one.moveTo(const Offset(100, 400));
      await two.moveTo(const Offset(300, 400));
      await tester.pump();
      await one.up();
      await two.up();
      await tester.pumpAndSettle();

      expect(ViewTransform.fromMatrix(transform.value).isZoomed, isTrue);

      await tester.tapAt(const Offset(200, 400));
      await tester.pump(const Duration(milliseconds: 100));
      await tester.tapAt(const Offset(200, 400));
      await tester.pumpAndSettle();

      expect(transform.value, Matrix4.identity());
      expect(transport.sent, isEmpty);
    });

    testWidgets('control on: a pinch zooms the view without the viewer\'s own gestures', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);
      final transform = TransformationController();
      await pumpSurface(tester, sender: sender, transform: transform, enabled: true);

      final one = await tester.startGesture(const Offset(180, 400));
      final two = await tester.startGesture(const Offset(220, 400));
      for (var step = 1; step <= 20; step++) {
        await one.moveTo(Offset(180.0 - 4 * step, 400));
        await two.moveTo(Offset(220.0 + 4 * step, 400));
      }
      await one.up();
      await two.up();
      await tester.pump(const Duration(seconds: 1));

      final view = ViewTransform.fromMatrix(transform.value);
      expect(view.scale, greaterThan(1.5));
      // Zoomed about the fingers: the middle of the viewport is still the middle.
      final centre = (const Offset(200, 400) - view.offset) / view.scale;
      expect(centre.dx, closeTo(200, 4));
      expect(centre.dy, closeTo(400, 4));
      expect(transport.sent, isEmpty, reason: 'zooming is the phone\'s business, not the PC\'s');
    });

    testWidgets('switching control off mid-drag releases the button', (tester) async {
      final transport = RecordingTransport();
      final sender = InputSender(transport.send);
      final transform = TransformationController();
      await pumpSurface(tester, sender: sender, transform: transform, enabled: true);

      final finger = await tester.startGesture(const Offset(100, 350));
      await tester.pump(ControlGestures.longPressTimeout + const Duration(milliseconds: 1));
      await finger.moveTo(const Offset(200, 400));
      expect(transport.commands.first, InputCommands.mouseButton);

      await pumpSurface(tester, sender: sender, transform: transform, enabled: false);

      expect(transport.sent.last.$2, {'button': 'left', 'down': false});
      await finger.up();
      sender.dispose();
    });
  });

  group('SpecialKeysBar', () {
    Future<(RecordingTransport, RemoteKeyboardController, List<String>)> pumpBar(WidgetTester tester) async {
      final transport = RecordingTransport();
      final controller = RemoteKeyboardController(InputSender(transport.send));
      final explained = <String>[];
      addTearDown(controller.dispose);

      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Column(
              children: [
                RemoteKeyboardField(controller: controller),
                SpecialKeysBar(controller: controller, onClose: () {}, onExplain: explained.add),
              ],
            ),
          ),
        ),
      );

      return (transport, controller, explained);
    }

    Future<void> tapKey(WidgetTester tester, String label) async {
      final finder = find.text(label);
      await tester.scrollUntilVisible(finder, 200, scrollable: find.descendant(of: find.byType(ListView), matching: find.byType(Scrollable)));
      await tester.tap(finder);
      await tester.pump();
    }

    testWidgets('a key is pressed and released', (tester) async {
      final (transport, _, _) = await pumpBar(tester);

      await tapKey(tester, 'Esc');

      expect(transport.log, ['input.key {vk: 27, down: true}', 'input.key {vk: 27, down: false}']);
    });

    testWidgets('an armed Ctrl turns the next typed character into a shortcut, once', (tester) async {
      final (transport, controller, _) = await pumpBar(tester);

      await tapKey(tester, 'Ctrl');
      expect(controller.isArmed(VirtualKeys.control), isTrue);
      expect(transport.sent, isEmpty, reason: 'arming sends nothing by itself');

      controller.onFieldChanged('${KeyboardTranslator.sentinel}c');
      expect(transport.log, ['input.shortcut {keys: [17, 67]}']);
      expect(controller.isArmed(VirtualKeys.control), isFalse);
      expect(controller.field.text, KeyboardTranslator.sentinel, reason: 'the "c" is not left in the buffer');

      controller.onFieldChanged('${KeyboardTranslator.sentinel}c');
      expect(transport.log.last, 'input.text {text: c}');
    });

    testWidgets('typing into the hidden field reaches the PC as text, Backspace and Enter', (tester) async {
      final (transport, controller, _) = await pumpBar(tester);

      await tester.showKeyboard(find.byType(EditableText));
      tester.testTextInput.updateEditingValue(const TextEditingValue(text: '${KeyboardTranslator.sentinel}hi'));
      tester.testTextInput.updateEditingValue(const TextEditingValue(text: '${KeyboardTranslator.sentinel}h'));
      tester.testTextInput.updateEditingValue(const TextEditingValue(text: '${KeyboardTranslator.sentinel}h\n'));
      await tester.pump();

      expect(transport.log, [
        'input.text {text: hi}',
        'input.key {vk: 8, down: true}',
        'input.key {vk: 8, down: false}',
        'input.key {vk: 13, down: true}',
        'input.key {vk: 13, down: false}',
      ]);
      expect(controller.focus.hasFocus, isTrue, reason: 'Enter must not close the keyboard');
    });

    testWidgets('every one-tap shortcut sends the keys on its label', (tester) async {
      final (transport, _, _) = await pumpBar(tester);

      for (final (label, _) in SpecialKeysBar.shortcuts) {
        await tapKey(tester, label);
      }

      expect(transport.log, [
        'input.shortcut {keys: [18, 9]}', // Alt+Tab
        'input.shortcut {keys: [18, 115]}', // Alt+F4
        'input.shortcut {keys: [91, 68]}', // Win+D
        'input.shortcut {keys: [17, 67]}', // Ctrl+C
        'input.shortcut {keys: [17, 86]}', // Ctrl+V
        'input.shortcut {keys: [17, 88]}', // Ctrl+X
        'input.shortcut {keys: [17, 90]}', // Ctrl+Z
        'input.shortcut {keys: [17, 65]}', // Ctrl+A
        'input.shortcut {keys: [17, 83]}', // Ctrl+S
        'input.key {vk: 91, down: true}', // Start
        'input.key {vk: 91, down: false}',
        'input.shortcut {keys: [17, 16, 27]}', // Ctrl+Shift+Esc
      ]);
    });

    testWidgets('function keys and navigation use the Windows codes', (tester) async {
      final (transport, _, _) = await pumpBar(tester);

      await tapKey(tester, 'PgDn');
      await tapKey(tester, 'F12');

      expect(transport.log, [
        'input.key {vk: 34, down: true}',
        'input.key {vk: 34, down: false}',
        'input.key {vk: 123, down: true}',
        'input.key {vk: 123, down: false}',
      ]);
    });

    testWidgets('Ctrl+Alt+Del sends nothing and says why', (tester) async {
      final (transport, _, explained) = await pumpBar(tester);

      await tapKey(tester, 'Ctrl+Alt+Del');

      expect(transport.sent, isEmpty);
      expect(explained.single, contains('Windows does not let any app send Ctrl+Alt+Del'));
    });
  });
}
