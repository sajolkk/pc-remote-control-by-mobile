import 'dart:async';

import 'package:flutter/services.dart';

import 'block_list.dart';
import 'host_input.dart';
import 'input_mapper.dart';

/// A change on the phone that the host reacts to.
class HostPlatformEvent {
  const HostPlatformEvent.foreground(this.package)
      : accessibilityEnabled = null,
        stopRequested = false;

  const HostPlatformEvent.accessibility(this.accessibilityEnabled)
      : package = null,
        stopRequested = false;

  /// The owner tapped Stop on the sharing notification.
  const HostPlatformEvent.stop()
      : package = null,
        accessibilityEnabled = null,
        stopRequested = true;

  /// The package now in front, when this is a foreground change.
  final String? package;

  /// Whether the accessibility service is now on, when this is a service change.
  final bool? accessibilityEnabled;

  final bool stopRequested;
}

/// The native half of the host: the TLS listener, the foreground service, the accessibility
/// service and the app list, all behind one method channel to `HostPlugin.kt`.
class AndroidHostPlatform implements HostPlatformServices, BlockListStore {
  AndroidHostPlatform({
    MethodChannel? channel,
    EventChannel? events,
  })  : _channel = channel ?? const MethodChannel('com.pcremote/host'),
        _events = events ?? const EventChannel('com.pcremote/host/events');

  final MethodChannel _channel;
  final EventChannel _events;

  /// Foreground-app and accessibility changes, as they happen.
  Stream<HostPlatformEvent> get events => _events.receiveBroadcastStream().map((raw) {
        final map = raw is Map ? Map<String, dynamic>.from(raw) : const <String, dynamic>{};
        return switch (map['type']) {
          'accessibility' => HostPlatformEvent.accessibility(map['enabled'] as bool? ?? false),
          'stopRequested' => const HostPlatformEvent.stop(),
          _ => HostPlatformEvent.foreground(map['package'] as String?),
        };
      });

  // --- The TLS listener and the foreground service ---

  /// Starts the native TLS listener. Returns the port it bound, which may differ from
  /// [preferredPort] when that one was taken.
  Future<int> startTlsServer({
    required String certificatePem,
    required String privateKeyPem,
    required int preferredPort,
    required int loopbackPort,
    required String secret,
  }) async {
    final result = await _channel.invokeMethod<Map<Object?, Object?>>('startTlsServer', {
      'certPem': certificatePem,
      'keyPem': privateKeyPem,
      'port': preferredPort,
      'loopbackPort': loopbackPort,
      'secret': secret,
    });
    return (result?['port'] as int?) ?? 0;
  }

  Future<void> stopTlsServer() => _channel.invokeMethod<void>('stopTlsServer');

  /// Starts the foreground service of type `mediaProjection`. Must follow the capture consent.
  Future<void> startForegroundService() => _channel.invokeMethod<void>('startForegroundService');

  Future<void> stopForegroundService() => _channel.invokeMethod<void>('stopForegroundService');

  Future<void> requestNotificationPermission() =>
      _channel.invokeMethod<void>('requestNotificationPermission');

  Future<void> openAccessibilitySettings() => _channel.invokeMethod<void>('openAccessibilitySettings');

  Future<String?> foregroundPackage() => _channel.invokeMethod<String>('foregroundPackage');

  // --- HostPlatformServices ---

  @override
  Future<Size> screenSize() async {
    final result = await _channel.invokeMethod<Map<Object?, Object?>>('screenSize');
    return Size(
      ((result?['width'] as num?) ?? 0).toDouble(),
      ((result?['height'] as num?) ?? 0).toDouble(),
    );
  }

  @override
  Future<int> screenRotation() async {
    final result = await _channel.invokeMethod<Map<Object?, Object?>>('screenSize');
    return (result?['rotation'] as int?) ?? 0;
  }

  @override
  Future<bool> isAccessibilityEnabled() async =>
      await _channel.invokeMethod<bool>('isAccessibilityEnabled') ?? false;

  @override
  Future<bool> isLocked() async => await _channel.invokeMethod<bool>('isLocked') ?? false;

  @override
  Future<bool> tap(Offset at, {required Duration duration}) => _bool('tap', {
        'x': at.dx,
        'y': at.dy,
        'durationMs': duration.inMilliseconds,
      });

  @override
  Future<bool> swipe(Swipe swipe) => _bool('swipe', _swipeArgs(swipe));

  @override
  Future<bool> dragStart(Offset at) => _bool('dragStart', {'x': at.dx, 'y': at.dy});

  @override
  Future<bool> dragMove(Swipe segment) => _bool('dragMove', _swipeArgs(segment));

  @override
  Future<bool> dragEnd() => _bool('dragEnd');

  @override
  Future<bool> typeText(String text) => _bool('typeText', {'text': text});

  @override
  Future<bool> keyAction(PhoneKeyAction action) => _bool('keyAction', {'action': action.name});

  @override
  Future<bool> navAction(String action) => _bool('navAction', {'action': action});

  @override
  Future<List<InstalledApp>> listApps({required bool includeIcons}) async {
    final result = await _channel.invokeMethod<List<Object?>>('listApps', {'includeIcons': includeIcons});
    final foreground = await foregroundPackage();

    return [
      for (final raw in result ?? const <Object?>[])
        if (raw is Map)
          InstalledApp(
            package: raw['package'] as String? ?? '',
            name: raw['name'] as String? ?? '',
            iconPng: raw['iconPng'] as String?,
            isForeground: raw['package'] == foreground,
          ),
    ];
  }

  @override
  Future<bool> launchApp(String package) => _bool('launchApp', {'package': package});

  @override
  Future<Uint8List?> screenshotJpeg({required int maxWidth}) async {
    try {
      return await _channel.invokeMethod<Uint8List>('screenshot', {'maxWidth': maxWidth});
    } on PlatformException {
      return null;
    }
  }

  // --- BlockListStore ---

  @override
  Future<Set<String>> load() async {
    final result = await _channel.invokeMethod<List<Object?>>('getBlockedPackages');
    return {...?result?.whereType<String>()};
  }

  @override
  Future<void> save(Set<String> packages) =>
      _channel.invokeMethod<void>('setBlockedPackages', {'packages': packages.toList()});

  Future<bool> _bool(String method, [Map<String, dynamic>? args]) async {
    try {
      return await _channel.invokeMethod<bool>(method, args) ?? false;
    } on PlatformException {
      return false;
    }
  }

  static Map<String, dynamic> _swipeArgs(Swipe swipe) => {
        'x1': swipe.from.dx,
        'y1': swipe.from.dy,
        'x2': swipe.to.dx,
        'y2': swipe.to.dy,
        'durationMs': swipe.duration.inMilliseconds,
      };
}
