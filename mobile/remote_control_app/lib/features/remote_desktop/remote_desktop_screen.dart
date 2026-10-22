import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../../data/input/input_sender.dart';
import '../../data/media/media_client.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/input_messages.dart';
import '../../protocol/protocol.dart';
import '../../protocol/screen_messages.dart';
import 'control_gestures.dart';
import 'control_surface.dart';
import 'pointer_mapping.dart';
import 'remote_keyboard.dart';

/// The live view of the PC's screen (Phase 3), and the surface it is controlled from (Phase 4).
///
/// **Streaming only while visible.** The stream starts when this screen opens and stops when it
/// closes or the app goes to the background: an unwatched stream still costs the PC a capture and
/// an encoder, and the phone its battery.
///
/// **Every "why not" is explained.** A PC can be unable to stream for several unrelated reasons —
/// nobody signed in, locked, permission not granted, no encoder — and each gets its own message,
/// because a black rectangle with a spinner tells the user nothing (§6.4).
///
/// **Control is on when it can be, and visibly so.** Touches drive the PC's mouse whenever the PC
/// offers input and this device holds `ControlInput`; a toolbar menu switches to view-only and
/// back, and a small label always says which it is. A PC too old to take input shows no control at
/// all; a device without the permission is told how to get it and stays view-only.
///
/// **One PC per screen.** Opened from the Screen tab's list for the PC chosen there, so several
/// PCs can be connected while one is watched.
///
/// **Nothing stays held.** Whenever control ends — switched off, tab left, app backgrounded,
/// stream stopped or paused, permission withdrawn — the PC is told to release every key and button
/// this phone was holding.
class RemoteDesktopScreen extends StatefulWidget {
  const RemoteDesktopScreen({super.key, required this.connection});

  /// The connection to the PC whose screen this shows.
  final ConnectionController connection;

  @override
  State<RemoteDesktopScreen> createState() => _RemoteDesktopScreenState();
}

class _RemoteDesktopScreenState extends State<RemoteDesktopScreen> with WidgetsBindingObserver {
  late final MediaClient _media;
  late final ConnectionController _connection;
  late final InputSender _input;
  late final RemoteKeyboardController _keyboard;
  final _transform = TransformationController();

  List<MonitorInfo> _monitors = const [];
  QualityPreset _preset = QualityPreset.auto;
  bool _showStats = true;
  bool _fullscreen = false;
  bool _wantStream = true;
  bool _wasConnected = false;

  // Control (Phase 4). On by default; whether it is actually possible is decided per build from
  // the PC's capabilities and this device's permissions.
  bool _controlWanted = true;
  PointerStyle _pointerStyle = PointerStyle.direct;
  bool _keyboardOpen = false;
  bool _wasControlling = false;
  bool _wasLive = false;
  bool _permissionNoticeDismissed = false;
  bool _panMode = false;
  DateTime? _lastBackAt;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _connection = widget.connection..addListener(_onConnectionChanged);
    _media = MediaClient(_connection)..addListener(_onMediaChanged);
    _input = InputSender(_connection.send, onFault: (fault) => _snack(fault.message));
    _keyboard = RemoteKeyboardController(_input);
    WidgetsBinding.instance.addPostFrameCallback((_) => _startIfPossible());
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _connection.removeListener(_onConnectionChanged);
    _exitFullscreen();

