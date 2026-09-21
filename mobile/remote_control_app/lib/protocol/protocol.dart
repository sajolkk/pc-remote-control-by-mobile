/// The wire protocol, mirroring `windows/RemoteAgent.Protocol`.
///
/// Kept deliberately hand-written and small rather than generated, because it is the
/// contract between two codebases and a reader should be able to compare it against the
/// C# side line by line. Every name here matches the C# constant exactly; if they drift,
/// commands silently stop resolving.
library;

import 'dart:convert';
import 'dart:typed_data';

/// Protocol version this client speaks.
class ProtocolVersion {
  const ProtocolVersion._();

  /// The version emitted on the wire.
  static const int current = 1;

  /// The oldest version this client can still talk to.
  static const int minSupported = 1;
}

/// Frame kinds on the control channel. One byte on the wire.
enum FrameType {
  request(0x01),
  response(0x02),
  event(0x03),
  ping(0x04),
  pong(0x05);

  const FrameType(this.value);

  final int value;

  static FrameType? fromValue(int value) {
    for (final type in FrameType.values) {
      if (type.value == value) return type;
    }
    return null;
  }
}

/// Command names. Must match `CommandNames` on the PC exactly.
class Commands {
  const Commands._();

  static const ping = 'ping';
  static const hello = 'hello';
  static const deviceInfo = 'device.info';
  static const systemState = 'system.state';
  static const permissionsList = 'permissions.list';
  static const sessionRenew = 'session.renew';
  static const pairRequest = 'pair.request';

  static const systemLock = 'system.lock';
  static const systemSignOut = 'system.signout';
  static const systemSleep = 'system.sleep';
  static const systemRestart = 'system.restart';
  static const systemShutdown = 'system.shutdown';
  static const systemAbortShutdown = 'system.abort_shutdown';

  static const appList = 'app.list';
  static const appLaunch = 'app.launch';
  static const appFocus = 'app.focus';
  static const appClose = 'app.close';

  static const browserList = 'browser.list';
  static const browserOpen = 'browser.open';
  static const browserOpenUrl = 'browser.open_url';
  static const browserClose = 'browser.close';

  static const wolInfo = 'wol.info';

  // Screen and media (ViewScreen) — Phase 3.
  static const screenMonitorList = 'screen.monitor_list';
  static const screenSelectMonitor = 'screen.select_monitor';
  static const screenScreenshot = 'screen.screenshot';
  static const mediaOffer = 'media.offer';
  static const mediaIce = 'media.ice';
  static const mediaStop = 'media.stop';
  static const mediaSetQuality = 'media.set_quality';
}

/// Event topics the PC pushes.
class Events {
  const Events._();

  static const systemStateChanged = 'system.state_changed';
  static const sessionStateChanged = 'session.state_changed';
  static const monitorsChanged = 'monitors.changed';
  static const clipboardChanged = 'clipboard.changed';
  static const appStateChanged = 'app.state_changed';
  static const permissionsChanged = 'permissions.changed';
  static const pairingRevoked = 'pairing.revoked';

  /// Streaming telemetry, sent only to the connection that owns the stream.
  static const mediaQuality = 'media.quality';
}

/// Stable error codes. The app branches on these, never on the message text.
class ErrorCodes {
  const ErrorCodes._();

  static const malformedFrame = 'E_MALFORMED_FRAME';
  static const versionUnsupported = 'E_VERSION_UNSUPPORTED';
  static const replayDetected = 'E_REPLAY_DETECTED';
  static const rateLimited = 'E_RATE_LIMITED';

  static const notPaired = 'E_NOT_PAIRED';
  static const unauthenticated = 'E_UNAUTHENTICATED';
  static const permissionDenied = 'E_PERMISSION_DENIED';

  static const pairingDisabled = 'E_PAIRING_DISABLED';
  static const pairingTokenInvalid = 'E_PAIRING_TOKEN_INVALID';
  static const pairingRejected = 'E_PAIRING_REJECTED';
  static const pairingTimeout = 'E_PAIRING_TIMEOUT';

