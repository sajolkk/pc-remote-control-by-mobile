import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:uuid/uuid.dart';

import '../../protocol/protocol.dart';
import '../identity/device_identity.dart';

/// Thrown when the PC's certificate does not match the pinned fingerprint.
///
/// Deliberately its own type: it is the one connection failure that means "something is
/// impersonating your PC", and the UI must say that rather than showing a generic
/// "couldn't connect".
class CertificatePinMismatch implements Exception {
  const CertificatePinMismatch(this.expected, this.actual);

  final String expected;
  final String actual;

  @override
  String toString() =>
      'The PC presented certificate $actual but $expected was expected. '
      'Do not continue: another device may be impersonating your PC.';
}

/// Raised when the transport fails in an ordinary way.
class ControlChannelException implements Exception {
  const ControlChannelException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// The control channel: mutually authenticated TLS carrying framed JSON.
///
/// ## How trust works here
///
/// There is no certificate authority. The PC's certificate is accepted by
/// [SecureSocket]'s `onBadCertificate` callback — which is safe *only* because the
/// fingerprint is then compared against the value learned out-of-band from the QR code at
/// pairing time. Without that comparison this would be an unauthenticated channel, so the
/// two must never be separated: [connect] refuses to return a usable socket if the pin
/// does not match.
///
/// ## Framing
///
/// `[4-byte big-endian payload length][1-byte frame type][payload]`, identical to the PC
/// side. The reader buffers until a whole frame is available, because a TCP read returns
/// whatever happens to have arrived.
class ControlClient {
  ControlClient({required this.identity});

  /// Largest frame accepted from the PC, matching the PC's own limit.
  static const int maxPayloadBytes = 1024 * 1024;

  static const _headerBytes = 5;
  static const _uuid = Uuid();

  final DeviceIdentity identity;

  final _responses = <String, Completer<ResponseEnvelope>>{};
  final _events = StreamController<EventEnvelope>.broadcast();
  final _buffer = BytesBuilder(copy: false);

  SecureSocket? _socket;
  StreamSubscription<Uint8List>? _subscription;
  Timer? _keepalive;
  int _sequence = 0;
  String? _token;
  bool _closed = false;

  /// Events pushed by the PC.
  Stream<EventEnvelope> get events => _events.stream;

  /// Whether the channel is currently connected.
  bool get isConnected => _socket != null && !_closed;

  /// The session token in force, if the handshake has completed.
  String? get token => _token;

  /// The TLS version that was negotiated, for display in the UI.
  String tlsDescription = 'unknown';

  /// Raised when the connection drops for any reason.
  final _closedController = StreamController<String>.broadcast();

  /// Emits a human-readable reason when the connection ends.
  Stream<String> get onClosed => _closedController.stream;

  /// Connects and verifies the PC's pinned certificate.
  ///
  /// [expectedFingerprint] is null only during the very first pairing connection, when the
  /// fingerprint comes from the QR code and is checked by the caller against what the PC
  /// reports back.
  Future<void> connect({
    required String host,
    required int port,
    String? expectedFingerprint,
    Duration timeout = const Duration(seconds: 8),
  }) async {
    if (_socket != null) {
      throw const ControlChannelException('Already connected.');
    }

    final context = SecurityContext(withTrustedRoots: false)
      ..useCertificateChainBytes(utf8.encode(identity.certificatePem))
      ..usePrivateKeyBytes(utf8.encode(identity.privateKeyPem));

    String? actualFingerprint;
    CertificatePinMismatch? mismatch;

    final socket = await SecureSocket.connect(
      host,
      port,
      context: context,
      timeout: timeout,

      // There is no CA to validate against, so every certificate arrives "bad". Accepting
      // it here is only half the check: the fingerprint comparison below is what actually
      // establishes identity, and a mismatch closes the socket immediately.
      onBadCertificate: (certificate) {
        actualFingerprint = DeviceIdentityStore.fingerprintOfDer(certificate.der);

        if (expectedFingerprint == null) {
          return true;
        }

        if (actualFingerprint == expectedFingerprint) {
          return true;
        }

        mismatch = CertificatePinMismatch(expectedFingerprint, actualFingerprint!);
        return false;
      },
    ).catchError((Object error) {
      final pinFailure = mismatch;
      if (pinFailure != null) {
        throw pinFailure;
      }

      throw ControlChannelException('Could not reach the PC at $host:$port. ${_describe(error)}');
    });

    // Belt and braces: if the callback were ever changed to return true unconditionally,
    // this second check still refuses an unpinned connection.
    if (expectedFingerprint != null && actualFingerprint != expectedFingerprint) {
      socket.destroy();
      throw CertificatePinMismatch(expectedFingerprint, actualFingerprint ?? 'unknown');
    }

    socket.setOption(SocketOption.tcpNoDelay, true);
    tlsDescription = socket.selectedProtocol ?? 'TLS';

    _socket = socket;
    _closed = false;
    _sequence = 0;

    _subscription = socket.listen(
      _onData,
      onError: (Object error) => _shutdown('The connection failed: ${_describe(error)}'),
      onDone: () => _shutdown('The PC closed the connection.'),
      cancelOnError: true,
    );
  }

