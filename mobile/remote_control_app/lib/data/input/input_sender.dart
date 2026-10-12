import 'dart:async';

import '../../protocol/input_messages.dart';
import '../../protocol/protocol.dart';

/// How a command reaches the PC. Shaped like `ConnectionController.send`, which is what the app
/// passes; tests pass a recorder.
typedef InputTransport = Future<ResponseEnvelope?> Function(String command, {Map<String, dynamic>? args});

/// Why the PC refused input, in words the user can act on.
class InputFault {
  const InputFault(this.code, this.message);

  factory InputFault.from(ProtocolError error) => InputFault(
        error.code,
        switch (error.code) {
          ErrorCodes.desktopLocked =>
            'The PC is locked, so it cannot be controlled. Unlock it at the PC.',
          ErrorCodes.secureDesktop =>
            'Windows is showing a secure prompt (UAC or Ctrl+Alt+Del). It can only be answered '
                'at the PC.',
          ErrorCodes.permissionDenied =>
            'This device is not allowed to control the mouse and keyboard. Enable it on the PC.',
          ErrorCodes.sessionUnavailable => 'Nobody is signed in to the PC, so there is nothing to control.',
          ErrorCodes.unknownCommand ||
          ErrorCodes.notSupported =>
            'This PC cannot be controlled from the phone. Update PC-Remote on the PC.',
          _ => error.message,
        },
      );

  final String code;
  final String message;
}

/// The phone's half of remote input: every `input.*` command, typed.
///
/// **Nothing here waits for the PC.** A request is written to the socket the moment it is made
/// and its response is only looked at afterwards, for errors. Input that queued behind a slow
/// answer would arrive late and in a burst, which is worse than not arriving: the cursor would
/// keep moving after the finger stopped.
///
/// **Motion is coalesced, everything else is not.** Pointer moves and wheel scrolling are
/// continuous, so only the latest matters: at most one is ever pending, and at most one is sent
/// per [minMotionInterval] (~60/s). Buttons, keys and text are discrete and each is sent at once —
/// after flushing the pending motion, so a click can never overtake the move that aimed it.
///
/// **Errors are said once.** A locked PC refuses every move of a drag; the user needs to hear
/// that one time, not sixty times a second. A fault is reported when its code first appears and
/// not again until a command has succeeded in between.
class InputSender {
  InputSender(
    this._send, {
    this.onFault,
    this.minMotionInterval = const Duration(milliseconds: 16),
  });

  final InputTransport _send;

  /// Called with each new kind of refusal. See the class comment for what "new" means.
  final void Function(InputFault fault)? onFault;

  /// The shortest gap between two motion commands.
  final Duration minMotionInterval;

  _Motion? _pending;
  Timer? _cooldown;
  String? _lastFaultCode;
  bool _dirty = false;
  bool _disposed = false;

  /// Moves the cursor to a point on a monitor; [x] and [y] are 0..1. Coalesced.
  void moveTo(double x, double y, {String? monitorId}) =>
      _motion(_Motion.absolute(x, y, monitorId));

  /// Moves the cursor by PC pixels. Coalesced: pending deltas add up, none is dropped.
  void moveBy(int dx, int dy) {
    if (dx == 0 && dy == 0) return;
    _motion(_Motion.relative(dx, dy));
  }

  /// Turns the wheel; 120 units to a notch, [dy] > 0 up, [dx] > 0 right. Coalesced like [moveBy].
  void scroll(int dx, int dy, {double? x, double? y, String? monitorId}) {
    if (dx == 0 && dy == 0) return;
    _motion(_Motion.scroll(dx, dy, x, y, monitorId));
  }

  /// Presses or releases a mouse button, moving to the position first when one is given.
  void button(MouseButton button, {required bool down, double? x, double? y, String? monitorId}) =>
      _ordered(
        InputCommands.mouseButton,
        InputArgs.button(button, down: down, x: x, y: y, monitorId: monitorId),
      );

  /// Clicks, or double-clicks when [count] is 2.
  void click(MouseButton button, {int count = 1, double? x, double? y, String? monitorId}) =>
      _ordered(
        InputCommands.mouseClick,
        InputArgs.click(button, count: count, x: x, y: y, monitorId: monitorId),
      );

  /// Presses or releases one key.
  void key(int vk, {required bool down}) => _ordered(InputCommands.key, InputArgs.key(vk, down: down));

  /// Presses and releases one key.
  void tapKey(int vk) {
    key(vk, down: true);
    key(vk, down: false);
  }

