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
    this.viaRemoteAddress = false,
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

  /// Whether the live connection was made through the PC's remote address rather than found
  /// on the local network.
  final bool viaRemoteAddress;

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
    bool? viaRemoteAddress,
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
        viaRemoteAddress: viaRemoteAddress ?? this.viaRemoteAddress,
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
///
/// A broadcast cannot cross a VPN, so a PC reached over one (Tailscale and the like) is never
/// found that way. For that the user can give the PC a remote address, which is tried last:
/// last address, then the scan, then the remote address. Every one of those connections is held
/// to the same pinned certificate — an address is only ever a place to look, never a reason to
/// trust what answers there.
class ConnectionController extends ChangeNotifier {
  ConnectionController({
    required DeviceIdentityStore identityStore,
    required PairedPcStore pairedStore,
    DiscoveryService? discovery,
    ControlClient Function(DeviceIdentity identity)? clientFactory,
  })  : _identityStore = identityStore,
        _pairedStore = pairedStore,
        _discovery = discovery ?? DiscoveryService(),
        _clientFactory = clientFactory ?? _newClient;

  static ControlClient _newClient(DeviceIdentity identity) => ControlClient(identity: identity);

  static const _maxBackoff = Duration(seconds: 30);

  // How long each step of finding the PC may take. The local steps are cut short when a remote
  // address is waiting behind them: away from home they are certain to fail, and the user is
  // watching a spinner until they have.
  static const _directTimeout = Duration(seconds: 3);
  static const _scanTime = Duration(seconds: 4);
  static const _scanTimeBeforeRemote = Duration(milliseconds: 2500);
  static const _foundTimeout = Duration(seconds: 8);
  static const _foundTimeoutBeforeRemote = Duration(seconds: 4);
  static const _remoteTimeout = Duration(seconds: 8);

  final DeviceIdentityStore _identityStore;
  final PairedPcStore _pairedStore;
  final DiscoveryService _discovery;

  /// Builds the client for one connection attempt. Replaceable so the order addresses are
  /// tried in can be tested without a network.
  final ControlClient Function(DeviceIdentity identity) _clientFactory;

  ControlClient? _client;
  StreamSubscription<EventEnvelope>? _eventSubscription;
  StreamSubscription<String>? _closedSubscription;
  Timer? _reconnectTimer;
  Timer? _renewTimer;
  Duration _backoff = const Duration(seconds: 1);
  bool _wantConnection = false;
  bool _disposed = false;

  /// Identifies the newest attempt. An older one that is still waiting on the network when a
  /// newer one starts, or when the user disconnects, must not go on to install its connection.
  int _attemptId = 0;

  ConnectionStatus _status = const ConnectionStatus();

  /// The current connection status.
  ConnectionStatus get status => _status;

  /// The live client, for feature code to send commands through.
  ControlClient? get client => _client;

  /// The PC's IP address on the live connection, or null when there is none.
  String? get peerAddress => _client?.peerAddress;

  /// Connects to a paired PC and keeps the connection up until [disconnect].
  Future<void> connect(PairedPc pc) async {
    _wantConnection = true;
    _backoff = const Duration(seconds: 1);
    _set(_status.copyWith(pc: pc, requiresRepair: false, clearMessage: true));
    await _attempt(pc);
  }

  /// Stops connecting and closes any live connection.
  /// Replaces the stored record this connection shows, e.g. after the user renamed the PC, so
  /// the new name appears at once rather than at the next connection.
  void updatePc(PairedPc pc) {
    if (_status.pc?.deviceId != pc.deviceId) return;
    _set(_status.copyWith(pc: pc));
  }

  Future<void> disconnect() async {
    _wantConnection = false;
    _attemptId++;
    _reconnectTimer?.cancel();
    _renewTimer?.cancel();

    await _teardown();

    _set(const ConnectionStatus(phase: ConnectionPhase.idle));
  }

