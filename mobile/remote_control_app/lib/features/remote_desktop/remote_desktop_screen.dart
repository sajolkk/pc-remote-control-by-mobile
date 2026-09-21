import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../../app/providers.dart';
import '../../data/media/media_client.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';
import '../../protocol/screen_messages.dart';

/// The live view of the PC's screen (Phase 3).
///
/// **Streaming only while visible.** The stream starts when this screen opens and stops when it
/// closes or the app goes to the background: an unwatched stream still costs the PC a capture and
/// an encoder, and the phone its battery.
///
/// **Every "why not" is explained.** A PC can be unable to stream for several unrelated reasons —
/// nobody signed in, locked, permission not granted, no encoder — and each gets its own message,
/// because a black rectangle with a spinner tells the user nothing (§6.4).
class RemoteDesktopScreen extends ConsumerStatefulWidget {
  const RemoteDesktopScreen({super.key});

  @override
  ConsumerState<RemoteDesktopScreen> createState() => _RemoteDesktopScreenState();
}

class _RemoteDesktopScreenState extends ConsumerState<RemoteDesktopScreen> with WidgetsBindingObserver {
  late final MediaClient _media;
  final _transform = TransformationController();

  List<MonitorInfo> _monitors = const [];
  QualityPreset _preset = QualityPreset.auto;
  bool _showStats = true;
  bool _fullscreen = false;
  bool _wantStream = true;
  bool _wasConnected = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _media = MediaClient(ref.read(connectionProvider));
    WidgetsBinding.instance.addPostFrameCallback((_) => _startIfPossible());
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _exitFullscreen();
    _media.dispose();
    _transform.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    switch (state) {
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
        if (_media.value.isActive) _media.stop();
      case AppLifecycleState.resumed:
        _startIfPossible();
      default:
        break;
    }
  }

  bool _canStream(ConnectionStatus status) =>
      status.isConnected &&
      status.has(Capabilities.screenCapture) &&
      status.granted(Permissions.viewScreen) &&
      status.sessionAgentConnected &&
      !status.isLoggedOut;

  Future<void> _startIfPossible() async {
    if (!mounted || !_wantStream || _media.value.isActive) return;

    final status = ref.read(connectionProvider).status;
    if (!_canStream(status)) return;

    await _loadMonitors();
    await _media.start(monitorId: _media.value.monitorId, quality: _preset);
  }

  Future<void> _loadMonitors() async {
    final response = await ref.read(connectionProvider).send(Commands.screenMonitorList);
    if (!mounted || response == null || !response.ok) return;

    setState(() => _monitors = MonitorList.fromJson(response.map).monitors);
  }

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(connectionProvider).status;

    // The control connection dropping takes the stream with it; the PC ends its side too. When
    // the connection comes back, pick up where the user left off.
    if (status.isConnected != _wasConnected) {
      _wasConnected = status.isConnected;
      if (status.isConnected) {
        WidgetsBinding.instance.addPostFrameCallback((_) => _startIfPossible());
      } else if (_media.value.isActive) {
        // Deferred: stopping notifies listeners, which is not allowed in the middle of a build.
        WidgetsBinding.instance.addPostFrameCallback((_) => _media.stop());
      }
    }

    final blocker = _blocker(status);
    if (blocker != null) {
      return _Explanation(icon: blocker.$1, title: blocker.$2, detail: blocker.$3);
    }

    return ValueListenableBuilder<MediaViewState>(
      valueListenable: _media,
      builder: (context, state, _) => Scaffold(
        backgroundColor: Colors.black,
        body: SafeArea(
          top: !_fullscreen,
          bottom: !_fullscreen,
          child: Stack(
            children: [
              Positioned.fill(child: _video(state)),
              if (state.phase == MediaPhase.paused) _PausedOverlay(message: state.message),
              if (state.phase == MediaPhase.negotiating || state.phase == MediaPhase.connecting)
                _Progress(message: state.message ?? 'Connecting…'),
              if (state.phase == MediaPhase.failed || state.phase == MediaPhase.idle)
                _Stopped(
                  message: state.message,
                  onStart: () {
                    _wantStream = true;
                    _startIfPossible();
                  },
                ),
              _TopBar(
                state: state,
                monitors: _monitors,
                preset: _preset,
                fullscreen: _fullscreen,
                showStats: _showStats,
                onSelectMonitor: _selectMonitor,
                onSelectPreset: _selectPreset,
                onScreenshot: _screenshot,
                onToggleStats: () => setState(() => _showStats = !_showStats),
                onToggleFullscreen: _toggleFullscreen,
                onStop: () {
                  _wantStream = false;
                  _media.stop();
                },
              ),
              if (_showStats && state.quality != null && state.isActive)
                Positioned(left: 8, bottom: 8, child: _Telemetry(state: state)),
            ],
          ),
        ),
      ),
    );
  }

  Widget _video(MediaViewState state) {
    return GestureDetector(
      // Double-tap resets zoom: the quickest way back after pinching into a corner.
      onDoubleTap: () => _transform.value = Matrix4.identity(),
      child: InteractiveViewer(
        transformationController: _transform,
        minScale: 1,
        maxScale: 6,
        child: RTCVideoView(
          _media.renderer,
          objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitContain,
        ),
      ),
    );
  }

  /// Why the stream cannot start at all, or null when it can.
  (IconData, String, String)? _blocker(ConnectionStatus status) {
    if (!status.isConnected) {
      return (
        Icons.link_off,
        'Not connected',
        'Connect to a PC from the Devices tab to see its screen.',
      );
    }

    if (!status.granted(Permissions.viewScreen)) {
      return (
        Icons.visibility_off_outlined,
        'Screen viewing is not allowed',
        'This device has not been granted "View screen" on the PC. The PC\'s owner can grant it '
            'in PC-Remote on the PC.',
      );
    }

    if (status.isLoggedOut || !status.sessionAgentConnected) {
      return (
        Icons.person_off_outlined,
        'Nobody is signed in',
        'There is no desktop to show until someone signs in at the PC. Power controls still work.',
      );
    }

    if (!status.has(Capabilities.screenCapture)) {
      return (
        Icons.desktop_access_disabled_outlined,
        'This PC cannot stream its screen',
        'Its display driver does not support screen capture, or Windows has no H.264 encoder '
            '(Windows "N" editions need the Media Feature Pack).',
      );
    }

    return null;
  }

  Future<void> _selectMonitor(MonitorInfo monitor) async {
    final error = await _media.selectMonitor(monitor.id);
    if (error != null) _snack(error);
    _transform.value = Matrix4.identity();
  }

  Future<void> _selectPreset(QualityPreset preset) async {
    setState(() => _preset = preset);
    final error = await _media.setQuality(preset);
    if (error != null) _snack(error);
  }

  Future<void> _screenshot() async {
    final response = await ref.read(connectionProvider).send(Commands.screenScreenshot, args: {
      if (_media.value.monitorId != null) 'monitorId': _media.value.monitorId,
      'maxWidth': 1920,
    });

    if (!mounted) return;

    if (response == null || !response.ok) {
      _snack(response?.error?.message ?? 'The screenshot could not be taken.');
      return;
    }

    final data = response.map['data'] as String?;
    if (data == null || data.isEmpty) {
      _snack('The PC returned an empty screenshot.');
      return;
    }

    final bytes = base64Decode(data);
    await showDialog<void>(
      context: context,
      builder: (context) => Dialog.fullscreen(
        backgroundColor: Colors.black,
        child: Stack(
          children: [
            Positioned.fill(
              child: InteractiveViewer(maxScale: 8, child: Center(child: Image.memory(bytes))),
            ),
            Positioned(
              top: 8,
              right: 8,
              child: SafeArea(
                child: IconButton.filledTonal(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _toggleFullscreen() async {
    if (_fullscreen) {
      await _exitFullscreen();
    } else {
      await SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);
      await SystemChrome.setPreferredOrientations(
        [DeviceOrientation.landscapeLeft, DeviceOrientation.landscapeRight],
      );
    }

    if (mounted) setState(() => _fullscreen = !_fullscreen);
  }

  Future<void> _exitFullscreen() async {
    await SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    await SystemChrome.setPreferredOrientations(const []);
  }

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
  }
}

class _TopBar extends StatelessWidget {
  const _TopBar({
    required this.state,
    required this.monitors,
    required this.preset,
    required this.fullscreen,
    required this.showStats,
    required this.onSelectMonitor,
    required this.onSelectPreset,
    required this.onScreenshot,
    required this.onToggleStats,
    required this.onToggleFullscreen,
    required this.onStop,
  });

  final MediaViewState state;
  final List<MonitorInfo> monitors;
  final QualityPreset preset;
  final bool fullscreen;
  final bool showStats;
  final ValueChanged<MonitorInfo> onSelectMonitor;
  final ValueChanged<QualityPreset> onSelectPreset;
  final VoidCallback onScreenshot;
  final VoidCallback onToggleStats;
  final VoidCallback onToggleFullscreen;
  final VoidCallback onStop;

  @override
  Widget build(BuildContext context) {
    final live = state.isActive;

    return Positioned(
      top: 4,
      right: 4,
      child: Material(
        color: Colors.black54,
        borderRadius: BorderRadius.circular(24),
        child: IconTheme(
          data: const IconThemeData(color: Colors.white),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (monitors.length > 1)
                PopupMenuButton<MonitorInfo>(
                  tooltip: 'Monitor',
                  icon: const Icon(Icons.monitor),
                  enabled: live,
                  onSelected: onSelectMonitor,
                  itemBuilder: (_) => [
                    for (final monitor in monitors)
                      CheckedPopupMenuItem(
                        value: monitor,
                        checked: monitor.id == state.monitorId,
                        child: ListTile(
                          contentPadding: EdgeInsets.zero,
                          title: Text(monitor.name),
                          subtitle: Text(monitor.resolution),
                        ),
                      ),
                  ],
                ),
              PopupMenuButton<QualityPreset>(
                tooltip: 'Quality',
                icon: const Icon(Icons.high_quality_outlined),
                enabled: live,
                onSelected: onSelectPreset,
                itemBuilder: (_) => [
                  for (final option in QualityPreset.all)
                    CheckedPopupMenuItem(
                      value: option,
                      checked: option.label == preset.label,
                      child: Text(option.label),
                    ),
                ],
              ),
              IconButton(
                tooltip: 'Screenshot',
                icon: const Icon(Icons.photo_camera_outlined),
                onPressed: onScreenshot,
              ),
              IconButton(
                tooltip: showStats ? 'Hide stream details' : 'Show stream details',
                icon: Icon(showStats ? Icons.insights : Icons.insights_outlined),
                onPressed: onToggleStats,
              ),
              IconButton(
                tooltip: fullscreen ? 'Exit full screen' : 'Full screen',
                icon: Icon(fullscreen ? Icons.fullscreen_exit : Icons.fullscreen),
                onPressed: onToggleFullscreen,
              ),
              if (live)
                IconButton(
                  tooltip: 'Stop streaming',
                  icon: const Icon(Icons.stop_circle_outlined),
                  onPressed: onStop,
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The telemetry overlay (§9.5): FPS, bitrate, loss, RTT, codec, encoder and one quality light.
class _Telemetry extends StatelessWidget {
  const _Telemetry({required this.state});

  final MediaViewState state;

  @override
  Widget build(BuildContext context) {
    final q = state.quality!;
    final light = switch (q.indicator) {
      'good' => Colors.greenAccent,
      'fair' => Colors.amberAccent,
      _ => Colors.redAccent,
    };

    final decoded = state.decodedFps;
    final lines = <String>[
      '${q.width}×${q.height} · ${q.fps.toStringAsFixed(0)}/${q.targetFps} fps'
          '${decoded == null ? '' : ' · shown ${decoded.toStringAsFixed(0)}'}',
      '${(q.bitrateKbps / 1000).toStringAsFixed(1)} Mbit/s · loss ${q.lossPercent.toStringAsFixed(1)} %'
          '${q.rttMs == null ? '' : ' · RTT ${q.rttMs!.toStringAsFixed(0)} ms'}',
      '${q.codec} · ${q.encoder.isEmpty ? 'encoder' : q.encoder} (${q.hardware ? 'hardware' : 'software'})'
          ' · PC ${q.pipelineMs.toStringAsFixed(1)} ms',
    ];

    return DecoratedBox(
      decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(8)),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        child: DefaultTextStyle(
          style: const TextStyle(color: Colors.white, fontSize: 11, fontFeatures: [FontFeature.tabularFigures()]),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.circle, size: 10, color: light),
                  const SizedBox(width: 6),
                  Text(
                    q.isDegraded ? 'Degraded — the network or PC cannot keep up' : q.indicator.toUpperCase(),
                    style: const TextStyle(fontWeight: FontWeight.w600),
                  ),
                ],
              ),
              for (final line in lines) Text(line),
            ],
          ),
        ),
      ),
    );
  }
}

class _PausedOverlay extends StatelessWidget {
  const _PausedOverlay({this.message});

  final String? message;

  @override
  Widget build(BuildContext context) {
    return Positioned.fill(
      child: ColoredBox(
        color: Colors.black87,
        child: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(Icons.lock_outline, color: Colors.white70, size: 48),
                const SizedBox(height: 16),
                Text(
                  message ?? 'The PC paused the stream.',
                  textAlign: TextAlign.center,
                  style: const TextStyle(color: Colors.white),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Progress extends StatelessWidget {
  const _Progress({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const CircularProgressIndicator(color: Colors.white),
          const SizedBox(height: 16),
          Text(message, style: const TextStyle(color: Colors.white70)),
        ],
      ),
    );
  }
}

class _Stopped extends StatelessWidget {
  const _Stopped({required this.onStart, this.message});

  final String? message;
  final VoidCallback onStart;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (message != null) ...[
              Text(message!, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70)),
              const SizedBox(height: 16),
            ],
            FilledButton.icon(
              onPressed: onStart,
              icon: const Icon(Icons.play_arrow),
              label: const Text('Show screen'),
            ),
          ],
        ),
      ),
    );
  }
}

class _Explanation extends StatelessWidget {
  const _Explanation({required this.icon, required this.title, required this.detail});

  final IconData icon;
  final String title;
  final String detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 48, color: theme.colorScheme.outline),
            const SizedBox(height: 16),
            Text(title, style: theme.textTheme.titleMedium, textAlign: TextAlign.center),
            const SizedBox(height: 8),
            Text(detail, style: theme.textTheme.bodyMedium, textAlign: TextAlign.center),
          ],
        ),
      ),
    );
  }
}
