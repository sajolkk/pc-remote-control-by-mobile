import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';
import 'package:remote_control_app/protocol/protocol.dart';

void main() {
  group('PairingQrPayload', () {
    test('parses a well-formed payload', () {
      final raw = jsonEncode({
        'v': 1,
        'id': 'pc-device-id',
        'n': 'Office PC',
        'fp': 'abc123',
        't': 'pairing-token',
        'p': 47800,
        'h': ['192.168.1.20', '10.0.0.5'],
      });

      final payload = PairingQrPayload.tryParse(raw);

      expect(payload, isNotNull);
      expect(payload!.deviceId, 'pc-device-id');
      expect(payload.deviceName, 'Office PC');
      expect(payload.fingerprint, 'abc123');
      expect(payload.token, 'pairing-token');
      expect(payload.port, 47800);
      expect(payload.hosts, ['192.168.1.20', '10.0.0.5']);
    });

    test('rejects a payload missing any required field', () {
      // A partial payload cannot produce a secure pairing: without the fingerprint there is
      // nothing to pin, and without the token the PC will refuse anyway. Rejecting outright
      // beats proceeding with half the information.
      const required = ['id', 'fp', 't', 'p'];

      for (final missing in required) {
        final fields = <String, dynamic>{
          'id': 'pc',
          'fp': 'fingerprint',
          't': 'token',
          'p': 47800,
        }..remove(missing);

        expect(
          PairingQrPayload.tryParse(jsonEncode(fields)),
          isNull,
          reason: 'A payload without "$missing" must not be accepted.',
        );
      }
    });

    test('rejects input that is not our QR code', () {
      // The camera sees every code in view, so anything unrecognised must be ignored quietly
      // rather than treated as a failed pairing.
      for (final raw in <String>[
        '',
        'https://example.com',
        'not json',
        '[]',
        '{"unrelated":true}',
        '12345',
      ]) {
        expect(PairingQrPayload.tryParse(raw), isNull, reason: 'Should ignore: $raw');
      }
    });

    test('tolerates a missing name and missing hosts', () {
      // Both are optional: the name is cosmetic and discovery can find the PC without hints.
      final payload = PairingQrPayload.tryParse(jsonEncode({
        'id': 'pc',
        'fp': 'fingerprint',
        't': 'token',
        'p': 47800,
      }));

      expect(payload, isNotNull);
      expect(payload!.deviceName, 'Windows PC');
      expect(payload.hosts, isEmpty);
    });
  });

  group('DiscoveryBeacon', () {
    test('parses a beacon and records the address it came from', () {
      final beacon = DiscoveryBeacon.fromJson(
        {'v': 1, 'id': 'pc-1', 'name': 'Living Room PC', 'port': 47801, 'proto': 1},
        '192.168.1.44',
      );

      expect(beacon.deviceId, 'pc-1');
      expect(beacon.deviceName, 'Living Room PC');
      expect(beacon.port, 47801);
      expect(beacon.address, '192.168.1.44');
      expect(beacon.isCompatible, isTrue);
    });

    test('flags a PC speaking an unsupported protocol version', () {
      // Reported as incompatible rather than silently connected to, so the user is told to
      // update instead of watching commands fail.
      final future = DiscoveryBeacon.fromJson(
        {'id': 'pc-1', 'port': 47800, 'proto': ProtocolVersion.current + 1},
        '192.168.1.44',
      );

      expect(future.isCompatible, isFalse);
    });

    test('survives a beacon with missing fields', () {
      final beacon = DiscoveryBeacon.fromJson(const {}, '192.168.1.44');

      expect(beacon.deviceId, isEmpty);
      expect(beacon.deviceName, 'Windows PC');
      expect(beacon.address, '192.168.1.44');
    });
  });

  group('ResponseEnvelope', () {
    test('parses a success with a payload', () {
      final response = ResponseEnvelope.fromJson({
        'v': 1,
        'id': 'abc',
        'ok': true,
        'data': {'token': 'session-token', 'permissions': ['ViewScreen']},
      });

      expect(response.ok, isTrue);
      expect(response.map['token'], 'session-token');
      expect(response.error, isNull);
    });

    test('parses a failure and exposes the stable code', () {
      // The app branches on the code, never on the message, so the code must survive parsing
      // exactly.
      final response = ResponseEnvelope.fromJson({
        'v': 1,
        'id': 'abc',
        'ok': false,
        'err': {'code': 'E_PERMISSION_DENIED', 'msg': 'Not granted', 'retryable': false},
      });

      expect(response.ok, isFalse);
      expect(response.error!.code, ErrorCodes.permissionDenied);
      expect(response.error!.retryable, isFalse);
    });

    test('treats a malformed response as a failure rather than throwing', () {
      final response = ResponseEnvelope.fromJson(const {});

      expect(response.ok, isFalse);
      expect(response.map, isEmpty);
    });
  });

  group('RequestEnvelope', () {
    test('emits the short field names the PC expects', () {
      // The PC reads "cmd", "seq", "ts" and "tok". Renaming any of them silently breaks every
      // command, so the wire shape is pinned here.
      final json = const RequestEnvelope(
        id: 'r1',
        command: Commands.ping,
        sequence: 7,
        timestampMs: 1758300000000,
        args: {'a': 1},
        token: 'tok',
      ).toJson();

      expect(json['v'], ProtocolVersion.current);
      expect(json['id'], 'r1');
      expect(json['cmd'], 'ping');
      expect(json['seq'], 7);
      expect(json['ts'], 1758300000000);
      expect(json['args'], {'a': 1});
      expect(json['tok'], 'tok');
    });

    test('omits args and token when absent', () {
      final json = const RequestEnvelope(
        id: 'r1',
        command: Commands.hello,
        sequence: 1,
        timestampMs: 1,
      ).toJson();

      expect(json.containsKey('args'), isFalse);
      expect(json.containsKey('tok'), isFalse);
    });
  });

  group('fingerprints', () {
    test('match the definition the PC uses', () {
      // The two codebases must agree exactly on "SHA-256 over the DER certificate, base64url,
      // unpadded", or every pairing fails with a mismatch that looks like an attack. This pins
      // the definition against an independently computed value.
      final data = List<int>.generate(64, (index) => index);

      expect(
        DeviceIdentityStore.fingerprintOfDer(data),
        '_eq5rPNxA2K9JljNyaKej5x1f8-YEWA6jER80dkVEQg',
      );
    });

    test('short form matches what the PC displays', () {
      final data = List<int>.generate(64, (index) => index);
      final fingerprint = DeviceIdentityStore.fingerprintOfDer(data);

      expect(
        DeviceIdentityStore.shortFingerprint(fingerprint),
        'FD:EA:B9:AC:F3:71:03:62',
      );
    });

    test('is unpadded and url-safe', () {
      // It travels inside a QR code and inside JSON, so '+', '/' and '=' must not appear.
      final fingerprint = DeviceIdentityStore.fingerprintOfDer(List<int>.filled(100, 7));

      expect(fingerprint.contains('='), isFalse);
      expect(fingerprint.contains('+'), isFalse);
      expect(fingerprint.contains('/'), isFalse);
    });

    test('short form degrades rather than throwing on malformed input', () {
      expect(() => DeviceIdentityStore.shortFingerprint('not-base64!!'), returnsNormally);
    });
  });

  group('Base64Url', () {
    test('round-trips bytes', () {
      for (var length = 1; length <= 40; length++) {
        final data = List<int>.generate(length, (index) => (index * 31) % 256);
        final encoded = Base64Url.encode(data);

        expect(encoded.contains('='), isFalse);
        expect(Base64Url.tryDecode(encoded), data);
      }
    });

    test('returns null for malformed input', () {
      expect(Base64Url.tryDecode('!!!'), isNull);
    });
  });

  group('permission labels', () {
    test('every known group has a human-readable label', () {
      const groups = [
        Permissions.viewScreen,
        Permissions.controlInput,
        Permissions.launchApps,
        Permissions.manageFiles,
        Permissions.powerControls,
        Permissions.clipboard,
      ];

      for (final group in groups) {
        expect(Permissions.label(group), isNotEmpty, reason: '$group needs a label.');
      }

      // The ones whose wire name would read badly in a UI are rewritten. "Clipboard" is
      // already the right word for a person, so it maps to itself deliberately.
      expect(Permissions.label(Permissions.viewScreen), 'View screen');
      expect(Permissions.label(Permissions.controlInput), 'Control mouse and keyboard');
      expect(Permissions.label(Permissions.powerControls), 'Power controls');
    });

    test('an unknown group falls back to its raw name', () {
      // Forward compatibility: a newer PC may report a group this build has never heard of,
      // and showing the raw name beats showing nothing.
      expect(Permissions.label('FutureGroup'), 'FutureGroup');
    });
  });
}
