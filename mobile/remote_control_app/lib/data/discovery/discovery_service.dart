import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../../protocol/protocol.dart';

/// Finds PC-Remote agents on the local network.
///
/// ## Why UDP broadcast rather than mDNS
///
/// The architecture specified mDNS with UDP as a fallback; this reverses the priority. On
/// Android, multicast reception is unreliable in ways the app cannot control — several Wi-Fi
/// chipsets drop multicast in power save, battery optimisation can suspend the listener, and
/// plenty of consumer routers filter multicast between wireless clients. A directed broadcast
/// to a known port has none of those failure modes.
///
/// mDNS remains worth adding as an additional path, because it makes the PC visible to
/// generic service browsers. It is not worth being the only path.
///
/// ## What discovery is and is not
///
/// It answers "which PCs are here", nothing more. The beacon carries no secret, and finding a
/// PC grants no access: the control channel still requires a pairing record and a client
/// certificate. So an attacker learning that a PC exists gains nothing, which is why the
/// probe is unauthenticated.
class DiscoveryService {
  DiscoveryService({
    this.port = DiscoveryConstants.defaultUdpPort,
    this.probeInterval = const Duration(seconds: 2),
  });

  /// The UDP port the PC answers on.
  final int port;

  /// How often a probe is re-sent while a scan is running.
  final Duration probeInterval;

  /// Scans for PCs for [duration], emitting each one as it answers.
  ///
  /// Results are emitted as they arrive rather than collected, so the UI can show the first
  /// PC immediately instead of waiting for the whole scan.
  Stream<DiscoveryBeacon> scan({Duration duration = const Duration(seconds: 5)}) {
    final controller = StreamController<DiscoveryBeacon>();
    final seen = <String>{};

    RawDatagramSocket? socket;
    Timer? probeTimer;
    Timer? stopTimer;

    Future<void> cleanup() async {
      probeTimer?.cancel();
      stopTimer?.cancel();
      socket?.close();
      if (!controller.isClosed) await controller.close();
    }

    controller.onCancel = cleanup;

    Future<void> start() async {
      try {
        // Bind to an ephemeral port on all interfaces: the reply comes back to whatever
        // source port the probe left from.
        socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
        socket!.broadcastEnabled = true;

        socket!.listen((RawSocketEvent event) {
          if (event != RawSocketEvent.read) return;

          final datagram = socket!.receive();
          if (datagram == null) return;
          if (datagram.data.length > DiscoveryConstants.maxDatagramBytes) return;

          final beacon = _tryParseResponse(datagram);
          if (beacon == null) return;

          // A PC with several interfaces answers once per interface; the first answer wins,
          // because it came back over the route that actually works.
          if (!seen.add(beacon.deviceId)) return;

          if (!controller.isClosed) controller.add(beacon);
        });

        void sendProbe() {
          final probe = utf8.encode(DiscoveryConstants.probeMagic);

          for (final target in _broadcastTargets()) {
            try {
              socket!.send(probe, target, port);
            } on SocketException {
              // A down interface or a blocked broadcast address. Others may still work, so
              // one failure must not abort the scan.
            }
          }
        }

        sendProbe();

        // Repeated because UDP is lossy and because a PC may finish starting mid-scan.
        probeTimer = Timer.periodic(probeInterval, (_) => sendProbe());
        stopTimer = Timer(duration, cleanup);
      } on Object catch (error) {
        if (!controller.isClosed) {
          controller.addError(error);
          await controller.close();
        }
      }
    }

    controller.onListen = start;
    return controller.stream;
  }

  /// Probes one known address directly, for reconnecting to a PC already paired.
  ///
  /// Faster and more reliable than a broadcast scan when the address is already known, and it
  /// works on networks where broadcast is filtered.
  Future<DiscoveryBeacon?> probe(
    String host, {
    Duration timeout = const Duration(seconds: 2),
  }) async {
    RawDatagramSocket? socket;

    try {
      socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);

      final completer = Completer<DiscoveryBeacon?>();

      socket.listen((RawSocketEvent event) {
        if (event != RawSocketEvent.read) return;

        final datagram = socket!.receive();
        if (datagram == null) return;

        final beacon = _tryParseResponse(datagram);
        if (beacon != null && !completer.isCompleted) {
          completer.complete(beacon);
        }
      });

      socket.send(utf8.encode(DiscoveryConstants.probeMagic), InternetAddress(host), port);

      return await completer.future.timeout(timeout, onTimeout: () => null);
    } on Object {
      return null;
    } finally {
      socket?.close();
    }
  }

  DiscoveryBeacon? _tryParseResponse(Datagram datagram) {
    final magic = utf8.encode(DiscoveryConstants.responseMagic);
    if (datagram.data.length <= magic.length) return null;

    for (var i = 0; i < magic.length; i++) {
      if (datagram.data[i] != magic[i]) return null;
    }

    try {
      final json = jsonDecode(utf8.decode(datagram.data.sublist(magic.length)));
      if (json is! Map<String, dynamic>) return null;

      return DiscoveryBeacon.fromJson(json, datagram.address.address);
    } on FormatException {
      return null;
    }
  }

  /// Builds the list of addresses to probe.
  ///
  /// Both the limited broadcast address and each interface's directed broadcast, because
  /// some access points drop one and some drop the other.
  Future<List<InternetAddress>> _broadcastTargetsAsync() async {
    final targets = <InternetAddress>[InternetAddress('255.255.255.255')];

    try {
      final interfaces = await NetworkInterface.list(
        includeLoopback: false,
        type: InternetAddressType.IPv4,
      );

      for (final interface in interfaces) {
        for (final address in interface.addresses) {
          final directed = _directedBroadcast(address.address);
          if (directed != null) targets.add(InternetAddress(directed));
        }
      }
    } on Object {
      // Interface enumeration can fail on some platforms; limited broadcast still works.
    }

    return targets;
  }

  List<InternetAddress> _cachedTargets = [InternetAddress('255.255.255.255')];

  List<InternetAddress> _broadcastTargets() {
    // Refreshed opportunistically: the cached list is used for this probe and updated for
    // the next, which keeps send() synchronous inside the socket callback.
    _broadcastTargetsAsync().then((value) => _cachedTargets = value).ignore();
    return _cachedTargets;
  }

  /// Assumes a /24, which is what essentially every home and office LAN uses.
  ///
  /// The real prefix length is not exposed by `NetworkInterface`, and guessing wrong only
  /// costs one wasted datagram — the limited broadcast address covers the same segment.
  static String? _directedBroadcast(String address) {
    final parts = address.split('.');
    if (parts.length != 4) return null;

    return '${parts[0]}.${parts[1]}.${parts[2]}.255';
  }
}
