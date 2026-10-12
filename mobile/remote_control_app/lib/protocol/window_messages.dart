/// Messages for the open-window commands (`window.list`, `window.action`).
///
/// Field names match the wire contract exactly. The window `id` is opaque: this app never
/// builds or interprets one, it only hands back an id the PC itself produced, and the PC
/// checks that the id still names a live window before acting on it.
library;

/// One top-level window as the PC reported it.
class WindowInfo {
  const WindowInfo({
    required this.id,
    required this.title,
    required this.processName,
    this.isMinimized = false,
    this.isMaximized = false,
    this.isForeground = false,
  });

  factory WindowInfo.fromJson(Map<String, dynamic> json) => WindowInfo(
        id: json['id'] as String? ?? '',
        title: json['title'] as String? ?? '',
        processName: json['processName'] as String? ?? '',
        isMinimized: json['isMinimized'] as bool? ?? false,
        isMaximized: json['isMaximized'] as bool? ?? false,
        isForeground: json['isForeground'] as bool? ?? false,
      );

  /// Parses the result of `window.list`, keeping the PC's order (foreground first).
  ///
  /// Entries without an id are dropped: nothing could be done with them anyway.
  static List<WindowInfo> listFromJson(Map<String, dynamic> json) {
    final windows = json['windows'];

    return (windows is List ? windows : const <dynamic>[])
        .whereType<Map<String, dynamic>>()
        .map(WindowInfo.fromJson)
        .where((window) => window.id.isNotEmpty)
        .toList(growable: false);
  }

  final String id;
  final String title;
  final String processName;
  final bool isMinimized;
  final bool isMaximized;
  final bool isForeground;

  /// What to call the window in the UI. Some windows have no title, and a blank row is useless.
  String get displayTitle {
    if (title.trim().isNotEmpty) return title;
    if (processName.trim().isNotEmpty) return processName;
    return 'Untitled window';
  }
}

/// What can be done to a window. [wireName] is the value of the `action` argument.
enum WindowAction {
  focus('focus'),
  minimize('minimize'),
  maximize('maximize'),
  restore('restore'),

  /// A polite close request. The PC never kills the process, so an app with unsaved work gets
  /// to put its own prompt on screen.
  close('close');

  const WindowAction(this.wireName);

  final String wireName;

  /// The `args` object for `window.action`.
  Map<String, dynamic> argsFor(String windowId) => {
        'windowId': windowId,
        'action': wireName,
      };
}

/// The PC's answer to `window.action`.
class WindowActionResult {
  const WindowActionResult({required this.succeeded, this.detail});

  factory WindowActionResult.fromJson(Map<String, dynamic> json) => WindowActionResult(
        // A PC that answered without the flag did not report a failure, so it counts as done.
        succeeded: json['succeeded'] as bool? ?? true,
        detail: json['detail'] as String?,
      );

  final bool succeeded;

  /// The PC's own explanation, e.g. why Windows refused a focus change.
  final String? detail;
}
