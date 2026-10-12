import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/input/input_sender.dart';
import 'package:remote_control_app/features/remote_desktop/control_gestures.dart';
import 'package:remote_control_app/protocol/input_messages.dart';

import 'input_testing.dart';

/// A 100 × 100 picture with a bar below it (y > 100), so positions read directly as percentages.
Offset? _toMonitor(Offset local, {bool clamp = false}) {
  final x = local.dx / 100;
  final y = local.dy / 100;

  if (clamp) return Offset(x.clamp(0.0, 1.0).toDouble(), y.clamp(0.0, 1.0).toDouble());
  return x < 0 || x > 1 || y < 0 || y > 1 ? null : Offset(x, y);
}

class _Rig {
  _Rig(PointerStyle style) {
    sender = InputSender(transport.send);
    gestures = ControlGestures(
      sender,
      style: style,
      toMonitor: _toMonitor,
      monitorId: () => 'mon-a',
      onZoom: (previousFocal, focal, factor) => zooms.add(factor),
      onPan: pans.add,
      takeModifiers: () {
        final taken = modifiers;
        modifiers = const [];
        return taken;
      },
      onLongPress: () => longPresses++,
    );
  }

  final transport = RecordingTransport();
  late final InputSender sender;
  late final ControlGestures gestures;
  final zooms = <double>[];
  final pans = <Offset>[];
  List<int> modifiers = const [];
  var longPresses = 0;

  Duration at(int ms) => Duration(milliseconds: ms);

  /// Moves two fingers by [delta] each, a few pixels at a time and turn about, the way a
  /// digitiser reports them.
  void moveBoth(Offset first, Offset second, Offset delta, {required int from, int steps = 10}) {
    for (var i = 1; i <= steps; i++) {
      gestures.move(1, first + delta * (i / steps), at(from + i * 8));
      gestures.move(2, second + delta * (i / steps), at(from + i * 8));
    }
  }

  List<String> get log => transport.log;
}

/// A test with a fresh rig, which is wound down afterwards so no timer outlives the test.
void _rigTest(String description, PointerStyle style, Future<void> Function(WidgetTester tester, _Rig rig) body) =>
    testWidgets(description, (tester) async {
      final rig = _Rig(style);
      await body(tester, rig);

      rig.gestures.reset();
      rig.sender.dispose();
    });

