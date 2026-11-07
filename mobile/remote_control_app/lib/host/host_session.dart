// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import '../protocol/frame_codec.dart';
import '../protocol/protocol.dart';
import 'host_catalog.dart';
import 'paired_viewer_store.dart';
import 'session_security.dart';

/// An authenticated, framed byte stream to one viewer, with what TLS learned about the peer.
///
/// On the phone the TLS listener is native (see `TlsControlServer.kt`): Dart's TLS server has no
/// way to accept a self-signed client certificate and read its fingerprint, which is the one
/// thing the protocol needs from it. Native code terminates TLS and hands the plaintext over a
/// loopback socket; tests hand over an in-memory stream.
abstract class HostTransport {
  Stream<Uint8List> get incoming;

  void write(List<int> bytes);

  Future<void> close();

  /// The viewer's address, for the approval dialog and the audit line. Never an authorization
  /// input.
  String get remoteAddress;

  /// `SHA-256` of the viewer's DER certificate, base64url: its identity.
  String get peerFingerprint;

  /// Negotiated TLS version and cipher, for display.
  String get tlsDescription;
}

/// The outcome of one command.
class HostResult {
  const HostResult._({required this.ok, this.data, this.code, this.message, this.retryable = false});

  const HostResult.success([Object? data]) : this._(ok: true, data: data);

  const HostResult.fail(String code, String message, {bool retryable = false})
      : this._(ok: false, code: code, message: message, retryable: retryable);

  static const HostResult empty = HostResult.success();

  static HostResult invalidArguments(String message) =>
      HostResult.fail(ErrorCodes.invalidArguments, message);

  static HostResult notSupported(String what) =>
      HostResult.fail(ErrorCodes.notSupported, 'This phone does not support $what.');

  static HostResult notFound(String what) => HostResult.fail(ErrorCodes.notFound, '$what was not found.');

  /// The refusal every input and app command gets while a blocked app is in front.
  static const HostResult blocked = HostResult.fail(
    ErrorCodes.blockedByOwner,
    'The app in front is blocked by the phone\'s owner.',
    retryable: true,
  );

  final bool ok;
  final Object? data;
  final String? code;
  final String? message;
  final bool retryable;

  Map<String, dynamic> toResponse(String id) => {
        'v': ProtocolVersion.current,
        'id': id,
        'ok': ok,
        if (ok && data != null) 'data': data,
        if (!ok) 'err': {'code': code, 'msg': message, 'retryable': retryable},
      };
}

/// Who is calling, as decided by the pairing record and the presented token.
class CallerIdentity {
  const CallerIdentity({
    required this.connectionId,
    required this.remoteAddress,
    required this.stage,
    this.viewer,
  });

  final String connectionId;
  final String remoteAddress;
  final CommandStage stage;

  /// The paired viewer, or null while the peer is unpaired.
  final PairedViewer? viewer;

  bool granted(String permission) => viewer?.granted(permission) ?? false;
}

/// One command, with everything a handler may need.
class HostCall {
  const HostCall({required this.command, required this.args, required this.caller, required this.session});

  final String command;
  final Map<String, dynamic> args;
  final CallerIdentity caller;
  final HostSession session;
}

typedef HostHandler = Future<HostResult> Function(HostCall call);

/// Routes commands to handlers after the catalog's checks: declared, reachable at this stage, and
/// permitted. Mirrors the PC's `CommandDispatcher` and `AuthorizationGate`.
class HostRouter {
  final _handlers = <String, HostHandler>{};

  void register(String command, HostHandler handler) {
    assert(HostCommandCatalog.find(command) != null, 'Register $command in the catalog first.');
    _handlers[command] = handler;
  }

