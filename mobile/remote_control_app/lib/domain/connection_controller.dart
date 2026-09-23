// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:async';

import 'package:device_info_plus/device_info_plus.dart';
import 'package:flutter/foundation.dart';

import '../data/control/control_client.dart';
import '../data/discovery/discovery_service.dart';
import '../data/identity/device_identity.dart';
import '../data/storage/paired_pc_store.dart';
import '../protocol/protocol.dart';

/// Where a connection attempt has got to.
enum ConnectionPhase {
  /// Nothing in progress.
  idle,

  /// Looking for the PC on the network.
  locating,

  /// TLS handshake in progress.
  connecting,

  /// Completing the protocol handshake.
  authenticating,

  /// Connected and usable.
  ready,

  /// Connected, but the PC reports it cannot serve desktop features right now.
  degraded,

  /// Waiting to retry.
  reconnecting,

  /// Stopped for a reason the user must act on.
  failed,
}

/// A snapshot of the connection, for the UI to render.
@immutable
class ConnectionStatus {
  const ConnectionStatus({
    this.phase = ConnectionPhase.idle,
    this.pc,
    this.message,
    this.permissions = const [],
    this.capabilities = const [],
    this.sessionState = 'Unknown',
    this.userName,
    this.sessionAgentConnected = false,
    this.tlsDescription,
    this.latencyMs,
    this.requiresRepair = false,
  });

  final ConnectionPhase phase;
  final PairedPc? pc;

  /// A message suitable for display. Set for every non-ready phase.
  final String? message;

  final List<String> permissions;
  final List<String> capabilities;

  /// `Active`, `Locked`, `LoggedOut` or `Unknown`, as reported by the PC.
  final String sessionState;

  final String? userName;
  final bool sessionAgentConnected;
  final String? tlsDescription;
  final int? latencyMs;

  /// Set when the PC refused because the pairing is gone, so the UI can offer to re-pair
  /// rather than retrying forever.
  final bool requiresRepair;

  bool get isConnected => phase == ConnectionPhase.ready || phase == ConnectionPhase.degraded;

  bool has(String capability) => capabilities.contains(capability);

  bool granted(String permission) => permissions.contains(permission);

  /// Whether the desktop is locked, which is why screen and input are unavailable.
  bool get isLocked => sessionState == 'Locked';

  /// Whether nobody is signed in to the PC.
  bool get isLoggedOut => sessionState == 'LoggedOut';

  ConnectionStatus copyWith({
    ConnectionPhase? phase,
    PairedPc? pc,
    String? message,
    List<String>? permissions,
    List<String>? capabilities,
    String? sessionState,
    String? userName,
    bool? sessionAgentConnected,
    String? tlsDescription,
    int? latencyMs,
    bool? requiresRepair,
    bool clearMessage = false,
  }) =>
      ConnectionStatus(
        phase: phase ?? this.phase,
        pc: pc ?? this.pc,
        message: clearMessage ? null : (message ?? this.message),
        permissions: permissions ?? this.permissions,
        capabilities: capabilities ?? this.capabilities,
        sessionState: sessionState ?? this.sessionState,
        userName: userName ?? this.userName,
        sessionAgentConnected: sessionAgentConnected ?? this.sessionAgentConnected,
        tlsDescription: tlsDescription ?? this.tlsDescription,
        latencyMs: latencyMs ?? this.latencyMs,
        requiresRepair: requiresRepair ?? this.requiresRepair,
      );
}

/// Owns the connection to one PC: finding it, connecting, authenticating, reconnecting.
///
/// ## Reconnection policy
///
/// Reconnect is automatic and backed off, with one important exception: if the PC says the
/// pairing is gone (`E_NOT_PAIRED`), retrying is pointless and looks like a hang. That case
/// stops and asks the user to pair again. Distinguishing "temporarily unreachable" from
/// "no longer authorised" is the difference between a spinner and an explanation.
///
/// ## Finding the PC
///
/// The last known address is tried first, because it usually still works and is instant. If
/// it does not answer, a broadcast scan finds the PC by device id — so a DHCP lease change
/// or moving between networks costs a couple of seconds rather than a re-pair.
class ConnectionController extends ChangeNotifier {
  ConnectionController({
    required DeviceIdentityStore identityStore,
    required PairedPcStore pairedStore,
    DiscoveryService? discovery,
  })  : _identityStore = identityStore,
        _pairedStore = pairedStore,
        _discovery = discovery ?? DiscoveryService();


