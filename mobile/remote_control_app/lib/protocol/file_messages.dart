/// Messages for the file commands (`file.list`, `file.open`, `file.create`, `file.rename`,
/// `file.copy`, `file.move`, `file.delete`).
///
/// Field names match the wire contract exactly. Paths are absolute Windows paths and are
/// treated as opaque wherever possible: this app sends back paths the PC gave it, and the PC
/// canonicalises and checks every one against its allowed folders regardless. The only path
/// arithmetic done here is for display (the breadcrumb) and for refusing an obviously
/// pointless move before asking.
library;

/// One file, folder or root as the PC reported it.
class FileEntry {
  const FileEntry({
    required this.name,
    required this.path,
    required this.isDirectory,
    this.size,
    this.modified,
    this.isRoot = false,
    this.readOnly = false,
  });

  factory FileEntry.fromJson(Map<String, dynamic> json) => FileEntry(
        name: json['name'] as String? ?? '',
        path: json['path'] as String? ?? '',
        isDirectory: json['isDirectory'] as bool? ?? false,

        // A JSON number, which may decode as an int or a double depending on its size.
        size: (json['size'] as num?)?.toInt(),
        modified:
            json['modified'] is String ? DateTime.tryParse(json['modified'] as String) : null,
        isRoot: json['isRoot'] as bool? ?? false,
        readOnly: json['readOnly'] as bool? ?? false,
      );

  /// Parses the `{ entry: FileEntry }` result of create, rename, copy and move.
  static FileEntry? fromResult(Map<String, dynamic> json) {
    final entry = json['entry'];
    return entry is Map<String, dynamic> ? FileEntry.fromJson(entry) : null;
  }

  /// The file name, or a friendly label ("Documents", "Local Disk (C:)") for a root.
  final String name;

  /// The absolute Windows path. This, never [name], is what goes back to the PC.
  final String path;

  final bool isDirectory;

  /// Bytes. Files only.
  final int? size;

  /// Last modified, in UTC as sent.
  final DateTime? modified;

  /// Whether this is one of the PC's allowed top-level folders or drives.
  final bool isRoot;

  final bool readOnly;
}

/// The result of `file.list`.
class FileListResult {
  const FileListResult({
    required this.path,
    required this.entries,
    this.parent,
    this.truncated = false,
  });

  factory FileListResult.fromJson(Map<String, dynamic> json) {
    final parent = json['parent'] as String?;
    final entries = json['entries'];

    return FileListResult(
      path: json['path'] as String? ?? '',

      // Absent at a root and in the roots view. An empty string means the same thing.
      parent: parent == null || parent.isEmpty ? null : parent,
      entries: (entries is List ? entries : const <dynamic>[])
          .whereType<Map<String, dynamic>>()
          .map(FileEntry.fromJson)
          .where((entry) => entry.path.isNotEmpty)
          .toList(growable: false),
      truncated: json['truncated'] as bool? ?? false,
    );
  }

  /// The folder listed, or "" for the roots view.
  final String path;

  /// The folder above, or null when there is none the PC will show.
  final String? parent;

  /// Folders first, then files, each alphabetical. The PC sorts; the order is kept as sent.
  final List<FileEntry> entries;

  /// Whether the PC stopped at its limit and the folder holds more than is listed.
  final bool truncated;

  bool get isRootsView => path.isEmpty;
}

/// Builds the `args` object of each file command, so the field names live in one place.
class FileArgs {
  const FileArgs._();

  /// `file.list`. An empty [path] asks for the roots and is sent as an empty object.
  static Map<String, dynamic> list(String path) => {if (path.isNotEmpty) 'path': path};

  static Map<String, dynamic> open(String path) => {'path': path};

  static Map<String, dynamic> create({
    required String parent,
    required String name,
    required bool isDirectory,
  }) =>
      {'parent': parent, 'name': name, 'isDirectory': isDirectory};

  static Map<String, dynamic> rename({required String path, required String newName}) =>
      {'path': path, 'newName': newName};

  /// [destination] is the folder to copy into, not the new file's own path.
  static Map<String, dynamic> copy({required String path, required String destination}) =>
      {'path': path, 'destination': destination};

  /// [destination] is the folder to move into, not the new file's own path.
  static Map<String, dynamic> move({required String path, required String destination}) =>
      {'path': path, 'destination': destination};

  /// Sends to the Recycle Bin unless [permanent]. The flag is only put on the wire when set,
  /// so the default can never be turned into a permanent delete by a slip here.
  static Map<String, dynamic> delete({required String path, bool permanent = false}) =>
      {'path': path, if (permanent) 'permanent': true};
}

/// The rules a new file or folder name must satisfy.
///
/// The PC enforces the same rules and is the authority; checking here only saves a round trip
/// and lets the dialog say what is wrong while the user is still typing.
class FileNames {
  const FileNames._();

  /// Windows' limit for a single path component.
  static const int maxLength = 255;

  static const _reserved = {
    'CON', 'PRN', 'AUX', 'NUL', //
    'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9', //
    'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9',
  };

  /// Characters Windows refuses in a name. The first three are what make a name more than one
  /// path component; the rest can never be created on a Windows disk either.
  static const _forbidden = r'\/:*?"<>|';

