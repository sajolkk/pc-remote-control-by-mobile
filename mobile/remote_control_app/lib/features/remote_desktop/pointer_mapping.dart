/// The geometry of remote control, kept free of widgets so every rule in it can be tested with
/// plain numbers: where a touch lands on the PC's monitor, how the zoomed view is kept on screen,
/// how fast the trackpad moves the cursor, and how finger travel becomes wheel notches.
library;

import 'dart:math' as math;

import 'package:flutter/widgets.dart';

/// The picture's size as it is shown: a stream rotated by a quarter turn is taller than it is wide.
Size? displayedVideoSize(double width, double height, int rotation) {
  if (width <= 0 || height <= 0) return null;
  return rotation % 180 == 0 ? Size(width, height) : Size(height, width);
}

/// Where a picture of [video]'s shape sits inside [viewport] when fitted whole and centred — the
/// rectangle the video actually occupies, with the letterbox bars outside it.
Rect letterboxRect(Size viewport, Size video) {
  if (viewport.isEmpty || video.isEmpty) return Rect.zero;

  final scale = math.min(viewport.width / video.width, viewport.height / video.height);
  final size = Size(video.width * scale, video.height * scale);

  return Rect.fromLTWH(
    (viewport.width - size.width) / 2,
    (viewport.height - size.height) / 2,
    size.width,
    size.height,
  );
}

/// Maps a touch to a point on the streamed monitor, each axis 0..1 from its top-left corner.
///
/// [local] is the touch in the viewport's own coordinates, [transform] the zoom-and-pan matrix
/// applied to the video inside it, [viewport] the size of that area and [video] the picture's
/// shape (only its aspect ratio matters). The touch is first taken back through the zoom, then
/// measured against the rectangle the picture occupies.
///
/// A touch on the letterbox bars is not on the monitor and gives null — unless [clamp] is set, as
/// it is during a drag, when leaving the picture should pin the cursor to its edge rather than
/// drop the drag.
Offset? monitorPoint({
  required Offset local,
  required Matrix4 transform,
  required Size viewport,
  required Size video,
  bool clamp = false,
}) {
  final content = letterboxRect(viewport, video);
  if (content.isEmpty) return null;

  final inverse = Matrix4.identity();
  if (inverse.copyInverse(transform) == 0) return null;

  final scene = MatrixUtils.transformPoint(inverse, local);
  if (!scene.dx.isFinite || !scene.dy.isFinite) return null;

  final x = (scene.dx - content.left) / content.width;
  final y = (scene.dy - content.top) / content.height;

  if (clamp) return Offset(x.clamp(0.0, 1.0).toDouble(), y.clamp(0.0, 1.0).toDouble());
  if (x < 0 || x > 1 || y < 0 || y > 1) return null;

  return Offset(x, y);
}

/// The zoom and pan of the view: a uniform scale and a translation, the only kind of matrix the
/// viewer ever holds.
@immutable
class ViewTransform {
  const ViewTransform(this.scale, this.offset);

  factory ViewTransform.fromMatrix(Matrix4 matrix) =>
      ViewTransform(matrix.entry(0, 0), Offset(matrix.entry(0, 3), matrix.entry(1, 3)));

  static const identity = ViewTransform(1, Offset.zero);
  static const minScale = 1.0;
  static const maxScale = 6.0;

  final double scale;
  final Offset offset;

  bool get isZoomed => scale > 1.001;

  Matrix4 toMatrix() => Matrix4.identity()
    ..setEntry(0, 0, scale)
    ..setEntry(1, 1, scale)
    ..setEntry(0, 3, offset.dx)
    ..setEntry(1, 3, offset.dy);

  /// The same view, kept inside [viewport]: never smaller than the viewport, never showing
  /// anything beyond its edges.
  ViewTransform clampTo(Size viewport) {
    final s = scale.isFinite ? scale.clamp(minScale, maxScale).toDouble() : minScale;
    final minX = viewport.width * (1 - s);
    final minY = viewport.height * (1 - s);

    return ViewTransform(
      s,
      Offset(
        offset.dx.isFinite ? offset.dx.clamp(minX, 0.0).toDouble() : 0,
        offset.dy.isFinite ? offset.dy.clamp(minY, 0.0).toDouble() : 0,
      ),
    );
  }