    // Before the stream goes: the PC must not be left holding a key because the tab was left
    // mid-drag.
    _input.dispose();
    _keyboard.dispose();
    _media.removeListener(_onMediaChanged);
    _media.dispose();
    _transform.dispose();
    super.dispose();
  }

  void _onConnectionChanged() {
    if (mounted) setState(() {});
  }

  /// The stream stopping or pausing ends control: there is nothing left to aim at.
  void _onMediaChanged() {
    final live = _media.value.phase == MediaPhase.streaming;
    if (_wasLive && !live) _input.releaseAll();
    _wasLive = live;
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    switch (state) {
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
        _input.releaseAll();
        if (_keyboardOpen) _closeKeyboard();
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

    if (!_canStream(_connection.status)) return;

    await _loadMonitors();
    await _media.start(monitorId: _media.value.monitorId, quality: _preset);
  }

  Future<void> _loadMonitors() async {
    final response = await _connection.send(Commands.screenMonitorList);
    if (!mounted || response == null || !response.ok) return;

    setState(() => _monitors = MonitorList.fromJson(response.map).monitors);
  }

  @override
  Widget build(BuildContext context) {
    final status = _connection.status;

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

    // Control needs three things: a PC new enough to take input, the permission, and the user
    // not having switched to view-only.
    final canPoint = status.has(InputCapabilities.mouse);
    final canType = status.has(InputCapabilities.keyboard);
    final controlOffered = status.isConnected && (canPoint || canType);
    final controlGranted = status.granted(Permissions.controlInput);
    final controlling = controlOffered && controlGranted && _controlWanted;

    if (controlling != _wasControlling) {
      _wasControlling = controlling;
      if (!controlling) {
        // Switched off, permission withdrawn or connection lost — possibly mid-gesture. Deferred:
        // closing the keyboard rebuilds, which is not allowed in the middle of a build.
        WidgetsBinding.instance.addPostFrameCallback((_) {
          _input.releaseAll();
          if (mounted && _keyboardOpen) _closeKeyboard();
        });
      }
    }

    final blocker = _blocker(status);
    if (blocker != null) {
      return Scaffold(
        appBar: AppBar(title: Text(_pcName)),
        body: _Explanation(icon: blocker.$1, title: blocker.$2, detail: blocker.$3),
      );
    }

    final mode = !controlOffered
        ? _ControlMode.unsupported
        : !controlGranted
            ? _ControlMode.notGranted
            : !_controlWanted
                ? _ControlMode.viewOnly
                : !canPoint
                    ? _ControlMode.keyboardOnly
                    : (_pointerStyle == PointerStyle.direct ? _ControlMode.direct : _ControlMode.trackpad);

    return PopScope(
      // A drag that starts at the edge of the phone's screen is also Android's back gesture, and
      // the PC's own edges (window borders, the first column of text) sit exactly there. Leaving
      // the screen in the middle of dragging something on the PC is never what was meant, so
      // while controlling, back only closes the keyboard, and leaving takes a second, deliberate
      // press.
      canPop: !controlling,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _onBackWhileControlling();
      },
      child: ValueListenableBuilder<MediaViewState>(
        valueListenable: _media,
        builder: (context, state, _) => Scaffold(
          backgroundColor: Colors.black,
          body: SafeArea(
            top: !_fullscreen,
            bottom: !_fullscreen,
            child: Column(
              children: [
                Expanded(child: _stage(state, mode, canPoint: canPoint, canType: canType)),
                if (_keyboardOpen && controlling && canType)
                  SpecialKeysBar(controller: _keyboard, onClose: _closeKeyboard, onExplain: _snack),
              ],
            ),
          ),
        ),
      ),
    );
  }

  void _onBackWhileControlling() {
    // The system took the touch for its own gesture, so whatever it was doing on the PC is over.
    _input.releaseAll();

    if (_keyboardOpen) {
      _closeKeyboard();
      return;
    }

    final now = DateTime.now();
    final last = _lastBackAt;
    if (last != null && now.difference(last) < const Duration(seconds: 2)) {
      _leave();
      return;
    }

    _lastBackAt = now;
    _snack('Press back again to close the screen.');
  }

  String get _pcName => _connection.status.pc?.deviceName ?? 'PC';

  /// Back to the list of PCs. The stream stops as this screen goes.
  void _leave() {
    _input.releaseAll();
    if (mounted) Navigator.of(context).pop();
  }

  void _togglePanMode() {
    setState(() => _panMode = !_panMode);
    _snack(_panMode
        ? 'Move: drag to move the screen left, right, up and down. Pinch to zoom. '
            'Tap the tool again to control the PC.'
        : 'Move off. Touches control the PC again.');
  }

  /// The picture and everything drawn over it.
  Widget _stage(MediaViewState state, _ControlMode mode, {required bool canPoint, required bool canType}) {
    final controlling = mode.isControlling;

    // Touches only reach the PC while there is a live picture to aim at, and not while the move
    // tool has them.
    final pointing = controlling && canPoint && state.phase == MediaPhase.streaming && !_panMode;

    return Stack(
      children: [
        Positioned.fill(child: _video(state, pointing: pointing)),
        if (_keyboardOpen && controlling && canType)
          Positioned(left: 0, top: 0, child: RemoteKeyboardField(controller: _keyboard)),
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
        Positioned(
          top: 4,
          left: 4,
          child: Material(
            color: Colors.black54,
            shape: const CircleBorder(),
            clipBehavior: Clip.antiAlias,
            child: IconButton(
              tooltip: 'Back to the list of PCs',
              icon: const Icon(Icons.arrow_back, color: Colors.white),
              visualDensity: VisualDensity.compact,
              onPressed: _leave,
            ),
          ),
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
          mode: mode,
          transform: _transform,
          keyboardOpen: _keyboardOpen,
          showKeyboardButton: controlling && canType,
          showPointerStyles: canPoint,
          onSelectControl: _selectControl,
          onToggleKeyboard: _keyboardOpen ? _closeKeyboard : _openKeyboard,
          onResetZoom: () => _transform.value = Matrix4.identity(),
          onExplainPermission: _explainPermission,
          panMode: _panMode,
          onTogglePanMode: _togglePanMode,
        ),
        Positioned(
          top: 50,
          left: 8,
          child: IgnorePointer(child: _PcLabel(name: _pcName, moving: _panMode)),
        ),
        // Drawn over the picture but never in the way of it: a touch on a label is a touch on
        // the PC underneath.
        if (mode != _ControlMode.unsupported)
          Positioned(top: 50, right: 8, child: IgnorePointer(child: _ModeBadge(mode: mode))),
        if (_showStats && state.quality != null && state.isActive)
          Positioned(left: 8, bottom: 8, child: IgnorePointer(child: _Telemetry(state: state))),
        if (mode == _ControlMode.notGranted && !_permissionNoticeDismissed)
          Positioned(
            left: 8,
            right: 8,
            bottom: 8,
            child: _PermissionNotice(
              onDismiss: () => setState(() => _permissionNoticeDismissed = true),
            ),
          ),
      ],
    );
  }

  Widget _video(MediaViewState state, {required bool pointing}) {
    return ControlSurface(
      transform: _transform,
      sender: _input,
      enabled: pointing,
      panMode: _panMode,
      style: _pointerStyle,
      videoSize: _videoSize,
      monitorId: state.monitorId,
      takeModifiers: _keyboard.takeModifiers,
      child: RTCVideoView(
        _media.renderer,
        objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitContain,
      ),
    );
  }

  /// The picture's shape, which is what places it inside its letterbox. The decoder knows best;
  /// before its first frame, the PC's telemetry and then the monitor list are the next best.
  Size? _videoSize() {
    final video = _media.renderer.value;
    final decoded = displayedVideoSize(video.width, video.height, video.rotation);
    if (decoded != null) return decoded;

    final quality = _media.value.quality;
    if (quality != null && quality.width > 0 && quality.height > 0) {
      return Size(quality.width.toDouble(), quality.height.toDouble());
    }

    for (final monitor in _monitors) {
      if (monitor.id == _media.value.monitorId && monitor.width > 0 && monitor.height > 0) {
        return Size(monitor.width.toDouble(), monitor.height.toDouble());
      }
    }

    return null;
  }

  void _selectControl(_ControlChoice choice) {
    switch (choice) {
      case _ControlChoice.viewOnly:
        // The build that follows sees control end and releases everything the PC is holding.
        setState(() => _controlWanted = false);
        _snack('View only. Pinch to zoom, drag to move, double-tap to reset.');

      case _ControlChoice.direct:
        setState(() {
          _controlWanted = true;
          _pointerStyle = PointerStyle.direct;
        });
        _snack(_ControlMode.direct.hint);

      case _ControlChoice.trackpad:
        setState(() {
          _controlWanted = true;
          _pointerStyle = PointerStyle.trackpad;
        });
        _snack(_ControlMode.trackpad.hint);

      case _ControlChoice.keyboardOnly:
        setState(() => _controlWanted = true);

      case _ControlChoice.guide:
        _showGuide();
    }
  }

  void _openKeyboard() {
    setState(() => _keyboardOpen = true);

    // The field exists only from the next frame on.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _keyboardOpen) _keyboard.show();
    });
  }

  void _closeKeyboard() {
    _keyboard.hide();
    if (mounted) setState(() => _keyboardOpen = false);
  }

  void _explainPermission() => setState(() => _permissionNoticeDismissed = false);

  Future<void> _showGuide() async {
    await showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Controlling the PC'),
        content: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              for (final (title, lines) in _guide) ...[
                Text(title, style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 4),
                for (final line in lines)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 2),
                    child: Text('• $line', style: Theme.of(context).textTheme.bodySmall),
                  ),
                const SizedBox(height: 12),
              ],
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Close')),
        ],
      ),
    );
  }

  static const _guide = <(String, List<String>)>[
    (
      'Touch',
      [
        'Tap: left click where you touch',
        'Double-tap: double click',
        'Swipe: scroll what is under your finger',
        'Touch and hold, then lift: right click',
        'Touch and hold, then move: drag with the left button held (select text, move windows)',
      ],
    ),
    (
      'Trackpad',
      [
        'Drag: move the cursor (faster drags go further)',
        'Tap: left click at the cursor',
        'Two-finger tap: right click',
        'Tap, then touch again and drag: drag with the left button held',
      ],
    ),
    (
      'Both',
      [
        'Two-finger drag: scroll the PC',
        'Pinch: zoom the view; keep the fingers down and drag to move it',
        'Three-finger drag: move the zoomed view',
        'Move tool (the arrows on the toolbar): one finger moves the screen left, right, up and '
            'down, without touching the PC',
        'Ctrl, Alt, Shift and Win on the keys bar apply to the next key, character or click',
      ],
    ),
  ];

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
    final response = await _connection.send(Commands.screenScreenshot, args: {
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
    ScaffoldMessenger.of(context)
      ..clearSnackBars()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

/// What touches on the picture currently do, and why.
enum _ControlMode {
  /// The PC does not offer input (an older build): control is not shown at all.
  unsupported,

  /// The PC offers it but this device lacks `ControlInput`.
  notGranted,

  /// Possible, and switched off by the user.
  viewOnly,

  /// The PC takes keys but not the mouse.
  keyboardOnly,
  direct,
  trackpad;

  bool get isControlling => this == keyboardOnly || this == direct || this == trackpad;

  IconData get icon => switch (this) {
        unsupported || viewOnly => Icons.visibility_outlined,
        notGranted => Icons.lock_person_outlined,
        keyboardOnly => Icons.keyboard_outlined,
        direct => Icons.touch_app_outlined,
        trackpad => Icons.mouse_outlined,
      };

  String get label => switch (this) {
        unsupported || viewOnly => 'View only',
        notGranted => 'View only · control not enabled',
        keyboardOnly => 'Keyboard control',
        direct => 'Touch control',
        trackpad => 'Trackpad control',
      };

  /// The one-line reminder shown when the mode is chosen.
  String get hint => switch (this) {
        direct => 'Touch: tap to click, swipe to scroll, hold for right click, hold then move to '
            'drag. Pinch zooms, three fingers move the view.',
        trackpad => 'Trackpad: drag to move the cursor, tap to click, two-finger tap for right '
            'click. Two fingers scroll, pinch zooms, three fingers move the view.',
        _ => label,
      };
}

/// The entries of the toolbar's control menu.
enum _ControlChoice { viewOnly, direct, trackpad, keyboardOnly, guide }

/// The small label saying what touches do right now.
class _ModeBadge extends StatelessWidget {
  const _ModeBadge({required this.mode});

  final _ControlMode mode;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(12)),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(mode.icon, size: 12, color: mode.isControlling ? Colors.greenAccent : Colors.white70),
            const SizedBox(width: 5),
            Text(mode.label, style: const TextStyle(color: Colors.white, fontSize: 11)),
          ],
        ),
      ),
    );
  }
}

