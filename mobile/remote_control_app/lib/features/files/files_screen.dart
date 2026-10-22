import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../protocol/file_messages.dart';
import '../../protocol/protocol.dart';
import '../system/system_screen.dart';

/// A copy or move the user has started and not yet finished by choosing where it goes.
class _Transfer {
  const _Transfer({required this.entry, required this.move, required this.fromFolder});

  final FileEntry entry;
  final bool move;

  /// The folder the entry was in when it was picked up.
  final String fromFolder;
}

enum _EntryAction { open, rename, copy, move, delete }

/// A browser for the files on the PC.
///
/// Everything shown comes from the PC, and every path sent back is one the PC produced: the
/// only text the user contributes is a single file name, which is checked here and again on
/// the PC. What this device may reach is decided on the PC — it lists its own allowed folders
/// as the roots and refuses anything outside them — so this screen has no notion of an
/// arbitrary path, and no field to type one into.
///
/// Files stay on the PC. There is no upload or download here because the PC does not offer
/// them; "Open" opens the file on the PC's own screen.
class FilesScreen extends ConsumerStatefulWidget {
  const FilesScreen({super.key, this.active = true});

  /// Whether this screen is the one on show. It can be kept alive behind a sibling so the
  /// folder being browsed is not lost, and must not claim the back button while hidden.
  final bool active;

  @override
  ConsumerState<FilesScreen> createState() => _FilesScreenState();
}

class _FilesScreenState extends ConsumerState<FilesScreen> {
  /// The folder on show. Empty for the roots view.
  String _path = '';

  /// The listing of [_path], or null while it has not loaded or could not be.
  FileListResult? _listing;

  /// The roots as last listed, kept for labelling the breadcrumb.
  List<FileEntry> _roots = const [];

  _Transfer? _transfer;
  String? _error;
  bool _loading = false;
  bool _busy = false;
  bool _requested = false;

  /// Counts listings asked for, so an answer that arrives after the user has already moved on
  /// to another folder is dropped instead of replacing what they are looking at.
  int _request = 0;

  bool get _atRoots => _path.isEmpty;

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(connectionStatusProvider);

    // Loaded the first time the feature is actually usable, and again after it has been away.
    final available = status.isConnected &&
        status.granted(Permissions.manageFiles) &&
        status.has(Capabilities.files);

    if (!available) {
      _requested = false;
    } else if (!_requested) {
      _requested = true;
      WidgetsBinding.instance.addPostFrameCallback((_) => _load());
    }

    if (!status.isConnected) {
      return const NotConnectedView();
    }

