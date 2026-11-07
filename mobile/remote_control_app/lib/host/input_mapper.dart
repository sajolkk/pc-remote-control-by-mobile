/// How a viewer's input commands, which speak the PC's language of monitors, mouse buttons and
/// Windows virtual keys, become touches and actions on an Android phone. Pure arithmetic and
/// tables, so every rule can be tested without a phone.
library;

import 'dart:ui';

import '../protocol/input_messages.dart';

/// What a virtual key means on a phone.
enum PhoneKeyAction {
  back,
  home,
  recents,
  deleteBackward,
  deleteForward,
  enter,
  tab,
  moveLeft,
  moveRight,
  moveUp,
  moveDown,
  lineStart,
  lineEnd,
  volumeUp,
  volumeDown,
  volumeMute,
}

/// A touch stroke the accessibility service should perform, in screen pixels.
class Swipe {
  const Swipe(this.from, this.to, this.duration);

  final Offset from;
  final Offset to;
  final Duration duration;
}

/// Maps normalised monitor coordinates and PC keys to phone pixels and actions.
class PhoneInputMapper {
  const PhoneInputMapper();

  /// How long the finger stays down for a long press, the phone's right click.
  static const longPressDuration = Duration(milliseconds: 600);

  /// How long a tap lasts.
  static const tapDuration = Duration(milliseconds: 40);

  /// One wheel notch (120 units) swipes this fraction of the screen's shorter side.
  static const notchFraction = 0.08;

  /// The point on a [screen] of the given pixel size that a viewer's (x, y) in 0..1 names.
  ///
  /// Out-of-range values are clamped rather than refused, as the PC does: a drag that leaves the
  /// picture pins to its edge. NaN and infinity are refused.
  Offset? toPixels(num? x, num? y, Size screen) {
    if (x == null || y == null || screen.isEmpty) return null;

    final fx = x.toDouble();
    final fy = y.toDouble();
    if (!fx.isFinite || !fy.isFinite) return null;

    return Offset(
      (fx.clamp(0.0, 1.0) * (screen.width - 1)).roundToDouble(),
      (fy.clamp(0.0, 1.0) * (screen.height - 1)).roundToDouble(),
    );
  }

  /// Turns wheel units into a swipe whose content follows the finger: a viewer dragging a finger
  /// down sends `dy` > 0 (scroll up), and the phone's content must move down with it.
  ///
  /// `dx` has the opposite sign convention on the wire (right drag → negative), so it is negated.
  /// Null when there is nothing to swipe.
  Swipe? scrollToSwipe(num? dx, num? dy, Offset at, Size screen) {
    final x = (dx ?? 0).toDouble();
    final y = (dy ?? 0).toDouble();
    if ((x == 0 && y == 0) || !x.isFinite || !y.isFinite || screen.isEmpty) return null;

    final notch = screen.shortestSide * notchFraction;
    final delta = Offset(-x / 120 * notch, y / 120 * notch);

    final to = Offset(
      (at.dx + delta.dx).clamp(0.0, screen.width - 1),
      (at.dy + delta.dy).clamp(0.0, screen.height - 1),
    );

    if (to == at) return null;

    // Longer swipes take longer, so the fling velocity stays moderate.
    final millis = (delta.distance / screen.shortestSide * 400).clamp(60.0, 400.0).round();
    return Swipe(at, to, Duration(milliseconds: millis));
  }

  /// What a virtual key does on the phone, or null for a key with no phone meaning.
  ///
  /// Letters, digits and punctuation never arrive here: the viewer types them as `input.text`.
  /// Escape is Back because that is what Escape means on every phone keyboard that has one.
  static PhoneKeyAction? forVirtualKey(int vk) => switch (vk) {
        VirtualKeys.escape => PhoneKeyAction.back,
        0xA6 => PhoneKeyAction.back, // VK_BROWSER_BACK
        VirtualKeys.win => PhoneKeyAction.home,
        0xAC => PhoneKeyAction.home, // VK_BROWSER_HOME
        0x5D => PhoneKeyAction.recents, // VK_APPS
        VirtualKeys.back => PhoneKeyAction.deleteBackward,
        VirtualKeys.delete => PhoneKeyAction.deleteForward,
        VirtualKeys.enter => PhoneKeyAction.enter,
        VirtualKeys.tab => PhoneKeyAction.tab,
        VirtualKeys.left => PhoneKeyAction.moveLeft,
        VirtualKeys.right => PhoneKeyAction.moveRight,
        VirtualKeys.up => PhoneKeyAction.moveUp,
        VirtualKeys.down => PhoneKeyAction.moveDown,
        VirtualKeys.home => PhoneKeyAction.lineStart,
        VirtualKeys.end => PhoneKeyAction.lineEnd,
        VirtualKeys.volumeUp => PhoneKeyAction.volumeUp,
        VirtualKeys.volumeDown => PhoneKeyAction.volumeDown,
        VirtualKeys.volumeMute => PhoneKeyAction.volumeMute,
        _ => null,
      };

  /// The text a virtual key types when pressed on its own, for the few keys that do.
  static String? textForVirtualKey(int vk) => switch (vk) {
        VirtualKeys.space => ' ',
        _ => null,
      };

  /// The phone has one `MouseButton.left` finger; `right` is a long press. The middle button has
  /// no phone meaning.
  static bool isLongPress(String? button) => button == MouseButton.right.wire;
}

/// Follows one viewer's held button, so a press-move-release becomes one continuous drag stroke.
///
/// The PC's `HeldInputTracker` releases everything on disconnect; this does the same, and also
/// tells the caller what stroke segment to perform after each event. The accessibility service
/// continues a stroke from wherever the last segment ended, so each segment starts there.
class DragTracker {
  Offset? _position;
  bool _down = false;

  bool get isDown => _down;

  Offset? get position => _position;

  /// A move while the button is down returns the segment to perform; otherwise it only remembers
  /// where the pointer is, so a later press without a position has somewhere to land.
  Swipe? move(Offset to) {
    final from = _position;
    _position = to;

    if (!_down || from == null || from == to) return null;
    return Swipe(from, to, const Duration(milliseconds: 16));
  }

  /// The button went down, at [at] or where the pointer last was. Null when there is nowhere.
  Offset? press(Offset? at) {
    final point = at ?? _position;
    if (point == null) return null;

    _position = point;
    _down = true;
    return point;
  }

  /// The button came up. True when a drag was in progress and must be ended.
  bool release() {
    final was = _down;
    _down = false;
    return was;
  }

  void reset() {
    _down = false;
    _position = null;
  }
}