  Future<void> _attempt(PairedPc requested) async {
    if (!_wantConnection || _disposed) return;

    final attempt = ++_attemptId;
    bool superseded() => attempt != _attemptId || _disposed;

    await _teardown();

    var pc = requested;

    try {
      // The record as it is stored now, not as it was when this connection was first asked
      // for: a retry may be running long after the user changed the PC's remote address.
      pc = await _pairedStore.find(requested.deviceId) ?? requested;

      final identity = await _identityStore.getOrCreate();
      if (superseded()) return;

      final connected = await _reach(pc, identity);

      if (superseded()) {
        await connected.client.dispose();
        return;
      }

      final client = connected.client;
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

      if (superseded()) return;

      _eventSubscription = client.events.listen(_onEvent);
      _closedSubscription = client.onClosed.listen((reason) => _onClosed(pc, reason));

      _scheduleRenewal(hello.tokenTtlSeconds);

      // Read again so that an address the user edited while this was connecting is kept.
      final stored = await _pairedStore.find(pc.deviceId) ?? pc;

      final seen = stored.copyWith(
        deviceName: hello.deviceName,
        lastSeenAt: DateTime.now(),
        permissions: hello.permissions,
      );

      // The remembered address is where the PC is on the local network. Getting through on the
      // remote address says nothing about that, so it must not replace it: back at home the
      // local address is still the fastest way in, and the remote address stays what the user
      // typed.
      final updated = connected.viaRemote
          ? seen
          : seen.copyWith(lastAddress: connected.host, lastPort: connected.port);

      await _pairedStore.save(updated);
      if (superseded()) return;

      _backoff = const Duration(seconds: 1);

      _set(_status.copyWith(
        phase: ConnectionPhase.ready,
        pc: updated,
        permissions: hello.permissions,
        capabilities: hello.capabilities,
        tlsDescription: client.tlsDescription,
        viaRemoteAddress: connected.viaRemote,
        clearMessage: true,
      ));

      await refreshState();
    } on CertificatePinMismatch catch (error) {
      if (superseded()) return;

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
      if (superseded()) return;

      if (error.code == ErrorCodes.notPaired) {
        // The PC's owner removed this device. No amount of retrying brings a pairing back, and
        // a banner counting down to the next attempt hides the one thing that would: pairing
        // again.
        _wantConnection = false;
        await _teardown();

        _set(_status.copyWith(
          phase: ConnectionPhase.failed,
          message: '${pc.displayName} no longer recognises this device. Pair with it again.',
          requiresRepair: true,
        ));
        return;
      }

      _scheduleRetry(pc, error.message);
    } on Object catch (error) {
      if (superseded()) return;
      _scheduleRetry(pc, 'Could not connect: $error');
    }
  }