void main() {
  const frame = Duration(milliseconds: 16);

  group('direct touch', () {
    _rigTest('a tap is a left click where it landed, on the streamed monitor', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(25, 75), rig.at(0));
      rig.gestures.up(1, rig.at(80));

      expect(rig.log, ['input.mouse_click {button: left, count: 1, x: 0.25, y: 0.75, monitorId: mon-a}']);
      rig.gestures.reset();
    });

    _rigTest('a double tap lands both clicks on the same pixel', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(25, 75), rig.at(0));
      rig.gestures.up(1, rig.at(60));
      // The second tap is a few pixels off, as fingers are.
      rig.gestures.down(2, const Offset(31, 70), rig.at(180));
      rig.gestures.up(2, rig.at(240));

      expect(rig.log, [
        'input.mouse_click {button: left, count: 1, x: 0.25, y: 0.75, monitorId: mon-a}',
        'input.mouse_click {button: left, count: 1, x: 0.25, y: 0.75, monitorId: mon-a}',
      ]);
      rig.gestures.reset();
    });

    _rigTest('two taps far apart in time or place are two separate clicks', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(10, 10), rig.at(0));
      rig.gestures.up(1, rig.at(50));
      rig.gestures.down(2, const Offset(12, 10), rig.at(900));
      rig.gestures.up(2, rig.at(950));
      rig.gestures.down(3, const Offset(90, 90), rig.at(1000));
      rig.gestures.up(3, rig.at(1050));

      expect(rig.transport.sent.map((entry) => entry.$2!['x']), [0.1, 0.12, 0.9]);
      rig.gestures.reset();
    });

    _rigTest('holding still is a right click, and lifting afterwards does nothing more', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 40), rig.at(0));
      await tester.pump(ControlGestures.longPressTimeout - const Duration(milliseconds: 1));
      expect(rig.log, isEmpty);

      await tester.pump(const Duration(milliseconds: 2));
      expect(rig.log, ['input.mouse_click {button: right, count: 1, x: 0.4, y: 0.4, monitorId: mon-a}']);
      expect(rig.longPresses, 1);

      rig.gestures.move(1, const Offset(60, 60), rig.at(600));
      rig.gestures.up(1, rig.at(700));
      expect(rig.log, hasLength(1));
    });

    _rigTest('a drag presses where the finger went down, follows it, and releases', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(10, 10), rig.at(0));
      rig.gestures.move(1, const Offset(13, 10), rig.at(10));
      expect(rig.log, isEmpty, reason: 'inside the slop: still a possible tap');

      rig.gestures.move(1, const Offset(30, 10), rig.at(20));
      await tester.pump(frame);
      rig.gestures.move(1, const Offset(50, 20), rig.at(40));
      rig.gestures.move(1, const Offset(60, 30), rig.at(45));
      rig.gestures.up(1, rig.at(60));

      expect(rig.log, [
        'input.mouse_button {button: left, down: true, x: 0.1, y: 0.1, monitorId: mon-a}',
        'input.mouse_move {x: 0.3, y: 0.1, monitorId: mon-a}',
        'input.mouse_move {x: 0.5, y: 0.2, monitorId: mon-a}',
        // The last position is flushed before the release, so the drop lands where the finger was.
        'input.mouse_move {x: 0.6, y: 0.3, monitorId: mon-a}',
        'input.mouse_button {button: left, down: false}',
      ]);

      // The long-press timer died with the drag.
      await tester.pump(const Duration(seconds: 1));
      expect(rig.log, hasLength(5));
    });

    _rigTest('a drag that leaves the picture is pinned to its edge, not dropped', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 90), rig.at(0));
      rig.gestures.move(1, const Offset(50, 150), rig.at(20));
      rig.gestures.up(1, rig.at(40));

      expect(rig.log[1], 'input.mouse_move {x: 0.5, y: 1.0, monitorId: mon-a}');
      expect(rig.log.last, 'input.mouse_button {button: left, down: false}');
    });

    _rigTest('touches on the letterbox bars do nothing', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 150), rig.at(0));
      rig.gestures.up(1, rig.at(50));

      rig.gestures.down(2, const Offset(50, 150), rig.at(1000));
      rig.gestures.move(2, const Offset(50, 50), rig.at(1050));
      rig.gestures.up(2, rig.at(1100));

      rig.gestures.down(3, const Offset(50, 150), rig.at(2000));
      await tester.pump(const Duration(seconds: 1));
      rig.gestures.up(3, rig.at(3000));

      expect(rig.log, isEmpty);
    });

    _rigTest('an armed modifier is held around the click, then released', PointerStyle.direct, (tester, rig) async {
      rig.modifiers = const [VirtualKeys.control];

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.up(1, rig.at(50));
      rig.gestures.down(2, const Offset(80, 80), rig.at(1000));
      rig.gestures.up(2, rig.at(1050));

      expect(rig.transport.commands, [
        InputCommands.key,
        InputCommands.mouseClick,
        InputCommands.key,
        InputCommands.mouseClick,
      ]);
      expect(rig.transport.sent[0].$2, {'vk': 17, 'down': true});
      expect(rig.transport.sent[2].$2, {'vk': 17, 'down': false});
      rig.gestures.reset();
    });

    _rigTest('a cancelled drag still releases the button', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(10, 10), rig.at(0));
      rig.gestures.move(1, const Offset(40, 10), rig.at(20));
      rig.gestures.cancel(1);

      expect(rig.log.last, 'input.mouse_button {button: left, down: false}');
    });
  });

  group('two and three fingers', () {
    _rigTest('dragging two fingers together scrolls what is under them', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 20), rig.at(0));
      rig.gestures.down(2, const Offset(60, 20), rig.at(10));
      expect(rig.log, isEmpty, reason: 'the first finger never became a tap or a press');

      // Both fingers down by 40: two notches at 18 px each, content following the fingers.
      rig.moveBoth(const Offset(40, 20), const Offset(60, 20), const Offset(0, 40), from: 20);
      rig.gestures.up(1, rig.at(200));
      rig.gestures.up(2, rig.at(210));
      await tester.pump(frame);

      final scrolls = rig.transport.sent.where((entry) => entry.$1 == InputCommands.scroll).toList();
      expect(scrolls, isNotEmpty);
      expect(scrolls.fold<int>(0, (sum, entry) => sum + (entry.$2!['dy'] as int)), 240);
      expect(scrolls.every((entry) => entry.$2!['dx'] == 0), isTrue);
      expect(scrolls.first.$2, containsPair('x', 0.5));
      expect(scrolls.first.$2, containsPair('y', 0.2));
      expect(scrolls.first.$2, containsPair('monitorId', 'mon-a'));
      expect(rig.transport.commands.toSet(), {InputCommands.scroll}, reason: 'no click, no zoom');
      expect(rig.zooms, isEmpty);
    });

    _rigTest('pinching zooms the view and sends the PC nothing', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 50), rig.at(0));
      rig.gestures.down(2, const Offset(60, 50), rig.at(10));
      rig.gestures.move(1, const Offset(20, 50), rig.at(30));
      rig.gestures.move(2, const Offset(80, 50), rig.at(30));
      rig.gestures.move(2, const Offset(100, 50), rig.at(40));
      rig.gestures.up(1, rig.at(60));
      rig.gestures.up(2, rig.at(70));

      expect(rig.zooms, isNotEmpty);
      expect(rig.zooms.every((factor) => factor > 1), isTrue);
      expect(rig.log, isEmpty);
    });

    _rigTest('once a scroll, always a scroll: spreading the fingers later does not zoom', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 20), rig.at(0));
      rig.gestures.down(2, const Offset(60, 20), rig.at(10));
      rig.moveBoth(const Offset(40, 20), const Offset(60, 20), const Offset(0, 20), from: 20);
      rig.gestures.move(2, const Offset(95, 40), rig.at(150));
      rig.gestures.up(1, rig.at(200));
      rig.gestures.up(2, rig.at(210));

      expect(rig.zooms, isEmpty);
    });

    _rigTest('three fingers move the view and send the PC nothing', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(30, 50), rig.at(0));
      rig.gestures.down(2, const Offset(50, 50), rig.at(5));
      rig.gestures.down(3, const Offset(70, 50), rig.at(10));
      for (final pointer in [1, 2, 3]) {
        rig.gestures.move(pointer, Offset(30.0 + 20 * (pointer - 1), 80), rig.at(30));
      }
      for (final pointer in [1, 2, 3]) {
        rig.gestures.up(pointer, rig.at(60));
      }

      expect(rig.pans.fold<Offset>(Offset.zero, (sum, delta) => sum + delta).dy, closeTo(30, 1e-9));
      expect(rig.log, isEmpty);
    });

    _rigTest('the finger left behind after a two-finger gesture does not click or drag', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 50), rig.at(0));
      rig.gestures.down(2, const Offset(60, 50), rig.at(10));
      rig.gestures.up(2, rig.at(500));
      rig.gestures.move(1, const Offset(90, 90), rig.at(600));
      rig.gestures.up(1, rig.at(700));

      expect(rig.log, isEmpty);
    });
  });

  group('trackpad', () {
    _rigTest('a drag moves the cursor relatively, and never clicks', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.move(1, const Offset(56, 50), rig.at(100)); // crosses the slop; no jump
      rig.gestures.move(1, const Offset(60, 52), rig.at(200)); // slow: one to one
      await tester.pump(frame);
      rig.gestures.up(1, rig.at(300));

      expect(rig.log, ['input.mouse_move {dx: 4, dy: 2}']);
      await tester.pump(const Duration(seconds: 1));
      expect(rig.log, hasLength(1));
    });

    _rigTest('a fast drag is accelerated', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(0, 50), rig.at(0));
      rig.gestures.move(1, const Offset(10, 50), rig.at(8));
      rig.gestures.move(1, const Offset(50, 50), rig.at(16)); // 5 px/ms: full gain
      rig.gestures.up(1, rig.at(30));

      expect(rig.transport.sent.single.$2, {'dx': 160, 'dy': 0});
    });

    _rigTest('a tap clicks at the cursor: button down at once, up a moment later', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.up(1, rig.at(60));
      expect(rig.log, ['input.mouse_button {button: left, down: true}'], reason: 'no position: at the cursor');

      await tester.pump(ControlGestures.tapHoldTimeout);
      expect(rig.log.last, 'input.mouse_button {button: left, down: false}');
      expect(rig.log, hasLength(2));
    });

    _rigTest('a touch held too long to be a tap does not click', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.up(1, rig.at(600));
      await tester.pump(const Duration(seconds: 1));

      expect(rig.log, isEmpty);
    });

    _rigTest('a double tap is two full clicks', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.up(1, rig.at(50));
      await tester.pump(const Duration(milliseconds: 80));
      rig.gestures.down(2, const Offset(50, 50), rig.at(130));
      rig.gestures.up(2, rig.at(180));
      await tester.pump(ControlGestures.tapHoldTimeout);

      expect(rig.log, [
        'input.mouse_button {button: left, down: true}',
        'input.mouse_button {button: left, down: false}',
        'input.mouse_button {button: left, down: true}',
        'input.mouse_button {button: left, down: false}',
      ]);
    });

    _rigTest('tap, then touch again and drag: one continuous press, not a double click', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.up(1, rig.at(50));
      await tester.pump(const Duration(milliseconds: 80));

      rig.gestures.down(2, const Offset(50, 50), rig.at(130));
      await tester.pump(const Duration(milliseconds: 400));
      expect(rig.log, hasLength(1), reason: 'the button stays down while the finger is back');

      rig.gestures.move(2, const Offset(56, 50), rig.at(600));
      rig.gestures.move(2, const Offset(60, 50), rig.at(700));
      rig.gestures.up(2, rig.at(800));

      expect(rig.log, [
        'input.mouse_button {button: left, down: true}',
        'input.mouse_move {dx: 4, dy: 0}',
        'input.mouse_button {button: left, down: false}',
      ]);
    });

    _rigTest('a two-finger tap is a right click at the cursor', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 50), rig.at(0));
      rig.gestures.down(2, const Offset(60, 50), rig.at(15));
      rig.gestures.up(1, rig.at(90));
      rig.gestures.up(2, rig.at(100));
      await tester.pump(const Duration(seconds: 1));

      expect(rig.log, ['input.mouse_click {button: right, count: 1}']);
    });

    _rigTest('a two-finger drag scrolls at the cursor, with no position', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(40, 150), rig.at(0));
      rig.gestures.down(2, const Offset(60, 150), rig.at(10));
      rig.moveBoth(const Offset(40, 150), const Offset(60, 150), const Offset(0, -40), from: 20);
      rig.gestures.up(1, rig.at(600));
      rig.gestures.up(2, rig.at(610));
      await tester.pump(frame);

      expect(rig.transport.commands.toSet(), {InputCommands.scroll});
      expect(rig.transport.sent.first.$2, isNot(contains('x')), reason: 'the whole surface is the pad, bars included');
      expect(rig.transport.sent.fold<int>(0, (sum, entry) => sum + (entry.$2!['dy'] as int)), -240);
    });
  });

  group('reset', () {
    _rigTest('lets go of a held button and forgets the gesture', PointerStyle.direct, (tester, rig) async {

      rig.gestures.down(1, const Offset(10, 10), rig.at(0));
      rig.gestures.move(1, const Offset(40, 10), rig.at(20));
      rig.gestures.reset();
      expect(rig.log.last, 'input.mouse_button {button: left, down: false}');

      final sent = rig.log.length;
      rig.gestures.move(1, const Offset(80, 10), rig.at(40));
      rig.gestures.up(1, rig.at(60));
      expect(rig.log, hasLength(sent), reason: 'the finger still down belongs to nothing now');
    });

    _rigTest('ends a trackpad tap whose button is still down', PointerStyle.trackpad, (tester, rig) async {

      rig.gestures.down(1, const Offset(50, 50), rig.at(0));
      rig.gestures.up(1, rig.at(50));
      rig.gestures.reset();

      expect(rig.log, [
        'input.mouse_button {button: left, down: true}',
        'input.mouse_button {button: left, down: false}',
      ]);
      await tester.pump(const Duration(seconds: 1));
      expect(rig.log, hasLength(2));
    });
  });
}
