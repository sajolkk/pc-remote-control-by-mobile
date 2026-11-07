import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/host/host_session.dart';
import 'package:remote_control_app/host/tls_bridge.dart';

/// Plays the native TLS listener: connects to the loopback port and writes the header line.
Future<Socket> _proxy(int port, String header) async {
  final socket = await Socket.connect(InternetAddress.loopbackIPv4, port);
  socket.add(utf8.encode(header));
  await socket.flush();
  return socket;
}

void main() {
  late TlsBridgeListener bridge;
  final accepted = <HostTransport>[];

  setUp(() async {
    accepted.clear();
    bridge = TlsBridgeListener(secret: 's3cret', onConnection: accepted.add);
    await bridge.start();
  });

  tearDown(() => bridge.stop());

  test('a connection with the right secret becomes a transport carrying what TLS learned', () async {
    final socket = await _proxy(bridge.port, 'PCRH1 s3cret fp-abc 192.168.1.20 TLSv1.3 TLS_AES_128_GCM_SHA256\nhello');
    await Future<void>.delayed(const Duration(milliseconds: 200));

    expect(accepted, hasLength(1));
    final transport = accepted.single;
    expect(transport.peerFingerprint, 'fp-abc');
    expect(transport.remoteAddress, '192.168.1.20');
    expect(transport.tlsDescription, 'TLSv1.3 TLS_AES_128_GCM_SHA256');

    // Bytes after the header belong to the viewer, and bytes written go back to the proxy.
    final received = <int>[];
    final done = Completer<void>();
    transport.incoming.listen(received.addAll, onDone: done.complete);
    transport.write(utf8.encode('world'));

    final echoed = <int>[];
    final sub = socket.listen(echoed.addAll);
    await Future<void>.delayed(const Duration(milliseconds: 200));

    expect(utf8.decode(received), 'hello');
    expect(utf8.decode(echoed), 'world');

    await sub.cancel();
    socket.destroy();
    await done.future.timeout(const Duration(seconds: 2));
  });

  test('a connection with the wrong secret, or no header, is dropped without becoming a viewer', () async {
    final wrong = await _proxy(bridge.port, 'PCRH1 nope fp-abc 192.168.1.20 TLS\n');
    final garbage = await _proxy(bridge.port, 'GET / HTTP/1.1\r\n');

    final wrongClosed = Completer<void>();
    final garbageClosed = Completer<void>();
    wrong.listen((_) {}, onDone: wrongClosed.complete, onError: (_) => wrongClosed.complete());
    garbage.listen((_) {}, onDone: garbageClosed.complete, onError: (_) => garbageClosed.complete());

    await Future.wait([wrongClosed.future, garbageClosed.future]).timeout(const Duration(seconds: 3));
    expect(accepted, isEmpty);
  });

  test('an endless header is cut off', () async {
    final socket = await Socket.connect(InternetAddress.loopbackIPv4, bridge.port);
    final closed = Completer<void>();
    socket.listen((_) {}, onDone: closed.complete, onError: (_) => closed.complete());

    socket.add(List<int>.filled(2000, 0x41));
    await socket.flush();

    await closed.future.timeout(const Duration(seconds: 3));
    expect(accepted, isEmpty);
  });
}
