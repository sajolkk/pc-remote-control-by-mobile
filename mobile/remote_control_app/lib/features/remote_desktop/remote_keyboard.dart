import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../data/input/input_sender.dart';
import '../../data/input/keyboard_translator.dart';
import '../../protocol/input_messages.dart';

/// The phone's keyboard, pointed at the PC.
///
/// Owns the hidden field the soft keyboard types into, the [KeyboardTranslator] that turns its
/// edits into keys, and the sticky modifiers the special-keys bar arms. Notifies when the armed
/// modifiers change, which is all the bar needs to redraw.
class RemoteKeyboardController extends ChangeNotifier {
  RemoteKeyboardController(this._sender);

  final InputSender _sender;
  final _translator = KeyboardTranslator();

  /// The hidden field's text. Starts — and is regularly put back to — the sentinel alone.
  final TextEditingController field = TextEditingController.fromValue(_empty);

  final FocusNode focus = FocusNode(debugLabel: 'remote keyboard');

  static const _empty = TextEditingValue(
    text: KeyboardTranslator.sentinel,
    selection: TextSelection.collapsed(offset: KeyboardTranslator.sentinel.length),
  );

  bool isArmed(int vk) => _translator.isArmed(vk);

  /// Arms or disarms a sticky modifier.
  void toggleModifier(int vk) {
    _translator.toggle(vk);
    notifyListeners();
  }

  /// Hands the armed modifiers to a mouse click, disarming them.
  List<int> takeModifiers() {
    final taken = _translator.takeModifiers();
    if (taken.isNotEmpty) notifyListeners();
    return taken;
  }

  /// A key from the bar.
  void pressKey(int vk) => _apply(_translator.specialKey(vk));

  /// A one-tap shortcut from the bar.
  void pressShortcut(List<int> keys) => _apply(_translator.shortcut(keys));

  /// The soft keyboard edited the hidden field.
  void onFieldChanged(String text) {
    final composing = field.value.composing;
    final update = _translator.textChanged(text, composing: composing.isValid && !composing.isCollapsed);

    _apply(update.actions);
    if (update.resetField) _resetField();
  }

  /// Raises the soft keyboard.
  void show() {
    focus.requestFocus();

    // Already focused but dismissed with the back button: focus alone would not bring it back.
    SystemChannels.textInput.invokeMethod<void>('TextInput.show');
  }

  /// Lowers the soft keyboard and forgets anything half-done.
  void hide() {
    focus.unfocus();
    _resetField();

    if (_translator.takeModifiers().isNotEmpty) notifyListeners();
  }

  void _resetField() {
    _translator.reset();
    field.value = _empty;
  }

  void _apply(List<KeyAction> actions) {
    for (final action in actions) {
      switch (action) {
        case TypeText(:final text):
          _sender.text(text);
        case TapKey(:final vk):
          _sender.tapKey(vk);
        case PressShortcut(:final keys):
          _sender.shortcut(keys);
      }
    }

    // Using a key disarms the modifiers, so the bar is told whenever anything was sent.
    if (actions.isNotEmpty) notifyListeners();
  }

  @override
  void dispose() {
    field.dispose();
    focus.dispose();
    super.dispose();
  }
}

/// The invisible text field the soft keyboard is attached to.
///
/// Configured to make the keyboard as literal as it can be made — no autocorrect, no suggestions,
/// no smart punctuation, no learning from what is typed into someone's PC — and multi-line, so
/// Enter arrives as a newline in the text like any other character instead of as a "done" action
/// that would close the keyboard. Keyboards that compose regardless are handled by the translator.
class RemoteKeyboardField extends StatelessWidget {
  const RemoteKeyboardField({super.key, required this.controller});

  final RemoteKeyboardController controller;

  @override
  Widget build(BuildContext context) {
    return IgnorePointer(
      child: ExcludeSemantics(
        child: Opacity(
          opacity: 0,
          child: SizedBox(
            width: 1,
            height: 1,
            child: EditableText(
              controller: controller.field,
              focusNode: controller.focus,
              onChanged: controller.onFieldChanged,
              style: const TextStyle(fontSize: 1, color: Colors.transparent),
              cursorColor: Colors.transparent,
              backgroundCursorColor: Colors.transparent,
              showCursor: false,
              maxLines: null,
              keyboardType: TextInputType.multiline,
              textInputAction: TextInputAction.newline,
              textCapitalization: TextCapitalization.none,
              autocorrect: false,
              enableSuggestions: false,
              enableIMEPersonalizedLearning: false,
              smartDashesType: SmartDashesType.disabled,
              smartQuotesType: SmartQuotesType.disabled,
              enableInteractiveSelection: false,
              rendererIgnoresPointer: true,
            ),
          ),
        ),
      ),
    );
  }
}

/// The strip of keys a phone keyboard does not have, shown above it.
///
/// One row, scrolled sideways: escape and tab, the sticky modifiers, navigation, the shortcuts
/// people actually use on someone else's desktop, then the function keys.
class SpecialKeysBar extends StatelessWidget {
  const SpecialKeysBar({
    super.key,
    required this.controller,
    required this.onClose,
    required this.onExplain,
  });

  final RemoteKeyboardController controller;

  /// Closes the keyboard and this bar.
  final VoidCallback onClose;

  /// Shows a sentence to the user (why a key is not offered).
  final ValueChanged<String> onExplain;