  static const _maxBackoff = Duration(seconds: 30);

  final DeviceIdentityStore _identityStore;
  final PairedPcStore _pairedStore;
  final DiscoveryService _discovery;

  ControlClient? _client;
  StreamSubscription<EventEnvelope>? _eventSubscription;
  StreamSubscription<String>? _closedSubscription;
  Timer? _reconnectTimer;
  Timer? _renewTimer;
  Duration _backoff = const Duration(seconds: 1);
  bool _wantConnection = false;
  bool _disposed = false;

  ConnectionStatus _status = const ConnectionStatus();

  /// The current connection status.
  ConnectionStatus get status => _status;

  /// The live client, for feature code to send commands through.
  ControlClient? get client => _client;

  /// Connects to a paired PC and keeps the connection up until [disconnect].
  Future<void> connect(PairedPc pc) async {
    _wantConnection = true;
    _backoff = const Duration(seconds: 1);
    _set(_status.copyWith(pc: pc, requiresRepair: false, clearMessage: true));
    await _attempt(pc);
  }

  /// Stops connecting and closes any live connection.
  Future<void> disconnect() async {
    _wantConnection = false;
    _reconnectTimer?.cancel();
    _renewTimer?.cancel();

    await _teardown();

    _set(const ConnectionStatus(phase: ConnectionPhase.idle));
  }

  Future<void> _attempt(PairedPc pc) async {
    if (!_wantConnection || _disposed) return;

    await _teardown();

    try {
      final identity = await _identityStore.getOrCreate();

      // Direct connect first: the remembered address usually still works, and connecting to it
      // needs no network search at all, so it also works where discovery is blocked.
      var connected = await _connectDirect(pc, identity);

      if (connected == null) {
        _set(_status.copyWith(phase: ConnectionPhase.locating, message: 'Looking for ${pc.deviceName}…'));

        final endpoint = await _locate(pc);
        if (endpoint == null) {
          _scheduleRetry(pc, '${pc.deviceName} is not answering on this network.');
          return;
        }

        final client = ControlClient(identity: identity);

        _set(_status.copyWith(
          phase: ConnectionPhase.connecting,
          message: 'Connecting to ${endpoint.host}…',
        ));

        await client.connect(
          host: endpoint.host,
          port: endpoint.port,

          // The pinned fingerprint. A PC presenting anything else is refused outright rather
          // than trusted on first use.
          expectedFingerprint: pc.fingerprint,
        );

        connected = (client: client, host: endpoint.host, port: endpoint.port);
      }

      final client = connected.client;
      final endpoint = (host: connected.host, port: connected.port);
      _client = client;

      _set(_status.copyWith(
        phase: ConnectionPhase.authenticating,
        message: 'Authenticating…',
        tlsDescription: client.tlsDescription,
      ));

      final device = await describeThisDevice();

      final hello = await client.hello(
        deviceName: device.name,
        platform: device.platform,
        appVersion: '0.2.0',
      );

      _eventSubscription = client.events.listen(_onEvent);
      _closedSubscription = client.onClosed.listen((reason) => _onClosed(pc, reason));

      _scheduleRenewal(hello.tokenTtlSeconds);

      final updated = pc.copyWith(
        deviceName: hello.deviceName,
        lastAddress: endpoint.host,
        lastPort: endpoint.port,
        lastSeenAt: DateTime.now(),
        permissions: hello.permissions,
      );

      await _pairedStore.save(updated);

      _backoff = const Duration(seconds: 1);

      _set(_status.copyWith(
        phase: ConnectionPhase.ready,
        pc: updated,
        permissions: hello.permissions,
        capabilities: hello.capabilities,
        tlsDescription: client.tlsDescription,
        clearMessage: true,
      ));

      await refreshState();
    } on CertificatePinMismatch catch (error) {
      // Never retried. Either the PC was reinstalled — in which case re-pairing is the fix —
      // or something is impersonating it, in which case retrying is exactly wrong.
      _wantConnection = false;
      await _teardown();

      _set(_status.copyWith(
        phase: ConnectionPhase.failed,
        message: error.toString(),
        requiresRepair: true,
      ));
    } on ControlChannelException catch (error) {
      _scheduleRetry(pc, error.message);
    } on Object catch (error) {
      _scheduleRetry(pc, 'Could not connect: $error');
    }
  }