  Future<HostResult> dispatch(HostCall call) async {
    final descriptor = HostCommandCatalog.find(call.command);
    if (descriptor == null) {
      return HostResult.fail(ErrorCodes.unknownCommand, 'Unknown command "${call.command}".');
    }

    switch (call.caller.stage) {
      case CommandStage.unpaired:
        if (descriptor.stage != CommandStage.unpaired) {
          return const HostResult.fail(ErrorCodes.notPaired, 'This device is not paired with the phone.');
        }
      case CommandStage.paired:
        if (descriptor.stage == CommandStage.authenticated) {
          return const HostResult.fail(
            ErrorCodes.unauthenticated,
            'Complete the handshake before sending commands.',
          );
        }
      case CommandStage.authenticated:
        break;
    }

    final permission = descriptor.permission;
    if (permission != null && !call.caller.granted(permission)) {
      return HostResult.fail(
        ErrorCodes.permissionDenied,
        'This device has not been granted "${Permissions.label(permission)}" on the phone.',
      );
    }

    final handler = _handlers[call.command];
    if (handler == null) return HostResult.notSupported(call.command);

    try {
      return await handler(call);
    } on Object {
      // Never the exception text: it may carry paths or internals (§7.5).
      return HostResult.fail(ErrorCodes.internal, 'The command failed on the phone.');
    }
  }
}

/// One viewer connection: the PC's `ClientSession`, in Dart.
///
/// Requests are processed strictly one at a time, in order, so the sequence check means something
/// and an input command can never overtake the `release_all` meant to precede it.
class HostSession {
  HostSession({
    required this.transport,
    required this.connectionId,
    required PairedViewer? viewer,
    required HostRouter router,
    required SessionTokenService tokens,
    required PairedViewerStore store,
    Clock clock = systemClock,
    this.keepaliveTimeout = const Duration(seconds: 15),
    int maxClockSkewSeconds = 30,
    int maxRequestsPerMinute = 6000,
  })  : _viewer = viewer,
        _router = router,
        _tokens = tokens,
        _store = store,
        _clock = clock,
        _replay = ReplayGuard(maxClockSkewSeconds: maxClockSkewSeconds),
        _rate = RequestRateLimiter(clock: clock, maxRequestsPerMinute: maxRequestsPerMinute),
        connectedAt = clock() {
    _lastActivity = connectedAt;
  }

  final HostTransport transport;
  final String connectionId;
  final HostRouter _router;
  final SessionTokenService _tokens;
  final PairedViewerStore _store;
  final Clock _clock;
  final ReplayGuard _replay;
  final RequestRateLimiter _rate;
  final Duration keepaliveTimeout;
  final DateTime connectedAt;

  final _codec = FrameCodec();
  final _queue = <Frame>[];
  final _closed = Completer<String>();

  PairedViewer? _viewer;
  SessionToken? _token;
  StreamSubscription<Uint8List>? _subscription;
  Timer? _keepalive;
  bool _pumping = false;
  bool _closing = false;
  late DateTime _lastActivity;

  String get remoteAddress => transport.remoteAddress;

  String get peerFingerprint => transport.peerFingerprint;

  String get tlsDescription => transport.tlsDescription;

  /// The paired viewer, or null while the peer is unpaired.
  PairedViewer? get viewer => _viewer;

  bool get isAuthenticated => _token != null;

  /// Completes with the reason when the connection ends.
  Future<String> get closed => _closed.future;

  bool get isClosed => _closed.isCompleted;

  /// Reads until the viewer disconnects, the keepalive expires or something fatal arrives.
  void run() {
    _subscription = transport.incoming.listen(
      _onData,
      onError: (Object error) => close(reason: 'The connection failed.'),
      onDone: () => close(reason: 'The viewer disconnected.'),
      cancelOnError: true,
    );

    // Checked often enough that a phone whose Wi-Fi dropped is noticed within a few seconds,
    // because until then its held touch stays held.
    _keepalive = Timer.periodic(const Duration(seconds: 5), (_) {
      if (_clock().difference(_lastActivity) > keepaliveTimeout) {
        close(reason: 'The viewer stopped answering.');
      }
    });
  }

