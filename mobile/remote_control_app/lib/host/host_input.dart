// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:typed_data';
import 'dart:ui';

import '../protocol/input_messages.dart';
import 'block_list.dart';
import 'host_session.dart';
import 'input_mapper.dart';

/// One installed app, as the phone lists it.
class InstalledApp {
  const InstalledApp({required this.package, required this.name, this.iconPng, this.isForeground = false});

  final String package;
  final String name;

  /// Base64 PNG, when asked for.
  final String? iconPng;

  /// Whether this app's window is in front right now.
  final bool isForeground;
}

/// What the phone itself does for the host: everything that needs Android. Implemented over a
/// platform channel on a phone and faked in tests.
abstract class HostPlatformServices {
  /// The screen's size in pixels in its current orientation.
  Future<Size> screenSize();

  /// 0, 90, 180 or 270 degrees.
  Future<int> screenRotation();

  /// Whether the accessibility service is switched on, without which no input can be performed.
  Future<bool> isAccessibilityEnabled();

  /// Whether the keyguard is engaged.
  Future<bool> isLocked();

  Future<bool> tap(Offset at, {required Duration duration});

  Future<bool> swipe(Swipe swipe);

  /// A continued stroke: [dragStart] puts the finger down, each [dragMove] slides it, [dragEnd]
  /// lifts it. The finger stays down between calls.
  Future<bool> dragStart(Offset at);

  Future<bool> dragMove(Swipe segment);

  Future<bool> dragEnd();

  /// Inserts text at the cursor of the focused field.
  Future<bool> typeText(String text);

  /// Performs one of the key-like actions.
  Future<bool> keyAction(PhoneKeyAction action);

  /// One of [PhoneNavActions].
  Future<bool> navAction(String action);

  Future<List<InstalledApp>> listApps({required bool includeIcons});

  Future<bool> launchApp(String package);

  /// A JPEG of the screen no wider than [maxWidth], or null when the phone cannot take one.
  Future<Uint8List?> screenshotJpeg({required int maxWidth});
}

/// Turns a viewer's input commands into touches on this phone, through the block list.
///
/// One instance per host, one [DragTracker] per connection: a viewer that disconnects mid-drag
/// has its finger lifted, and a second viewer's drag is not confused with the first's.
///
/// **Every input passes the gate first.** While a blocked app is in front, each command is refused
/// with `E_BLOCKED_BY_OWNER` before anything reaches the accessibility service, and a drag in
/// progress is ended: a finger left down could otherwise land on the blocked app when it came up.
class InputGateway {
  InputGateway({
    required HostPlatformServices platform,
    required BlockListGate gate,
    PhoneInputMapper mapper = const PhoneInputMapper(),
  })  : _platform = platform,
        _gate = gate,
        _mapper = mapper {
    _gate.addListener(_onGateChanged);
  }

  final HostPlatformServices _platform;
  final BlockListGate _gate;
  final PhoneInputMapper _mapper;
  final _drags = <String, DragTracker>{};
  bool _wasBlocked = false;