  /// The fingerprint the PC presented on this connection.
  String? serverFingerprint;

  /// Sends a command and waits for its response.
  Future<ResponseEnvelope> send(
    String command, {
    Map<String, dynamic>? args,
    bool includeToken = true,
    Duration timeout = const Duration(seconds: 20),
  }) async {
    final socket = _socket;
    if (socket == null || _closed) {
      throw const ControlChannelException('Not connected.');
    }

    _sequence++;

    final request = RequestEnvelope(
      id: _uuid.v4().substring(0, 8),
      command: command,
      sequence: _sequence,
      timestampMs: DateTime.now().toUtc().millisecondsSinceEpoch,
      args: args,
      token: includeToken ? _token : null,
    );

    final completer = Completer<ResponseEnvelope>();
    _responses[request.id] = completer;

    try {
      _writeFrame(socket, FrameType.request, utf8.encode(jsonEncode(request.toJson())));

      return await completer.future.timeout(
        timeout,
        onTimeout: () => throw ControlChannelException(
          'The PC did not answer "$command" within ${timeout.inSeconds}s.',
        ),
      );
    } finally {
      _responses.remove(request.id);
    }
  }

  /// Completes the handshake and stores the session token.
  Future<HelloResult> hello({
    required String deviceName,
    required String platform,
    required String appVersion,
  }) async {
    final response = await send(
      Commands.hello,
      includeToken: false,
      args: {
        'minVersion': ProtocolVersion.minSupported,
        'maxVersion': ProtocolVersion.current,
        'deviceName': deviceName,
        'platform': platform,
        'appVersion': appVersion,
      },
    );

    if (!response.ok) {
      throw ControlChannelException(response.error?.message ?? 'The handshake was refused.');
    }

    final result = HelloResult.fromJson(response.map);
    _token = result.token;
    _startKeepalive(result.keepaliveTimeoutSeconds);
    return result;
  }

  /// Submits a pairing token. Only valid on a connection that is not yet paired.
  Future<ResponseEnvelope> pair({
    required String pairingToken,
    required String deviceId,
    required String deviceName,
    required String platform,
    required String model,
  }) =>
      send(
        Commands.pairRequest,
        includeToken: false,

        // Pairing waits for a human to click Allow on the PC, so the timeout has to be
        // generous — a 20-second default would fail while the user is still reading.
        timeout: const Duration(minutes: 2),
        args: {
          'deviceId': deviceId,
          'deviceName': deviceName,
          'platform': platform,
          'model': model,
          'token': pairingToken,
          'clientFingerprint': identity.fingerprint,
        },
      );

  /// Renews the session token before it expires.
  Future<void> renew() async {
    final response = await send(Commands.sessionRenew);
    if (response.ok) {
      _token = response.map['token'] as String? ?? _token;
    }
  }

  void _startKeepalive(int timeoutSeconds) {
    _keepalive?.cancel();

    // Ping at a third of the PC's idle timeout: frequent enough that a healthy connection
    // is never dropped, rare enough to be irrelevant to battery.
    final interval = Duration(seconds: (timeoutSeconds ~/ 3).clamp(2, 30));

    _keepalive = Timer.periodic(interval, (_) {
      final socket = _socket;
      if (socket == null || _closed) return;

      try {
        final payload = utf8.encode(jsonEncode({
          'nonce': _uuid.v4().substring(0, 8),
          'ts': DateTime.now().toUtc().millisecondsSinceEpoch,
        }));

        _writeFrame(socket, FrameType.ping, payload);
      } on Object {
        // A failed write means the socket is gone; the done/error handler deals with it.
      }
    });
  }

  void _writeFrame(SecureSocket socket, FrameType type, List<int> payload) {
    if (payload.length > maxPayloadBytes) {
      throw const ControlChannelException('The message is too large to send.');
    }

    final header = Uint8List(_headerBytes);
    header[0] = (payload.length >> 24) & 0xFF;
    header[1] = (payload.length >> 16) & 0xFF;
    header[2] = (payload.length >> 8) & 0xFF;
    header[3] = payload.length & 0xFF;
    header[4] = type.value;

    socket.add(header);
    if (payload.isNotEmpty) {
      socket.add(payload);
    }
  }

