import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'host_session.dart';

/// The Dart end of the host's control listener.
///
/// Native code owns the TLS socket (see the note on [HostTransport]). For every viewer whose TLS
/// handshake succeeded it opens a plain connection to this loopback port, writes one header line,
/// and then pumps the decrypted bytes both ways:
///
/// ```
/// PCRH1 <secret> <peerFingerprint> <remoteAddress> <tlsDescription>\n
/// ```
///
/// The secret is random per start and known only to this process and its native half; a
/// connection without it is dropped, so no other app on the phone can present itself as a viewer
/// by connecting to the loopback port and forging the header.
class TlsBridgeListener {
  TlsBridgeListener({required this.secret, required this.onConnection});

  static const magic = 'PCRH1';

  /// The shared secret the native side must present.
  final String secret;

  /// Called with each authenticated viewer connection.
  final void Function(HostTransport transport) onConnection;

  ServerSocket? _server;

  int get port => _server?.port ?? 0;

  Future<int> start() async {
    final server = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    _server = server;
    server.listen(_accept, onError: (Object _) {});
    return server.port;
  }

  void _accept(Socket socket) {
    socket.setOption(SocketOption.tcpNoDelay, true);
    _HeaderReader(socket, secret, onConnection).start();
  }

  Future<void> stop() async {
    await _server?.close();
    _server = null;
  }
}

/// Parses one header line, then becomes the viewer's transport.
class _HeaderReader {
  _HeaderReader(this._socket, this._secret, this._onConnection);

  static const _maxHeaderBytes = 512;

  final Socket _socket;
  final String _secret;
  final void Function(HostTransport) _onConnection;
  final _header = BytesBuilder(copy: false);

  _SocketTransport? _transport;

  void start() {
    _socket.listen(
      _onData,
      onError: (Object error) => _transport?._controller.addError(error),
      onDone: () => _transport?._controller.close(),
      cancelOnError: true,
    );
  }

  void _onData(Uint8List chunk) {
    final transport = _transport;
    if (transport != null) {
      transport._controller.add(chunk);
      return;
    }

    _header.add(chunk);
    final bytes = _header.toBytes();
    final newline = bytes.indexOf(0x0A);

    if (newline < 0) {
      if (bytes.length > _maxHeaderBytes) _socket.destroy();
      return;
    }

    final fields = ascii.decode(bytes.sublist(0, newline), allowInvalid: true).split(' ');
    if (fields.length < 4 || fields[0] != TlsBridgeListener.magic || fields[1] != _secret) {
      _socket.destroy();
      return;
    }

    final transportNow = _SocketTransport(
      _socket,
      peerFingerprint: fields[2],
      remoteAddress: fields[3],
      tlsDescription: fields.length > 4 ? fields.sublist(4).join(' ') : 'TLS',
    );
    _transport = transportNow;

    final rest = bytes.sublist(newline + 1);
    if (rest.isNotEmpty) transportNow._controller.add(Uint8List.fromList(rest));

    _onConnection(transportNow);
  }
}

class _SocketTransport implements HostTransport {
  _SocketTransport(
    this._socket, {
    required this.peerFingerprint,
    required this.remoteAddress,
    required this.tlsDescription,
  });

  final Socket _socket;
  final _controller = StreamController<Uint8List>();

  @override
  final String peerFingerprint;

  @override
  final String remoteAddress;

  @override
  final String tlsDescription;

  @override
  Stream<Uint8List> get incoming => _controller.stream;

  @override
  void write(List<int> bytes) => _socket.add(bytes);

  @override
  Future<void> close() async {
    _socket.destroy();
    if (!_controller.isClosed) await _controller.close();
  }
}
