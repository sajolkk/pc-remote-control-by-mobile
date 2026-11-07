import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/host/host_pairing.dart';
import 'package:remote_control_app/host/paired_viewer_store.dart';
import 'package:remote_control_app/protocol/protocol.dart';

import 'host_testing.dart';

void main() {
  late FakeClock clock;
  late InMemoryPairedViewerStore store;
  late FakeApprover approver;
  late HostPairingService pairing;

  setUp(() {
    clock = FakeClock();
    store = InMemoryPairedViewerStore();
    approver = FakeApprover(const PairingDecision(approved: true));
    pairing = HostPairingService(
      store: store,
      approver: approver.call,
      hostFingerprint: 'host-fp',
      clock: clock.call,
      approvalTimeout: const Duration(milliseconds: 200),
    );
  });

  Map<String, dynamic> request({String token = '', String fingerprint = 'viewer-fp', String name = 'Pixel B'}) => {
        'deviceId': 'viewer-b',
        'deviceName': name,
        'platform': 'android',
        'model': 'Pixel 8',
        'token': token,
        'clientFingerprint': fingerprint,
      };

  group('the QR path', () {
    test('refuses while pairing is closed, without asking anyone', () async {
      final result = await pairing.tryPair(request(token: 'x'), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      expect(result.outcome, PairingOutcome.disabled);
      expect(result.errorCode, ErrorCodes.pairingDisabled);
      expect(approver.asked, isEmpty);
    });

    test('a wrong token never reaches the owner, and burns an attempt', () async {
      pairing.openWindow();

      final result = await pairing.tryPair(request(token: 'wrong'), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      expect(result.outcome, PairingOutcome.tokenInvalid);
      expect(result.errorCode, ErrorCodes.pairingTokenInvalid);
      expect(approver.asked, isEmpty);
      expect(pairing.failedAttempts, 1);
      expect(pairing.isOpen, isTrue);
    });

    test('too many wrong tokens close the window', () async {
      pairing.openWindow();

      PairingAttemptResult? last;
      for (var i = 0; i < 5; i++) {
        last = await pairing.tryPair(request(token: 'wrong $i'), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      }

      expect(last!.outcome, PairingOutcome.tooManyAttempts);
      expect(last.errorCode, ErrorCodes.rateLimited);
      expect(pairing.isOpen, isFalse);
    });

    test('the window expires on its own', () async {
      final window = pairing.openWindow();
      clock.advance(const Duration(minutes: 6));

      expect(pairing.isOpen, isFalse);

      final result = await pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      expect(result.outcome, PairingOutcome.disabled);
    });

    test('a claimed fingerprint that is not the TLS peer is a relay, and refused', () async {
      final window = pairing.openWindow();

      final result = await pairing.tryPair(
        request(token: window.token, fingerprint: 'somebody-else'),
        peerFingerprint: 'viewer-fp',
        remoteAddress: '10.0.0.2',
      );

      expect(result.outcome, PairingOutcome.fingerprintMismatch);
      expect(approver.asked, isEmpty);
    });

    test('with the right token the owner is asked, and approval saves the default permissions', () async {
      final window = pairing.openWindow();

      final result = await pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      expect(result.succeeded, isTrue);
      expect(approver.asked, hasLength(1));
      expect(approver.asked.single.deviceName, 'Pixel B');
      expect(approver.asked.single.remoteAddress, '10.0.0.2');
      expect(approver.asked.single.comparisonCode, isNull);

      final saved = await store.findByDeviceId('viewer-b');
      expect(saved, isNotNull);
      expect(saved!.fingerprint, 'viewer-fp', reason: 'The TLS peer, not anything the request said.');
      expect(saved.permissions, PairedViewer.defaultPermissions);

      expect(pairing.isOpen, isFalse, reason: 'The token is single-use: the window closes.');
    });

    test('the token cannot be used twice', () async {
      final window = pairing.openWindow();
      await pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      final again = await pairing.tryPair(
        request(token: window.token, fingerprint: 'viewer-c'),
        peerFingerprint: 'viewer-c',
        remoteAddress: '10.0.0.3',
      );

      expect(again.outcome, PairingOutcome.disabled);
    });

    test('"full control" is decided by the owner alone and grants every permission', () async {
      approver.decision = const PairingDecision(approved: true, grantFullControl: true);
      final window = pairing.openWindow();

      await pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      final saved = await store.findByDeviceId('viewer-b');
      expect(saved!.permissions, PairedViewer.allPermissions);
    });

    test('a refusal saves nothing', () async {
      approver.decision = PairingDecision.declined;
      final window = pairing.openWindow();

      final result = await pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      expect(result.outcome, PairingOutcome.rejected);
      expect(result.errorCode, ErrorCodes.pairingRejected);
      expect(await store.getAll(), isEmpty);
    });

    test('nobody answering is a refusal', () async {
      approver.hold = Completer<PairingDecision>();
      final window = pairing.openWindow();

      final result = await pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      expect(result.outcome, PairingOutcome.approvalTimeout);
      expect(result.errorCode, ErrorCodes.pairingTimeout);
      expect(await store.getAll(), isEmpty);
    });

    test('a second device while the owner is being asked is told to wait', () async {
      approver.hold = Completer<PairingDecision>();
      final window = pairing.openWindow();

      final first = pairing.tryPair(request(token: window.token), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      await Future<void>.delayed(Duration.zero);

      final second = await pairing.tryPair(
        request(token: window.token, fingerprint: 'viewer-c'),
        peerFingerprint: 'viewer-c',
        remoteAddress: '10.0.0.3',
      );

      expect(second.outcome, PairingOutcome.busy);
      approver.hold!.complete(const PairingDecision(approved: true));
      expect((await first).succeeded, isTrue);
    });

    test('a connection without a certificate is refused whatever it claims', () async {
      final window = pairing.openWindow();
      final result = await pairing.tryPair(request(token: window.token), peerFingerprint: '', remoteAddress: '10.0.0.2');
      expect(result.outcome, PairingOutcome.fingerprintMismatch);
    });

    test('a request without a device id is incomplete', () async {
      final window = pairing.openWindow();
      final args = request(token: window.token)..['deviceId'] = ' ';
      final result = await pairing.tryPair(args, peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      expect(result.outcome, PairingOutcome.invalidRequest);
      expect(result.errorCode, ErrorCodes.invalidArguments);
    });
  });

  group('the code path (no QR)', () {
    test('shows the owner the code derived from both certificates, and needs no window', () async {
      final result = await pairing.tryPair(request(), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      expect(result.succeeded, isTrue);
      expect(approver.asked.single.comparisonCode, matches(RegExp(r'^\d{3} \d{3}$')));
      expect(pairing.isOpen, isFalse, reason: 'No QR window is involved.');
    });

    test('after a refusal the same address must wait a minute', () async {
      approver.decision = PairingDecision.declined;
      await pairing.tryPair(request(), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');

      approver.decision = const PairingDecision(approved: true);
      final soon = await pairing.tryPair(request(), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      expect(soon.outcome, PairingOutcome.tooManyAttempts);

      clock.advance(const Duration(seconds: 61));
      final later = await pairing.tryPair(request(), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      expect(later.succeeded, isTrue);
    });

    test('can be switched off', () async {
      final strict = HostPairingService(
        store: store,
        approver: approver.call,
        hostFingerprint: 'host-fp',
        clock: clock.call,
        allowCodePairing: false,
      );

      final result = await strict.tryPair(request(), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
      expect(result.outcome, PairingOutcome.disabled);
    });
  });

  test('display names are trimmed, capped and stripped of control characters', () {
    expect(HostPairingService.sanitizeDisplayName('  Pixel\n8\u0007 '), 'Pixel8');
    expect(HostPairingService.sanitizeDisplayName('x' * 100).length, 64);
    expect(HostPairingService.sanitizeDisplayName(null), '');
  });

  test('a viewer with a blank name is saved under a generic one', () async {
    final window = pairing.openWindow();
    await pairing.tryPair(request(token: window.token, name: '  '), peerFingerprint: 'viewer-fp', remoteAddress: '10.0.0.2');
    expect((await store.findByDeviceId('viewer-b'))!.deviceName, 'Mobile device');
  });
}
