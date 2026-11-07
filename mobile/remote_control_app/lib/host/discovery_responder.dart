// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:convert';
import 'dart:io';

import '../protocol/protocol.dart';

/// Answers LAN discovery probes while this phone is sharing: the PC's `UdpDiscoveryResponder`.
///
/// The beacon carries only what a viewer needs to attempt a connection, nothing else; a probe must
/// start with the magic prefix or it is ignored; and replies to one address are throttled, so the
/// responder cannot be used as a reflector.
class DiscoveryResponder {
  DiscoveryResponder({
    required DiscoveryBeacon Function() beacon,
    this.port = DiscoveryConstants.defaultUdpPort,
    this.minReplyInterval = const Duration(milliseconds: 250),
  }) : _beacon = beacon;

  final DiscoveryBeacon Function() _beacon;
  final int port;
  final Duration minReplyInterval;

  RawDatagramSocket? _socket;
  final _lastReply = <String, DateTime>{};

  bool get isRunning => _socket != null;

  /// Binds the discovery port. Returns false when it could not; viewers can still connect by an
  /// address they already know, so this is not fatal.
  Future<bool> start() async {
    if (_socket != null) return true;

    try {
      final socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, port, reuseAddress: true);
      socket.broadcastEnabled = true;
      socket.listen((event) {
        if (event != RawSocketEvent.read) return;
        final datagram = socket.receive();
        if (datagram != null) _handle(socket, datagram);
      });
      _socket = socket;
      return true;
    } on SocketException {
      return false;
    }
  }

  void _handle(RawDatagramSocket socket, Datagram datagram) {
    final data = datagram.data;
    if (data.isEmpty || data.length > DiscoveryConstants.maxDatagramBytes) return;

    final magic = ascii.encode(DiscoveryConstants.probeMagic);
    if (data.length < magic.length) return;
    for (var i = 0; i < magic.length; i++) {
      if (data[i] != magic[i]) return;
    }

    if (!_allowReply(datagram.address.address)) return;

    final payload = utf8.encode(jsonEncode(_beacon().toJson()));
    final reply = [...ascii.encode(DiscoveryConstants.responseMagic), ...payload];

    try {
      socket.send(reply, datagram.address, datagram.port);
    } on SocketException {
      // The prober may already be gone.
    }
  }

  bool _allowReply(String address) {
    final now = DateTime.now();
    final last = _lastReply[address];
    if (last != null && now.difference(last) < minReplyInterval) return false;

    _lastReply[address] = now;

    if (_lastReply.length > 512) {
      final cutoff = now.subtract(const Duration(minutes: 5));
      _lastReply.removeWhere((_, time) => time.isBefore(cutoff));
    }

    return true;
  }

  void stop() {
    _socket?.close();
    _socket = null;
    _lastReply.clear();
  }
}
