import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/control/control_client.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';
import 'package:remote_control_app/protocol/protocol.dart';
import 'package:remote_control_app/protocol/screen_messages.dart';

void main() {
  test('a phone\'s QR code reads back as a phone, with every field a PC\'s has', () {
    const payload = PairingQrPayload(
      deviceId: 'phone-a',
      deviceName: 'Pixel A',
      fingerprint: 'fp',
      token: 'tok',
      port: 47800,
      hosts: ['192.168.1.10'],
      kind: DeviceKinds.phone,
    );

    final parsed = PairingQrPayload.tryParse(payload.encode())!;

    expect(parsed.deviceId, 'phone-a');
    expect(parsed.deviceName, 'Pixel A');
    expect(parsed.fingerprint, 'fp');
    expect(parsed.token, 'tok');
    expect(parsed.port, 47800);
    expect(parsed.hosts, ['192.168.1.10']);
    expect(parsed.isPhone, isTrue);
  });

  test('a PC\'s QR code, which has no kind, is a PC', () {
    final parsed = PairingQrPayload.tryParse('{"v":1,"id":"pc","n":"Desk","fp":"f","t":"t","p":47800,"h":[]}')!;
    expect(parsed.isPhone, isFalse);
    expect(parsed.kind, DeviceKinds.pc);
  });

  test('a phone\'s beacon round-trips and a PC\'s beacon omits the kind', () {
    const beacon = DiscoveryBeacon(
      deviceId: 'phone-a',
      deviceName: 'Pixel A',
      port: 47801,
      protocolVersion: 1,
      address: '',
      kind: DeviceKinds.phone,
    );

    final parsed = DiscoveryBeacon.fromJson(beacon.toJson(), '192.168.1.10');
    expect(parsed.isPhone, isTrue);
    expect(parsed.port, 47801);
    expect(parsed.address, '192.168.1.10');

    expect(const DiscoveryBeacon(deviceId: 'pc', deviceName: 'Desk', port: 1, protocolVersion: 1, address: '').toJson(),
        isNot(contains('kind')));
  });

  test('a paired record keeps its kind, and an old record is a PC', () {
    final phone = PairedPc(deviceId: 'a', deviceName: 'A', fingerprint: 'f', pairedAt: DateTime(2026), kind: DeviceKinds.phone);
    final restored = PairedPc.fromJson(phone.toJson());

    expect(restored.isPhone, isTrue);
    expect(restored.withNickname('Mine').isPhone, isTrue);
    expect(restored.copyWith(deviceName: 'B').isPhone, isTrue);
    expect(restored.copyWith(kind: DeviceKinds.pc).isPhone, isFalse);

    final old = PairedPc.fromJson({'deviceId': 'b', 'fingerprint': 'f', 'pairedAt': '2026-01-01T00:00:00Z'});
    expect(old.isPhone, isFalse);
    expect(old.toJson(), isNot(contains('kind')));
  });

  test('the handshake result carries the kind, defaulting to a PC', () {
    expect(HelloResult.fromJson({'deviceKind': 'phone'}).deviceKind, DeviceKinds.phone);
    expect(HelloResult.fromJson({}).deviceKind, DeviceKinds.pc);
  });

  test('the blocked-by-owner pause has words of its own', () {
    expect(MediaPauseReasons.describe(MediaPauseReasons.blockedByOwner), contains('blocked by its owner'));
    expect(ErrorCodes.blockedByOwner, 'E_BLOCKED_BY_OWNER');
  });
}
