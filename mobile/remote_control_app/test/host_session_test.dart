import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/host/paired_viewer_store.dart';
import 'package:remote_control_app/protocol/frame_codec.dart';
import 'package:remote_control_app/protocol/input_messages.dart';
import 'package:remote_control_app/protocol/protocol.dart';

import 'host_testing.dart';

void main() {
  late HostRig rig;

  setUp(() => rig = HostRig());
  tearDown(() => rig.dispose());

  group('an unpaired viewer', () {
    test('may only pair', () async {
      final viewer = await rig.connect(fingerprint: 'new-fp');

      final hello = await viewer.hello();
      expect(hello.ok, isFalse);
      expect(hello.error!.code, ErrorCodes.notPaired);

      final state = await viewer.send(Commands.systemState);
      expect(state.error!.code, ErrorCodes.notPaired);
    });

    test('pairs by QR token and gets the phone\'s identity back', () async {
      final window = rig.pairing.openWindow();
      final viewer = await rig.connect(fingerprint: 'new-fp');

      final response = await viewer.pair(token: window.token);

      expect(response.ok, isTrue, reason: '${response.error}');
      expect(response.map['deviceId'], 'phone-a');
      expect(response.map['fingerprint'], HostRig.hostFingerprint);
      expect(response.map['deviceKind'], DeviceKinds.phone);
      expect(response.map['permissions'], PairedViewer.defaultPermissions);

      expect(rig.approver.asked.single.fingerprintShort, isNotEmpty);
      expect((await rig.store.findByFingerprint('new-fp'))!.deviceId, 'viewer-b');
    });

    test('a bad token is refused with the pairing code, and the owner is not asked', () async {
      rig.pairing.openWindow();
      final viewer = await rig.connect(fingerprint: 'new-fp');

      final response = await viewer.pair(token: 'nope');

      expect(response.error!.code, ErrorCodes.pairingTokenInvalid);
      expect(rig.approver.asked, isEmpty);
    });

    test('a viewer that forgot the phone while still paired here is simply confirmed', () async {
      await rig.store.save(pairedViewer('fp-a'));
      final viewer = await rig.connect(fingerprint: 'fp-a');

      final response = await viewer.pair(token: 'anything');

      expect(response.ok, isTrue);
      expect(response.map['deviceId'], 'phone-a');
      expect(rig.approver.asked, isEmpty);
    });
  });

  group('the handshake', () {
    setUp(() => rig.store.save(pairedViewer('fp-a')));

    test('issues a token bound to the connection and reports honest capabilities', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');

      final hello = await viewer.hello();

      expect(hello.ok, isTrue, reason: '${hello.error}');
      expect(hello.map['deviceKind'], DeviceKinds.phone);
      expect(hello.map['token'], isNotEmpty);
      expect(hello.map['permissions'], PairedViewer.defaultPermissions);

      final capabilities = (hello.map['capabilities'] as List).cast<String>();
      expect(capabilities, containsAll([Capabilities.screenCapture, InputCapabilities.mouse, Capabilities.phoneNav, Capabilities.apps]));
      expect(capabilities, isNot(contains(Capabilities.files)));
      expect(capabilities, isNot(contains(Capabilities.powerShutdown)));
    });

    test('without the accessibility service, input is not advertised', () async {
      rig.platform.accessibility = false;
      final viewer = await rig.connect(fingerprint: 'fp-a');

      final hello = await viewer.hello();

      final capabilities = (hello.map['capabilities'] as List).cast<String>();
      expect(capabilities, contains(Capabilities.screenCapture));
      expect(capabilities, isNot(contains(InputCapabilities.mouse)));
      expect(capabilities, isNot(contains(Capabilities.phoneNav)));
    });

    test('commands need the token, and the token works only on its own connection', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');

      final before = await viewer.send(Commands.systemState);
      expect(before.error!.code, ErrorCodes.unauthenticated);

      await viewer.hello();
      final after = await viewer.send(Commands.systemState);
      expect(after.ok, isTrue);
      expect(after.map['sessionAgentConnected'], isTrue);

      final other = await rig.connect(fingerprint: 'fp-a');
      other.token = viewer.token;
      final stolen = await other.send(Commands.systemState);
      expect(stolen.error!.code, ErrorCodes.unauthenticated);
    });

    test('renaming at the handshake is kept', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello(name: 'My other phone');
      expect((await rig.store.findByFingerprint('fp-a'))!.deviceName, 'My other phone');
    });

    test('refuses a protocol range it cannot serve', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      final response = await viewer.send(Commands.hello, includeToken: false, args: {'minVersion': 7, 'maxVersion': 9});
      expect(response.error!.code, ErrorCodes.versionUnsupported);
    });

    test('a viewer the owner disconnected is refused but told to keep trying', () async {
      await rig.store.save(pairedViewer('fp-a', blocked: true));
      final viewer = await rig.connect(fingerprint: 'fp-a');

      final hello = await viewer.hello();

      expect(hello.error!.code, ErrorCodes.policyDenied);
      expect(hello.error!.retryable, isTrue);
    });

    test('renewal replaces the token and invalidates the old one', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();
      final old = viewer.token;

      final renewed = await viewer.send(Commands.sessionRenew);
      expect(renewed.ok, isTrue);
      viewer.token = renewed.map['token'] as String;
      expect(viewer.token, isNot(old));

      expect((await viewer.send(Commands.systemState)).ok, isTrue);

      viewer.token = old;
      expect((await viewer.send(Commands.systemState)).error!.code, ErrorCodes.unauthenticated);
    });
  });

  group('permissions', () {
    test('a command needs its permission group', () async {
      await rig.store.save(pairedViewer('fp-a', permissions: [Permissions.viewScreen]));
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      final tap = await viewer.send(InputCommands.mouseClick, args: {'button': 'left', 'x': 0.5, 'y': 0.5});
      expect(tap.error!.code, ErrorCodes.permissionDenied);

      final apps = await viewer.send(Commands.appList);
      expect(apps.error!.code, ErrorCodes.permissionDenied);

      final monitors = await viewer.send(Commands.screenMonitorList);
      expect(monitors.ok, isTrue);
    });

    test('a permission the owner withdraws applies to the next command, with an event', () async {
      await rig.store.save(pairedViewer('fp-a'));
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      await rig.store.save(pairedViewer('fp-a', permissions: [Permissions.viewScreen]));
      await rig.sessions.refreshAll();
      await pumpEventQueue();

      expect(viewer.events.map((e) => e.topic), contains(Events.permissionsChanged));
      final tap = await viewer.send(InputCommands.mouseClick, args: {'button': 'left', 'x': 0.5, 'y': 0.5});
      expect(tap.error!.code, ErrorCodes.permissionDenied);
    });

    test('revoking the pairing closes the connection after saying so', () async {
      await rig.store.save(pairedViewer('fp-a'));
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      await rig.store.remove('viewer-fp-a');
      await rig.sessions.refreshAll();
      await pumpEventQueue();

      expect(viewer.events.map((e) => e.topic), contains(Events.pairingRevoked));
      expect(viewer.transport.closedByHost, isTrue);
      expect(rig.sessions.count, 0);
    });
  });

  group('the wire', () {
    setUp(() => rig.store.save(pairedViewer('fp-a')));

    test('an unknown command is refused by name', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();
      final response = await viewer.send('file.list');
      expect(response.error!.code, ErrorCodes.unknownCommand);
    });

    test('a repeated sequence number ends the connection', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      final response = await viewer.send(Commands.ping, seq: viewer.sequence);
      expect(response.error!.code, ErrorCodes.replayDetected);
      await pumpEventQueue();
      expect(viewer.transport.closedByHost, isTrue);
    });

    test('a sequence gap ends the connection', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      final response = await viewer.send(Commands.ping, seq: viewer.sequence + 2);
      expect(response.error!.code, ErrorCodes.replayDetected);
    });

    test('a timestamp far from now is refused', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      final response = await viewer.send(Commands.ping, ts: rig.clock.now.subtract(const Duration(minutes: 5)).millisecondsSinceEpoch);
      expect(response.error!.code, ErrorCodes.replayDetected);
    });

    test('an unsupported envelope version is refused without ending the connection', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      final response = await viewer.send(Commands.ping, version: 42);
      expect(response.error!.code, ErrorCodes.versionUnsupported);
      expect(viewer.transport.closedByHost, isFalse);
    });

    test('a ping is answered with a pong', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      viewer.ping();
      await pumpEventQueue();
      expect(viewer.pongs, 1);
    });

    test('a malformed frame ends the connection', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      viewer.sendRaw([0x7F, 0xFF, 0xFF, 0xFF, 0x01]);
      await pumpEventQueue();
      expect(viewer.transport.closedByHost, isTrue);
    });

    test('a viewer that goes quiet is dropped after the keepalive timeout', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      rig.clock.advance(const Duration(seconds: 20));
      await Future<void>.delayed(const Duration(seconds: 6));

      expect(viewer.transport.closedByHost, isTrue);
    }, timeout: const Timeout(Duration(seconds: 20)));

    test('frames split across chunks are reassembled', () {
      final codec = FrameCodec();
      final frame = codec.encode(FrameType.request, List<int>.generate(300, (i) => i & 0xFF));

      final decoder = FrameCodec();
      final first = decoder.decode(frame.sublist(0, 3));
      final second = decoder.decode(frame.sublist(3, 200));
      final third = decoder.decode([...frame.sublist(200), ...frame]);

      expect(first, isEmpty);
      expect(second, isEmpty);
      expect(third, hasLength(2));
      expect(third.first.type, FrameType.request);
      expect(third.first.payload.length, 300);
    });

    test('the frame header matches the PC: big-endian length, then the type byte', () {
      final frame = FrameCodec().encode(FrameType.event, [1, 2, 3]);
      expect(frame.sublist(0, 5), [0, 0, 0, 3, 0x03]);
    });
  });

  group('the screen', () {
    setUp(() => rig.store.save(pairedViewer('fp-a')));

    test('lists one monitor and forwards the offer', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();

      final monitors = await viewer.send(Commands.screenMonitorList);
      final list = monitors.map['monitors'] as List;
      expect(list, hasLength(1));
      expect((list.single as Map)['width'], 1080);
      expect((list.single as Map)['height'], 2400);

      final offer = await viewer.send(Commands.mediaOffer, args: {'sdp': 'v=0'});
      expect(offer.ok, isTrue);
      expect(rig.screen.offers, [viewer.session!.connectionId]);
    });

    test('another monitor id is unknown', () async {
      final viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();
      final response = await viewer.send(Commands.screenSelectMonitor, args: {'monitorId': 'DISPLAY2'});
      expect(response.error!.code, ErrorCodes.notFound);
    });
  });
}
