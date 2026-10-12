import '../../protocol/input_messages.dart';

/// One thing to do on the PC's keyboard.
sealed class KeyAction {
  const KeyAction();
}

/// Type [text] as Unicode (`input.text`).
class TypeText extends KeyAction {
  const TypeText(this.text);

  final String text;

  @override
  bool operator ==(Object other) => other is TypeText && other.text == text;

  @override
  int get hashCode => text.hashCode;

  @override
  String toString() => 'TypeText("$text")';
}

/// Press and release one key (`input.key` down, then up).
class TapKey extends KeyAction {
  const TapKey(this.vk);

  final int vk;

  @override
  bool operator ==(Object other) => other is TapKey && other.vk == vk;

  @override
  int get hashCode => vk.hashCode;

  @override
  String toString() => 'TapKey($vk)';
}

/// Press [keys] together (`input.shortcut`).
class PressShortcut extends KeyAction {
  const PressShortcut(this.keys);

  final List<int> keys;

  @override
  bool operator ==(Object other) =>
      other is PressShortcut && other.keys.length == keys.length && _sameKeys(other.keys);

  bool _sameKeys(List<int> other) {
    for (var i = 0; i < keys.length; i++) {
      if (keys[i] != other[i]) return false;
    }
    return true;
  }

  @override
  int get hashCode => Object.hashAll(keys);

  @override
  String toString() => 'PressShortcut($keys)';
}

/// What an edit of the hidden text field means for the PC.
class KeyboardUpdate {
  const KeyboardUpdate(this.actions, {this.resetField = false});

  final List<KeyAction> actions;

  /// The field should be put back to [KeyboardTranslator.sentinel] now.
  final bool resetField;
}

/// Turns what the phone's soft keyboard does to a hidden text field into keys on the PC.
///
/// A soft keyboard does not send keystrokes; it edits text. So the field's text is compared with
/// what it held a moment ago, and the difference is replayed: characters that disappeared become
/// Backspace, characters that appeared are typed. That one rule covers plain typing, paste, emoji,
/// and — the reason for doing it this way — predictive keyboards and IMEs, which rewrite the word
/// being composed as often as they like. Whatever they replace is backspaced and retyped, so
/// nothing is typed twice and nothing is missed.
///
/// **The sentinel.** An empty field gives Backspace nothing to delete, so the keyboard reports
/// nothing and the PC would never hear it. The field therefore always starts with a run of
/// filler characters that are never sent; eating into them is how Backspace is detected once the
/// phone's own buffer is exhausted.
///
/// **Sticky modifiers.** Ctrl, Alt, Shift and Win are armed by a tap and apply to the next key,
/// typed character or mouse click, then let go by themselves. An armed Ctrl followed by "c" is the
/// shortcut Ctrl+C, not the letter.
class KeyboardTranslator {
  /// What the field holds when the phone's buffer is empty. Digits rather than spaces: keyboards
  /// rewrite runs of spaces (two become ". "), and leave digits alone.
  static const sentinel = '1111111111111111';

  /// How much typed text is kept in the field before it is emptied again. The field only needs
  /// enough history for the keyboard to revise the current word.
  static const maxBuffer = 64;

  String _text = sentinel;
  final Set<int> _armed = <int>{};

  /// The field's text as last seen.
  String get text => _text;

  /// The armed modifiers, in press order.
  List<int> get armed => [
        for (final vk in VirtualKeys.modifiers)
          if (_armed.contains(vk)) vk,
      ];

  bool isArmed(int vk) => _armed.contains(vk);

  /// Arms or disarms one modifier.
  void toggle(int vk) {
    if (!VirtualKeys.modifiers.contains(vk)) return;
    if (!_armed.remove(vk)) _armed.add(vk);
  }

  /// Hands over the armed modifiers and disarms them: they have been used.
  List<int> takeModifiers() {
    final taken = armed;
    _armed.clear();
    return taken;
  }

  /// Forgets the buffer, as after the field was reset.
  void reset() => _text = sentinel;