    if (!status.granted(Permissions.manageFiles)) {
      return Scaffold(
        appBar: AppBar(title: const Text('Files')),
        body: const Center(
          child: Padding(
            padding: EdgeInsets.all(32),
            child: Text(
              'This device does not have permission to manage files on the PC.\n\n'
              'Open PC-Remote on the PC, find this device under Paired Devices, and turn on '
              '"Manage files".',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    if (!status.has(Capabilities.files)) {
      return Scaffold(
        appBar: AppBar(title: const Text('Files')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Text(
              status.isLoggedOut
                  ? 'Nobody is signed in to the PC, so its files cannot be listed.'
                  : 'This PC did not report that it can share its files. '
                      'It may need a newer version of PC-Remote.',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    final crumbs = FilePaths.breadcrumbs(roots: _roots, path: _path);
    final listing = _listing;
    final transfer = _transfer;

    return PopScope(
      // Back climbs out of the folder first. Only at the top does it leave the app as usual.
      canPop: !widget.active || _atRoots,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _goUp();
      },
      child: Scaffold(
        appBar: AppBar(
          leading: _atRoots
              ? null
              : IconButton(
                  tooltip: 'Up',
                  onPressed: _goUp,
                  icon: const Icon(Icons.arrow_upward),
                ),
          title: Text(_atRoots ? 'Files' : crumbs.last.label),
          actions: [
            if (!_atRoots && listing != null)
              PopupMenuButton<bool>(
                tooltip: 'New',
                enabled: !_busy,
                icon: const Icon(Icons.add),
                onSelected: (isDirectory) => _create(isDirectory: isDirectory),
                itemBuilder: (context) => const [
                  PopupMenuItem(value: true, child: Text('New folder')),
                  PopupMenuItem(value: false, child: Text('New file')),
                ],
              ),
            IconButton(
              tooltip: 'Refresh',
              onPressed: _loading ? null : _load,
              icon: const Icon(Icons.refresh),
            ),
          ],
        ),
        body: Column(
          children: [
            if (!_atRoots) _Breadcrumbs(crumbs: crumbs, onOpen: _open),

            if (_loading || _busy) const LinearProgressIndicator(),

            if (listing != null && listing.truncated)
              _Banner(
                'This folder holds more than can be shown. Only the first '
                '${listing.entries.length} items are listed.',
              ),

            Expanded(
              child: RefreshIndicator(
                onRefresh: _load,
                child: _content(listing),
              ),
            ),

            if (transfer != null)
              _TransferBar(
                transfer: transfer,
                blocked: _transferBlock(transfer),
                busy: _busy,
                onCancel: () => setState(() => _transfer = null),
                onConfirm: () => _finishTransfer(transfer),
              ),
          ],
        ),
      ),
    );
  }

  Widget _content(FileListResult? listing) {
    final error = _error;

    if (listing == null) {
      // Always a scrollable, so pull-to-refresh works on the error and loading states too.
      return ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(32),
        children: [
          if (error != null) ...[
            Icon(
              Icons.folder_off_outlined,
              size: 40,
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
            const SizedBox(height: 12),
            Text(error, textAlign: TextAlign.center),
            const SizedBox(height: 16),
            Wrap(
              alignment: WrapAlignment.center,
              spacing: 8,
              children: [
                if (!_atRoots) OutlinedButton(onPressed: _goUp, child: const Text('Go up')),
                FilledButton.tonal(onPressed: _load, child: const Text('Try again')),
              ],
            ),
          ],
        ],
      );
    }

    if (listing.entries.isEmpty) {
      return ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.all(32),
        children: [
          Text(
            _atRoots
                ? 'The PC is not sharing any folders with this device.'
                : 'This folder is empty.',
            textAlign: TextAlign.center,
          ),
        ],
      );
    }

    return ListView.builder(
      physics: const AlwaysScrollableScrollPhysics(),
      itemCount: listing.entries.length,
      itemBuilder: (context, index) {
        final entry = listing.entries[index];

        return _EntryTile(
          entry: entry,
          onTap: () => entry.isDirectory ? _open(entry.path) : _showActions(entry),
          onMore: () => _showActions(entry),
        );
      },
    );
  }

  /// Shows a folder. An empty path shows the roots.
  Future<void> _open(String path) {
    setState(() {
      _path = path;
      _listing = null;
      _error = null;
    });

    return _load();
  }

  Future<void> _goUp() {
    final crumbs = FilePaths.breadcrumbs(roots: _roots, path: _path);
    return _open(FilePaths.upTarget(crumbs: crumbs, listing: _listing));
  }

  Future<void> _load() async {
    if (!mounted) return;

    final request = ++_request;
    final path = _path;

    setState(() {
      _loading = true;
      _error = null;
    });

    try {
      final response =
          await ref.read(connectionProvider).send(Commands.fileList, args: FileArgs.list(path));

      if (!mounted || request != _request) return;

      if (response == null) {
        setState(() {
          _listing = null;
          _error = 'Not connected to the PC.';
        });
        return;
      }

      if (!response.ok) {
        // The old listing goes too: a folder that can no longer be read must not go on
        // looking as though it can.
        setState(() {
          _listing = null;
          _error = _explain(response.error);
        });
        return;
      }

      final result = FileListResult.fromJson(response.map);

      setState(() {
        _listing = result;

        if (path.isEmpty) {
          _roots = result.entries;
        } else if (result.path.isNotEmpty) {
          // The PC's own spelling of the folder, which is the one to send back.
          _path = result.path;
        }
      });
    } finally {
      if (mounted && request == _request) setState(() => _loading = false);
    }
  }

  Future<void> _showActions(FileEntry entry) async {
    final action = await showModalBottomSheet<_EntryAction>(
      context: context,
      showDragHandle: true,
      builder: (sheetContext) => SafeArea(
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              ListTile(
                leading: Icon(_iconFor(entry)),
                title: Text(entry.name, maxLines: 2, overflow: TextOverflow.ellipsis),
                subtitle: Text(entry.path, maxLines: 2, overflow: TextOverflow.ellipsis),
              ),
              const Divider(height: 1),
              ListTile(
                leading: const Icon(Icons.open_in_new),
                title: const Text('Open on PC'),
                subtitle: Text(
                  entry.isDirectory
                      ? "Shows this folder in Explorer on the PC's screen"
                      : "Opens with its usual app on the PC's screen",
                ),
                onTap: () => Navigator.of(sheetContext).pop(_EntryAction.open),
              ),

              // A root is one of the PC's shared folders or a whole drive. It can be opened
              // and browsed, but not renamed, moved or deleted from here.
              if (!entry.isRoot) ...[
                ListTile(
                  leading: const Icon(Icons.drive_file_rename_outline),
                  title: const Text('Rename'),
                  onTap: () => Navigator.of(sheetContext).pop(_EntryAction.rename),
                ),
                ListTile(
                  leading: const Icon(Icons.copy),
                  title: const Text('Copy to…'),
                  onTap: () => Navigator.of(sheetContext).pop(_EntryAction.copy),
                ),
                ListTile(
                  leading: const Icon(Icons.drive_file_move_outline),
                  title: const Text('Move to…'),
                  onTap: () => Navigator.of(sheetContext).pop(_EntryAction.move),
                ),
                ListTile(
                  leading: Icon(
                    Icons.delete_outline,
                    color: Theme.of(sheetContext).colorScheme.error,
                  ),
                  title: const Text('Delete'),
                  onTap: () => Navigator.of(sheetContext).pop(_EntryAction.delete),
                ),
              ],
              const SizedBox(height: 8),
            ],
          ),
        ),
      ),
    );

    if (!mounted || action == null) return;

    switch (action) {
      case _EntryAction.open:
        await _run(
          Commands.fileOpen,
          FileArgs.open(entry.path),
          (_) => 'Opened ${entry.name} on the PC.',
        );

      case _EntryAction.rename:
        await _rename(entry);

      case _EntryAction.copy:
      case _EntryAction.move:
        // No message here: the bar that appears says what to do next, and a snackbar would
        // sit on top of it.
        setState(() {
          _transfer = _Transfer(
            entry: entry,
            move: action == _EntryAction.move,
            fromFolder: _path,
          );
        });

      case _EntryAction.delete:
        await _delete(entry);
    }
  }

  Future<void> _create({required bool isDirectory}) async {
    final parent = _path;
    if (parent.isEmpty) return;

    final name = await showDialog<String>(
      context: context,
      builder: (_) => _NameDialog(
        title: isDirectory ? 'New folder' : 'New file',
        label: isDirectory ? 'Folder name' : 'File name',
        confirm: 'Create',
      ),
    );

    if (!mounted || name == null) return;

    // The PC never overwrites: a name already taken comes back as a refusal with its reason.
    final done = await _run(
      Commands.fileCreate,
      FileArgs.create(parent: parent, name: name, isDirectory: isDirectory),
      (data) => 'Created ${FileEntry.fromResult(data)?.name ?? name}.',
    );

    if (done) await _load();
  }

  Future<void> _rename(FileEntry entry) async {
    final name = await showDialog<String>(
      context: context,
      builder: (_) => _NameDialog(
        title: 'Rename',
        label: 'New name',
        confirm: 'Rename',
        initial: entry.name,
        selectStem: !entry.isDirectory,
      ),
    );

    if (!mounted || name == null || name == entry.name) return;

    final done = await _run(
      Commands.fileRename,
      FileArgs.rename(path: entry.path, newName: name),
      (data) => 'Renamed to ${FileEntry.fromResult(data)?.name ?? name}.',
    );

    if (done) await _load();
  }

  Future<void> _delete(FileEntry entry) async {
    final permanent = await showDialog<bool>(
      context: context,
      builder: (_) => _DeleteDialog(entry: entry),
    );

    if (!mounted || permanent == null) return;

    final done = await _run(
      Commands.fileDelete,
      FileArgs.delete(path: entry.path, permanent: permanent),
      (_) => permanent
          ? 'Permanently deleted ${entry.name}.'
          : 'Moved ${entry.name} to the Recycle Bin.',
    );

    if (!mounted) return;

    if (done) {
      // What was picked up for a copy or move is gone if it was this, or was inside this.
      final transfer = _transfer;
      if (transfer != null && FilePaths.isSameOrInside(transfer.entry.path, entry.path)) {
        setState(() => _transfer = null);
      }

      await _load();
    }
  }

  /// Why the pending copy or move cannot land in the folder on show, or null when it can.
  String? _transferBlock(_Transfer transfer) {
    if (_atRoots) return 'Open the folder to put it in.';
    if (_listing == null) return _loading ? 'Opening…' : 'This folder could not be opened.';

    if (transfer.entry.isDirectory && FilePaths.isSameOrInside(_path, transfer.entry.path)) {
      return 'A folder cannot be put inside itself.';
    }

    if (transfer.move && FilePaths.same(_path, transfer.fromFolder)) {
      return 'It is already in this folder.';
    }

    return null;
  }

  Future<void> _finishTransfer(_Transfer transfer) async {
    final destination = _path;
    if (_transferBlock(transfer) != null) return;

    final name = transfer.entry.name;

    final done = await _run(
      transfer.move ? Commands.fileMove : Commands.fileCopy,
      transfer.move
          ? FileArgs.move(path: transfer.entry.path, destination: destination)
          : FileArgs.copy(path: transfer.entry.path, destination: destination),
      (data) {
        // A copy that met an existing name lands under another one, chosen by the PC. Saying
        // which keeps the user from hunting for a file that is not called what they expect.
        final landed = FileEntry.fromResult(data)?.name ?? name;

        if (transfer.move) return 'Moved $name here.';
        return landed == name ? 'Copied $name here.' : 'Copied $name here as $landed.';
      },
    );

    if (!mounted) return;

    if (done) {
      setState(() => _transfer = null);
      await _load();
    }
  }

  /// Sends one file command and reports the outcome. True when the PC did it.
  Future<bool> _run(
    String command,
    Map<String, dynamic> args,
    String Function(Map<String, dynamic> data) success,
  ) async {
    setState(() => _busy = true);

    try {
      final response = await ref.read(connectionProvider).send(command, args: args);

      if (!mounted) return false;

      if (response == null) {
        _toast('Not connected to the PC.');
        return false;
      }

      if (!response.ok) {
        _toast(_explain(response.error));

        // Something on screen is no longer there, so the list is out of date.
        if (response.error?.code == ErrorCodes.notFound) await _load();
        return false;
      }

      _toast(success(response.map));
      return true;
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Turns a refusal into something a person can act on. Where the PC explained itself — it
  /// knows whether a file is in use, protected or outside the shared folders — its own words
  /// are used.
  String _explain(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.permissionDenied =>
          'This device cannot manage files. Enable "Manage files" on the PC.',
        ErrorCodes.sessionUnavailable =>
          'Nobody is signed in to the PC, so its files are unavailable.',
        ErrorCodes.accessDenied => error?.message ?? 'The PC refused access to that.',
        ErrorCodes.notFound => error?.message ?? 'That is no longer there.',
        ErrorCodes.invalidArguments => error?.message ?? 'That request was not valid.',
        ErrorCodes.unknownCommand ||
        ErrorCodes.notSupported =>
          'This PC cannot do that. It may need a newer version of PC-Remote.',
        _ => error?.message ?? 'The command failed.',
      };

  void _toast(String message) {
    ScaffoldMessenger.of(context)
      ..clearSnackBars()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

IconData _iconFor(FileEntry entry) {
  if (entry.isRoot) {
    // A drive's path is nothing but the drive ("C:\"); a shared folder's is longer.
    return entry.path.length <= 3 ? Icons.storage : Icons.folder_special_outlined;
  }

  return entry.isDirectory ? Icons.folder : Icons.insert_drive_file_outlined;
}

class _EntryTile extends StatelessWidget {
  const _EntryTile({required this.entry, required this.onTap, required this.onMore});

  final FileEntry entry;
  final VoidCallback onTap;
  final VoidCallback onMore;

  @override
  Widget build(BuildContext context) {
    final size = entry.size;
    final modified = entry.modified;

    final details = entry.isRoot
        ? entry.path
        : [
            if (!entry.isDirectory && size != null) formatFileSize(size),
            if (modified != null) formatFileTime(modified),
            if (entry.readOnly) 'Read-only',
          ].join(' · ');

    return ListTile(
      leading: Icon(
        _iconFor(entry),
        color: entry.isDirectory ? Theme.of(context).colorScheme.primary : null,
      ),
      title: Text(entry.name, maxLines: 1, overflow: TextOverflow.ellipsis),
      subtitle:
          details.isEmpty ? null : Text(details, maxLines: 1, overflow: TextOverflow.ellipsis),
      onTap: onTap,
      onLongPress: onMore,
      trailing: IconButton(
        tooltip: 'Actions',
        onPressed: onMore,
        icon: const Icon(Icons.more_vert),
      ),
    );
  }
}

/// The trail from the roots to the folder on show. Each step is a way straight back to it.
class _Breadcrumbs extends StatelessWidget {
  const _Breadcrumbs({required this.crumbs, required this.onOpen});

  final List<FileCrumb> crumbs;
  final ValueChanged<String> onOpen;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Material(
      color: scheme.surfaceContainerLow,
      child: SizedBox(
        height: 40,
        width: double.infinity,
        child: LayoutBuilder(
          builder: (context, constraints) => SingleChildScrollView(
            scrollDirection: Axis.horizontal,

            // Reversed so a deep folder opens scrolled to its own end of the trail, which is
            // the end the user is at.
            reverse: true,
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: ConstrainedBox(
              // A reversed scroll view would otherwise push a short trail to the right edge.
              constraints: BoxConstraints(minWidth: constraints.maxWidth - 16),
              child: Row(
                children: [
              for (var i = 0; i < crumbs.length; i++) ...[
                if (i > 0) Icon(Icons.chevron_right, size: 18, color: scheme.onSurfaceVariant),
                TextButton(
                  style: TextButton.styleFrom(
                    visualDensity: VisualDensity.compact,
                    padding: const EdgeInsets.symmetric(horizontal: 6),
                    minimumSize: const Size(0, 32),
                  ),

                  // The last crumb is where the user already is.
                  onPressed: i == crumbs.length - 1 ? null : () => onOpen(crumbs[i].path),
                  child: Text(crumbs[i].label),
                ),
              ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _Banner extends StatelessWidget {
  const _Banner(this.message);

  final String message;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Container(
      width: double.infinity,
      color: scheme.tertiaryContainer,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      child: Text(
        message,
        style: Theme.of(context).textTheme.bodySmall?.copyWith(color: scheme.onTertiaryContainer),
      ),
    );
  }
}

/// Shown while a copy or move is waiting for its destination.
class _TransferBar extends StatelessWidget {
  const _TransferBar({
    required this.transfer,
    required this.blocked,
    required this.busy,
    required this.onCancel,
    required this.onConfirm,
  });

  final _Transfer transfer;

  /// Why it cannot go in the folder on show, or null when it can.
  final String? blocked;

  final bool busy;
  final VoidCallback onCancel;
  final VoidCallback onConfirm;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final text = Theme.of(context).textTheme;

    return Material(
      color: scheme.secondaryContainer,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 8, 8),
        child: Row(
          children: [
            Icon(
              transfer.move ? Icons.drive_file_move_outline : Icons.copy,
              size: 20,
              color: scheme.onSecondaryContainer,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    '${transfer.move ? 'Moving' : 'Copying'} ${transfer.entry.name}',
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: text.bodyMedium?.copyWith(color: scheme.onSecondaryContainer),
                  ),
                  Text(
                      blocked ?? 'Open the folder it should go in.',
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: text.bodySmall?.copyWith(color: scheme.onSecondaryContainer),
                    ),
                ],
              ),
            ),
            TextButton(onPressed: busy ? null : onCancel, child: const Text('Cancel')),
            const SizedBox(width: 4),
            FilledButton(
              onPressed: busy || blocked != null ? null : onConfirm,
              child: Text(transfer.move ? 'Move here' : 'Paste here'),
            ),
          ],
        ),
      ),
    );
  }
}

/// Asks for one file or folder name, and will not hand back one the PC would refuse.
class _NameDialog extends StatefulWidget {
  const _NameDialog({
    required this.title,
    required this.label,
    required this.confirm,
    this.initial = '',
    this.selectStem = false,
  });

  final String title;
  final String label;
  final String confirm;
  final String initial;

  /// Whether to preselect the part before the extension, as a file manager does on rename.
  final bool selectStem;

  @override
  State<_NameDialog> createState() => _NameDialogState();
}

class _NameDialogState extends State<_NameDialog> {
  late final TextEditingController _controller;

  /// Errors are held back until the user has typed, so the dialog does not open complaining.
  bool _touched = false;

  @override
  void initState() {
    super.initState();

    final initial = widget.initial;
    final dot = initial.lastIndexOf('.');

    _controller = TextEditingController(text: initial)
      ..selection = TextSelection(
        baseOffset: 0,
        extentOffset: widget.selectStem && dot > 0 ? dot : initial.length,
      );
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  // Spaces around a name are a typing accident (keyboards add one after a suggestion), and a
  // trailing one is not allowed at all, so they are dropped rather than reported.
  String get _name => _controller.text.trim();

  @override
  Widget build(BuildContext context) {
    final problem = FileNames.validate(_name);

    return AlertDialog(
      title: Text(widget.title),
      content: TextField(
        controller: _controller,
        autofocus: true,
        autocorrect: false,
        enableSuggestions: false,
        textInputAction: TextInputAction.done,
        onChanged: (_) => setState(() => _touched = true),
        onSubmitted: (_) => _submit(),
        decoration: InputDecoration(
          labelText: widget.label,
          errorText: _touched ? problem : null,
          errorMaxLines: 2,
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Cancel')),
        FilledButton(
          onPressed: problem == null ? _submit : null,
          child: Text(widget.confirm),
        ),
      ],
    );
  }

  void _submit() {
    if (FileNames.validate(_name) != null) {
      setState(() => _touched = true);
      return;
    }

    Navigator.of(context).pop(_name);
  }
}

/// Confirms a delete. Pops false for the Recycle Bin, true for a permanent delete, and nothing
/// when cancelled.
///
/// The Recycle Bin is the default and the only thing the main button does until the user has
/// deliberately ticked the box: a permanent delete on a PC one is not sitting at should take
/// two decisions, not one.
class _DeleteDialog extends StatefulWidget {
  const _DeleteDialog({required this.entry});

  final FileEntry entry;

  @override
  State<_DeleteDialog> createState() => _DeleteDialogState();
}

class _DeleteDialogState extends State<_DeleteDialog> {
  bool _permanent = false;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final entry = widget.entry;

    return AlertDialog(
      title: Text('Delete ${entry.name}?'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            _permanent
                ? (entry.isDirectory
                    ? 'This folder and everything in it will be deleted from the PC for good. '
                        'This cannot be undone.'
                    : 'This file will be deleted from the PC for good. This cannot be undone.')
                : (entry.isDirectory
                    ? "This folder and everything in it will be moved to the PC's Recycle Bin, "
                        'where it can be restored.'
                    : "This file will be moved to the PC's Recycle Bin, where it can be "
                        'restored.'),
          ),
          const SizedBox(height: 8),
          CheckboxListTile(
            value: _permanent,
            onChanged: (value) => setState(() => _permanent = value ?? false),
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
            dense: true,
            title: const Text('Delete permanently instead'),
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Cancel')),
        FilledButton(
          style: _permanent
              ? FilledButton.styleFrom(
                  backgroundColor: scheme.error,
                  foregroundColor: scheme.onError,
                )
              : null,
          onPressed: () => Navigator.of(context).pop(_permanent),
          child: Text(_permanent ? 'Delete permanently' : 'Move to Recycle Bin'),
        ),
      ],
    );
  }
}