  /// Types text as Unicode. Longer text than one command may carry is split.
  void text(String text) {
    final normalised = text.replaceAll('\r\n', '\n').replaceAll('\r', '\n');
    for (final chunk in InputArgs.chunkText(normalised)) {
      _ordered(InputCommands.text, InputArgs.text(chunk));
    }
  }

  /// Presses [keys] in order and releases them in reverse.
  void shortcut(List<int> keys) {
    if (keys.isEmpty || keys.length > InputArgs.maxShortcutKeys) return;
    _ordered(InputCommands.shortcut, InputArgs.shortcut(keys));
  }

  /// Lets go of everything the PC is holding for this phone, and forgets any motion not yet sent.
  ///
  /// Called whenever control ends for any reason. Skipped when nothing has been sent since the
  /// last release: there is nothing to let go of.
  void releaseAll() {
    _pending = null;
    _cooldown?.cancel();
    _cooldown = null;

    if (!_dirty) return;

    _dispatch(InputCommands.releaseAll, null, observe: false);
    _dirty = false;
  }

  /// Releases everything and stops. Later calls are ignored.
  void dispose() {
    if (_disposed) return;

    releaseAll();
    _disposed = true;
  }

  void _motion(_Motion motion) {
    if (_disposed) return;

    final pending = _pending;
    if (pending == null) {
      _pending = motion;
    } else if (pending.absorbs(motion)) {
      _pending = pending.merge(motion);
    } else {
      // A different kind of motion: the earlier one goes out first, so order is preserved.
      _flush();
      _pending = motion;
    }

    if (_cooldown == null) {
      _flush();
      _startCooldown();
    }
  }

  void _startCooldown() {
    _cooldown = Timer(minMotionInterval, () {
      _cooldown = null;

      if (_pending != null) {
        _flush();
        _startCooldown();
      }
    });
  }

  void _flush() {
    final pending = _pending;
    if (pending == null) return;

    _pending = null;
    _dispatch(pending.command, pending.args);
  }

  void _ordered(String command, Map<String, dynamic> args) {
    if (_disposed) return;

    _flush();
    _dispatch(command, args);
  }

  void _dispatch(String command, Map<String, dynamic>? args, {bool observe = true}) {
    _dirty = true;

    try {
      final response = _send(command, args: args);
      if (observe) {
        response.then(_observe, onError: (Object _) {});
      } else {
        response.ignore();
      }
    } on Object {
      // Not connected, or the socket went away mid-write. The connection banner says so; input
      // that cannot be delivered is simply dropped.
    }
  }

  void _observe(ResponseEnvelope? response) {
    // Null means "not connected", which the rest of the app already explains.
    if (response == null || _disposed) return;

    if (response.ok) {
      _lastFaultCode = null;
      return;
    }

    final error = response.error ?? const ProtocolError(code: ErrorCodes.internal, message: 'The PC refused the input.');
    if (error.code == _lastFaultCode) return;

    _lastFaultCode = error.code;
    onFault?.call(InputFault.from(error));
  }
}

enum _MotionKind { absolute, relative, scroll }

/// One not-yet-sent motion command.
class _Motion {
  const _Motion.absolute(double this.x, double this.y, this.monitorId)
      : kind = _MotionKind.absolute,
        dx = 0,
        dy = 0;

  const _Motion.relative(this.dx, this.dy)
      : kind = _MotionKind.relative,
        x = null,
        y = null,
        monitorId = null;

  const _Motion.scroll(this.dx, this.dy, this.x, this.y, this.monitorId) : kind = _MotionKind.scroll;

  final _MotionKind kind;
  final int dx;
  final int dy;
  final double? x;
  final double? y;
  final String? monitorId;

  bool absorbs(_Motion other) => other.kind == kind;

  /// Positions replace each other; deltas accumulate.
  _Motion merge(_Motion later) => switch (kind) {
        _MotionKind.absolute => later,
        _MotionKind.relative => _Motion.relative(dx + later.dx, dy + later.dy),
        _MotionKind.scroll => _Motion.scroll(dx + later.dx, dy + later.dy, later.x, later.y, later.monitorId),
      };

  String get command => kind == _MotionKind.scroll ? InputCommands.scroll : InputCommands.mouseMove;

  Map<String, dynamic> get args => switch (kind) {
        _MotionKind.absolute => InputArgs.moveTo(x!, y!, monitorId: monitorId),
        _MotionKind.relative => InputArgs.moveBy(dx, dy),
        _MotionKind.scroll => InputArgs.scroll(dx, dy, x: x, y: y, monitorId: monitorId),
      };
}
