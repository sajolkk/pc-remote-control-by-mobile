import 'dart:convert';

import '../protocol/input_messages.dart';
import '../protocol/protocol.dart';
import 'block_list.dart';
import 'host_catalog.dart';
import 'host_input.dart';
import 'host_pairing.dart';
import 'host_session.dart';
import 'paired_viewer_store.dart';
import 'session_security.dart';

/// The screen stream, as the handlers see it. Implemented over WebRTC on a phone, faked in tests.
abstract class HostScreenService {
  /// Answers a viewer's SDP offer. Returns the `media.offer` result or a failure.
  Future<HostResult> offer(String connectionId, Map<String, dynamic> args);

  Future<HostResult> addCandidate(String connectionId, Map<String, dynamic> args);

  Future<void> stop(String connectionId, String? sessionId);

  Future<HostResult> setQuality(String connectionId, Map<String, dynamic> args);

  /// The media session id of [connectionId]'s stream, or null when it has none.
  String? sessionIdOf(String connectionId);
}

/// Facts about this phone that the handlers report.
class HostInfo {
  const HostInfo({
    required this.deviceId,
    required this.deviceName,
    required this.fingerprint,
    required this.controlPort,
    required this.localAddresses,
    required this.appVersion,
    this.model = '',
    this.osVersion = '',
  });

  final String deviceId;
  final String deviceName;
  final String fingerprint;
  final int controlPort;
  final List<String> localAddresses;
  final String appVersion;
  final String model;
  final String osVersion;
}

/// Everything the handlers need, in one place, so a test can build it from fakes.
class HostEnvironment {
  HostEnvironment({
    required this.info,
    required this.store,
    required this.tokens,
    required this.pairing,
    required this.gate,
    required this.sessions,
    required this.platform,
    required this.screen,
    required this.input,
    this.clock = systemClock,
    this.keepaliveTimeoutSeconds = 15,
    this.maxClockSkewSeconds = 30,
  });

  final HostInfo info;
  final PairedViewerStore store;
  final SessionTokenService tokens;
  final HostPairingService pairing;
  final BlockListGate gate;
  final HostSessionManager sessions;
  final HostPlatformServices platform;
  final HostScreenService screen;
  final InputGateway input;
  final Clock clock;
  final int keepaliveTimeoutSeconds;
  final int maxClockSkewSeconds;

  /// What this phone can do, reported honestly: the screen, touch and keys through the
  /// accessibility service, the app list, and the Android navigation actions. Nothing else.
  Future<List<String>> capabilities() async {
    final accessibility = await platform.isAccessibilityEnabled();

    return [
      Capabilities.screenCapture,
      Capabilities.hardwareEncode,
      if (accessibility) ...[
        Capabilities.input,
        InputCapabilities.mouse,
        InputCapabilities.keyboard,
        Capabilities.phoneNav,
        Capabilities.volume,
      ],
      Capabilities.apps,
      Capabilities.sessionAgent,
    ];
  }
}

