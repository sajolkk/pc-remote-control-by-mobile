import 'dart:async';

import 'package:flutter/widgets.dart';

import '../../data/input/input_sender.dart';
import '../../protocol/input_messages.dart';
import 'pointer_mapping.dart';

/// How touches on the picture drive the PC's pointer.
enum PointerStyle {
  /// The finger is the pointer: a tap clicks what is under it.
  direct,

  /// The picture is a laptop touchpad: the finger pushes the cursor around.
  trackpad,
}

/// Turns raw touches into pointer input for the PC, and into zooming and panning of the view.
///
/// Fed pointer by pointer rather than built from gesture recognisers, because the rules overlap in
/// ways recognisers fight over — a finger that has not moved yet may become a tap, a long press, a
/// drag or the first half of a pinch — and input has to start the instant that is known, not when
/// an arena resolves.
///
/// ## The rules
///
/// Both styles:
/// - two fingers dragged together scroll the PC (the content follows the fingers);
/// - two fingers pinched zoom the view, and moving them while pinched drags the view;
/// - three fingers dragged move the zoomed view.
///
/// [PointerStyle.direct]: tap = left click there; a second tap straight after, in the same place,
/// lands on exactly the same pixel so Windows sees a double click; hold still = right click; drag =
/// press the left button where the finger went down, move, release where it lifts. Touches on the
/// letterbox bars do nothing.
///
/// [PointerStyle.trackpad]: drag = move the cursor; tap = left click at the cursor; two-finger tap =
/// right click; tap, then touch again and drag = drag with the left button held.
///
/// Which of scroll and pinch a two-finger gesture is gets decided once, by whichever happens
/// first — the fingers travelling together or their spacing changing — and stays decided until
/// they lift, so a scroll never turns into an accidental zoom halfway through.
class ControlGestures {
  ControlGestures(
    this._sender, {
    required this.toMonitor,
    required this.monitorId,
    required this.onZoom,
    required this.onPan,
    this.takeModifiers,
    this.onLongPress,
    this.style = PointerStyle.direct,
  });

  /// Travel before a still finger counts as moving.
  static const dragSlop = 10.0;

  /// The same for the trackpad, where the first pixels of movement matter more.
  static const trackpadSlop = 4.0;

  /// How long a still finger is held for a right click.
  static const longPressTimeout = Duration(milliseconds: 500);

  /// The longest touch that is still a tap on the trackpad.
  static const tapTimeout = Duration(milliseconds: 250);

  /// How soon, and how near, a second tap must be to pair with the first.
  static const doubleTapTimeout = Duration(milliseconds: 350);
  static const doubleTapSlop = 40.0;

  /// How long the trackpad keeps the button down after a tap, waiting to see whether the finger
  /// comes back to drag. The click is not delayed by this — only its release is.
  static const tapHoldTimeout = Duration(milliseconds: 180);

  /// Travel of the fingers together that makes a two-finger gesture a scroll, and change of
  /// their spacing that makes it a pinch.
  static const scrollSlop = 12.0;
  static const pinchSlop = 20.0;

  final InputSender _sender;

  /// A touch in the viewport's coordinates to a point on the monitor (each axis 0..1), or null
  /// for the letterbox bars. With `clamp`, the nearest point on the monitor instead of null.
  final Offset? Function(Offset local, {bool clamp}) toMonitor;

  /// The monitor being streamed, as the PC named it.
  final String? Function() monitorId;

  /// A pinch step: what was under `previousFocal` should now be under `focal`, scaled by `factor`.
  final void Function(Offset previousFocal, Offset focal, double factor) onZoom;

  /// A view-panning step.
  final void Function(Offset delta) onPan;

  /// Hands over the armed keyboard modifiers (Ctrl, Shift…) for a click to be made with.
  final List<int> Function()? takeModifiers;

  /// The long press was recognised; a good moment for haptic feedback.
  final VoidCallback? onLongPress;

  PointerStyle style;

  final _pointers = <int, Offset>{};
  _Phase _phase = _Phase.idle;

  // One finger.
  Offset _downAt = Offset.zero;
  Offset? _downOnMonitor;
  Duration _downTime = Duration.zero;
  Timer? _longPress;

  // The previous tap, for pairing a double tap.
  Duration? _lastTapTime;
  Offset? _lastTapAt;
  Offset? _lastTapOnMonitor;

  // Trackpad.
  Offset _last = Offset.zero;
  Duration _lastTime = Duration.zero;
  bool _trackMoved = false;
  bool _buttonHeld = false;
  Timer? _tapHold;
  final _carry = PixelCarry();

  // Modifiers pressed for the click or drag in progress.
  List<int> _heldModifiers = const [];

  // Two and three fingers.
  Offset _startFocal = Offset.zero;
  double _startSpan = 0;
  Offset _lastFocal = Offset.zero;
  double _lastSpan = 0;
  Duration _multiTime = Duration.zero;
  Offset? _scrollOnMonitor;
  final _wheel = WheelAccumulator();

