import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../data/input/input_sender.dart';
import 'control_gestures.dart';
import 'pointer_mapping.dart';

/// The zoomable picture, and — when [enabled] — the surface that turns touches on it into input.
///
/// View-only, this is exactly the viewer it replaced: pinch to zoom, drag to pan, double-tap to
/// reset. With control on, one finger belongs to the PC, so the viewer's own gestures are switched
/// off and [ControlGestures] drives both the PC and the zoom (see its rules).
///
/// Either way, the zoom survives the viewport changing size — rotation, full screen, the keyboard
/// opening — with whatever was in the middle still in the middle.
class ControlSurface extends StatefulWidget {
  const ControlSurface({
    super.key,
    required this.transform,
    required this.sender,
    required this.enabled,
    required this.style,
    required this.videoSize,
    required this.monitorId,
    required this.child,
    this.takeModifiers,
  });

  /// The zoom and pan, shared with the screen so it can reset them.
  final TransformationController transform;

  final InputSender sender;

  /// Whether touches control the PC. When false the surface only views.
  final bool enabled;

  final PointerStyle style;

  /// The picture's size as shown, or null before the first frame says. Asked at the moment of
  /// each touch, because the stream can change resolution at any time.
  final Size? Function() videoSize;

  /// The monitor being streamed.
  final String? monitorId;

  /// See [ControlGestures.takeModifiers].
  final List<int> Function()? takeModifiers;

  /// The video.
  final Widget child;

  @override
  State<ControlSurface> createState() => _ControlSurfaceState();
}

class _ControlSurfaceState extends State<ControlSurface> {
  late final ControlGestures _gestures;
  Size _viewport = Size.zero;

  @override
  void initState() {
    super.initState();
    _gestures = ControlGestures(
      widget.sender,
      style: widget.style,
      toMonitor: _toMonitor,
      monitorId: () => widget.monitorId,
      onZoom: (previousFocal, focal, factor) =>
          _setView(_view.zoomBy(factor, previousFocal, focal, _viewport)),
      onPan: (delta) => _setView(_view.panBy(delta, _viewport)),
      takeModifiers: () => widget.takeModifiers?.call() ?? const [],
      onLongPress: HapticFeedback.mediumImpact,
    );
  }

  @override
  void didUpdateWidget(ControlSurface oldWidget) {
    super.didUpdateWidget(oldWidget);

    // A gesture must not straddle a change of rules: a drag begun as a click-drag cannot finish
    // as a cursor move, and one begun with control on must not leave the button down.
    if (oldWidget.enabled != widget.enabled || oldWidget.style != widget.style) {
      _gestures.reset();
      _gestures.style = widget.style;
    }
  }

  @override
  void dispose() {
    _gestures.reset();
    super.dispose();
  }

  ViewTransform get _view => ViewTransform.fromMatrix(widget.transform.value);

  void _setView(ViewTransform view) => widget.transform.value = view.toMatrix();

  Offset? _toMonitor(Offset local, {bool clamp = false}) {
    final video = widget.videoSize();
    if (video == null) return null;

    return monitorPoint(
      local: local,
      transform: widget.transform.value,
      viewport: _viewport,
      video: video,
      clamp: clamp,
    );
  }

  void _onViewport(Size size) {
    if (size == _viewport) return;

    final previous = _viewport;
    _viewport = size;
    if (previous.isEmpty) return;

    // Deferred: the controller notifies the viewer, which may not be rebuilt during layout.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _setView(_view.refit(previous, _viewport));
    });
  }

  @override
  Widget build(BuildContext context) {
    final enabled = widget.enabled;

    return LayoutBuilder(
      builder: (context, constraints) {
        _onViewport(constraints.biggest);

        return Listener(
          behavior: HitTestBehavior.opaque,
          onPointerDown: enabled ? (e) => _gestures.down(e.pointer, e.localPosition, e.timeStamp) : null,
          onPointerMove: enabled ? (e) => _gestures.move(e.pointer, e.localPosition, e.timeStamp) : null,
          onPointerUp: enabled ? (e) => _gestures.up(e.pointer, e.timeStamp) : null,
          onPointerCancel: enabled ? (e) => _gestures.cancel(e.pointer) : null,
          child: GestureDetector(
            // Double-tap resets zoom: the quickest way back after pinching into a corner. With
            // control on a double tap is a double click, and the toolbar resets the zoom instead.
            onDoubleTap: enabled ? null : () => widget.transform.value = Matrix4.identity(),
            child: InteractiveViewer(
              transformationController: widget.transform,
              minScale: ViewTransform.minScale,
              maxScale: ViewTransform.maxScale,
              panEnabled: !enabled,
              scaleEnabled: !enabled,
              child: widget.child,
            ),
          ),
        );
      },
    );
  }
}