  void _onData(Uint8List chunk) {
    _lastActivity = _clock();

    List<Frame> frames;
    try {
      frames = _codec.decode(chunk);
    } on MalformedFrameException {
      // The stream position is unknown from here on; nothing after it can be trusted.
      close(reason: 'The viewer sent a malformed message.');
      return;
    }

    _queue.addAll(frames);
    _pump();
  }

  Future<void> _pump() async {
    if (_pumping) return;
    _pumping = true;

    try {
      while (_queue.isNotEmpty && !_closing) {
        final frame = _queue.removeAt(0);
        await _handleFrame(frame);
      }
    } finally {
      _pumping = false;
    }
  }

  Future<void> _handleFrame(Frame frame) async {
    switch (frame.type) {
      case FrameType.request:
        await _handleRequest(frame.payload);
      case FrameType.ping:
        // Echoed verbatim so the viewer measures the round trip against its own clock.
        _write(FrameType.pong, frame.payload);
      case FrameType.pong:
      case FrameType.response:
      case FrameType.event:
        break;
    }
  }

  Future<void> _handleRequest(Uint8List payload) async {
    Map<String, dynamic>? request;
    try {
      final decoded = jsonDecode(utf8.decode(payload));
      if (decoded is Map<String, dynamic>) request = decoded;
    } on FormatException {
      request = null;
    }

    if (request == null) {
      _respond('', const HostResult.fail(ErrorCodes.malformedFrame, 'The request could not be parsed.'));
      return;
    }

    final id = request['id'] as String? ?? '';
    final version = request['v'] as int? ?? 0;

    if (version < ProtocolVersion.minSupported || version > ProtocolVersion.current) {
      _respond(
        id,
        HostResult.fail(
          ErrorCodes.versionUnsupported,
          'This phone speaks protocol versions ${ProtocolVersion.minSupported}-${ProtocolVersion.current}.',
        ),
      );
      return;
    }

    final sequence = (request['seq'] as num?)?.toInt() ?? 0;
    final timestamp = (request['ts'] as num?)?.toInt() ?? 0;
    final verdict = _replay.evaluate(sequence, timestamp, _clock().millisecondsSinceEpoch);

    if (verdict != ReplayVerdict.accepted) {
      // Fatal by design, as on the PC.
      _respond(id, HostResult.fail(ErrorCodes.replayDetected, ReplayGuard.describe(verdict)));
      await close(reason: 'Replay guard: ${ReplayGuard.describe(verdict)}.');
      return;
    }

    // After the replay guard, so a throttled request still advances the sequence.
    if (!_rate.tryAcquire()) {
      _respond(
        id,
        const HostResult.fail(ErrorCodes.rateLimited, 'Too many requests. Slow down.', retryable: true),
      );
      return;
    }

    final command = request['cmd'] as String? ?? '';
    final rawArgs = request['args'];
    final args = rawArgs is Map<String, dynamic> ? rawArgs : const <String, dynamic>{};

    final result = await _router.dispatch(HostCall(
      command: command,
      args: args,
      caller: _buildCaller(request['tok'] as String?),
      session: this,
    ));

    if (!_closing) _respond(id, result);
  }

  CallerIdentity _buildCaller(String? presentedToken) {
    final viewer = _viewer;
    if (viewer == null) {
      return CallerIdentity(
        connectionId: connectionId,
        remoteAddress: remoteAddress,
        stage: CommandStage.unpaired,
      );
    }

    final valid = _tokens.validate(presentedToken, connectionId) != null;

    // Permissions come from the live record, not the token's snapshot, so a permission the owner
    // withdrew applies to the very next command.
    return CallerIdentity(
      connectionId: connectionId,
      remoteAddress: remoteAddress,
      stage: valid ? CommandStage.authenticated : CommandStage.paired,
      viewer: viewer,
    );
  }