  /// A finger touched the picture.
  void down(int pointer, Offset position, Duration time) {
    _pointers[pointer] = position;

    switch (_pointers.length) {
      case 1:
        _beginOne(position, time);

      case 2:
        _endOne();
        _phase = _Phase.twoUndecided;
        _startFocal = _lastFocal = _focal;
        _startSpan = _lastSpan = _span;
        _multiTime = time;

      case 3:
        _phase = _Phase.pan;
        _lastFocal = _focal;

      default:
        _phase = _Phase.spent;
    }
  }

  /// A finger moved.
  void move(int pointer, Offset position, Duration time) {
    if (!_pointers.containsKey(pointer)) return;
    _pointers[pointer] = position;

    switch (_phase) {
      case _Phase.directPending:
        if ((position - _downAt).distance < dragSlop) return;

        _longPress?.cancel();
        _pressModifiers();
        _sender.button(
          MouseButton.left,
          down: true,
          x: _downOnMonitor!.dx,
          y: _downOnMonitor!.dy,
          monitorId: monitorId(),
        );
        _buttonHeld = true;
        _phase = _Phase.directDrag;
        _dragTo(position);

      case _Phase.directDrag:
        _dragTo(position);

      case _Phase.trackpad:
        _trackTo(position, time);

      case _Phase.twoUndecided:
        final focal = _focal;
        final span = _span;

        if ((span - _startSpan).abs() >= pinchSlop) {
          _phase = _Phase.pinch;
          _lastFocal = focal;
          _lastSpan = span;
        } else if ((focal - _startFocal).distance >= scrollSlop) {
          // In direct style the wheel turns over whatever the fingers are on, so a scroll that
          // starts on the bars has nothing to turn over.
          _scrollOnMonitor = style == PointerStyle.direct ? toMonitor(_startFocal) : null;
          if (style == PointerStyle.direct && _scrollOnMonitor == null) {
            _phase = _Phase.spent;
            return;
          }

          _wheel.reset();
          _phase = _Phase.scroll;
          _lastFocal = _startFocal;
          _scrollTo(focal);
        }

      case _Phase.scroll:
        _scrollTo(_focal);

      case _Phase.pinch:
        final focal = _focal;
        final span = _span;
        if (_lastSpan > 0 && span > 0) onZoom(_lastFocal, focal, span / _lastSpan);
        _lastFocal = focal;
        _lastSpan = span;

      case _Phase.pan:
        final focal = _focal;
        onPan(focal - _lastFocal);
        _lastFocal = focal;

      case _Phase.idle:
      case _Phase.spent:
        break;
    }
  }

  /// A finger lifted.
  void up(int pointer, Duration time) {
    if (_pointers.remove(pointer) == null) return;

    switch (_phase) {
      case _Phase.directPending:
        _longPress?.cancel();
        _directTap(time);

      case _Phase.directDrag:
        _releaseButton();

      case _Phase.trackpad:
        _trackpadUp(time);

      case _Phase.twoUndecided:
        // Two fingers that came and went without travelling: the trackpad's right click.
        if (style == PointerStyle.trackpad && time - _multiTime <= doubleTapTimeout) {
          _click(MouseButton.right);
        }

      default:
        break;
    }

    // Whatever fingers remain belong to a gesture that is over; they do nothing until they lift.
    _phase = _pointers.isEmpty ? _Phase.idle : _Phase.spent;
  }

  /// The system took the touch away (a notification shade, an incoming call).
  void cancel(int pointer) {
    if (_pointers.remove(pointer) == null) return;

    _endOne();
    _phase = _pointers.isEmpty ? _Phase.idle : _Phase.spent;
  }

  /// Ends whatever is in progress and lets go of the button, as when control is switched off.
  void reset() {
    _endOne();
    _tapHold?.cancel();
    _tapHold = null;
    _releaseButton();
    _pointers.clear();
    _lastTapTime = null;
    _phase = _Phase.idle;
  }

  void _beginOne(Offset position, Duration time) {
    _downAt = position;
    _downTime = time;

    if (style == PointerStyle.direct) {
      _finishTapHold();
      _downOnMonitor = toMonitor(position);
      if (_downOnMonitor == null) {
        _phase = _Phase.spent;
        return;
      }

      _phase = _Phase.directPending;
      _longPress?.cancel();
      _longPress = Timer(longPressTimeout, _onLongPress);
      return;
    }

    _phase = _Phase.trackpad;
    _last = position;
    _lastTime = time;
    _trackMoved = false;
    _carry.reset();

    // A finger coming back while the button is still down from a tap: the button stays down, and
    // this touch becomes either a drag or the second click of a double click.
    _tapHold?.cancel();
    _tapHold = null;
  }