  static const _modifiers = <(String, int)>[
    ('Ctrl', VirtualKeys.control),
    ('Alt', VirtualKeys.alt),
    ('Shift', VirtualKeys.shift),
    ('Win', VirtualKeys.win),
  ];

  static const _navigation = <(String, int)>[
    ('←', VirtualKeys.left),
    ('↑', VirtualKeys.up),
    ('↓', VirtualKeys.down),
    ('→', VirtualKeys.right),
    ('Home', VirtualKeys.home),
    ('End', VirtualKeys.end),
    ('PgUp', VirtualKeys.pageUp),
    ('PgDn', VirtualKeys.pageDown),
    ('Del', VirtualKeys.delete),
  ];

  /// The one-tap shortcuts. Letters are their own virtual-key codes ('C' is 67).
  static const shortcuts = <(String, List<int>)>[
    ('Alt+Tab', [VirtualKeys.alt, VirtualKeys.tab]),
    ('Alt+F4', [VirtualKeys.alt, VirtualKeys.f1 + 3]),
    ('Win+D', [VirtualKeys.win, 0x44]),
    ('Ctrl+C', [VirtualKeys.control, 0x43]),
    ('Ctrl+V', [VirtualKeys.control, 0x56]),
    ('Ctrl+X', [VirtualKeys.control, 0x58]),
    ('Ctrl+Z', [VirtualKeys.control, 0x5A]),
    ('Ctrl+A', [VirtualKeys.control, 0x41]),
    ('Ctrl+S', [VirtualKeys.control, 0x53]),
    ('Start', [VirtualKeys.win]),
    ('Ctrl+Shift+Esc', [VirtualKeys.control, VirtualKeys.shift, VirtualKeys.escape]),
  ];

  static const ctrlAltDelExplanation =
      'Windows does not let any app send Ctrl+Alt+Del. Press it on the PC\'s own keyboard. '
      'For Task Manager, use Ctrl+Shift+Esc.';

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Material(
      color: scheme.surfaceContainerHighest,
      child: SizedBox(
        height: 44,
        child: ListenableBuilder(
          listenable: controller,
          builder: (context, _) => Row(
            children: [
              IconButton(
                tooltip: 'Show the keyboard',
                visualDensity: VisualDensity.compact,
                icon: const Icon(Icons.keyboard_outlined),
                onPressed: controller.show,
              ),
              Expanded(
                child: ListView(
                  scrollDirection: Axis.horizontal,
                  padding: const EdgeInsets.symmetric(horizontal: 2, vertical: 5),
                  children: [
                    _KeyCap(label: 'Esc', onTap: () => controller.pressKey(VirtualKeys.escape)),
                    _KeyCap(label: 'Tab', onTap: () => controller.pressKey(VirtualKeys.tab)),
                    const _Gap(),
                    for (final (label, vk) in _modifiers)
                      _KeyCap(
                        label: label,
                        selected: controller.isArmed(vk),
                        tooltip: '$label applies to the next key, character or click',
                        onTap: () => controller.toggleModifier(vk),
                      ),
                    const _Gap(),
                    for (final (label, vk) in _navigation)
                      _KeyCap(label: label, onTap: () => controller.pressKey(vk)),
                    const _Gap(),
                    for (final (label, keys) in shortcuts)
                      _KeyCap(label: label, onTap: () => controller.pressShortcut(keys)),
                    _KeyCap(
                      label: 'Ctrl+Alt+Del',
                      unavailable: true,
                      onTap: () => onExplain(ctrlAltDelExplanation),
                    ),
                    const _Gap(),
                    for (var number = 1; number <= 12; number++)
                      _KeyCap(
                        label: 'F$number',
                        onTap: () => controller.pressKey(VirtualKeys.function(number)),
                      ),
                  ],
                ),
              ),
              IconButton(
                tooltip: 'Close the keyboard',
                visualDensity: VisualDensity.compact,
                icon: const Icon(Icons.keyboard_hide_outlined),
                onPressed: onClose,
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _KeyCap extends StatelessWidget {
  const _KeyCap({
    required this.label,
    required this.onTap,
    this.selected = false,
    this.unavailable = false,
    this.tooltip,
  });

  final String label;
  final VoidCallback onTap;

  /// An armed modifier.
  final bool selected;

  /// Drawn as disabled, but still tappable so it can say why.
  final bool unavailable;

  final String? tooltip;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    final background = selected ? scheme.primary : scheme.surface;
    final foreground = selected
        ? scheme.onPrimary
        : (unavailable ? scheme.onSurface.withValues(alpha: 0.38) : scheme.onSurface);

    final cap = Padding(
      padding: const EdgeInsets.symmetric(horizontal: 2),
      child: Material(
        color: background,
        borderRadius: BorderRadius.circular(6),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          // Never takes focus: that would close the keyboard this bar sits on.
          canRequestFocus: false,
          onTap: () {
            HapticFeedback.selectionClick();
            onTap();
          },
          child: ConstrainedBox(
            constraints: const BoxConstraints(minWidth: 40),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 9),
              child: Center(
                child: Text(
                  label,
                  style: TextStyle(color: foreground, fontSize: 13, fontWeight: FontWeight.w500),
                ),
              ),
            ),
          ),
        ),
      ),
    );

    return tooltip == null ? cap : Tooltip(message: tooltip, child: cap);
  }
}

class _Gap extends StatelessWidget {
  const _Gap();

  @override
  Widget build(BuildContext context) => const SizedBox(width: 8);
}
