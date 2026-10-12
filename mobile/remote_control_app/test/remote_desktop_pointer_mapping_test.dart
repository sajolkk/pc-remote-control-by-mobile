import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/features/remote_desktop/pointer_mapping.dart';

void main() {
  const video = Size(1920, 1080);
  final identity = Matrix4.identity();

  Offset? map(Offset local, Size viewport, {Matrix4? transform, Size size = video, bool clamp = false}) =>
      monitorPoint(local: local, transform: transform ?? identity, viewport: viewport, video: size, clamp: clamp);

  void expectPoint(Offset? actual, double x, double y) {
    expect(actual, isNotNull);
    expect(actual!.dx, closeTo(x, 1e-9));
    expect(actual.dy, closeTo(y, 1e-9));
  }

  group('letterboxRect', () {
    test('a wide picture in a tall viewport gets bars above and below', () {
      // Portrait phone: 400 wide, so the 16:9 picture is 400 × 225, centred in 800.
      expect(letterboxRect(const Size(400, 800), video), const Rect.fromLTWH(0, 287.5, 400, 225));
    });

    test('a wide picture in a wider viewport gets bars left and right', () {
      // Landscape 21:9-ish phone: 360 tall, so the picture is 640 × 360, centred in 800.
      expect(letterboxRect(const Size(800, 360), video), const Rect.fromLTWH(80, 0, 640, 360));
    });

    test('a portrait monitor in a landscape viewport', () {
      expect(letterboxRect(const Size(800, 400), const Size(1080, 1920)), const Rect.fromLTWH(287.5, 0, 225, 400));
    });

    test('degenerate sizes give no rectangle', () {
      expect(letterboxRect(Size.zero, video).isEmpty, isTrue);
      expect(letterboxRect(const Size(400, 800), Size.zero).isEmpty, isTrue);
    });
  });

  group('monitorPoint, unzoomed', () {
    const portrait = Size(400, 800);
    const landscape = Size(800, 360);

    test('portrait: the picture\'s corners and centre', () {
      expectPoint(map(const Offset(0, 287.5), portrait), 0, 0);
      expectPoint(map(const Offset(400, 512.5), portrait), 1, 1);
      expectPoint(map(const Offset(200, 400), portrait), 0.5, 0.5);
      expectPoint(map(const Offset(100, 287.5 + 56.25), portrait), 0.25, 0.25);
    });

    test('portrait: the bars above and below are not the monitor', () {
      expect(map(const Offset(200, 100), portrait), isNull);
      expect(map(const Offset(200, 287), portrait), isNull);
      expect(map(const Offset(200, 513), portrait), isNull);
      expect(map(const Offset(200, 790), portrait), isNull);
    });

    test('landscape: the picture\'s corners and centre', () {
      expectPoint(map(const Offset(80, 0), landscape), 0, 0);
      expectPoint(map(const Offset(720, 360), landscape), 1, 1);
      expectPoint(map(const Offset(400, 180), landscape), 0.5, 0.5);
    });

    test('landscape: the bars left and right are not the monitor', () {
      expect(map(const Offset(40, 180), landscape), isNull);
      expect(map(const Offset(79, 180), landscape), isNull);
      expect(map(const Offset(721, 180), landscape), isNull);
    });

    test('a viewport of the picture\'s own shape has no bars', () {
      const exact = Size(640, 360);

      expectPoint(map(Offset.zero, exact), 0, 0);
      expectPoint(map(const Offset(640, 360), exact), 1, 1);
      expectPoint(map(const Offset(160, 270), exact), 0.25, 0.75);
    });

    test('only the aspect ratio of the stream matters, not its resolution', () {
      // The PC drops to 960 × 540 on a weak network; the same touch is the same place.
      const touch = Offset(123, 400);

      expect(map(touch, portrait, size: const Size(960, 540)), map(touch, portrait));
    });

    test('with clamp, a touch on the bars is pinned to the nearest edge', () {
      expectPoint(map(const Offset(200, 100), portrait, clamp: true), 0.5, 0);
      expectPoint(map(const Offset(-50, 700), portrait, clamp: true), 0, 1);
    });

    test('before the picture has a size, nothing maps', () {
      expect(map(const Offset(200, 400), portrait, size: Size.zero), isNull);
    });
  });

  group('monitorPoint, zoomed and panned', () {
    const portrait = Size(400, 800);

    test('zoomed 2× about the top-left, each screen pixel is half a picture pixel', () {
      final zoom = const ViewTransform(2, Offset.zero).toMatrix();

      // Screen (400, 800) shows scene (200, 400): the centre of the picture.
      expectPoint(map(const Offset(400, 800), portrait, transform: zoom), 0.5, 0.5);
      // Screen (200, 575) shows scene (100, 287.5): the picture's top edge, a quarter across.
      expectPoint(map(const Offset(200, 575), portrait, transform: zoom), 0.25, 0);
    });

    test('zoomed 2× and panned to the picture\'s bottom-right quarter', () {
      // Scene = (screen - offset) / scale. With this offset the viewport shows scene x 200..400,
      // y 400..800 — the bottom-right of the picture and the bar beneath it.
      final view = const ViewTransform(2, Offset(-400, -800)).toMatrix();

      expectPoint(map(Offset.zero, portrait, transform: view), 0.5, 0.5);
      expectPoint(map(const Offset(400, 225), portrait, transform: view), 1, 1);
      expectPoint(map(const Offset(200, 112.5), portrait, transform: view), 0.75, 0.75);
      expect(map(const Offset(200, 400), portrait, transform: view), isNull, reason: 'the bar, magnified');
    });

    test('zoomed 4× into the middle', () {
      // The middle of the viewport stays the middle of the picture.
      final view = const ViewTransform(4, Offset(-600, -1200)).toMatrix();

      expectPoint(map(const Offset(200, 400), portrait, transform: view), 0.5, 0.5);
      // 40 screen pixels right is 10 scene pixels: 10 / 400 of the width.
      expectPoint(map(const Offset(240, 400), portrait, transform: view), 0.525, 0.5);
    });

    test('agrees with the matrix the viewer itself would build', () {
      final view = const ViewTransform(3, Offset(-250, -900)).toMatrix();
      final scene = MatrixUtils.transformPoint(Matrix4.inverted(view), const Offset(310, 420));
      final content = letterboxRect(portrait, video);

      expectPoint(
        map(const Offset(310, 420), portrait, transform: view),
        (scene.dx - content.left) / content.width,
        (scene.dy - content.top) / content.height,
      );
    });

    test('a matrix that cannot be inverted maps nothing', () {
      expect(map(const Offset(200, 400), portrait, transform: Matrix4.zero()), isNull);
    });
  });

  group('displayedVideoSize', () {
    test('a quarter-turned stream is as tall as it was wide', () {
      expect(displayedVideoSize(1920, 1080, 0), const Size(1920, 1080));
      expect(displayedVideoSize(1920, 1080, 180), const Size(1920, 1080));
      expect(displayedVideoSize(1920, 1080, 90), const Size(1080, 1920));
      expect(displayedVideoSize(1920, 1080, 270), const Size(1080, 1920));
    });

    test('no frame yet means no size', () {
      expect(displayedVideoSize(0, 0, 0), isNull);
    });
  });

  group('ViewTransform', () {
    const viewport = Size(400, 800);

    test('round-trips through the viewer\'s matrix', () {
      final view = ViewTransform.fromMatrix(const ViewTransform(2.5, Offset(-120, -300)).toMatrix());

      expect(view.scale, 2.5);
      expect(view.offset, const Offset(-120, -300));
      expect(ViewTransform.fromMatrix(Matrix4.identity()).isZoomed, isFalse);
    });

    test('cannot be panned off the picture or zoomed out past whole', () {
      expect(const ViewTransform(2, Offset(50, 50)).clampTo(viewport).offset, Offset.zero);
      expect(const ViewTransform(2, Offset(-900, -2000)).clampTo(viewport).offset, const Offset(-400, -800));
      expect(const ViewTransform(0.4, Offset(30, 30)).clampTo(viewport).scale, 1);
      expect(const ViewTransform(0.4, Offset(30, 30)).clampTo(viewport).offset, Offset.zero);
      expect(const ViewTransform(40, Offset.zero).clampTo(viewport).scale, ViewTransform.maxScale);
    });

    test('a pinch keeps what is under the fingers under the fingers', () {
      const focal = Offset(300, 600);
      final zoomed = ViewTransform.identity.zoomBy(2, focal, focal, viewport);

      expect(zoomed.scale, 2);
      // The scene point that was under the fingers still maps to them.
      expect(focal * zoomed.scale + zoomed.offset, focal);
    });

    test('moving the pinched fingers drags the view', () {
      final start = const ViewTransform(2, Offset(-200, -400));
      final moved = start.zoomBy(1, const Offset(200, 400), const Offset(230, 380), viewport);

      expect(moved.scale, 2);
      expect(moved.offset, const Offset(-170, -420));
    });

    test('panning stops at the edges', () {
      final view = const ViewTransform(2, Offset(-200, -400));

      expect(view.panBy(const Offset(50, -100), viewport).offset, const Offset(-150, -500));
      expect(view.panBy(const Offset(999, 999), viewport).offset, Offset.zero);
    });

    test('refit keeps the centre in the centre when the viewport changes shape', () {
      // Zoomed 2× on the middle of a portrait viewport, then the phone is turned.
      final view = const ViewTransform(2, Offset(-200, -400));
      final turned = view.refit(viewport, const Size(800, 400));

      expect(turned.scale, 2);
      expect(turned.offset, const Offset(-400, -200), reason: 'still looking at the middle');
      expect(ViewTransform.identity.refit(viewport, const Size(800, 400)).offset, Offset.zero);
    });
  });

  group('trackpadGain', () {
    test('slow is one to one, fast is multiplied, and it never jumps', () {
      expect(trackpadGain(0), 1);
      expect(trackpadGain(0.1), 1);
      expect(trackpadGain(5), 4);
      expect(trackpadGain(double.nan), 1);

      var previous = 1.0;
      for (var speed = 0.0; speed <= 2; speed += 0.05) {
        final gain = trackpadGain(speed);
        expect(gain, greaterThanOrEqualTo(previous));
        expect(gain - previous, lessThan(0.2));
        previous = gain;
      }
    });
  });

  group('PixelCarry', () {
    test('slow movement is not lost to rounding', () {
      final carry = PixelCarry();
      var total = 0;

      // Ten moves of 0.3 px: individually nothing, together three pixels.
      for (var i = 0; i < 10; i++) {
        total += carry.add(const Offset(0.3, 0)).dx;
      }

      expect(total, inInclusiveRange(2, 3));
      expect(carry.add(const Offset(-2.5, 1.5)), isNot((dx: 0, dy: 0)));
    });
  });

  group('WheelAccumulator', () {
    test('emits whole notches, and dragging down scrolls up', () {
      final wheel = WheelAccumulator(pixelsPerNotch: 20);

      expect(wheel.add(const Offset(0, 10)), (dx: 0, dy: 0), reason: 'not a notch yet');
      expect(wheel.add(const Offset(0, 10)), (dx: 0, dy: 120));
      expect(wheel.add(const Offset(0, 50)), (dx: 0, dy: 240), reason: '10 px carried over');
      expect(wheel.add(const Offset(0, -70)), (dx: 0, dy: -360));
    });

    test('dragging right scrolls left', () {
      final wheel = WheelAccumulator(pixelsPerNotch: 20);

      expect(wheel.add(const Offset(40, 3)), (dx: -240, dy: 0));
      expect(wheel.add(const Offset(-20, 0)), (dx: 120, dy: 0));
    });

    test('stays on the axis it started along', () {
      final wheel = WheelAccumulator(pixelsPerNotch: 20);
      wheel.add(const Offset(2, 25));

      expect(wheel.add(const Offset(60, 0)), (dx: 0, dy: 0), reason: 'a sideways wobble during a vertical scroll');

      wheel.reset();
      expect(wheel.add(const Offset(60, 0)), (dx: -360, dy: 0), reason: 'a new gesture chooses again');
    });
  });
}