  /// The field now holds [newText]. [composing] says the keyboard is still working on a word, in
  /// which case the field is left alone unless it has to be reset.
  KeyboardUpdate textChanged(String newText, {bool composing = false}) {
    final old = _text;
    _text = newText;

    if (newText == old) return const KeyboardUpdate([]);

    var prefix = _commonPrefix(old, newText);
    var removed = old.substring(prefix);
    var added = newText.substring(prefix);
    var forceReset = false;

    // An edit that starts inside the sentinel is either Backspace on an empty buffer — the field
    // is now just a shorter sentinel — or something stranger, such as the keyboard's own cursor
    // control having moved the caret. For the strange cases only the part that really changed is
    // replayed, so the filler can never be typed on the PC.
    if (prefix < sentinel.length && added.isNotEmpty) {
      final suffix = _commonSuffix(removed, added);
      removed = removed.substring(0, removed.length - suffix);
      added = added.substring(0, added.length - suffix);

      // Filler that vanished in such an edit (a select-all and replace, say) was never on the PC,
      // so it is not backspaced there either.
      final filler = sentinel.length - prefix;
      removed = removed.substring(filler < removed.length ? filler : removed.length);
      forceReset = true;
    }

    final backspaces = removed.runes.length;
    final actions = <KeyAction>[];

    if (_armed.isNotEmpty && (backspaces > 0 || added.isNotEmpty)) {
      final modifiers = takeModifiers();
      final vk = backspaces == 0
          ? VirtualKeys.forCharacter(added)
          : (backspaces == 1 && added.isEmpty ? VirtualKeys.back : null);

      if (vk != null) {
        actions.add(PressShortcut([...modifiers, vk]));

        // The character went to the PC as a shortcut, not as text, so it must not stay in the
        // buffer: a later Backspace would delete something on the PC that was never typed.
        return KeyboardUpdate(actions, resetField: backspaces == 0 || _needsReset(newText, composing));
      }

      // Anything else (punctuation, an emoji, a pasted run) has no layout-independent key to
      // combine with. It is typed as it is and the modifiers are let go.
    }

    for (var i = 0; i < backspaces; i++) {
      actions.add(const TapKey(VirtualKeys.back));
    }

    if (added == '\n') {
      actions.add(const TapKey(VirtualKeys.enter));
    } else if (added.isNotEmpty) {
      actions.add(TypeText(added));
    }

    return KeyboardUpdate(actions, resetField: forceReset || _needsReset(newText, composing));
  }

  /// A key from the special-keys bar, combined with any armed modifiers.
  List<KeyAction> specialKey(int vk) {
    final modifiers = takeModifiers();
    return [modifiers.isEmpty ? TapKey(vk) : PressShortcut([...modifiers, vk])];
  }

  /// A one-tap shortcut. Armed modifiers join it (Shift armed + Alt+Tab switches backwards).
  List<KeyAction> shortcut(List<int> keys) {
    final combined = <int>[
      ...takeModifiers(),
    ];

    for (final key in keys) {
      if (!combined.contains(key)) combined.add(key);
    }

    if (combined.isEmpty || combined.length > InputArgs.maxShortcutKeys) return const [];
    return [combined.length == 1 ? TapKey(combined.single) : PressShortcut(combined)];
  }

  /// Whether the field has drifted far enough from "sentinel plus a little text" to be put back.
  /// Never while the keyboard is composing: resetting the field would discard its word.
  bool _needsReset(String text, bool composing) {
    if (composing) return false;

    final intact = _commonPrefix(text, sentinel);
    return intact < sentinel.length ~/ 2 || text.length - intact > maxBuffer;
  }

  /// Length of the shared start, never ending between the halves of a surrogate pair.
  static int _commonPrefix(String a, String b) {
    final limit = a.length < b.length ? a.length : b.length;
    var i = 0;
    while (i < limit && a.codeUnitAt(i) == b.codeUnitAt(i)) {
      i++;
    }

    if (i > 0 && _isHighSurrogate(a.codeUnitAt(i - 1))) i--;
    return i;
  }

  /// Length of the shared end, never starting between the halves of a surrogate pair.
  static int _commonSuffix(String a, String b) {
    final limit = a.length < b.length ? a.length : b.length;
    var i = 0;
    while (i < limit && a.codeUnitAt(a.length - 1 - i) == b.codeUnitAt(b.length - 1 - i)) {
      i++;
    }

    if (i > 0 && _isLowSurrogate(a.codeUnitAt(a.length - i))) i--;
    return i;
  }

  static bool _isHighSurrogate(int unit) => unit >= 0xD800 && unit <= 0xDBFF;

  static bool _isLowSurrogate(int unit) => unit >= 0xDC00 && unit <= 0xDFFF;
}
