import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/host/block_list.dart';
import 'package:remote_control_app/host/host_input.dart';
import 'package:remote_control_app/host/paired_viewer_store.dart';
import 'package:remote_control_app/protocol/input_messages.dart';
import 'package:remote_control_app/protocol/protocol.dart';

import 'host_testing.dart';

void main() {
  group('the gate', () {
    test('is blocked exactly while a blocked app is in front', () {
      final gate = BlockListGate(blocked: {'com.bank'});
      expect(gate.isBlocked, isFalse);

      gate.setForeground('com.bank');
      expect(gate.isBlocked, isTrue);

      gate.setForeground('com.browser');
      expect(gate.isBlocked, isFalse);
    });

    test('changing the list applies to the app already in front', () {
      final gate = BlockListGate(foregroundPackage: 'com.bank');
      var notified = 0;
      gate.addListener(() => notified++);

      gate.setBlocked('com.bank', true);
      expect(gate.isBlocked, isTrue);
      expect(notified, 1);

      gate.setBlocked('com.bank', false);
      expect(gate.isBlocked, isFalse);

      gate.setBlocked('com.bank', false);
      expect(notified, 2, reason: 'No change, no notification.');
    });

    test('replacing the list keeps the foreground', () {
      final gate = BlockListGate(foregroundPackage: 'com.bank');
      gate.replaceAll({'com.other', 'com.bank'});
      expect(gate.isBlocked, isTrue);
      expect(gate.blocked, {'com.other', 'com.bank'});
    });
  });

  group('while a blocked app is in front', () {
    late HostRig rig;
    late TestViewer viewer;

    setUp(() async {
      rig = HostRig();
      rig.platform.apps = const [
        InstalledApp(package: 'com.bank', name: 'Bank'),
        InstalledApp(package: 'com.browser', name: 'Browser'),
      ];
      rig.platform.screenshot = Uint8List.fromList([1, 2, 3]);
      rig.gate.replaceAll({'com.bank'});
      await rig.store.save(pairedViewer('fp-a', permissions: PairedViewer.allPermissions));
      viewer = await rig.connect(fingerprint: 'fp-a');
      await viewer.hello();
    });

    tearDown(() => rig.dispose());

    test('every input is dropped with the blocked code, and nothing reaches the phone', () async {
      rig.gate.setForeground('com.bank');

      for (final (command, args) in [
        (InputCommands.mouseClick, InputArgs.click(MouseButton.left, x: 0.5, y: 0.5)),
        (InputCommands.mouseMove, InputArgs.moveTo(0.5, 0.5)),
        (InputCommands.scroll, InputArgs.scroll(0, 120, x: 0.5, y: 0.5)),
        (InputCommands.text, InputArgs.text('secret')),
        (InputCommands.key, InputArgs.key(VirtualKeys.enter, down: false)),
        (Commands.phoneNav, InputArgs.phoneNav(PhoneNavActions.recents)),
      ]) {
        final response = await viewer.send(command, args: args);
        expect(response.ok, isFalse, reason: command);
        expect(response.error!.code, ErrorCodes.blockedByOwner, reason: command);
      }

      expect(rig.platform.log, isEmpty);
    });

    test('input works again the moment another app is in front', () async {
      rig.gate.setForeground('com.bank');
      await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, x: 0.5, y: 0.5));

      rig.gate.setForeground('com.browser');
      final response = await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, x: 0.5, y: 0.5));

      expect(response.ok, isTrue);
      expect(rig.platform.log, ['tap 540,1200 40ms']);
    });

    test('blocking mid-session, with the app already in front, takes effect at once', () async {
      rig.gate.setForeground('com.browser');
      expect((await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, x: 0.5, y: 0.5))).ok, isTrue);

      rig.gate.setBlocked('com.browser', true);
      final blocked = await viewer.send(InputCommands.mouseClick, args: InputArgs.click(MouseButton.left, x: 0.5, y: 0.5));
      expect(blocked.error!.code, ErrorCodes.blockedByOwner);
    });

    test('a drag in progress is ended when a blocked app comes to the front', () async {
      rig.gate.setForeground('com.browser');
      await viewer.send(InputCommands.mouseButton, args: InputArgs.button(MouseButton.left, down: true, x: 0.1, y: 0.1));

      rig.gate.setForeground('com.bank');
      await pumpEventQueue();

      expect(rig.platform.log, ['dragStart 108,240', 'dragEnd']);

      final move = await viewer.send(InputCommands.mouseMove, args: InputArgs.moveTo(0.2, 0.1));
      expect(move.error!.code, ErrorCodes.blockedByOwner);
    });

    test('screenshots are refused', () async {
      rig.gate.setForeground('com.bank');
      final response = await viewer.send(Commands.screenScreenshot);
      expect(response.error!.code, ErrorCodes.blockedByOwner);
      expect(rig.platform.log, isEmpty);

      rig.gate.setForeground('com.browser');
      expect((await viewer.send(Commands.screenScreenshot)).ok, isTrue);
    });

    test('the app list marks blocked apps, and launching one is refused by name', () async {
      final list = await viewer.send(Commands.appList);
      final apps = (list.map['apps'] as List).cast<Map<String, dynamic>>();

      expect(apps.firstWhere((a) => a['id'] == 'com.bank')['blocked'], isTrue);
      expect(apps.firstWhere((a) => a['id'] == 'com.browser')['blocked'], isFalse);

      final launch = await viewer.send(Commands.appLaunch, args: {'appId': 'com.bank'});
      expect(launch.error!.code, ErrorCodes.blockedByOwner);
      expect(rig.platform.log, isEmpty);

      final allowed = await viewer.send(Commands.appLaunch, args: {'appId': 'com.browser'});
      expect(allowed.ok, isTrue);
      expect(rig.platform.log, ['launch com.browser']);
    });

    test('launching anything is refused while a blocked app is in front', () async {
      rig.gate.setForeground('com.bank');
      final launch = await viewer.send(Commands.appLaunch, args: {'appId': 'com.browser'});
      expect(launch.error!.code, ErrorCodes.blockedByOwner);
    });

    test('the system state says so, for the owner\'s own screen', () async {
      rig.gate.setForeground('com.bank');
      final state = await viewer.send(Commands.systemState);
      expect(state.map['blockedByOwner'], isTrue);
    });
  });
}