/// Which PC this is, and whether the move tool has the touches.
class _PcLabel extends StatelessWidget {
  const _PcLabel({required this.name, required this.moving});

  final String name;
  final bool moving;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(12)),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 180),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                moving ? Icons.open_with : Icons.desktop_windows_outlined,
                size: 12,
                color: moving ? Colors.greenAccent : Colors.white70,
              ),
              const SizedBox(width: 5),
              Flexible(
                child: Text(
                  moving ? '$name · moving the screen' : name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(color: Colors.white, fontSize: 11),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Why this device can look but not touch, in the same words and shape as the System tab's
/// notice about power controls.
class _PermissionNotice extends StatelessWidget {
  const _PermissionNotice({required this.onDismiss});

  final VoidCallback onDismiss;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    return Container(
      padding: const EdgeInsets.fromLTRB(14, 10, 4, 12),
      decoration: BoxDecoration(
        color: scheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Icon(Icons.lock_person_outlined, size: 20, color: scheme.onSurfaceVariant),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Mouse and keyboard control is not enabled for this device',
                    style: theme.textTheme.titleSmall,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    'You can watch the screen but not control it. Open PC-Remote on the PC, find '
                    'this device under Paired Devices, and turn on '
                    '${Permissions.label(Permissions.controlInput)}.',
                    style: theme.textTheme.bodySmall,
                  ),
                ],
              ),
            ),
          ),
          IconButton(
            tooltip: 'Dismiss',
            visualDensity: VisualDensity.compact,
            icon: const Icon(Icons.close, size: 18),
            onPressed: onDismiss,
          ),
        ],
      ),
    );
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
    required this.mode,
    required this.transform,
    required this.keyboardOpen,
    required this.showKeyboardButton,
    required this.showPointerStyles,
    required this.onSelectControl,
    required this.onToggleKeyboard,
    required this.onResetZoom,
    required this.onExplainPermission,
    required this.panMode,
    required this.onTogglePanMode,
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

  final _ControlMode mode;
  final TransformationController transform;
  final bool keyboardOpen;
  final bool showKeyboardButton;

  /// Whether the PC takes mouse input, so the touch and trackpad styles can be offered.
  final bool showPointerStyles;
  final ValueChanged<_ControlChoice> onSelectControl;
  final VoidCallback onToggleKeyboard;
  final VoidCallback onResetZoom;
  final VoidCallback onExplainPermission;

  /// Whether the move tool is on: touches move the picture instead of controlling the PC.
  final bool panMode;
  final VoidCallback onTogglePanMode;

  @override
  Widget build(BuildContext context) {
    final live = state.isActive;

    // Spans the width so that on a narrow phone the buttons scroll instead of running off the
    // edge; the alignment keeps the pill hugging the right as before, and the empty part of the
    // strip lets touches through to the picture.
    // Starts clear of the back button in the top-left corner.
    return Positioned(
      top: 4,
      right: 4,
      left: 52,
      child: Align(
        alignment: Alignment.topRight,
        child: Material(
          color: Colors.black54,
          borderRadius: BorderRadius.circular(24),
          clipBehavior: Clip.antiAlias,
          child: IconTheme(
            data: const IconThemeData(color: Colors.white),
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              reverse: true,
              // Slightly tighter buttons, so the whole bar fits a narrow phone without scrolling.
              child: IconButtonTheme(
                data: IconButtonThemeData(
                  style: IconButton.styleFrom(visualDensity: VisualDensity.compact),
                ),
                child: Row(mainAxisSize: MainAxisSize.min, children: _buttons(live)),
              ),
            ),
          ),
        ),
      ),
    );
  }

  List<Widget> _buttons(bool live) => [
      if (mode == _ControlMode.notGranted)
        IconButton(
          tooltip: 'Control is not enabled for this device',
          icon: Icon(mode.icon),
          onPressed: onExplainPermission,
        )
      else if (mode != _ControlMode.unsupported)
        PopupMenuButton<_ControlChoice>(
          tooltip: 'Control',
          icon: Icon(mode.icon, color: mode.isControlling ? Colors.greenAccent : Colors.white),
          onSelected: onSelectControl,
          itemBuilder: (_) => [
            if (showPointerStyles) ...[
              CheckedPopupMenuItem(
                value: _ControlChoice.direct,
                checked: mode == _ControlMode.direct,
                child: const ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text('Touch'),
                  subtitle: Text('Tap the PC where you touch'),
                ),
              ),
              CheckedPopupMenuItem(
                value: _ControlChoice.trackpad,
                checked: mode == _ControlMode.trackpad,
                child: const ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text('Trackpad'),
                  subtitle: Text('Push the cursor around'),
                ),
              ),
            ] else
              CheckedPopupMenuItem(
                value: _ControlChoice.keyboardOnly,
                checked: mode == _ControlMode.keyboardOnly,
                child: const ListTile(
                  contentPadding: EdgeInsets.zero,
                  title: Text('Keyboard'),
                  subtitle: Text('This PC takes keys but not the mouse'),
                ),
              ),
            CheckedPopupMenuItem(
              value: _ControlChoice.viewOnly,
              checked: mode == _ControlMode.viewOnly,
              child: const ListTile(
                contentPadding: EdgeInsets.zero,
                title: Text('View only'),
                subtitle: Text('Touches zoom and move the picture'),
              ),
            ),
            const PopupMenuDivider(),
            const PopupMenuItem(
              value: _ControlChoice.guide,
              child: ListTile(
                contentPadding: EdgeInsets.zero,
                leading: Icon(Icons.help_outline),
                title: Text('Gestures'),
              ),
            ),
          ],
        ),
      // The move tool: one finger moves the picture around, for reaching the parts of a zoomed
      // screen that are off the phone's.
      IconButton(
        tooltip: panMode ? 'Stop moving the screen' : 'Move the screen',
        icon: Icon(Icons.open_with, color: panMode ? Colors.greenAccent : Colors.white),
        onPressed: onTogglePanMode,
      ),
      if (showKeyboardButton)
        IconButton(
          tooltip: keyboardOpen ? 'Close the keyboard' : 'Type on the PC',
          icon: Icon(keyboardOpen ? Icons.keyboard_hide_outlined : Icons.keyboard_outlined),
          onPressed: onToggleKeyboard,
        ),
      // With control on a double tap is a double click, so resetting the zoom needs a
      // button — shown only while there is a zoom to reset.
      if (mode.isControlling || panMode)
        ValueListenableBuilder<Matrix4>(
          valueListenable: transform,
          builder: (context, matrix, _) => ViewTransform.fromMatrix(matrix).isZoomed
              ? IconButton(
                  tooltip: 'Reset zoom',
                  icon: const Icon(Icons.zoom_out_map),
                  onPressed: onResetZoom,
                )
              : const SizedBox.shrink(),
        ),
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
      ];
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