  /// Returns what is wrong with [name], or null when it is acceptable.
  static String? validate(String name) {
    if (name.isEmpty) return 'Enter a name.';
    if (name.length > maxLength) return 'That name is too long.';

    for (final unit in name.codeUnits) {
      if (unit < 0x20 || unit == 0x7F) return 'A name cannot contain control characters.';

      if (_forbidden.codeUnits.contains(unit)) {
        return 'A name cannot contain ${String.fromCharCode(unit)}';
      }
    }

    if (name.endsWith('.') || name.endsWith(' ')) {
      return 'A name cannot end with a dot or a space.';
    }

    // Windows reserves these device names with or without an extension: "NUL.txt" is as
    // unusable as "NUL".
    final stem = name.split('.').first.trimRight().toUpperCase();
    if (_reserved.contains(stem)) return '"$stem" is a name Windows reserves.';

    return null;
  }
}

/// One step of the breadcrumb: what to show, and the folder it leads to ("" for the roots).
class FileCrumb {
  const FileCrumb({required this.label, required this.path});

  final String label;
  final String path;
}

/// Path logic for the file browser. Display and navigation only: nothing here decides what the
/// phone may reach — the PC does that for every command.
class FilePaths {
  const FilePaths._();

  /// The label of the first crumb, which leads back to the list of roots.
  static const String rootsLabel = 'PC';

  /// A form two spellings of the same Windows path share: backslashes, no trailing separator,
  /// one case. For comparison only, never sent.
  static String _key(String path) => _trim(path).toLowerCase();

  static String _trim(String path) {
    var result = path.replaceAll('/', r'\');
    while (result.endsWith(r'\')) {
      result = result.substring(0, result.length - 1);
    }
    return result;
  }

  /// Whether two paths name the same folder, ignoring case and a trailing separator.
  static bool same(String a, String b) => _key(a) == _key(b);

  /// Whether [path] is [ancestor] itself or somewhere beneath it.
  static bool isSameOrInside(String path, String ancestor) {
    final child = _key(path);
    final parent = _key(ancestor);

    if (parent.isEmpty) return false;
    return child == parent || child.startsWith('$parent\\');
  }

  /// The trail from the roots view down to [path].
  ///
  /// The root is whichever of [roots] contains the path most specifically, so a folder under
  /// both "Documents" and "Local Disk (C:)" is shown under Documents. A path under no known
  /// root — the PC canonicalised it somewhere unexpected — still gets a usable trail of the
  /// roots view and the folder itself.
  static List<FileCrumb> breadcrumbs({required List<FileEntry> roots, required String path}) {
    final crumbs = <FileCrumb>[const FileCrumb(label: rootsLabel, path: '')];
    if (path.isEmpty) return crumbs;

    FileEntry? root;
    for (final candidate in roots) {
      if (!isSameOrInside(path, candidate.path)) continue;
      if (root == null || _key(candidate.path).length > _key(root.path).length) {
        root = candidate;
      }
    }

    if (root == null) {
      crumbs.add(FileCrumb(label: nameOf(path), path: path));
      return crumbs;
    }

    crumbs.add(FileCrumb(label: root.name.isEmpty ? nameOf(root.path) : root.name, path: root.path));

    var base = _trim(root.path);
    final rest = _trim(path).substring(base.length);

    for (final segment in rest.split(r'\')) {
      if (segment.isEmpty) continue;
      base = '$base\\$segment';
      crumbs.add(FileCrumb(label: segment, path: base));
    }

    return crumbs;
  }

  /// Where "up" leads from the folder at the end of [crumbs]: "" means the roots view.
  ///
  /// The PC's own `parent` wins when the folder was listed, because the PC knows where its
  /// allowed folders end. The trail is the fallback for a folder that could not be listed —
  /// which is exactly when the user most needs a way back out.
  static String upTarget({required List<FileCrumb> crumbs, FileListResult? listing}) {
    final current = crumbs.isEmpty ? '' : crumbs.last.path;
    if (current.isEmpty) return '';

    if (listing != null && same(listing.path, current)) {
      return listing.parent ?? '';
    }

    return crumbs.length >= 2 ? crumbs[crumbs.length - 2].path : '';
  }

  /// The last component of a path, for display. A bare drive ("C:\") is returned as it is.
  static String nameOf(String path) {
    final trimmed = _trim(path);
    final cut = trimmed.lastIndexOf(r'\');

    if (cut < 0 || cut == trimmed.length - 1) return trimmed.isEmpty ? path : trimmed;
    return trimmed.substring(cut + 1);
  }
}

/// Formats a byte count the way a file manager does: "512 B", "1.50 KB", "23.4 MB", "420 GB".
String formatFileSize(int bytes) {
  if (bytes < 1024) return '$bytes B';

  const units = ['KB', 'MB', 'GB', 'TB'];
  var value = bytes / 1024;
  var unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  // Three significant figures: enough to tell files apart, short enough to scan.
  final digits = value >= 100 ? 0 : (value >= 10 ? 1 : 2);
  return '${value.toStringAsFixed(digits)} ${units[unit]}';
}

/// Formats a modified time in the phone's own time zone, as "2026-03-09 14:05".
String formatFileTime(DateTime time) {
  final local = time.toLocal();
  String two(int value) => value.toString().padLeft(2, '0');

  return '${local.year}-${two(local.month)}-${two(local.day)} '
      '${two(local.hour)}:${two(local.minute)}';
}
