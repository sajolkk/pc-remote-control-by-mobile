import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/storage/paired_pc_store.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('PairedPc', () {
    test('loads a record written before remote addresses existed', () {
      // Exactly what an earlier build stored. Anyone upgrading has records like this, and
      // failing to read one would silently unpair their PC.
      final json = jsonDecode('''
        {"deviceId":"pc-1","deviceName":"Office PC","fingerprint":"fp-abc",
         "pairedAt":"2026-01-05T10:00:00.000","lastAddress":"192.168.1.20","lastPort":47800,
         "lastSeenAt":"2026-02-01T09:30:00.000","permissions":["ViewScreen","LaunchApps"]}
      ''') as Map<String, dynamic>;

      final pc = PairedPc.fromJson(json);

      expect(pc.deviceId, 'pc-1');
      expect(pc.deviceName, 'Office PC');
      expect(pc.fingerprint, 'fp-abc');
      expect(pc.lastAddress, '192.168.1.20');
      expect(pc.lastPort, 47800);
      expect(pc.permissions, ['ViewScreen', 'LaunchApps']);

      expect(pc.remoteAddress, isNull);
      expect(pc.remotePort, isNull);
      expect(pc.remoteAddressLabel, isNull);
    });

    test('an old record round-trips without growing new fields', () {
      final json = <String, dynamic>{
        'deviceId': 'pc-1',
        'deviceName': 'Office PC',
        'fingerprint': 'fp-abc',
        'pairedAt': '2026-01-05T10:00:00.000',
        'lastAddress': '192.168.1.20',
        'lastPort': 47800,
        'permissions': <String>[],
      };

      final written = PairedPc.fromJson(json).toJson();

      expect(written.containsKey('remoteAddress'), isFalse);
      expect(written.containsKey('remotePort'), isFalse);
      expect(written['lastAddress'], '192.168.1.20');
    });

    test('stores the remote address beside the LAN address, not instead of it', () {
      final pc = _pc().withRemoteAddress(const RemoteAddress(host: '100.64.0.7', port: 50000));
      final json = pc.toJson();

      expect(json['lastAddress'], '192.168.1.20');
      expect(json['lastPort'], 47800);
      expect(json['remoteAddress'], '100.64.0.7');
      expect(json['remotePort'], 50000);

      final restored = PairedPc.fromJson(jsonDecode(jsonEncode(json)) as Map<String, dynamic>);

      expect(restored.lastAddress, '192.168.1.20');
      expect(restored.remoteAddress, '100.64.0.7');
      expect(restored.remotePort, 50000);
      expect(restored.effectiveRemotePort, 50000);
      expect(restored.remoteAddressLabel, '100.64.0.7:50000');
    });

    test('a remote address without a port uses the PC\'s own port', () {
      final pc = _pc(lastPort: 47811)
          .withRemoteAddress(const RemoteAddress(host: 'office-pc.tailnet.ts.net'));

      expect(pc.toJson().containsKey('remotePort'), isFalse);
      expect(pc.effectiveRemotePort, 47811);
      expect(pc.remoteAddressLabel, 'office-pc.tailnet.ts.net');
    });

    test('copyWith keeps the remote address when the LAN address changes', () {
      final pc = _pc().withRemoteAddress(const RemoteAddress(host: '100.64.0.7'));
      final moved = pc.copyWith(lastAddress: '192.168.1.99', lastPort: 47801);

      expect(moved.lastAddress, '192.168.1.99');
      expect(moved.remoteAddress, '100.64.0.7');
    });

    test('withRemoteAddress(null) removes it and touches nothing else', () {
      final pc = _pc().withRemoteAddress(const RemoteAddress(host: '100.64.0.7', port: 1234));
      final cleared = pc.withRemoteAddress(null);

      expect(cleared.remoteAddress, isNull);
      expect(cleared.remotePort, isNull);
      expect(cleared.lastAddress, pc.lastAddress);
      expect(cleared.fingerprint, pc.fingerprint);
    });

    test('ignores a blank or nonsensical stored remote address', () {
      final pc = PairedPc.fromJson({
        ..._pc().toJson(),
        'remoteAddress': '   ',
        'remotePort': 700000,
      });

      expect(pc.remoteAddress, isNull);
      expect(pc.remotePort, isNull);
    });
  });

  group('RemoteAddress.tryParse', () {
    test('accepts a host or IP, with or without a port', () {
      expect(RemoteAddress.tryParse('100.64.0.7')!.host, '100.64.0.7');
      expect(RemoteAddress.tryParse('100.64.0.7')!.port, isNull);

      final withPort = RemoteAddress.tryParse(' office-pc.tailnet.ts.net:47800 ')!;
      expect(withPort.host, 'office-pc.tailnet.ts.net');
      expect(withPort.port, 47800);

      expect(RemoteAddress.tryParse('office-pc')!.host, 'office-pc');
    });

    test('accepts IPv6, bare or bracketed with a port', () {
      final bare = RemoteAddress.tryParse('fd7a:115c:a1e0::1')!;
      expect(bare.host, 'fd7a:115c:a1e0::1');
      expect(bare.port, isNull);

      final bracketed = RemoteAddress.tryParse('[fd7a:115c:a1e0::1]:47800')!;
      expect(bracketed.host, 'fd7a:115c:a1e0::1');
      expect(bracketed.port, 47800);
      expect(bracketed.toString(), '[fd7a:115c:a1e0::1]:47800');
    });

    test('rejects what is not an address', () {
      for (final input in <String>[
        '',
        '   ',
        'http://100.64.0.7',
        '100.64.0.7/path',
        'two words',
        'host:',
        'host:abc',
        'host:0',
        'host:65536',
        '-host',
        '[fd7a::1',
        '[fd7a::1]47800',
        'user@host',
      ]) {
        expect(RemoteAddress.tryParse(input), isNull, reason: 'Should reject: "$input"');
      }
    });
  });

  group('PairedPcStore', () {
    setUp(() => FlutterSecureStorage.setMockInitialValues({}));

    test('reads a store written by an earlier build', () async {
      FlutterSecureStorage.setMockInitialValues({
        'paired.pcs': jsonEncode([
          {
            'deviceId': 'pc-1',
            'deviceName': 'Office PC',
            'fingerprint': 'fp-abc',
            'pairedAt': '2026-01-05T10:00:00.000',
            'lastAddress': '192.168.1.20',
            'lastPort': 47800,
            'permissions': ['ViewScreen'],
          },
        ]),
      });

      final all = await PairedPcStore().getAll();

      expect(all, hasLength(1));
      expect(all.single.lastAddress, '192.168.1.20');
      expect(all.single.remoteAddress, isNull);
    });

    test('setRemoteAddress adds, replaces and clears without disturbing the rest', () async {
      final store = PairedPcStore();
      await store.save(_pc());
      await store.save(_pc(deviceId: 'pc-2'));

      final added =
          await store.setRemoteAddress('pc-1', const RemoteAddress(host: '100.64.0.7', port: 5000));
      expect(added!.remoteAddress, '100.64.0.7');

      var stored = await store.find('pc-1');
      expect(stored!.remoteAddress, '100.64.0.7');
      expect(stored.remotePort, 5000);
      expect(stored.lastAddress, '192.168.1.20');
      expect(stored.fingerprint, 'fp-abc');
      expect((await store.find('pc-2'))!.remoteAddress, isNull);

      await store.setRemoteAddress('pc-1', const RemoteAddress(host: 'pc.example.net'));
      stored = await store.find('pc-1');
      expect(stored!.remoteAddress, 'pc.example.net');
      expect(stored.remotePort, isNull);

      await store.setRemoteAddress('pc-1', null);
      stored = await store.find('pc-1');
      expect(stored!.remoteAddress, isNull);
      expect(stored.lastAddress, '192.168.1.20');

      expect(await store.getAll(), hasLength(2));
    });

    test('setRemoteAddress on an unknown PC changes nothing', () async {
      final store = PairedPcStore();
      await store.save(_pc());

      expect(await store.setRemoteAddress('nope', const RemoteAddress(host: 'x')), isNull);
      expect(await store.getAll(), hasLength(1));
    });

    test('pairing again does not throw away the remote address', () async {
      // The pairing screens build a brand-new record with no idea a remote address was ever
      // entered. Saving it must not lose what the user typed.
      final store = PairedPcStore();
      await store.save(_pc());
      await store.setRemoteAddress('pc-1', const RemoteAddress(host: '100.64.0.7', port: 5000));

      await store.save(_pc(fingerprint: 'fp-new', lastAddress: '192.168.1.77'));

      final stored = await store.find('pc-1');
      expect(stored!.fingerprint, 'fp-new');
      expect(stored.lastAddress, '192.168.1.77');
      expect(stored.remoteAddress, '100.64.0.7');
      expect(stored.remotePort, 5000);
      expect(await store.getAll(), hasLength(1));
    });
  });
}

PairedPc _pc({
  String deviceId = 'pc-1',
  String fingerprint = 'fp-abc',
  String lastAddress = '192.168.1.20',
  int lastPort = 47800,
}) =>
    PairedPc(
      deviceId: deviceId,
      deviceName: 'Office PC',
      fingerprint: fingerprint,
      pairedAt: DateTime(2026, 1, 5, 10),
      lastAddress: lastAddress,
      lastPort: lastPort,
    );
