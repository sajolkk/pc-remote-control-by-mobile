import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/input/keyboard_translator.dart';
import 'package:remote_control_app/protocol/input_messages.dart';

void main() {
  const s = KeyboardTranslator.sentinel;
  const back = TapKey(VirtualKeys.back);

  /// Feeds a series of field contents (each given without the sentinel) and collects what the PC
  /// is told to do.
  List<KeyAction> type(KeyboardTranslator translator, List<String> buffers, {bool composing = false}) => [
        for (final buffer in buffers) ...translator.textChanged('$s$buffer', composing: composing).actions,
      ];

  group('text edits', () {
    test('inserted characters are typed', () {
      final translator = KeyboardTranslator();

      expect(type(translator, ['h', 'he', 'hey']), const [TypeText('h'), TypeText('e'), TypeText('y')]);
    });

    test('a deletion is a Backspace per character', () {
      final translator = KeyboardTranslator();
      type(translator, ['hello']);

      expect(type(translator, ['hell']), const [back]);
      expect(type(translator, ['he']), const [back, back]);
    });

    test('a replacement backspaces what changed and retypes it', () {
      // What a keyboard does when it "corrects" the word just typed.
      final translator = KeyboardTranslator();
      type(translator, ['teh']);

      expect(type(translator, ['the']), const [back, back, TypeText('he')]);
    });

    test('a change earlier in the buffer retypes everything after it', () {
      // The PC's caret is at the end of what was typed, so the tail has to go and come back.
      final translator = KeyboardTranslator();
      type(translator, ['teh cat']);

      expect(type(translator, ['the cat']), [...List.filled(6, back), const TypeText('he cat')]);
    });

    test('an unchanged field sends nothing', () {
      final translator = KeyboardTranslator();
      type(translator, ['a']);

      expect(type(translator, ['a']), isEmpty, reason: 'a selection-only update is not a keystroke');
    });

    test('Enter is the Return key', () {
      final translator = KeyboardTranslator();
      type(translator, ['ok']);

      expect(type(translator, ['ok\n']), const [TapKey(VirtualKeys.enter)]);
    });

    test('pasted text goes as one piece, line breaks included', () {
      final translator = KeyboardTranslator();

      expect(type(translator, ['one\ntwo']), const [TypeText('one\ntwo')]);
    });
  });

  group('composing keyboards', () {
    test('a word revised while composing is never typed twice', () {
      // A predictive keyboard re-sends its whole composing region on every key.
      final translator = KeyboardTranslator();
      final actions = type(translator, ['w', 'wo', 'wor', 'word', 'word '], composing: true);

      expect(actions, const [TypeText('w'), TypeText('o'), TypeText('r'), TypeText('d'), TypeText(' ')]);
    });

    test('an IME swapping its composition for the committed text replaces it on the PC', () {
      final translator = KeyboardTranslator();
      type(translator, ['n', 'ni'], composing: true);

      expect(type(translator, ['你']), const [back, back, TypeText('你')]);
    });

    test('the field is never reset in the middle of a composition', () {
      final translator = KeyboardTranslator();
      final long = 'x' * (KeyboardTranslator.maxBuffer + 10);

      expect(translator.textChanged('$s$long', composing: true).resetField, isFalse);
      expect(translator.textChanged('$s${long}y').resetField, isTrue, reason: 'committed: now it can be emptied');
    });
  });

  group('emoji', () {
    test('an emoji is typed whole and deleted with one Backspace', () {
      final translator = KeyboardTranslator();

      expect(type(translator, ['👍']), const [TypeText('👍')]);
      expect(type(translator, ['']), const [back], reason: 'two UTF-16 units, one character');
    });

    test('emoji that share their first half are not split down the middle', () {
      // 👍 and 👎 differ only in their second UTF-16 unit. Diffing by unit would backspace half a
      // character and type the other half.
      final translator = KeyboardTranslator();
      type(translator, ['👍']);

      expect(type(translator, ['👎']), const [back, TypeText('👎')]);
    });
  });

  group('the sentinel', () {
    test('Backspace on an empty buffer is still a Backspace', () {
      final translator = KeyboardTranslator();

      final update = translator.textChanged(s.substring(0, s.length - 1));

      expect(update.actions, const [back]);
      expect(update.resetField, isFalse, reason: 'plenty of filler left; no need to disturb the keyboard');
    });

    test('held Backspace keeps working, and the filler is topped up before it runs out', () {
      final translator = KeyboardTranslator();
      var resets = 0;
      var sent = 0;
      var field = s;

      for (var i = 0; i < 40; i++) {
        field = field.substring(0, field.length - 1);
        final update = translator.textChanged(field);
        sent += update.actions.where((action) => action == back).length;

        if (update.resetField) {
          resets++;
          translator.reset();
          field = s;
        }
      }

      expect(sent, 40);
      expect(resets, greaterThan(0));
    });

    test('is never typed on the PC, whatever the keyboard does to it', () {
      // The keyboard's own cursor control moved the caret to the start, then a letter was typed.
      final atStart = KeyboardTranslator();
      final inserted = atStart.textChanged('x$s');
      expect(inserted.actions, const [TypeText('x')]);
      expect(inserted.resetField, isTrue);

      // Everything was selected and replaced.
      final replaced = KeyboardTranslator();
      type(replaced, ['ab']);
      final update = replaced.textChanged('z');
      expect(update.actions, const [back, back, TypeText('z')], reason: 'only what was really typed is deleted');
      expect(update.resetField, isTrue);
    });

    test('a long buffer asks to be emptied', () {
      final translator = KeyboardTranslator();

      expect(translator.textChanged('${s}short').resetField, isFalse);
      expect(translator.textChanged('$s${'a' * (KeyboardTranslator.maxBuffer + 1)}').resetField, isTrue);
    });
  });

  group('sticky modifiers', () {
    test('Ctrl then "c" is the shortcut Ctrl+C, not the letter', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.control);

      final update = translator.textChanged('${s}c');

      expect(update.actions, const [
        PressShortcut([VirtualKeys.control, 0x43])
      ]);
      expect(update.resetField, isTrue, reason: 'the "c" was never typed, so it must not stay in the buffer');
      expect(translator.armed, isEmpty, reason: 'one use, then released');
    });

    test('the next character after that is plain text again', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.control);
      translator.textChanged('${s}c');
      translator.reset();

      expect(type(translator, ['c']), const [TypeText('c')]);
    });

    test('several modifiers combine, in a fixed order', () {
      final translator = KeyboardTranslator()
        ..toggle(VirtualKeys.shift)
        ..toggle(VirtualKeys.win)
        ..toggle(VirtualKeys.control);

      expect(translator.armed, const [VirtualKeys.control, VirtualKeys.shift, VirtualKeys.win]);
      expect(type(translator, ['S']), const [
        PressShortcut([VirtualKeys.control, VirtualKeys.shift, VirtualKeys.win, 0x53])
      ]);
    });

    test('a second tap disarms', () {
      final translator = KeyboardTranslator()
        ..toggle(VirtualKeys.alt)
        ..toggle(VirtualKeys.alt);

      expect(translator.armed, isEmpty);
      expect(type(translator, ['a']), const [TypeText('a')]);
    });

    test('apply to special keys, Enter and Backspace', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.alt);
      expect(translator.specialKey(VirtualKeys.function(4)), const [
        PressShortcut([VirtualKeys.alt, 115])
      ]);
      expect(translator.specialKey(VirtualKeys.function(4)), const [TapKey(115)], reason: 'released after one use');

      translator.toggle(VirtualKeys.control);
      expect(type(translator, ['\n']), const [
        PressShortcut([VirtualKeys.control, VirtualKeys.enter])
      ]);

      translator
        ..reset()
        ..toggle(VirtualKeys.control);
      expect(translator.textChanged(s.substring(0, s.length - 1)).actions, const [
        PressShortcut([VirtualKeys.control, VirtualKeys.back])
      ]);
    });

    test('a character with no key of its own is typed plainly and the modifier is released', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.control);

      expect(type(translator, ['👍']), const [TypeText('👍')]);
      expect(translator.armed, isEmpty);
    });

    test('join a one-tap shortcut, without doubling a key it already has', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.shift);
      expect(translator.shortcut(const [VirtualKeys.alt, VirtualKeys.tab]), const [
        PressShortcut([VirtualKeys.shift, VirtualKeys.alt, VirtualKeys.tab])
      ]);

      translator.toggle(VirtualKeys.control);
      expect(translator.shortcut(const [VirtualKeys.control, 0x43]), const [
        PressShortcut([VirtualKeys.control, 0x43])
      ]);
    });

    test('a single key "shortcut" (Start) is a key tap', () {
      expect(KeyboardTranslator().shortcut(const [VirtualKeys.win]), const [TapKey(VirtualKeys.win)]);
    });

    test('takeModifiers hands them to a mouse click and disarms', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.control);

      expect(translator.takeModifiers(), const [VirtualKeys.control]);
      expect(translator.takeModifiers(), isEmpty);
    });

    test('only modifiers can be armed', () {
      final translator = KeyboardTranslator()..toggle(VirtualKeys.escape);

      expect(translator.armed, isEmpty);
    });
  });
}
