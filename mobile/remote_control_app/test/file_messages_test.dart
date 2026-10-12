import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/protocol/file_messages.dart';
import 'package:remote_control_app/protocol/protocol.dart';

void main() {
  group('file commands', () {
    test('use the names the PC registers', () {
      expect(Commands.fileList, 'file.list');
      expect(Commands.fileOpen, 'file.open');
      expect(Commands.fileCreate, 'file.create');
      expect(Commands.fileRename, 'file.rename');
      expect(Commands.fileCopy, 'file.copy');
      expect(Commands.fileMove, 'file.move');
      expect(Commands.fileDelete, 'file.delete');
      expect(Capabilities.files, 'files');
      expect(Permissions.manageFiles, 'ManageFiles');
    });

    test('the permission is labelled for what it now does', () {
      expect(Permissions.label(Permissions.manageFiles), 'Manage files');
    });
  });

  group('FileArgs', () {
    // Each of these is compared as encoded JSON so the field names, and the absence of any
    // extra field, are pinned exactly as the contract writes them.
    test('file.list asks for the roots with an empty object', () {
      expect(jsonEncode(FileArgs.list('')), '{}');
    });

    test('file.list names a folder with "path"', () {
      expect(
        jsonEncode(FileArgs.list(r'C:\Users\me\Documents')),
        r'{"path":"C:\\Users\\me\\Documents"}',
      );
    });

    test('file.open', () {
      expect(jsonEncode(FileArgs.open(r'C:\a.txt')), r'{"path":"C:\\a.txt"}');
    });

    test('file.create', () {
      expect(
        jsonEncode(FileArgs.create(parent: r'C:\Docs', name: 'Notes', isDirectory: true)),
        r'{"parent":"C:\\Docs","name":"Notes","isDirectory":true}',
      );
      expect(
        jsonEncode(FileArgs.create(parent: r'C:\Docs', name: 'a.txt', isDirectory: false)),
        r'{"parent":"C:\\Docs","name":"a.txt","isDirectory":false}',
      );
    });

    test('file.rename', () {
      expect(
        jsonEncode(FileArgs.rename(path: r'C:\Docs\a.txt', newName: 'b.txt')),
        r'{"path":"C:\\Docs\\a.txt","newName":"b.txt"}',
      );
    });

    test('file.copy and file.move send the destination folder', () {
      expect(
        jsonEncode(FileArgs.copy(path: r'C:\Docs\a.txt', destination: r'D:\Backup')),
        r'{"path":"C:\\Docs\\a.txt","destination":"D:\\Backup"}',
      );
      expect(
        jsonEncode(FileArgs.move(path: r'C:\Docs\a.txt', destination: r'D:\Backup')),
        r'{"path":"C:\\Docs\\a.txt","destination":"D:\\Backup"}',
      );
    });

    test('file.delete goes to the Recycle Bin unless told otherwise', () {
      // "permanent" is left off entirely by default, so the PC's own default — the Recycle
      // Bin — is what applies.
      expect(jsonEncode(FileArgs.delete(path: r'C:\Docs\a.txt')), r'{"path":"C:\\Docs\\a.txt"}');
      expect(
        jsonEncode(FileArgs.delete(path: r'C:\Docs\a.txt', permanent: true)),
        r'{"path":"C:\\Docs\\a.txt","permanent":true}',
      );
    });
  });

  group('FileListResult', () {
    test('parses a folder listing exactly as the PC serializes it', () {
      final json = jsonDecode(r'''
        {"path":"C:\\Users\\me\\Documents","parent":"C:\\Users\\me",
         "entries":[
           {"name":"Projects","path":"C:\\Users\\me\\Documents\\Projects","isDirectory":true,
            "modified":"2026-03-09T14:05:00Z"},
           {"name":"notes.txt","path":"C:\\Users\\me\\Documents\\notes.txt","isDirectory":false,
            "size":5368709120,"modified":"2026-03-10T08:30:15.123Z","readOnly":true}
         ],
         "truncated":true}
      ''') as Map<String, dynamic>;

      final listing = FileListResult.fromJson(json);

      expect(listing.path, r'C:\Users\me\Documents');
      expect(listing.parent, r'C:\Users\me');
      expect(listing.truncated, isTrue);
      expect(listing.isRootsView, isFalse);
      expect(listing.entries, hasLength(2));

      final folder = listing.entries[0];
      expect(folder.name, 'Projects');
      expect(folder.path, r'C:\Users\me\Documents\Projects');
      expect(folder.isDirectory, isTrue);
      expect(folder.size, isNull);
      expect(folder.modified, DateTime.utc(2026, 3, 9, 14, 5));
      expect(folder.isRoot, isFalse);
      expect(folder.readOnly, isFalse);

      final file = listing.entries[1];
      expect(file.isDirectory, isFalse);

      // Larger than 32 bits: a "long" on the PC must not be squeezed through an int32.
      expect(file.size, 5368709120);
      expect(file.modified!.isUtc, isTrue);
      expect(file.readOnly, isTrue);
    });

    test('parses the roots view', () {
      final json = jsonDecode(r'''
        {"path":"","entries":[
           {"name":"Desktop","path":"C:\\Users\\me\\Desktop","isDirectory":true,"isRoot":true},
           {"name":"Local Disk (C:)","path":"C:\\","isDirectory":true,"isRoot":true}
         ],"truncated":false}
      ''') as Map<String, dynamic>;

      final listing = FileListResult.fromJson(json);

      expect(listing.isRootsView, isTrue);
      expect(listing.parent, isNull);
      expect(listing.truncated, isFalse);
      expect(listing.entries.every((entry) => entry.isRoot && entry.isDirectory), isTrue);
      expect(listing.entries[1].name, 'Local Disk (C:)');
      expect(listing.entries[1].path, r'C:\');
    });

    test('keeps the order the PC sent', () {
      // The PC sorts folders first, then files. Re-sorting here could only disagree with it.
      final listing = FileListResult.fromJson({
        'path': r'C:\x',
        'entries': [
          {'name': 'zeta', 'path': r'C:\x\zeta', 'isDirectory': true},
          {'name': 'alpha.txt', 'path': r'C:\x\alpha.txt', 'isDirectory': false},
        ],
      });

      expect(listing.entries.map((entry) => entry.name), ['zeta', 'alpha.txt']);
    });

    test('an empty folder and a malformed result both parse to no entries', () {
      final empty = FileListResult.fromJson({'path': r'C:\x', 'entries': <dynamic>[]});
      expect(empty.entries, isEmpty);
      expect(empty.truncated, isFalse);

      final malformed = FileListResult.fromJson(const {});
      expect(malformed.entries, isEmpty);
      expect(FileListResult.fromJson(const {'path': 'C:', 'entries': 'nope'}).entries, isEmpty);
      expect(malformed.path, isEmpty);
      expect(malformed.parent, isNull);
    });

    test('an empty parent means there is none', () {
      final listing = FileListResult.fromJson({'path': r'C:\', 'parent': '', 'entries': <dynamic>[]});
      expect(listing.parent, isNull);
    });

    test('a bad date does not lose the entry', () {
      final entry = FileEntry.fromJson({
        'name': 'a.txt',
        'path': r'C:\a.txt',
        'isDirectory': false,
        'size': 12,
        'modified': 'yesterday',
      });

      expect(entry.modified, isNull);
      expect(entry.size, 12);
    });
  });

  group('FileEntry.fromResult', () {
    test('reads the entry returned by create, rename, copy and move', () {
      final data = jsonDecode(r'''
        {"entry":{"name":"a (2).txt","path":"D:\\Backup\\a (2).txt","isDirectory":false,"size":3}}
      ''') as Map<String, dynamic>;

      final entry = FileEntry.fromResult(data);

      expect(entry, isNotNull);
      expect(entry!.name, 'a (2).txt');
      expect(entry.path, r'D:\Backup\a (2).txt');
    });

    test('is null when the PC sent no entry', () {
      expect(FileEntry.fromResult(const {}), isNull);
    });
  });

  group('FileNames.validate', () {
    test('accepts ordinary names', () {
      for (final name in <String>[
        'Notes',
        'notes.txt',
        'New folder (2)',
        '.gitignore',
        'archive.tar.gz',
        'Console',
        'COM10',
        'nulls.txt',
        'résumé – final.docx',
        '日本語',
      ]) {
        expect(FileNames.validate(name), isNull, reason: 'Should accept: $name');
      }
    });

    test('rejects an empty name', () {
      expect(FileNames.validate(''), isNotNull);
    });

    test('rejects anything that is more than one path component', () {
      for (final name in <String>[r'a\b', 'a/b', 'C:', 'a:b', r'..\secret', '../secret']) {
        expect(FileNames.validate(name), isNotNull, reason: 'Should reject: $name');
      }
    });

    test('rejects control characters', () {
      for (final name in <String>['a\nb', 'a\tb', 'a\u0000b', 'a\u001Fb', 'a\u007Fb']) {
        expect(FileNames.validate(name), isNotNull);
      }
    });

    test('rejects a trailing dot or space, which covers "." and ".."', () {
      for (final name in <String>['name.', 'name ', '.', '..', ' ']) {
        expect(FileNames.validate(name), isNotNull, reason: 'Should reject: "$name"');
      }
    });

    test('rejects reserved device names in any case, with or without an extension', () {
      for (final name in <String>['CON', 'con', 'PRN', 'AUX', 'NUL', 'nul.txt', 'COM1', 'com9.log', 'LPT1', 'Lpt9']) {
        expect(FileNames.validate(name), isNotNull, reason: 'Should reject: $name');
      }
    });

    test('rejects the other characters Windows cannot store', () {
      for (final name in <String>['a*b', 'a?b', 'a"b', 'a<b', 'a>b', 'a|b']) {
        expect(FileNames.validate(name), isNotNull, reason: 'Should reject: $name');
      }
    });

    test('rejects a name longer than a path component can be', () {
      expect(FileNames.validate('a' * 255), isNull);
      expect(FileNames.validate('a' * 256), isNotNull);
    });
  });

  group('FilePaths', () {
    const roots = [
      FileEntry(name: 'Documents', path: r'C:\Users\me\Documents', isDirectory: true, isRoot: true),
      FileEntry(name: 'Local Disk (C:)', path: r'C:\', isDirectory: true, isRoot: true),
      FileEntry(name: 'Data (D:)', path: r'D:\', isDirectory: true, isRoot: true),
    ];

    List<String> labels(String path) =>
        FilePaths.breadcrumbs(roots: roots, path: path).map((crumb) => crumb.label).toList();

    List<String> paths(String path) =>
        FilePaths.breadcrumbs(roots: roots, path: path).map((crumb) => crumb.path).toList();

    test('the roots view is a single crumb', () {
      expect(labels(''), ['PC']);
      expect(paths(''), ['']);
    });

    test('a root shows its friendly label', () {
      expect(labels(r'C:\Users\me\Documents'), ['PC', 'Documents']);
      expect(labels(r'D:\'), ['PC', 'Data (D:)']);
    });

    test('every crumb below the root leads to its own folder', () {
      expect(labels(r'C:\Users\me\Documents\Projects\2026'), ['PC', 'Documents', 'Projects', '2026']);
      expect(paths(r'C:\Users\me\Documents\Projects\2026'), [
        '',
        r'C:\Users\me\Documents',
        r'C:\Users\me\Documents\Projects',
        r'C:\Users\me\Documents\Projects\2026',
      ]);
    });

    test('under a drive root the crumb paths keep one separator', () {
      expect(paths(r'D:\Media\Films'), ['', r'D:\', r'D:\Media', r'D:\Media\Films']);
    });

    test('the most specific root wins when roots nest', () {
      // Documents is inside C:, and both are roots. Showing it under Documents matches where
      // the user came in.
      expect(labels(r'C:\Users\me\Documents\a'), ['PC', 'Documents', 'a']);
      expect(labels(r'C:\Users\me\Pictures'), ['PC', 'Local Disk (C:)', 'Users', 'me', 'Pictures']);
    });

    test('matching ignores case and a trailing separator, as Windows does', () {
      expect(labels(r'c:\users\ME\documents\Sub\'), ['PC', 'Documents', 'Sub']);
    });

    test('a folder that only shares a name prefix with a root is not inside it', () {
      // "Documents2" starts with "Documents" as text, but is a different folder.
      expect(labels(r'C:\Users\me\Documents2'), ['PC', 'Local Disk (C:)', 'Users', 'me', 'Documents2']);
    });

    test('a path under no known root still gets a way back', () {
      expect(labels(r'\\nas\share\films'), ['PC', 'films']);
      expect(paths(r'\\nas\share\films'), ['', r'\\nas\share\films']);
    });

    test('up follows the parent the PC reported', () {
      final crumbs = FilePaths.breadcrumbs(roots: roots, path: r'C:\Users\me\Documents\a');
      const listing = FileListResult(
        path: r'C:\Users\me\Documents\a',
        parent: r'C:\Users\me\Documents',
        entries: [],
      );

      expect(FilePaths.upTarget(crumbs: crumbs, listing: listing), r'C:\Users\me\Documents');
    });

    test('up from a root, where the PC reports no parent, is the roots view', () {
      final crumbs = FilePaths.breadcrumbs(roots: roots, path: r'C:\Users\me\Documents');
      const listing = FileListResult(path: r'C:\Users\me\Documents', entries: []);

      expect(FilePaths.upTarget(crumbs: crumbs, listing: listing), '');
    });

    test('up from a folder that could not be listed falls back to the trail', () {
      // E_ACCESS_DENIED or E_NOT_FOUND leaves no listing, and that is exactly when a way out
      // is needed.
      final crumbs = FilePaths.breadcrumbs(roots: roots, path: r'C:\Users\me\Documents\gone');

      expect(FilePaths.upTarget(crumbs: crumbs), r'C:\Users\me\Documents');
    });

    test('up ignores a listing that belongs to some other folder', () {
      final crumbs = FilePaths.breadcrumbs(roots: roots, path: r'D:\Media\Films');
      const stale = FileListResult(path: r'C:\Elsewhere', parent: r'C:\', entries: []);

      expect(FilePaths.upTarget(crumbs: crumbs, listing: stale), r'D:\Media');
    });

    test('up from the roots view stays there', () {
      expect(FilePaths.upTarget(crumbs: FilePaths.breadcrumbs(roots: roots, path: '')), '');
    });

    test('isSameOrInside is about folders, not text', () {
      expect(FilePaths.isSameOrInside(r'C:\a\b', r'C:\a'), isTrue);
      expect(FilePaths.isSameOrInside(r'C:\a', r'C:\a'), isTrue);
      expect(FilePaths.isSameOrInside(r'c:\A\', r'C:\a'), isTrue);
      expect(FilePaths.isSameOrInside(r'C:\ab', r'C:\a'), isFalse);
      expect(FilePaths.isSameOrInside(r'C:\a', r'C:\a\b'), isFalse);
      expect(FilePaths.isSameOrInside(r'C:\a', ''), isFalse);
    });

    test('same ignores case and a trailing separator', () {
      expect(FilePaths.same(r'C:\Docs', r'c:\docs\'), isTrue);
      expect(FilePaths.same(r'C:\Docs', r'C:\Docs2'), isFalse);
    });

    test('nameOf takes the last component', () {
      expect(FilePaths.nameOf(r'C:\Users\me\notes.txt'), 'notes.txt');
      expect(FilePaths.nameOf(r'C:\Users\me\'), 'me');
      expect(FilePaths.nameOf(r'C:\'), 'C:');
    });
  });

  group('formatting', () {
    test('sizes read like a file manager', () {
      expect(formatFileSize(0), '0 B');
      expect(formatFileSize(1023), '1023 B');
      expect(formatFileSize(1024), '1.00 KB');
      expect(formatFileSize(1536), '1.50 KB');
      expect(formatFileSize(24 * 1024 * 1024), '24.0 MB');
      expect(formatFileSize(5368709120), '5.00 GB');
      expect(formatFileSize(300 * 1024 * 1024 * 1024), '300 GB');
    });

    test('times are shown in the phone\'s own zone', () {
      final time = DateTime.utc(2026, 3, 9, 14, 5);
      final local = time.toLocal();

      String two(int value) => value.toString().padLeft(2, '0');

      expect(
        formatFileTime(time),
        '${local.year}-${two(local.month)}-${two(local.day)} ${two(local.hour)}:${two(local.minute)}',
      );
    });
  });
}