  /// Connects straight to the remembered address, without searching. Null when that fails.
  ///
  /// A certificate mismatch here is not treated as an attack: after a router restart another
  /// device may simply have been given the PC's old address. The search that follows finds the
  /// PC wherever it is now, and a mismatch there is final.
  Future<({ControlClient client, String host, int port})?> _connectDirect(
    PairedPc pc,
    DeviceIdentity identity,
  ) async {
    final host = pc.lastAddress;
    if (host == null) return null;

    _set(_status.copyWith(phase: ConnectionPhase.connecting, message: 'Connecting to $host…'));

    final client = ControlClient(identity: identity);
    try {
      await client.connect(
        host: host,
        port: pc.lastPort,
        expectedFingerprint: pc.fingerprint,
        timeout: const Duration(seconds: 3),
      );
      return (client: client, host: host, port: pc.lastPort);
    } on Object {
      await client.dispose();
      return null;
    }
  }

  /// Finds where the PC is right now.
  Future<({String host, int port})?> _locate(PairedPc pc) async {
    final remembered = pc.lastAddress;

    // Scan. Matching on device id rather than address is what makes a changed IP
    // a non-event.
    try {
      await for (final beacon in _discovery.scan(duration: const Duration(seconds: 4))) {
        if (beacon.deviceId == pc.deviceId) {
          return (host: beacon.address, port: beacon.port);
        }
      }
    } on Object {
      // Fall through: a failed scan is not fatal if we have a remembered address to try.
    }

    // Last resort: the remembered address again, in case it was only slow to answer the direct
    // attempt.
    if (remembered != null) {
      return (host: remembered, port: pc.lastPort);
    }

    return null;
  }

  void _scheduleRetry(PairedPc pc, String message) {
    _teardown().ignore();

    if (!_wantConnection || _disposed) {
      _set(_status.copyWith(phase: ConnectionPhase.idle, message: message));
      return;
    }

    _set(_status.copyWith(
      phase: ConnectionPhase.reconnecting,
      message: '$message Retrying in ${_backoff.inSeconds}s…',
    ));

    _reconnectTimer?.cancel();
    _reconnectTimer = Timer(_backoff, () => _attempt(pc));

    final doubled = _backoff * 2;
    _backoff = doubled > _maxBackoff ? _maxBackoff : doubled;
  }

  void _onClosed(PairedPc pc, String reason) {
    if (!_wantConnection) return;
    _scheduleRetry(pc, reason);
  }

  /// Every event the PC pushes, re-published so feature screens (the remote desktop's telemetry,
  /// monitor hot-plug) can listen without reaching into the connection's internals. Survives
  /// reconnects: a listener attached once keeps receiving from whichever connection is current.
  Stream<EventEnvelope> get events => _eventsOut.stream;

  final _eventsOut = StreamController<EventEnvelope>.broadcast();

