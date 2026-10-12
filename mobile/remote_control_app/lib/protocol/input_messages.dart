/// Wire names and argument builders for remote input (Phase 4), mirroring the `input.*` rows of
/// the PC's command catalogue.
///
/// Kept apart from `protocol.dart` so the two halves of Phase 4 can be written side by side, and
/// built as plain maps for the same reason the rest of the protocol is hand-written: a reader
/// should be able to hold this file against the contract table and tick off every field.
///
/// Null fields are omitted rather than sent as `null`, because the PC reads a missing `x`/`y` as
/// "act where the cursor already is" and a missing `monitorId` as "the monitor being streamed".
library;

/// Input command names. Must match `CommandNames` on the PC exactly.
class InputCommands {
  const InputCommands._();

  static const mouseMove = 'input.mouse_move';
  static const mouseButton = 'input.mouse_button';
  static const mouseClick = 'input.mouse_click';
  static const scroll = 'input.scroll';
  static const key = 'input.key';
  static const text = 'input.text';
  static const shortcut = 'input.shortcut';
  static const releaseAll = 'input.release_all';
}

/// The capability strings a PC advertises in `hello` when it can inject input. A PC that does not
/// list them is an older build, and control is hidden rather than offered and refused.
class InputCapabilities {
  const InputCapabilities._();

  static const mouse = 'input.mouse';
  static const keyboard = 'input.keyboard';
}

/// Mouse buttons, as the PC spells them.
enum MouseButton {
  left('left'),
  right('right'),
  middle('middle');

  const MouseButton(this.wire);

  final String wire;
}

/// The Windows virtual-key codes this app sends. Standard `VK_*` values.
class VirtualKeys {
  const VirtualKeys._();

  static const back = 8;
  static const tab = 9;
  static const enter = 13;
  static const shift = 16;
  static const control = 17;
  static const alt = 18;
  static const escape = 27;
  static const space = 32;
  static const pageUp = 33;
  static const pageDown = 34;
  static const end = 35;
  static const home = 36;
  static const left = 37;
  static const up = 38;
  static const right = 39;
  static const down = 40;
  static const printScreen = 44;
  static const insert = 45;
  static const delete = 46;
  static const win = 91;
  static const f1 = 112;

  /// F1..F12 are contiguous: 112..123.
  static int function(int number) => f1 + number - 1;

  /// The modifiers, in the order they are pressed in a shortcut.
  static const modifiers = <int>[control, alt, shift, win];

  /// The key that types [character] on its own, or null when no layout-independent key does.
  ///
  /// Only letters, digits, space, Enter and Tab qualify: punctuation lives on OEM keys whose
  /// meaning depends on the PC's keyboard layout, so it is typed as text instead.
  static int? forCharacter(String character) {
    if (character.length != 1) return null;

    final unit = character.codeUnitAt(0);
    if (unit >= 0x61 && unit <= 0x7A) return unit - 0x20; // a-z -> 'A'-'Z'
    if (unit >= 0x41 && unit <= 0x5A) return unit; // A-Z
    if (unit >= 0x30 && unit <= 0x39) return unit; // 0-9

    return switch (character) {
      ' ' => space,
      '\n' => enter,
      '\t' => tab,
      _ => null,
    };
  }
}

/// Builds the `args` object of each input command.
class InputArgs {
  const InputArgs._();

  /// The most text one `input.text` may carry, in UTF-16 units.
  static const maxTextUnits = 2000;

  /// The most keys one `input.shortcut` may carry.
  static const maxShortcutKeys = 6;

  /// `input.mouse_move`, absolute: [x] and [y] are 0..1 across one monitor.
  static Map<String, dynamic> moveTo(double x, double y, {String? monitorId}) => {
        'x': _unit(x),
        'y': _unit(y),
        if (monitorId != null) 'monitorId': monitorId,
      };

  /// `input.mouse_move`, relative: pixels on the PC. Chosen by the PC when `x`/`y` are absent.
  static Map<String, dynamic> moveBy(int dx, int dy) => {'dx': dx, 'dy': dy};

  /// `input.mouse_button`: press or release. With a position the PC moves there first.
  static Map<String, dynamic> button(
    MouseButton button, {
    required bool down,
    double? x,
    double? y,
    String? monitorId,
  }) =>
      {
        'button': button.wire,
        'down': down,
        ..._at(x, y, monitorId),
      };

  /// `input.mouse_click`: a whole click, or a double click when [count] is 2.
  static Map<String, dynamic> click(
    MouseButton button, {
    int count = 1,
    double? x,
    double? y,
    String? monitorId,
  }) =>
      {
        'button': button.wire,
        'count': count < 2 ? 1 : 2,
        ..._at(x, y, monitorId),
      };

  /// `input.scroll`: wheel units, 120 to a notch. [dy] > 0 scrolls up, [dx] > 0 scrolls right.
  static Map<String, dynamic> scroll(int dx, int dy, {double? x, double? y, String? monitorId}) => {
        'dx': dx,
        'dy': dy,
        ..._at(x, y, monitorId),
      };

  /// `input.key`: one key going down or coming up.
  static Map<String, dynamic> key(int vk, {required bool down}) => {'vk': vk, 'down': down};

  /// `input.text`: typed as Unicode. The caller keeps it within [maxTextUnits]; see [chunkText].
  static Map<String, dynamic> text(String text) => {'text': text};

  /// `input.shortcut`: pressed in order, released in reverse.
  static Map<String, dynamic> shortcut(List<int> keys) => {'keys': List<int>.of(keys)};

  /// Splits [text] into pieces no longer than [maxUnits], never between the two halves of a
  /// surrogate pair: half an emoji is not a character the PC can type.
  static List<String> chunkText(String text, {int maxUnits = maxTextUnits}) {
    final chunks = <String>[];
    var start = 0;

    while (start < text.length) {
      var end = start + maxUnits;
      if (end >= text.length) {
        end = text.length;
      } else if (_isHighSurrogate(text.codeUnitAt(end - 1)) && end - 1 > start) {
        end--;
      }

      chunks.add(text.substring(start, end));
      start = end;
    }

    return chunks;
  }

  /// A position is all or nothing: `x` without `y` would be read by the PC as "no position".
  static Map<String, dynamic> _at(double? x, double? y, String? monitorId) => {
        if (x != null && y != null) ...{
          'x': _unit(x),
          'y': _unit(y),
          if (monitorId != null) 'monitorId': monitorId,
        },
      };

  /// The PC clamps out-of-range values but rejects NaN and infinity; neither is ever sent.
  static double _unit(double value) => value.isFinite ? value.clamp(0.0, 1.0).toDouble() : 0.0;

  static bool _isHighSurrogate(int unit) => unit >= 0xD800 && unit <= 0xDBFF;
}