  /// Gets a pinned connection to the PC, trying each place it might be in turn: the last
  /// address, wherever a scan finds it, then the remote address the user entered.
  ///
  /// Throws [ControlChannelException] with the reason to show when none of them works.
  Future<({ControlClient client, String host, int port, bool viaRemote})> _reach(
    PairedPc pc,
    DeviceIdentity identity,
  ) async {
    final remoteHost = pc.remoteAddress;
    final hasRemote = remoteHost != null;

    // Direct connect first: the remembered address usually still works, and connecting to it
    // needs no network search at all, so it also works where discovery is blocked.
    final direct = await _connectDirect(pc, identity);
    if (direct != null) {
      return (client: direct.client, host: direct.host, port: direct.port, viaRemote: false);
    }

    _set(_status.copyWith(
      phase: ConnectionPhase.locating,
      message: 'Looking for ${pc.displayName}…',
    ));

    var failure = '${pc.displayName} is not answering on this network.';

    final endpoint = await _locate(
      pc,
      scanFor: hasRemote ? _scanTimeBeforeRemote : _scanTime,

      // The second try of the remembered address is for a PC that was only slow. With a remote
      // address to fall back on it is not worth the wait.
      retryRemembered: !hasRemote,
    );

    if (endpoint != null) {
      _set(_status.copyWith(
        phase: ConnectionPhase.connecting,
        message: 'Connecting to ${endpoint.host}…',
      ));

      try {
        // A certificate mismatch here is final and propagates: the scan matched this PC's own
        // id, so something else answering for it is not an accident.
        final client = await _open(
          identity,
          pc,
          host: endpoint.host,
          port: endpoint.port,
          timeout: hasRemote ? _foundTimeoutBeforeRemote : _foundTimeout,
        );

        return (client: client, host: endpoint.host, port: endpoint.port, viaRemote: false);
      } on ControlChannelException catch (error) {
        if (!hasRemote) rethrow;
        failure = error.message;
      }
    }

    if (!hasRemote) throw ControlChannelException(failure);

    final remotePort = pc.effectiveRemotePort;
    final remoteLabel = pc.remoteAddressLabel ?? remoteHost;

    _set(_status.copyWith(
      phase: ConnectionPhase.connecting,
      message: 'Trying the remote address $remoteLabel…',
    ));

    try {
      final client = await _open(
        identity,
        pc,
        host: remoteHost,
        port: remotePort,
        timeout: _remoteTimeout,
      );

      return (client: client, host: remoteHost, port: remotePort, viaRemote: true);
    } on CertificatePinMismatch {
      // Refused exactly like any other mismatch — nothing was connected. But the likeliest
      // cause is an address typed for the wrong machine, not a reinstalled PC, so the message
      // points at the address instead of asking for a new pairing, and the search goes on: the
      // PC may still turn up on the local network.
      throw ControlChannelException(
        'The device at $remoteLabel is not ${pc.displayName}: its certificate does not match. '
        'Check the remote address on the Devices tab.',
      );
    } on ControlChannelException {
      throw ControlChannelException(
        '${pc.displayName} is not answering on this network or at $remoteLabel.',
      );
    }
  }

  /// Opens one connection, pinned to the PC's fingerprint.
  ///
  /// Every address this controller ever connects to goes through here, so there is exactly one
  /// place the pin is supplied and no path that could leave it out.
  Future<ControlClient> _open(
    DeviceIdentity identity,
    PairedPc pc, {
    required String host,
    required int port,
    required Duration timeout,
  }) async {
    final client = _clientFactory(identity);

    try {
      await client.connect(
        host: host,
        port: port,

        // The pinned fingerprint. A PC presenting anything else is refused outright rather
        // than trusted on first use.
        expectedFingerprint: pc.fingerprint,
        timeout: timeout,
      );

      return client;
    } on Object {
      await client.dispose();
      rethrow;
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

    try {
      final client = await _open(
        identity,
        pc,
        host: host,
        port: pc.lastPort,
        timeout: _directTimeout,
      );

      return (client: client, host: host, port: pc.lastPort);
    } on Object {
      return null;
    }
  }

  /// Finds where the PC is on this network right now.
  Future<({String host, int port})?> _locate(
    PairedPc pc, {
    required Duration scanFor,
    required bool retryRemembered,
  }) async {
    final remembered = pc.lastAddress;

    // Scan. Matching on device id rather than address is what makes a changed IP
    // a non-event.
    try {
      await for (final beacon in _discovery.scan(duration: scanFor)) {
        if (beacon.deviceId == pc.deviceId) {
          return (host: beacon.address, port: beacon.port);
        }
      }
    } on Object {
      // Fall through: a failed scan is not fatal if we have a remembered address to try.
    }

    // Last resort: the remembered address again, in case it was only slow to answer the direct
    // attempt.
    if (retryRemembered && remembered != null) {
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

        // Agents start and stop with signing in and out, and what the PC can do changes with
        // them: signing in at the lock screen from this phone brings apps and files back.
        _refreshCapabilities().ignore();

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

  /// Re-reads what the PC can do now. The handshake reports it once; this keeps it current.
  Future<void> _refreshCapabilities() async {
    final client = _client;
    if (client == null || !client.isConnected) return;

    try {
      final response = await client.send(Commands.deviceInfo);
      if (!response.ok) return;

      final capabilities =
          (response.map['capabilities'] as List<dynamic>? ?? const []).whereType<String>().toList();
      if (_client == client) _set(_status.copyWith(capabilities: capabilities));
    } on Object {
      // Keeps the list it had; the connection's own error handling reports a real failure.
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