  static const unknownCommand = 'E_UNKNOWN_COMMAND';
  static const invalidArguments = 'E_INVALID_ARGUMENTS';
  static const notSupported = 'E_NOT_SUPPORTED';
  static const policyDenied = 'E_POLICY_DENIED';

  static const sessionUnavailable = 'E_SESSION_UNAVAILABLE';
  static const desktopLocked = 'E_DESKTOP_LOCKED';
  static const secureDesktop = 'E_SECURE_DESKTOP';
  static const notFound = 'E_NOT_FOUND';
  static const timeout = 'E_TIMEOUT';
  static const accessDenied = 'E_ACCESS_DENIED';
  static const internal = 'E_INTERNAL';
  static const busy = 'E_BUSY';
}

/// Permission group names. Must match `PermissionSet` on the PC.
class Permissions {
  const Permissions._();

  static const viewScreen = 'ViewScreen';
  static const controlInput = 'ControlInput';
  static const launchApps = 'LaunchApps';
  static const manageFiles = 'ManageFiles';
  static const powerControls = 'PowerControls';
  static const clipboard = 'Clipboard';

  /// Human-readable label for a permission group.
  static String label(String name) => switch (name) {
        viewScreen => 'View screen',
        controlInput => 'Control mouse and keyboard',
        launchApps => 'Open and close apps',
        manageFiles => 'Transfer files',
        powerControls => 'Power controls',
        clipboard => 'Clipboard',
        _ => name,
      };
}

/// Capability names the PC advertises. The UI renders from these rather than assuming
/// what a PC can do, which is what lets one app build work against any PC.
class Capabilities {
  const Capabilities._();

  static const screenCapture = 'screen.capture';
  static const multiMonitor = 'screen.multi_monitor';
  static const hardwareEncode = 'encode.h264.hardware';
  static const softwareEncode = 'encode.h264.software';
  static const input = 'input';
  static const apps = 'apps';
  static const browser = 'browser';
  static const clipboard = 'clipboard';
  static const files = 'files';
  static const volume = 'volume';
  static const powerSleep = 'power.sleep';
  static const powerShutdown = 'power.shutdown';
  static const powerSignOut = 'power.signout';
  static const wakeOnLan = 'wol';
  static const sessionAgent = 'session.agent';
}

/// A failure returned by the PC.
class ProtocolError {
  const ProtocolError({required this.code, required this.message, this.retryable = false});

  factory ProtocolError.fromJson(Map<String, dynamic> json) => ProtocolError(
        code: json['code'] as String? ?? ErrorCodes.internal,
        message: json['msg'] as String? ?? 'The command failed.',
        retryable: json['retryable'] as bool? ?? false,
      );

  final String code;
  final String message;
  final bool retryable;

  @override
  String toString() => '$code: $message';
}

/// A command sent to the PC.
class RequestEnvelope {
  const RequestEnvelope({
    required this.id,
    required this.command,
    required this.sequence,
    required this.timestampMs,
    this.args,
    this.token,
  });

  final String id;
  final String command;
  final int sequence;
  final int timestampMs;
  final Map<String, dynamic>? args;
  final String? token;

  Map<String, dynamic> toJson() => {
        'v': ProtocolVersion.current,
        'id': id,
        'ts': timestampMs,
        'seq': sequence,
        'cmd': command,
        if (args != null) 'args': args,
        if (token != null) 'tok': token,
      };
}

/// The PC's answer to a request.
class ResponseEnvelope {
  const ResponseEnvelope({required this.id, required this.ok, this.data, this.error});

  factory ResponseEnvelope.fromJson(Map<String, dynamic> json) => ResponseEnvelope(
        id: json['id'] as String? ?? '',
        ok: json['ok'] as bool? ?? false,
        data: json['data'],
        error: json['err'] is Map<String, dynamic>
            ? ProtocolError.fromJson(json['err'] as Map<String, dynamic>)
            : null,
      );

