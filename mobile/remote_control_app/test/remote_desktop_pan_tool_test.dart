import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/input/input_sender.dart';
import 'package:remote_control_app/features/remote_desktop/control_gestures.dart';
import 'package:remote_control_app/features/remote_desktop/control_surface.dart';
import 'package:remote_control_app/features/remote_desktop/pointer_mapping.dart';

import 'input_testing.dart';

void main() {
  const viewport = Size(400, 800);

  Future<void> pumpSurface(
    WidgetTester tester, {
    required InputSender sender,
    required TransformationController transform,
    required bool panMode,
  }) {
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
              enabled: true,
              panMode: panMode,
              videoSize: () => const Size(1920, 1080),
              monitorId: 'mon-a',
              style: PointerStyle.direct,
              child: const ColoredBox(color: Colors.black),
            ),
          ),
        ),
      ),
    );
  }

  testWidgets('with the move tool on, a touch reaches nothing on the PC', (tester) async {
    final transport = RecordingTransport();
    final sender = InputSender(transport.send);
    addTearDown(sender.dispose);

    await pumpSurface(tester, sender: sender, transform: TransformationController(), panMode: true);
    await tester.pumpAndSettle();

    await tester.tapAt(const Offset(200, 400));
    await tester.pump(const Duration(milliseconds: 500));
    await tester.dragFrom(const Offset(200, 400), const Offset(-80, 0));
    await tester.pumpAndSettle();

    expect(transport.sent, isEmpty);
  });

  testWidgets('turning the move tool on zooms in, so there is somewhere to move', (tester) async {
    final transport = RecordingTransport();
    final sender = InputSender(transport.send);
    addTearDown(sender.dispose);
    final transform = TransformationController();

    await pumpSurface(tester, sender: sender, transform: transform, panMode: false);
    expect(ViewTransform.fromMatrix(transform.value).isZoomed, isFalse);

    await pumpSurface(tester, sender: sender, transform: transform, panMode: true);
    await tester.pump();

    expect(ViewTransform.fromMatrix(transform.value).scale, ControlSurface.panModeZoom);
  });

  testWidgets('one finger moves the zoomed screen left, right, up and down', (tester) async {
    final transport = RecordingTransport();
    final sender = InputSender(transport.send);
    addTearDown(sender.dispose);
    final transform = TransformationController();

    await pumpSurface(tester, sender: sender, transform: transform, panMode: false);
    await pumpSurface(tester, sender: sender, transform: transform, panMode: true);
    await tester.pumpAndSettle();

    final start = ViewTransform.fromMatrix(transform.value).offset;

    // Finger to the left and up: the picture follows it.
    await tester.dragFrom(const Offset(200, 400), const Offset(-60, -60));
    await tester.pumpAndSettle();
    final moved = ViewTransform.fromMatrix(transform.value).offset;

    expect(moved.dx, lessThan(start.dx));
    expect(moved.dy, lessThan(start.dy));

    // And back to the right and down.
    await tester.dragFrom(const Offset(200, 400), const Offset(60, 60));
    await tester.pumpAndSettle();
    final back = ViewTransform.fromMatrix(transform.value).offset;

    expect(back.dx, greaterThan(moved.dx));
    expect(back.dy, greaterThan(moved.dy));
    expect(transport.sent, isEmpty);
  });
}