  /// Moves the view by [delta] viewport pixels.
  ViewTransform panBy(Offset delta, Size viewport) => ViewTransform(scale, offset + delta).clampTo(viewport);

  /// Scales by [factor] so that what was under [previousFocal] ends up under [focal]: a pinch
  /// zooms about the fingers, and moving the pinched fingers drags the view along.
  ViewTransform zoomBy(double factor, Offset previousFocal, Offset focal, Size viewport) {
    if (!factor.isFinite || factor <= 0) return this;

    final next = (scale * factor).clamp(minScale, maxScale).toDouble();
    final scenePoint = (previousFocal - offset) / scale;

    return ViewTransform(next, focal - scenePoint * next).clampTo(viewport);
  }

  /// The same zoom over a viewport that changed size (rotation, full screen, the keyboard
  /// opening), keeping whatever was at the centre at the centre.
  ViewTransform refit(Size from, Size to) {
    if (from.isEmpty || to.isEmpty) return ViewTransform.identity;

    final centre = (from.center(Offset.zero) - offset) / scale;
    final fraction = Offset(centre.dx / from.width, centre.dy / from.height);
    final moved = Offset(fraction.dx * to.width, fraction.dy * to.height);

    return ViewTransform(scale, to.center(Offset.zero) - moved * scale).clampTo(to);
  }
}

/// How many PC pixels the cursor moves per pixel of finger travel, given the finger's speed in
/// logical pixels per millisecond.
///
/// Slow movement is one to one, so a single pixel can be aimed at; a flick is multiplied, so the
/// cursor crosses a wide desktop without lifting the finger three times.
double trackpadGain(double speed) {
  const slow = 0.15;
  const fast = 1.5;
  const maxGain = 4.0;

  if (!speed.isFinite || speed <= slow) return 1;
  if (speed >= fast) return maxGain;

  return 1 + (maxGain - 1) * (speed - slow) / (fast - slow);
}

/// Turns fractional cursor movement into whole pixels without losing the fractions: what does not
/// make a pixel now is carried into the next move.
class PixelCarry {
  double _x = 0;
  double _y = 0;

  /// Adds [delta] and returns the whole pixels now due.
  ({int dx, int dy}) add(Offset delta) {
    _x += delta.dx;
    _y += delta.dy;

    final dx = _x.truncate();
    final dy = _y.truncate();
    _x -= dx;
    _y -= dy;

    return (dx: dx, dy: dy);
  }

  void reset() {
    _x = 0;
    _y = 0;
  }
}

/// Turns a two-finger drag into mouse-wheel notches.
///
/// Whole notches only (multiples of 120): plenty of Windows programs divide the delta by 120 and
/// do nothing with a smaller one. The gesture is locked to the axis it started along, because a
/// finger never moves in a perfectly straight line and a sideways wobble should not scroll
/// sideways.
///
/// Direction follows touch screens, not wheels: the content follows the fingers. Dragging down
/// scrolls up (`dy` > 0); dragging right scrolls left (`dx` < 0).
class WheelAccumulator {
  WheelAccumulator({this.pixelsPerNotch = 18});

  static const notch = 120;

  /// Finger travel, in logical pixels, that makes one notch.
  final double pixelsPerNotch;

  double _x = 0;
  double _y = 0;
  Axis? _axis;

  /// Adds finger movement and returns the wheel units now due.
  ({int dx, int dy}) add(Offset delta) {
    _x += delta.dx;
    _y += delta.dy;

    _axis ??= _x.abs() < pixelsPerNotch && _y.abs() < pixelsPerNotch
        ? null
        : (_y.abs() >= _x.abs() ? Axis.vertical : Axis.horizontal);

    switch (_axis) {
      case Axis.vertical:
        _x = 0;
        final notches = (_y / pixelsPerNotch).truncate();
        _y -= notches * pixelsPerNotch;
        return (dx: 0, dy: notches * notch);

      case Axis.horizontal:
        _y = 0;
        final notches = (_x / pixelsPerNotch).truncate();
        _x -= notches * pixelsPerNotch;
        return (dx: -notches * notch, dy: 0);

      case null:
        return (dx: 0, dy: 0);
    }
  }

  void reset() {
    _x = 0;
    _y = 0;
    _axis = null;
  }
}
