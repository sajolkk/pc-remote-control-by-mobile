// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:async';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';

import '../domain/connection_controller.dart';
import '../protocol/protocol.dart';
import 'android_host_platform.dart';
import 'block_list.dart';
import 'discovery_responder.dart';
import 'host_handlers.dart';
import 'host_identity.dart';
import 'host_input.dart';
import 'host_pairing.dart';
import 'host_session.dart';
import 'paired_viewer_store.dart';
import 'screen_share.dart';
import 'session_security.dart';
import 'tls_bridge.dart';

/// Where sharing has got to.
enum HostPhase { idle, starting, sharing, failed }

/// A pairing request waiting for the owner's answer.
class PendingApproval {
  PendingApproval(this.request);

  final PairingApprovalRequest request;
  final completer = Completer<PairingDecision>();
}

/// One connected viewer, for the Share screen's list.
class ConnectedViewer {
  const ConnectedViewer({
    required this.connectionId,
    required this.name,
    required this.address,
    required this.authenticated,
    required this.streaming,
    required this.since,
  });

  final String connectionId;
  final String name;
  final String address;
  final bool authenticated;
  final bool streaming;
  final DateTime since;
}

/// Runs this phone as a host: the Share screen's model.
///
/// Start order matters on Android, and is the reason this class exists:
///
/// 1. the owner grants screen capture (the system dialog, every time);
/// 2. the foreground service of type `mediaProjection` starts, which Android 14 allows only after
///    that consent, and which Android 10 and later require before the capture may begin;
/// 3. the capture begins and the shared video track exists;
/// 4. the TLS listener, the loopback bridge and the discovery responder come up;
/// 5. a pairing window opens and the QR code is shown.
///
/// Stopping undoes it in reverse. The accessibility service is separate: the owner enables it once
/// in Settings, and sharing works without it, just view-only.
class HostController extends ChangeNotifier {
  HostController({
    required HostIdentityStore identityStore,
    required PairedViewerStore viewerStore,
    required AndroidHostPlatform platform,
    required Future<String> Function() deviceName,
    this.appVersion = '0.3.0',
    int preferredPort = DiscoveryConstants.defaultControlPort,
    Clock clock = systemClock,
  })  : _identityStore = identityStore,
        _viewerStore = viewerStore,
        _platform = platform,
        _deviceName = deviceName,
        _preferredPort = preferredPort,
        _clock = clock {
    gate = BlockListGate();
    sessions = HostSessionManager();
    screen = ScreenShare(gate: gate, sessionOf: sessions.find);
    _input = InputGateway(platform: platform, gate: gate);
    _storeSubscription = viewerStore.changes.listen((_) {
      sessions.refreshAll().ignore();
      _refreshPaired();
    });
    _sessionSubscription = sessions.changes.listen((_) => notifyListeners());
    screen.addListener(notifyListeners);
    gate.addListener(notifyListeners);
    _refreshPaired();
    _loadBlockList();
  }

  final HostIdentityStore _identityStore;
  final PairedViewerStore _viewerStore;
  final AndroidHostPlatform _platform;
  final Future<String> Function() _deviceName;
  final int _preferredPort;
  final Clock _clock;
  final String appVersion;

  late final BlockListGate gate;
  late final HostSessionManager sessions;
  late final ScreenShare screen;
  late final InputGateway _input;

  HostPairingService? _pairing;
  HostRouter? _router;
  HostIdentity? _identity;
  TlsBridgeListener? _bridge;
  DiscoveryResponder? _responder;
  SessionTokenService? _tokens;
  StreamSubscription<void>? _storeSubscription;
  StreamSubscription<void>? _sessionSubscription;
  StreamSubscription<HostPlatformEvent>? _platformEvents;
  Timer? _windowTimer;

  HostPhase _phase = HostPhase.idle;
  String? _message;
  String _name = 'Phone';
  int _port = 0;
  List<String> _addresses = const [];
  List<PairedViewer> _paired = const [];
  PendingApproval? _pending;
  bool _accessibilityEnabled = false;
  bool _disposed = false;

  HostPhase get phase => _phase;

  /// Why sharing failed, or what it is doing.
  String? get message => _message;

  bool get isSharing => _phase == HostPhase.sharing;

  String get deviceName => _name;

  int get port => _port;

  List<String> get addresses => _addresses;

  HostIdentity? get identity => _identity;

  bool get accessibilityEnabled => _accessibilityEnabled;

  /// The open pairing window, or null.
  PairingWindow? get pairingWindow => _pairing?.currentWindow;

  /// The QR code's content, or null when pairing is closed.
  String? get qrPayload {
    final window = pairingWindow;
    final identity = _identity;
    if (window == null || identity == null) return null;

    return PairingQrPayload(
      deviceId: identity.deviceId,
      deviceName: _name,
      fingerprint: identity.fingerprint,
      token: window.token,
      port: _port,
      hosts: _addresses,
      kind: DeviceKinds.phone,
    ).encode();
  }