/// Builds the router with every handler a phone host has.
HostRouter buildHostRouter(HostEnvironment env) {
  final router = HostRouter();

  router.register(Commands.ping, (call) async => HostResult.success({'ts': _nowMs(env)}));

  router.register(Commands.pairRequest, (call) async {
    final session = call.session;

    final known = session.viewer;
    if (known != null) {
      // The viewer forgot this phone while the record here survived. Confirm what it already
      // holds; nothing new is granted, so nobody is asked.
      return HostResult.success(_pairResult(env, known.permissions));
    }

    final result = await env.pairing.tryPair(
      call.args,
      peerFingerprint: session.peerFingerprint,
      remoteAddress: session.remoteAddress,
    );

    if (!result.succeeded || result.viewer == null) {
      return HostResult.fail(result.errorCode, result.message);
    }

    return HostResult.success(_pairResult(env, result.viewer!.permissions));
  });

  router.register(Commands.hello, (call) async {
    final args = call.args;
    final peerMin = (args['minVersion'] as num?)?.toInt() ?? ProtocolVersion.minSupported;
    final peerMax = (args['maxVersion'] as num?)?.toInt() ?? ProtocolVersion.current;

    final agreed = negotiateVersion(peerMin, peerMax);
    if (agreed == null) {
      return HostResult.fail(
        ErrorCodes.versionUnsupported,
        'This phone speaks protocol versions ${ProtocolVersion.minSupported}-${ProtocolVersion.current}. '
        'Update the app.',
      );
    }

    final current = call.caller.viewer;
    if (current == null) {
      return const HostResult.fail(ErrorCodes.notPaired, 'This device is not paired with the phone.');
    }

    var viewer = await env.store.findByDeviceId(current.deviceId);
    if (viewer == null) {
      return const HostResult.fail(ErrorCodes.notPaired, "This device's pairing was revoked.");
    }

    if (viewer.blocked) {
      return const HostResult.fail(
        ErrorCodes.policyDenied,
        'Disconnected on the phone. It will reconnect when allowed again in Share this phone.',
        retryable: true,
      );
    }

    final name = HostPairingService.sanitizeDisplayName(args['deviceName'] as String?);
    viewer = viewer.copyWith(
      deviceName: name.isEmpty ? null : name,
      lastAddress: call.session.remoteAddress,
      lastSeenAt: env.clock(),
    );
    await env.store.save(viewer);

    // One live token per connection: a renewed or repeated handshake withdraws the previous one.
    env.tokens.revokeConnection(call.caller.connectionId);
    final token = env.tokens.issue(viewer.deviceId, call.caller.connectionId);
    call.session.onAuthenticated(token, viewer);

    return HostResult.success({
      'version': agreed,
      'deviceId': env.info.deviceId,
      'deviceName': env.info.deviceName,
      'deviceKind': DeviceKinds.phone,
      'token': token.value,
      'tokenTtlSec': env.tokens.lifetime.inSeconds,
      'permissions': viewer.permissions,
      'capabilities': await env.capabilities(),
      'keepaliveTimeoutSec': env.keepaliveTimeoutSeconds,
      'maxClockSkewSec': env.maxClockSkewSeconds,
    });
  });

  router.register(Commands.sessionRenew, (call) async {
    final current = call.caller.viewer!;
    final viewer = await env.store.findByDeviceId(current.deviceId);
    if (viewer == null) {
      return const HostResult.fail(ErrorCodes.notPaired, "This device's pairing was revoked.");
    }
    if (viewer.blocked) {
      return const HostResult.fail(ErrorCodes.policyDenied, 'Disconnected on the phone.', retryable: true);
    }

    env.tokens.revokeConnection(call.caller.connectionId);
    final token = env.tokens.issue(viewer.deviceId, call.caller.connectionId);
    call.session.onAuthenticated(token, viewer);

    return HostResult.success({
      'token': token.value,
      'tokenTtlSec': env.tokens.lifetime.inSeconds,
      'permissions': viewer.permissions,
    });
  });

  router.register(Commands.permissionsList, (call) async => HostResult.success({
        'granted': call.caller.viewer?.permissions ?? const <String>[],
        'available': PairedViewer.allPermissions,
      }));

  router.register(Commands.deviceInfo, (call) async => HostResult.success({
        'deviceId': env.info.deviceId,
        'deviceName': env.info.deviceName,
        'deviceKind': DeviceKinds.phone,
        'hostName': env.info.deviceName,
        'osVersion': env.info.osVersion,
        'architecture': env.info.model,
        'agentVersion': env.info.appVersion,
        'cpuCount': 0,
        'totalMemoryMb': 0,
        'localAddresses': env.info.localAddresses,
        'controlPort': env.info.controlPort,
        'tlsVersion': call.session.tlsDescription,
        'capabilities': await env.capabilities(),
      }));

  router.register(Commands.systemState, (call) async {
    final locked = await env.platform.isLocked();
    return HostResult.success({
      // The viewer streams only while the "session" is active and an agent is connected; on a
      // phone the agent is this app, so it is always connected.
      'sessionState': locked ? 'Locked' : 'Active',
      'userName': null,
      'sessionAgentConnected': true,
      'uptimeSec': 0,
      'shutdownPending': false,
      'blockedByOwner': env.gate.isBlocked,
    });
  });

  router.register(Commands.systemLock, (call) async {
    final done = await env.platform.navAction(PhoneNavActions.lock);
    return done
        ? HostResult.empty
        : const HostResult.fail(
            ErrorCodes.notSupported,
            'The phone could not lock itself. Remote control must be on in Accessibility settings.',
          );
  });

  router.register(Commands.appList, (call) async {
    final args = call.args;
    final query = (args['query'] as String? ?? '').trim().toLowerCase();
    final runningOnly = args['runningOnly'] as bool? ?? false;
    final includeIcons = args['includeIcons'] as bool? ?? false;
    final limit = ((args['limit'] as num?)?.toInt() ?? 300).clamp(1, 1000);

    final apps = await env.platform.listApps(includeIcons: includeIcons);

    final matching = apps.where((app) {
      if (query.isNotEmpty && !app.name.toLowerCase().contains(query)) return false;
      if (runningOnly && !app.isForeground) return false;
      return true;
    }).toList()
      ..sort((a, b) {
        if (a.isForeground != b.isForeground) return a.isForeground ? -1 : 1;
        return a.name.toLowerCase().compareTo(b.name.toLowerCase());
      });

    return HostResult.success({
      'apps': [
        for (final app in matching.take(limit))
          {
            'id': app.package,
            'name': app.name,
            'kind': 'Packaged',
            'publisher': app.package,
            'running': app.isForeground,
            'processCount': app.isForeground ? 1 : 0,
            'hasWindow': app.isForeground,
            if (app.iconPng != null) 'iconPng': app.iconPng,

            // Marked, so the viewer can say why Launch is disabled rather than let it fail.
            'blocked': env.gate.isPackageBlocked(app.package),
          },
      ],
      'total': matching.length,
      'catalogBuiltMs': _nowMs(env),
    });
  });

  Future<HostResult> launch(HostCall call) async {
    final package = call.args['appId'] as String? ?? '';
    if (package.isEmpty) return HostResult.invalidArguments('"appId" is required.');

    if (env.gate.isPackageBlocked(package)) {
      return const HostResult.fail(
        ErrorCodes.blockedByOwner,
        'That app is blocked from remote access by the phone\'s owner.',
      );
    }

    if (env.gate.isBlocked) return HostResult.blocked;

    final launched = await env.platform.launchApp(package);
    return HostResult.success({
      'appId': package,
      'succeeded': launched,
      'running': launched,
      'forced': false,
      if (!launched) 'detail': 'The app could not be opened.',
    });
  }

  router.register(Commands.appLaunch, launch);
  router.register(Commands.appFocus, launch);

  router.register(Commands.screenMonitorList, (call) async {
    final size = await env.platform.screenSize();
    final rotation = await env.platform.screenRotation();

    return HostResult.success({
      'monitors': [
        {
          'id': phoneMonitorId,
          'name': 'Phone screen',
          'width': size.width.round(),
          'height': size.height.round(),
          'x': 0,
          'y': 0,
          'scale': 1.0,
          'refreshHz': 0,
          'primary': true,
          'rotation': rotation,
        },
      ],
      'selectedId': phoneMonitorId,
    });
  });

  router.register(Commands.screenSelectMonitor, (call) async {
    final id = call.args['monitorId'] as String?;
    if (id == null || id.isEmpty) return HostResult.invalidArguments('A monitorId is required.');
    if (id != phoneMonitorId) return HostResult.notFound('That monitor');
    return HostResult.success({'monitorId': id});
  });

  router.register(Commands.screenScreenshot, (call) async {
    if (env.gate.isBlocked) return HostResult.blocked;

    final maxWidth = ((call.args['maxWidth'] as num?)?.toInt() ?? 1280).clamp(320, 4096);
    final jpeg = await env.platform.screenshotJpeg(maxWidth: maxWidth);
    if (jpeg == null) return HostResult.notSupported('screenshots (Android 11 or later is needed)');

    final size = await env.platform.screenSize();
    return HostResult.success({
      'monitorId': phoneMonitorId,
      'mime': 'image/jpeg',
      'width': size.width.round(),
      'height': size.height.round(),
      'data': base64Encode(jpeg),
    });
  });

  router.register(Commands.mediaOffer, (call) => env.screen.offer(call.caller.connectionId, call.args));
  router.register(Commands.mediaIce, (call) => env.screen.addCandidate(call.caller.connectionId, call.args));
  router.register(Commands.mediaStop, (call) async {
    await env.screen.stop(call.caller.connectionId, call.args['sessionId'] as String?);
    return HostResult.empty;
  });
  router.register(Commands.mediaSetQuality, (call) => env.screen.setQuality(call.caller.connectionId, call.args));

  for (final command in HostCommandCatalog.all.where((d) => HostCommandCatalog.isInput(d.name))) {
    router.register(command.name, env.input.handle);
  }

  return router;
}

/// The one monitor a phone has.
const phoneMonitorId = 'screen';

Map<String, dynamic> _pairResult(HostEnvironment env, List<String> permissions) => {
      'deviceId': env.info.deviceId,
      'deviceName': env.info.deviceName,
      'deviceKind': DeviceKinds.phone,
      'fingerprint': env.info.fingerprint,
      'permissions': permissions,
      'protocolVersion': ProtocolVersion.current,
    };

int _nowMs(HostEnvironment env) => env.clock().millisecondsSinceEpoch;

/// The version two peers should use, or null when their ranges do not overlap. Mirrors
/// `ProtocolVersion.Negotiate`.
int? negotiateVersion(int peerMin, int peerMax) {
  if (peerMin > peerMax) return null;

  final agreed = peerMax < ProtocolVersion.current ? peerMax : ProtocolVersion.current;
  final floor = peerMin > ProtocolVersion.minSupported ? peerMin : ProtocolVersion.minSupported;
  return agreed >= floor ? agreed : null;
}