  void _onEvent(EventEnvelope event) {
    if (!_eventsOut.isClosed) {
      _eventsOut.add(event);
    }

    switch (event.topic) {
      case Events.sessionStateChanged:
        final data = event.data ?? const {};
        _set(_status.copyWith(
          sessionState: data['sessionState'] as String? ?? _status.sessionState,
          userName: data['userName'] as String?,
          sessionAgentConnected:
              data['sessionAgentConnected'] as bool? ?? _status.sessionAgentConnected,
        ));

      case Events.permissionsChanged:
        final granted =
            (event.data?['permissions'] as List<dynamic>? ?? const []).whereType<String>().toList();
        _set(_status.copyWith(permissions: granted));

      case Events.pairingRevoked:
        // The PC revoked us. Retrying cannot help, so stop and say so.
        _wantConnection = false;
        _teardown().ignore();

        _set(_status.copyWith(
          phase: ConnectionPhase.failed,
          message: 'This device\'s access was revoked on the PC.',
          requiresRepair: true,
        ));
    }
  }

  /// Re-reads the PC's live state.
  Future<void> refreshState() async {
    final client = _client;
    if (client == null || !client.isConnected) return;

    try {
      final stopwatch = Stopwatch()..start();
      final response = await client.send(Commands.systemState);
      stopwatch.stop();

      if (!response.ok) {
        if (response.error?.code == ErrorCodes.notPaired) {
          _wantConnection = false;
          _set(_status.copyWith(
            phase: ConnectionPhase.failed,
            message: 'The PC no longer recognises this device. Pair again.',
            requiresRepair: true,
          ));
        }
        return;
      }

      final data = response.map;

      _set(_status.copyWith(
        sessionState: data['sessionState'] as String? ?? 'Unknown',
        userName: data['userName'] as String?,
        sessionAgentConnected: data['sessionAgentConnected'] as bool? ?? false,
        latencyMs: stopwatch.elapsedMilliseconds,
      ));
    } on Object {
      // The connection's own error handling will surface a real failure.
    }
  }

  /// Sends a command, returning null when not connected.
  Future<ResponseEnvelope?> send(String command, {Map<String, dynamic>? args}) async {
    final client = _client;
    if (client == null || !client.isConnected) return null;

    try {
      return await client.send(command, args: args);
    } on ControlChannelException {
      return null;
    }
  }

  void _scheduleRenewal(int ttlSeconds) {
    _renewTimer?.cancel();

    // Renew at two thirds of the lifetime: comfortably before expiry, and rare enough to be
    // invisible.
    final interval = Duration(seconds: (ttlSeconds * 2 ~/ 3).clamp(30, 3600));

    _renewTimer = Timer.periodic(interval, (_) async {
      final client = _client;
      if (client == null || !client.isConnected) return;

      try {
        await client.renew();
      } on Object {
        // An expired token surfaces as a refused command, which triggers reconnection.
      }
    });
  }

  Future<void> _teardown() async {
    await _eventSubscription?.cancel();
    _eventSubscription = null;

    await _closedSubscription?.cancel();
    _closedSubscription = null;

    final client = _client;
    _client = null;

    if (client != null) {
      await client.dispose();
    }
  }

  void _set(ConnectionStatus status) {
    if (_disposed) return;
    _status = status;
    notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _wantConnection = false;
    _reconnectTimer?.cancel();
    _renewTimer?.cancel();
    _teardown().ignore();
    _eventsOut.close().ignore();
    super.dispose();
  }
}

/// What this phone calls itself in the PC's approval dialog and paired-devices list.
Future<({String name, String platform, String model})> describeThisDevice() async {
  final info = DeviceInfoPlugin();

  try {
    if (defaultTargetPlatform == TargetPlatform.android) {
      final android = await info.androidInfo;
      return (
        name: android.model,
        platform: 'android',
        model: '${android.manufacturer} ${android.model}',
      );
    }

    if (defaultTargetPlatform == TargetPlatform.iOS) {
      final ios = await info.iosInfo;
      return (name: ios.name, platform: 'ios', model: ios.utsname.machine);
    }
  } on Object {
    // Device info is cosmetic: it decides what the PC's approval dialog displays, so a
    // failure degrades to a generic name rather than blocking the connection.
  }

  return (name: 'Mobile device', platform: defaultTargetPlatform.name, model: 'unknown');
}