  /// A viewer asking to pair, waiting for [decide].
  PendingApproval? get pendingApproval => _pending;

  List<PairedViewer> get pairedViewers => _paired;

  List<ConnectedViewer> get connectedViewers => [
        for (final session in sessions.all)
          ConnectedViewer(
            connectionId: session.connectionId,
            name: session.viewer?.deviceName ?? 'Pairing…',
            address: session.remoteAddress,
            authenticated: session.isAuthenticated,
            streaming: screen.sessionIdOf(session.connectionId) != null,
            since: session.connectedAt,
          ),
      ];

  // --- Lifecycle ---

  Future<void> start() async {
    if (_phase == HostPhase.starting || _phase == HostPhase.sharing) return;
    _set(HostPhase.starting, 'Asking for screen capture…');

    try {
      _name = await _deviceName();
      _accessibilityEnabled = await _platform.isAccessibilityEnabled();
      await _platform.requestNotificationPermission();

      if (!await screen.requestConsent()) {
        _set(HostPhase.failed, 'Screen capture was not allowed. Tap Start and choose "Start now".');
        return;
      }

      _set(HostPhase.starting, 'Starting the capture…');
      await _platform.startForegroundService();
      await screen.startCapture();

      _set(HostPhase.starting, 'Opening the connection…');
      final identity = await _identityStore.getOrCreate();
      _identity = identity;

      final tokens = SessionTokenService(clock: _clock);
      _tokens = tokens;

      final pairing = HostPairingService(
        store: _viewerStore,
        approver: _askOwner,
        hostFingerprint: identity.fingerprint,
        clock: _clock,
      );
      pairing.addListener(notifyListeners);
      _pairing = pairing;

      _router = buildHostRouter(HostEnvironment(
        info: HostInfo(
          deviceId: identity.deviceId,
          deviceName: _name,
          fingerprint: identity.fingerprint,
          controlPort: 0,
          localAddresses: const [],
          appVersion: appVersion,
        ),
        store: _viewerStore,
        tokens: tokens,
        pairing: pairing,
        gate: gate,
        sessions: sessions,
        platform: _platform,
        screen: screen,
        input: _input,
        clock: _clock,
      ));

      final secret = randomToken(32);
      final bridge = TlsBridgeListener(secret: secret, onConnection: _accept);
      final loopbackPort = await bridge.start();
      _bridge = bridge;

      _port = await _platform.startTlsServer(
        certificatePem: identity.certificatePem,
        privateKeyPem: identity.privateKeyPem,
        preferredPort: _preferredPort,
        loopbackPort: loopbackPort,
        secret: secret,
      );
      if (_port <= 0) throw StateError('The listener did not bind a port.');

      _addresses = await _localAddresses();

      // The router's info was built before the port was known; rebuild it now that it is.
      _router = buildHostRouter(HostEnvironment(
        info: HostInfo(
          deviceId: identity.deviceId,
          deviceName: _name,
          fingerprint: identity.fingerprint,
          controlPort: _port,
          localAddresses: _addresses,
          appVersion: appVersion,
        ),
        store: _viewerStore,
        tokens: tokens,
        pairing: pairing,
        gate: gate,
        sessions: sessions,
        platform: _platform,
        screen: screen,
        input: _input,
        clock: _clock,
      ));

      final responder = DiscoveryResponder(
        beacon: () => DiscoveryBeacon(
          deviceId: identity.deviceId,
          deviceName: _name,
          port: _port,
          protocolVersion: ProtocolVersion.current,
          address: '',
          kind: DeviceKinds.phone,
        ),
      );
      await responder.start();
      _responder = responder;

      _platformEvents = _platform.events.listen(_onPlatformEvent);
      gate.setForeground(await _platform.foregroundPackage());

      _windowTimer = Timer.periodic(const Duration(seconds: 15), (_) {
        // The window closes itself; the screen must notice and offer a new code.
        if (_pairing?.currentWindow == null) notifyListeners();
      });

      pairing.openWindow();
      _set(HostPhase.sharing, null);
    } on Object catch (error) {
      await _teardown();
      _set(HostPhase.failed, 'Sharing could not start: $error');
    }
  }

  Future<void> stop() async {
    await _teardown();
    _set(HostPhase.idle, null);
  }

  Future<void> _teardown() async {
    _windowTimer?.cancel();
    _windowTimer = null;

    await _platformEvents?.cancel();
    _platformEvents = null;

    _pending?.completer.complete(PairingDecision.declined);
    _pending = null;

    await sessions.closeAll('Sharing stopped.');
    _responder?.stop();
    _responder = null;

    try {
      await _platform.stopTlsServer();
    } on Object {
      // Not started.
    }

    await _bridge?.stop();
    _bridge = null;

    await screen.stopCapture();

    try {
      await _platform.stopForegroundService();
    } on Object {
      // Not started.
    }

    _pairing?.closeWindow();
    _pairing?.removeListener(notifyListeners);
    _pairing = null;
    _tokens = null;
    _router = null;
    _port = 0;
  }