  void _onData(Uint8List data) {
    _buffer.add(data);
    _drain();
  }

  /// Extracts every complete frame currently buffered.
  void _drain() {
    while (true) {
      final buffered = _buffer.toBytes();
      if (buffered.length < _headerBytes) {
        _buffer
          ..clear()
          ..add(buffered);
        return;
      }

      final length = (buffered[0] << 24) | (buffered[1] << 16) | (buffered[2] << 8) | buffered[3];

      if (length < 0 || length > maxPayloadBytes) {
        // A length we would not have sent means the stream is desynchronised or the peer is
        // not what we think. There is no safe way to resynchronise, so the connection ends.
        _shutdown('The PC sent a malformed message.');
        return;
      }

      final total = _headerBytes + length;
      if (buffered.length < total) {
        _buffer
          ..clear()
          ..add(buffered);
        return;
      }

      final type = FrameType.fromValue(buffered[4]);
      final payload = buffered.sublist(_headerBytes, total);

      _buffer
        ..clear()
        ..add(buffered.sublist(total));

      if (type != null) {
        _dispatch(type, payload);
      }
    }
  }

  void _dispatch(FrameType type, List<int> payload) {
    switch (type) {
      case FrameType.response:
        final decoded = _tryDecode(payload);
        if (decoded == null) return;

        final response = ResponseEnvelope.fromJson(decoded);
        _responses.remove(response.id)?.complete(response);

      case FrameType.event:
        final decoded = _tryDecode(payload);
        if (decoded == null) return;

        _events.add(EventEnvelope.fromJson(decoded));

      case FrameType.ping:
        // The PC does not currently ping us, but answering costs nothing and keeps the
        // protocol symmetric.
        final socket = _socket;
        if (socket != null) _writeFrame(socket, FrameType.pong, payload);

      case FrameType.pong:
      case FrameType.request:
        break;
    }
  }

  Map<String, dynamic>? _tryDecode(List<int> payload) {
    try {
      final decoded = jsonDecode(utf8.decode(payload));
      return decoded is Map<String, dynamic> ? decoded : null;
    } on FormatException {
      return null;
    }
  }

  void _shutdown(String reason) {
    if (_closed) return;
    _closed = true;

    _keepalive?.cancel();
    _keepalive = null;

    // Every waiter must be completed or the UI hangs on a future that will never finish.
    for (final completer in _responses.values) {
      if (!completer.isCompleted) {
        completer.completeError(ControlChannelException(reason));
      }
    }

    _responses.clear();
    _token = null;

    _subscription?.cancel();
    _subscription = null;

    try {
      _socket?.destroy();
    } on Object {
      // Already gone.
    }

    _socket = null;

    if (!_closedController.isClosed) {
      _closedController.add(reason);
    }
  }

  /// Closes the connection.
  Future<void> close() async {
    _shutdown('Disconnected.');
  }

  /// Releases every resource. The client cannot be reused afterwards.
  Future<void> dispose() async {
    await close();
    await _events.close();
    await _closedController.close();
  }

  static String _describe(Object error) {
    if (error is SocketException) {
      return error.osError?.message ?? error.message;
    }

    if (error is HandshakeException) {
      return 'The secure connection could not be established.';
    }

    if (error is TimeoutException) {
      return 'The connection timed out.';
    }

    return error.toString();
  }
}

/// The result of a successful handshake.
class HelloResult {
  const HelloResult({
    required this.version,
    required this.deviceId,
    required this.deviceName,
    required this.token,
    required this.tokenTtlSeconds,
    required this.permissions,
    required this.capabilities,
    required this.keepaliveTimeoutSeconds,
  });

  factory HelloResult.fromJson(Map<String, dynamic> json) => HelloResult(
        version: json['version'] as int? ?? 1,
        deviceId: json['deviceId'] as String? ?? '',
        deviceName: json['deviceName'] as String? ?? 'Windows PC',
        token: json['token'] as String? ?? '',
        tokenTtlSeconds: json['tokenTtlSec'] as int? ?? 600,
        permissions:
            (json['permissions'] as List<dynamic>? ?? const []).whereType<String>().toList(),
        capabilities:
            (json['capabilities'] as List<dynamic>? ?? const []).whereType<String>().toList(),
        keepaliveTimeoutSeconds: json['keepaliveTimeoutSec'] as int? ?? 15,
      );

  final int version;
  final String deviceId;
  final String deviceName;
  final String token;
  final int tokenTtlSeconds;
  final List<String> permissions;
  final List<String> capabilities;
  final int keepaliveTimeoutSeconds;

  bool has(String capability) => capabilities.contains(capability);

  bool granted(String permission) => permissions.contains(permission);
}
