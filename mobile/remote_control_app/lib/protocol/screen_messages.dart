/// Wire models for screen streaming (Phase 3), mirroring
/// `RemoteAgent.Protocol/Messages/ScreenMessages.cs`.
///
/// Parsing is lenient on purpose: a missing field takes a neutral default rather than throwing,
/// so a PC running a slightly different build degrades a label instead of breaking the viewer.
library;

/// One monitor from `screen.monitor_list`.
class MonitorInfo {
  const MonitorInfo({
    required this.id,
    required this.name,
    required this.width,
    required this.height,
    this.x = 0,
    this.y = 0,
    this.scale = 1.0,
    this.refreshHz = 0,
    this.primary = false,
    this.rotation = 0,
  });

  factory MonitorInfo.fromJson(Map<String, dynamic> json) => MonitorInfo(
        id: json['id'] as String? ?? '',
        name: json['name'] as String? ?? 'Display',
        width: (json['width'] as num?)?.toInt() ?? 0,
        height: (json['height'] as num?)?.toInt() ?? 0,
        x: (json['x'] as num?)?.toInt() ?? 0,
        y: (json['y'] as num?)?.toInt() ?? 0,
        scale: (json['scale'] as num?)?.toDouble() ?? 1.0,
        refreshHz: (json['refreshHz'] as num?)?.toInt() ?? 0,
        primary: json['primary'] as bool? ?? false,
        rotation: (json['rotation'] as num?)?.toInt() ?? 0,
      );

  /// Opaque, PC-generated id. Meaningless on any other PC.
  final String id;
  final String name;
  final int width;
  final int height;
  final int x;
  final int y;
  final double scale;
  final int refreshHz;
  final bool primary;
  final int rotation;

  String get resolution => '$width × $height${refreshHz > 0 ? ' · $refreshHz Hz' : ''}';
}

/// The monitor list plus which one this device is streaming.
class MonitorList {
  const MonitorList({required this.monitors, this.selectedId});

  factory MonitorList.fromJson(Map<String, dynamic> json) => MonitorList(
        monitors: (json['monitors'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(MonitorInfo.fromJson)
            .toList(growable: false),
        selectedId: json['selectedId'] as String?,
      );

  final List<MonitorInfo> monitors;
  final String? selectedId;
}

/// Why the PC stopped producing frames while the stream stays up.
class MediaPauseReasons {
  const MediaPauseReasons._();

  /// Locked, or a UAC / Ctrl+Alt+Del screen is showing. Nothing can capture that (§12.1).
  static const desktopUnavailable = 'desktop_unavailable';
  static const monitorLost = 'monitor_lost';
  static const recovering = 'recovering';

  static String describe(String? reason) => switch (reason) {
        desktopUnavailable =>
          'The PC is locked or showing a secure prompt. Windows does not allow that screen to be '
              'captured — unlock it at the PC and the picture returns by itself.',
        monitorLost => 'The monitor being shown was disconnected.',
        recovering => 'The display changed. Reconnecting to it…',
        _ => 'The PC paused the stream.',
      };
}

/// A `media.quality` event: the PC's view of the stream.
class MediaQuality {
  const MediaQuality({
    required this.sessionId,
    required this.state,
    this.reason,
    this.fps = 0,
    this.targetFps = 0,
    this.bitrateKbps = 0,
    this.targetBitrateKbps = 0,
    this.width = 0,
    this.height = 0,
    this.lossPercent = 0,
    this.rttMs,
    this.pipelineMs = 0,
    this.codec = 'H264',
    this.encoder = '',
    this.hardware = false,
    this.level = 0,
    this.degradedLevel = 1 << 30,
    this.indicator = 'good',
  });

  factory MediaQuality.fromJson(Map<String, dynamic> json) => MediaQuality(
        sessionId: json['sessionId'] as String? ?? '',
        state: json['state'] as String? ?? 'connecting',
        reason: json['reason'] as String?,
        fps: (json['fps'] as num?)?.toDouble() ?? 0,
        targetFps: (json['targetFps'] as num?)?.toInt() ?? 0,
        bitrateKbps: (json['bitrateKbps'] as num?)?.toInt() ?? 0,
        targetBitrateKbps: (json['targetBitrateKbps'] as num?)?.toInt() ?? 0,
        width: (json['width'] as num?)?.toInt() ?? 0,
        height: (json['height'] as num?)?.toInt() ?? 0,
        lossPercent: (json['lossPercent'] as num?)?.toDouble() ?? 0,
        rttMs: (json['rttMs'] as num?)?.toDouble(),
        pipelineMs: (json['pipelineMs'] as num?)?.toDouble() ?? 0,
        codec: json['codec'] as String? ?? 'H264',
        encoder: json['encoder'] as String? ?? '',
        hardware: json['hardware'] as bool? ?? false,
        level: (json['level'] as num?)?.toInt() ?? 0,
        degradedLevel: (json['degradedLevel'] as num?)?.toInt() ?? (1 << 30),
        indicator: json['indicator'] as String? ?? 'good',
      );

  final String sessionId;

  /// `connecting`, `streaming`, `paused` or `closed`.
  final String state;
  final String? reason;
  final double fps;
  final int targetFps;
  final int bitrateKbps;
  final int targetBitrateKbps;
  final int width;
  final int height;
  final double lossPercent;
  final double? rttMs;
  final double pipelineMs;
  final String codec;
  final String encoder;
  final bool hardware;
  final int level;
  final int degradedLevel;

  /// `good`, `fair` or `poor`: the one-glance summary (§9.5).
  final String indicator;

  bool get isPaused => state == 'paused';
  bool get isClosed => state == 'closed';

  /// The explicitly-labelled degraded mode: the bottom rung of the PC's quality ladder.
  bool get isDegraded => level >= degradedLevel;
}

/// A quality ceiling the user picked. Null fields leave the PC's maximum in place.
class QualityPreset {
  const QualityPreset(this.label, {this.maxHeight, this.maxFps});

  final String label;
  final int? maxHeight;
  final int? maxFps;

  Map<String, dynamic> toJson() => {
        if (maxHeight != null) 'maxHeight': maxHeight,
        if (maxFps != null) 'maxFps': maxFps,
      };

  /// "As good as the PC allows". Sent as deliberately huge ceilings rather than as no ceiling:
  /// the PC reads a missing field as "keep the current one", so an empty request would never undo
  /// an earlier 720p choice. The PC clamps these to its own configured limits.
  static const auto = QualityPreset('Auto', maxHeight: 4320, maxFps: 240);

  static const all = <QualityPreset>[
    auto,
    QualityPreset('1080p · 60 fps', maxHeight: 1080, maxFps: 60),
    QualityPreset('1080p · 30 fps', maxHeight: 1080, maxFps: 30),
    QualityPreset('720p · 30 fps', maxHeight: 720, maxFps: 30),
    QualityPreset('540p · 15 fps (data saver)', maxHeight: 540, maxFps: 15),
  ];
}