  Future<HostResult> handle(HostCall call) async {
    if (_gate.isBlocked) return HostResult.blocked;

    if (!await _platform.isAccessibilityEnabled()) {
      return const HostResult.fail(
        ErrorCodesForInput.accessibilityOff,
        'Remote control is not switched on in the phone\'s Accessibility settings.',
        retryable: true,
      );
    }

    final args = call.args;
    final drag = _drags.putIfAbsent(call.caller.connectionId, DragTracker.new);

    switch (call.command) {
      case InputCommands.mouseMove:
        final screen = await _platform.screenSize();
        final at = _mapper.toPixels(args['x'] as num?, args['y'] as num?, screen);

        if (at == null) {
          // Relative moves have no meaning without a cursor; the pointer stays where it was.
          final current = drag.position;
          if (current == null) return HostResult.empty;

          final dx = (args['dx'] as num?)?.toDouble() ?? 0;
          final dy = (args['dy'] as num?)?.toDouble() ?? 0;
          final moved = Offset(
            (current.dx + dx).clamp(0.0, screen.width - 1),
            (current.dy + dy).clamp(0.0, screen.height - 1),
          );
          return _segment(drag.move(moved));
        }

        return _segment(drag.move(at));

      case InputCommands.mouseButton:
        final down = args['down'];
        if (down is! bool) return HostResult.invalidArguments('"down" is required.');

        if (PhoneInputMapper.isLongPress(args['button'] as String?)) {
          // A held right button has no phone meaning; the release is where the long press happens.
          if (down) return HostResult.empty;
          final at = drag.position;
          if (at == null) return HostResult.empty;
          return _ok(await _platform.tap(at, duration: PhoneInputMapper.longPressDuration));
        }

        if (down) {
          final screen = await _platform.screenSize();
          final at = drag.press(_mapper.toPixels(args['x'] as num?, args['y'] as num?, screen));
          if (at == null) return HostResult.invalidArguments('A position is required for the first press.');
          return _ok(await _platform.dragStart(at));
        }

        return drag.release() ? _ok(await _platform.dragEnd()) : HostResult.empty;

      case InputCommands.mouseClick:
        final screen = await _platform.screenSize();
        final at = _mapper.toPixels(args['x'] as num?, args['y'] as num?, screen) ?? drag.position;
        if (at == null) return HostResult.invalidArguments('A position is required.');
        drag.move(at);

        if (PhoneInputMapper.isLongPress(args['button'] as String?)) {
          return _ok(await _platform.tap(at, duration: PhoneInputMapper.longPressDuration));
        }

        final count = (args['count'] as num?)?.toInt() ?? 1;
        var ok = await _platform.tap(at, duration: PhoneInputMapper.tapDuration);
        if (count >= 2) {
          await Future<void>.delayed(const Duration(milliseconds: 60));
          ok = await _platform.tap(at, duration: PhoneInputMapper.tapDuration) && ok;
        }
        return _ok(ok);

      case InputCommands.scroll:
        final screen = await _platform.screenSize();
        final at = _mapper.toPixels(args['x'] as num?, args['y'] as num?, screen) ??
            drag.position ??
            screen.center(Offset.zero);
        drag.move(at);

        final swipe = _mapper.scrollToSwipe(args['dx'] as num?, args['dy'] as num?, at, screen);
        return swipe == null ? HostResult.empty : _ok(await _platform.swipe(swipe));

      case InputCommands.key:
        final vk = (args['vk'] as num?)?.toInt();
        final down = args['down'];
        if (vk == null || down is! bool) return HostResult.invalidArguments('"vk" and "down" are required.');

        // Keys act on release, so a press-and-release pair acts once.
        if (down) return HostResult.empty;

        final action = PhoneInputMapper.forVirtualKey(vk);
        if (action != null) return _ok(await _platform.keyAction(action));

        final text = PhoneInputMapper.textForVirtualKey(vk);
        if (text != null) return _ok(await _platform.typeText(text));

        // Modifiers and function keys: nothing to do on a phone, and not an error.
        return HostResult.empty;

      case InputCommands.text:
        final text = args['text'];
        if (text is! String || text.isEmpty) return HostResult.invalidArguments('"text" is required.');
        if (text.length > InputArgs.maxTextUnits) {
          return HostResult.invalidArguments('Text is limited to ${InputArgs.maxTextUnits} characters.');
        }
        return _ok(await _platform.typeText(text));

      case InputCommands.shortcut:
        final keys = (args['keys'] as List<dynamic>? ?? const []).whereType<num>().map((k) => k.toInt());
        // A shortcut is pressed in order; on a phone only the last non-modifier key can mean anything.
        for (final vk in keys.toList().reversed) {
          final action = PhoneInputMapper.forVirtualKey(vk);
          if (action != null) return _ok(await _platform.keyAction(action));
        }
        return HostResult.empty;

      case InputCommands.releaseAll:
        await releaseAll(call.caller.connectionId);
        return HostResult.empty;

      default:
        // phone.nav
        final action = args['action'] as String?;
        if (action == null || !PhoneNavActions.all.contains(action)) {
          return HostResult.invalidArguments('"action" must be one of ${PhoneNavActions.all.join(', ')}.');
        }
        return _ok(await _platform.navAction(action));
    }
  }

  /// Lifts anything [connectionId] is holding. Called on `input.release_all` and on disconnect.
  Future<void> releaseAll(String connectionId) async {
    final drag = _drags.remove(connectionId);
    if (drag != null && drag.release()) await _platform.dragEnd();
  }

  Future<HostResult> _segment(Swipe? segment) async =>
      segment == null ? HostResult.empty : _ok(await _platform.dragMove(segment));

  HostResult _ok(bool performed) => performed
      ? HostResult.empty
      : const HostResult.fail(
          ErrorCodesForInput.accessibilityOff,
          'The phone could not perform the touch. Check that remote control is switched on in '
          'Accessibility settings.',
          retryable: true,
        );

  void _onGateChanged() {
    final blocked = _gate.isBlocked;
    if (blocked && !_wasBlocked) {
      // Lift every finger: one left down would come up on the blocked app.
      for (final id in _drags.keys.toList()) {
        releaseAll(id);
      }
    }
    _wasBlocked = blocked;
  }

  void dispose() {
    _gate.removeListener(_onGateChanged);
  }
}

/// Error codes that only a phone host sends about input.
class ErrorCodesForInput {
  const ErrorCodesForInput._();

  /// The accessibility service is off, so no touch can be performed. The PC's nearest equivalent
  /// is `E_SESSION_UNAVAILABLE`, and that is what is sent, so an older viewer explains it sensibly.
  static const accessibilityOff = 'E_SESSION_UNAVAILABLE';
}