  /// Issues a fresh QR code: a new single-use token.
  void newPairingCode() {
    _pairing?.openWindow();
    notifyListeners();
  }

  /// Re-reads whether the accessibility service is on, e.g. when the app comes back from Settings.
  Future<void> refreshAccessibility() async {
    _accessibilityEnabled = await _platform.isAccessibilityEnabled();
    notifyListeners();
  }

  Future<void> openAccessibilitySettings() => _platform.openAccessibilitySettings();

  // --- Connections ---

  void _accept(HostTransport transport) {
    final router = _router;
    final tokens = _tokens;
    if (router == null || tokens == null) {
      transport.close().ignore();
      return;
    }

    _viewerStore.findByFingerprint(transport.peerFingerprint).then((viewer) {
      if (_router == null) {
        transport.close().ignore();
        return;
      }

      final session = HostSession(
        transport: transport,
        connectionId: _connectionId(),
        viewer: viewer,
        router: router,
        tokens: tokens,
        store: _viewerStore,
        clock: _clock,
      );

      sessions.add(session);
      session.closed.whenComplete(() {
        _input.releaseAll(session.connectionId);
        screen.stop(session.connectionId, null).ignore();
      });
      session.run();
    });
  }

  Future<PairingDecision> _askOwner(PairingApprovalRequest request, Duration timeout) {
    final pending = PendingApproval(request);
    _pending = pending;
    notifyListeners();

    return pending.completer.future.whenComplete(() {
      if (_pending == pending) {
        _pending = null;
        notifyListeners();
      }
    });
  }

  /// The owner's answer to [pendingApproval].
  void decide({required bool approved, bool fullControl = false}) {
    final pending = _pending;
    if (pending == null || pending.completer.isCompleted) return;
    pending.completer.complete(PairingDecision(approved: approved, grantFullControl: fullControl));
  }

  // --- Paired viewers ---

  Future<void> forgetViewer(String deviceId) => _viewerStore.remove(deviceId);

  Future<void> setViewerBlocked(String deviceId, bool blocked) async {
    final viewer = await _viewerStore.findByDeviceId(deviceId);
    if (viewer != null) await _viewerStore.save(viewer.copyWith(blocked: blocked));
  }

  Future<void> setViewerPermission(String deviceId, String permission, bool granted) async {
    final viewer = await _viewerStore.findByDeviceId(deviceId);
    if (viewer == null) return;

    final permissions = {...viewer.permissions};
    if (granted) {
      permissions.add(permission);
    } else {
      permissions.remove(permission);
    }

    await _viewerStore.save(viewer.copyWith(permissions: permissions.toList()));
  }

  Future<void> disconnectViewer(String connectionId) async {
    await sessions.find(connectionId)?.close(reason: 'Disconnected by the owner.');
  }

  void _refreshPaired() {
    _viewerStore.getAll().then((all) {
      _paired = all..sort((a, b) => a.deviceName.compareTo(b.deviceName));
      if (!_disposed) notifyListeners();
    });
  }

  // --- Block list ---

  Future<void> _loadBlockList() async {
    try {
      gate.replaceAll(await _platform.load());
    } on Object {
      // Not on Android, or the channel is not up: an empty list, which blocks nothing.
    }
  }

  Future<void> setAppBlocked(String package, bool blocked) async {
    gate.setBlocked(package, blocked);
    await _platform.save(gate.blocked);
  }

  Future<List<InstalledApp>> listApps() => _platform.listApps(includeIcons: true);

  void _onPlatformEvent(HostPlatformEvent event) {
    if (event.stopRequested) {
      stop().ignore();
      return;
    }

    if (event.accessibilityEnabled == null) gate.setForeground(event.package);

    final enabled = event.accessibilityEnabled;
    if (enabled != null && enabled != _accessibilityEnabled) {
      _accessibilityEnabled = enabled;
      notifyListeners();
    }
  }

  // --- Helpers ---

  static Future<List<String>> _localAddresses() async {
    try {
      final interfaces = await NetworkInterface.list(includeLoopback: false, type: InternetAddressType.IPv4);
      return [
        for (final interface in interfaces)
          for (final address in interface.addresses)
            if (!address.isLinkLocal) address.address,
      ];
    } on Object {
      return const [];
    }
  }

  String _connectionId() {
    final random = Random.secure();
    const alphabet = '0123456789abcdef';
    return String.fromCharCodes(List<int>.generate(12, (_) => alphabet.codeUnitAt(random.nextInt(16))));
  }

  void _set(HostPhase phase, String? message) {
    if (_disposed) return;
    _phase = phase;
    _message = message;
    notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _storeSubscription?.cancel();
    _sessionSubscription?.cancel();
    _teardown().ignore();
    _input.dispose();
    screen.removeListener(notifyListeners);
    screen.dispose();
    gate.removeListener(notifyListeners);
    sessions.dispose().ignore();
    super.dispose();
  }
}

/// What this phone calls itself to viewers: its model name, as it calls itself to PCs.
Future<String> defaultHostName() async => (await describeThisDevice()).name;