  /// A second finger arrived, or the touch was cancelled: the one-finger gesture is abandoned.
  void _endOne() {
    _longPress?.cancel();
    _longPress = null;

    if (_phase == _Phase.directDrag || _phase == _Phase.trackpad) _releaseButton();
  }

  void _onLongPress() {
    if (_phase != _Phase.directPending) return;

    _phase = _Phase.spent;
    onLongPress?.call();
    _click(MouseButton.right, at: _downOnMonitor);
  }

  void _directTap(Duration time) {
    var at = _downOnMonitor;

    final lastTime = _lastTapTime;
    final paired = lastTime != null &&
        time - lastTime <= doubleTapTimeout &&
        (_downAt - _lastTapAt!).distance <= doubleTapSlop;

    // Windows only calls two clicks a double click when they land within a few pixels of each
    // other, and no finger is that steady. The second tap is put exactly where the first went.
    if (paired) at = _lastTapOnMonitor;

    _click(MouseButton.left, at: at);

    _lastTapTime = paired ? null : time;
    _lastTapAt = _downAt;
    _lastTapOnMonitor = at;
  }

  void _dragTo(Offset position) {
    final at = toMonitor(position, clamp: true);
    if (at == null) return;

    _sender.moveTo(at.dx, at.dy, monitorId: monitorId());
  }

  void _trackTo(Offset position, Duration time) {
    if (!_trackMoved) {
      if ((position - _downAt).distance < trackpadSlop) return;

      // Start from here, not from where the finger landed: the slop must not arrive as a jump.
      _trackMoved = true;
      _last = position;
      _lastTime = time;
      return;
    }

    final delta = position - _last;
    final elapsed = (time - _lastTime).inMicroseconds / 1000.0;
    final speed = elapsed > 0 ? delta.distance / elapsed : 0.0;

    _last = position;
    _lastTime = time;

    final pixels = _carry.add(delta * trackpadGain(speed));
    _sender.moveBy(pixels.dx, pixels.dy);
  }

  void _trackpadUp(Duration time) {
    if (_trackMoved) {
      // The end of a move, or of a tap-and-drag.
      _releaseButton();
      return;
    }

    // The finger came back after a tap and left again without moving: that ends the first click.
    // What follows is the second.
    _releaseButton();

    if (time - _downTime > tapTimeout) return;

    // Button down now, up a moment later — unless the finger returns first, in which case the
    // button is simply still down and the touch drags.
    _pressModifiers();
    _sender.button(MouseButton.left, down: true);
    _buttonHeld = true;
    _tapHold = Timer(tapHoldTimeout, _finishTapHold);
  }

  void _finishTapHold() {
    _tapHold?.cancel();
    _tapHold = null;
    _releaseButton();
  }

  void _scrollTo(Offset focal) {
    final wheel = _wheel.add(focal - _lastFocal);
    _lastFocal = focal;

    _sender.scroll(
      wheel.dx,
      wheel.dy,
      x: _scrollOnMonitor?.dx,
      y: _scrollOnMonitor?.dy,
      monitorId: monitorId(),
    );
  }

  /// A whole click, with any armed modifiers held around it. Without [at] it lands at the cursor.
  void _click(MouseButton button, {Offset? at}) {
    _finishTapHold();
    _pressModifiers();
    _sender.click(button, x: at?.dx, y: at?.dy, monitorId: monitorId());
    _releaseModifiers();
  }

  void _releaseButton() {
    if (_buttonHeld) {
      _buttonHeld = false;
      _sender.button(MouseButton.left, down: false);
    }

    _releaseModifiers();
  }

  void _pressModifiers() {
    if (_heldModifiers.isNotEmpty) return;

    _heldModifiers = takeModifiers?.call() ?? const [];
    for (final vk in _heldModifiers) {
      _sender.key(vk, down: true);
    }
  }

  void _releaseModifiers() {
    for (final vk in _heldModifiers.reversed) {
      _sender.key(vk, down: false);
    }

    _heldModifiers = const [];
  }

  Offset get _focal {
    var sum = Offset.zero;
    for (final position in _pointers.values) {
      sum += position;
    }
    return sum / _pointers.length.toDouble();
  }

  /// The distance between the first two fingers.
  double get _span {
    final positions = _pointers.values.take(2).toList(growable: false);
    return positions.length < 2 ? 0 : (positions[0] - positions[1]).distance;
  }
}

enum _Phase {
  idle,

  /// Direct: one finger down that has not yet moved, lifted or been held long enough.
  directPending,

  /// Direct: the left button is down and following the finger.
  directDrag,

  /// Trackpad: one finger down, moving the cursor or about to tap.
  trackpad,

  /// Two fingers down, not yet clearly a scroll or a pinch.
  twoUndecided,
  scroll,
  pinch,

  /// Three fingers: moving the view.
  pan,

  /// The gesture is over, or was never one; remaining fingers are ignored until they lift.
  spent,
}