  final String id;
  final bool ok;
  final dynamic data;
  final ProtocolError? error;

  /// The payload as an object, or an empty map when there was none.
  Map<String, dynamic> get map =>
      data is Map<String, dynamic> ? data as Map<String, dynamic> : const {};
}

/// An unsolicited state change pushed by the PC.
class EventEnvelope {
  const EventEnvelope({required this.topic, required this.timestampMs, this.data});

  factory EventEnvelope.fromJson(Map<String, dynamic> json) => EventEnvelope(
        topic: json['topic'] as String? ?? '',
        timestampMs: json['ts'] as int? ?? 0,
        data: json['data'] is Map<String, dynamic> ? json['data'] as Map<String, dynamic> : null,
      );

  final String topic;
  final int timestampMs;
  final Map<String, dynamic>? data;
}

/// What the PC advertises over UDP discovery.
class DiscoveryBeacon {
  const DiscoveryBeacon({
    required this.deviceId,
    required this.deviceName,
    required this.port,
    required this.protocolVersion,
    required this.address,
  });

  factory DiscoveryBeacon.fromJson(Map<String, dynamic> json, String address) => DiscoveryBeacon(
        deviceId: json['id'] as String? ?? '',
        deviceName: json['name'] as String? ?? 'Windows PC',
        port: json['port'] as int? ?? 0,
        protocolVersion: json['proto'] as int? ?? 1,
        address: address,
      );

  final String deviceId;
  final String deviceName;
  final int port;
  final int protocolVersion;
  final String address;

  /// Whether this PC speaks a protocol version this app understands.
  bool get isCompatible =>
      protocolVersion >= ProtocolVersion.minSupported && protocolVersion <= ProtocolVersion.current;
}

/// Constants for the UDP discovery exchange.
class DiscoveryConstants {
  const DiscoveryConstants._();

  static const int defaultUdpPort = 47801;
  static const int defaultControlPort = 47800;
  static const String probeMagic = 'PCRQ1';
  static const String responseMagic = 'PCRS1';
  static const int maxDatagramBytes = 512;
}

/// The payload encoded in the PC's pairing QR code.
class PairingQrPayload {
  const PairingQrPayload({
    required this.deviceId,
    required this.deviceName,
    required this.fingerprint,
    required this.token,
    required this.port,
    required this.hosts,
  });

  /// Parses a scanned QR code, returning null when it is not one of ours.
  static PairingQrPayload? tryParse(String raw) {
    try {
      final decoded = jsonDecode(raw);
      if (decoded is! Map<String, dynamic>) return null;

      final id = decoded['id'] as String?;
      final fingerprint = decoded['fp'] as String?;
      final token = decoded['t'] as String?;
      final port = decoded['p'] as int?;

      // All four are required. A partial payload cannot produce a secure pairing, so it
      // is rejected rather than half-used.
      if (id == null || fingerprint == null || token == null || port == null) return null;

      return PairingQrPayload(
        deviceId: id,
        deviceName: decoded['n'] as String? ?? 'Windows PC',
        fingerprint: fingerprint,
        token: token,
        port: port,
        hosts: (decoded['h'] as List<dynamic>? ?? const [])
            .whereType<String>()
            .toList(growable: false),
      );
    } on FormatException {
      return null;
    }
  }

  final String deviceId;
  final String deviceName;
  final String fingerprint;
  final String token;
  final int port;
  final List<String> hosts;
}

/// Encodes and decodes the base64url form the PC uses for binary values.
class Base64Url {
  const Base64Url._();

  /// Encodes bytes without padding.
  static String encode(List<int> data) =>
      base64Url.encode(data).replaceAll('=', '');

  /// Decodes an unpadded base64url string, returning null on malformed input.
  static Uint8List? tryDecode(String value) {
    try {
      final padded = value.padRight((value.length + 3) & ~3, '=');
      return base64Url.decode(padded);
    } on FormatException {
      return null;
    }
  }
}