  /// Records the token issued by the handshake, binding it to this connection.
  void onAuthenticated(SessionToken token, PairedViewer viewer) {
    _token = token;
    _viewer = viewer;
  }

  /// Re-reads the viewer's record after the store changed: gone means revoked (close with the
  /// event), blocked means disconnected by the owner (close silently), changed permissions are
  /// announced.
  Future<void> refreshViewer() async {
    final current = _viewer;
    if (current == null) return;

    final refreshed = await _store.findByDeviceId(current.deviceId);

    if (refreshed == null) {
      _tokens.revokeDevice(current.deviceId);
      await close(reason: 'The pairing was revoked.', finalEventTopic: Events.pairingRevoked);
      return;
    }

    if (refreshed.blocked) {
      _tokens.revokeDevice(current.deviceId);
      await close(reason: 'Disconnected by the owner.');
      return;
    }

    final changed = refreshed.permissions.toSet().difference(current.permissions.toSet()).isNotEmpty ||
        current.permissions.toSet().difference(refreshed.permissions.toSet()).isNotEmpty;
    _viewer = refreshed;

    if (changed) {
      sendEvent(Events.permissionsChanged, {'permissions': refreshed.permissions});
    }
  }

  /// Pushes an event to this viewer.
  void sendEvent(String topic, Map<String, dynamic>? data) {
    if (_closing) return;

    _write(
      FrameType.event,
      utf8.encode(jsonEncode({
        'v': ProtocolVersion.current,
        'topic': topic,
        'ts': _clock().millisecondsSinceEpoch,
        if (data != null) 'data': data,
      })),
    );
  }

  void _respond(String id, HostResult result) {
    _write(FrameType.response, utf8.encode(jsonEncode(result.toResponse(id))));
  }

  void _write(FrameType type, List<int> payload) {
    if (_closing) return;

    try {
      transport.write(_codec.encode(type, payload));
    } on Object {
      close(reason: 'The connection failed.');
    }
  }

  /// Ends the connection, optionally sending one last event first.
  Future<void> close({required String reason, String? finalEventTopic}) async {
    if (_closing) return;

    if (finalEventTopic != null) sendEvent(finalEventTopic, const {});
    _closing = true;

    _keepalive?.cancel();
    _keepalive = null;
    _tokens.revokeConnection(connectionId);

    await _subscription?.cancel();
    _subscription = null;

    try {
      await transport.close();
    } on Object {
      // Already gone.
    }

    if (!_closed.isCompleted) _closed.complete(reason);
  }
}

/// Every live viewer connection, by connection id. Mirrors the PC's `ClientSessionManager`.
class HostSessionManager {
  final _sessions = <String, HostSession>{};
  final _changes = StreamController<void>.broadcast();

  int get count => _sessions.length;

  Iterable<HostSession> get all => _sessions.values;

  /// Fires when a session is added or removed.
  Stream<void> get changes => _changes.stream;

  HostSession? find(String connectionId) => _sessions[connectionId];

  void add(HostSession session) {
    _sessions[session.connectionId] = session;
    session.closed.whenComplete(() {
      _sessions.remove(session.connectionId);
      _notify();
    });
    _notify();
  }

  /// Sends an event to every authenticated viewer.
  void broadcast(String topic, Map<String, dynamic>? data) {
    for (final session in _sessions.values.toList()) {
      if (session.isAuthenticated) session.sendEvent(topic, data);
    }
  }

  /// Every connection re-reads its viewer's record.
  Future<void> refreshAll() async {
    for (final session in _sessions.values.toList()) {
      await session.refreshViewer();
    }
  }

  Future<void> closeAll(String reason) async {
    for (final session in _sessions.values.toList()) {
      await session.close(reason: reason);
    }
  }

  void _notify() {
    if (!_changes.isClosed) _changes.add(null);
  }

  Future<void> dispose() async {
    await closeAll('Sharing stopped.');
    await _changes.close();
  }
}
