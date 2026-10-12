import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../protocol/protocol.dart';
import '../../protocol/window_messages.dart';
import '../system/system_screen.dart';

/// The PC's open windows: what Alt+Tab would show, with the same things one does there.
///
/// Like the app list, this comes entirely from the PC and can only act on an id the PC itself
/// produced. A window that closed between the list and the tap is simply reported as gone.
class WindowsScreen extends ConsumerStatefulWidget {
  const WindowsScreen({super.key});

  @override
  ConsumerState<WindowsScreen> createState() => _WindowsScreenState();
}

class _WindowsScreenState extends ConsumerState<WindowsScreen> {
  List<WindowInfo> _windows = const [];
  bool _loading = false;
  bool _loaded = false;
  bool _requested = false;
  String? _error;

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(connectionProvider).status;

    // Loaded the first time the feature is actually usable, and again after it has been away:
    // a permission granted on the PC while this screen is open should fill the list without
    // the user having to find the refresh button.
    final available = status.isConnected &&
        status.granted(Permissions.launchApps) &&
        status.has(Capabilities.windows);

    if (!available) {
      _requested = false;
    } else if (!_requested) {
      _requested = true;
      WidgetsBinding.instance.addPostFrameCallback((_) => _load());
    }

    if (!status.isConnected) {
      return const NotConnectedView();
    }

    if (!status.granted(Permissions.launchApps)) {
      return Scaffold(
        appBar: AppBar(title: const Text('Windows')),
        body: const Center(
          child: Padding(
            padding: EdgeInsets.all(32),
            child: Text(
              'This device does not have permission to manage open windows.\n\n'
              'Open PC-Remote on the PC, find this device under Paired Devices, and turn on '
              '"Open and close apps".',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    if (!status.has(Capabilities.windows)) {
      return Scaffold(
        appBar: AppBar(title: const Text('Windows')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Text(
              status.isLoggedOut
                  ? 'Nobody is signed in to the PC, so there are no windows to list.'
                  : 'This PC did not report that it can list open windows. '
                      'It may need a newer version of PC-Remote.',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text('Windows'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: _loading ? null : _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: Column(
        children: [
          if (_error != null)
            Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                _error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),

          if (_loading) const LinearProgressIndicator(),

          Expanded(
            child: RefreshIndicator(
              onRefresh: _load,
              child: _windows.isEmpty
                  // Still a scrollable, so pulling down works on an empty list too.
                  ? ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      children: [
                        if (_loaded && _error == null)
                          const Padding(
                            padding: EdgeInsets.all(32),
                            child: Text('No windows are open.', textAlign: TextAlign.center),
                          ),
                      ],
                    )
                  : ListView.builder(
                      physics: const AlwaysScrollableScrollPhysics(),
                      itemCount: _windows.length,
                      itemBuilder: (context, index) => _WindowTile(
                        window: _windows[index],
                        onAction: (action) => _act(_windows[index], action),
                      ),
                    ),
            ),
          ),
        ],
      ),
    );
  }

  Future<void> _load() async {
    if (!mounted) return;

    final connection = ref.read(connectionProvider);

    // Nothing to ask while the screen is only showing why it is unavailable.
    if (!connection.status.isConnected ||
        !connection.status.granted(Permissions.launchApps) ||
        !connection.status.has(Capabilities.windows)) {
      return;
    }

    setState(() {
      _loading = true;
      _error = null;
    });

    try {
      final response = await connection.send(Commands.windowList);

      if (!mounted) return;

      if (response == null) {
        setState(() => _error = 'Not connected to the PC.');
        return;
      }

      if (!response.ok) {
        setState(() => _error = _explain(response.error));
        return;
      }

      setState(() {
        _windows = WindowInfo.listFromJson(response.map);
        _loaded = true;
      });
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _act(WindowInfo window, WindowAction action) async {
    final response = await ref
        .read(connectionProvider)
        .send(Commands.windowAction, args: action.argsFor(window.id));

    if (!mounted) return;

    if (response == null) {
      _toast('Not connected.');
      return;
    }

    if (!response.ok) {
      _toast(_explain(response.error));

      // A stale id means the list on screen is out of date, so bring it up to date.
      if (response.error?.code == ErrorCodes.notFound) await _load();
      return;
    }

    // The PC reports partial success honestly — Windows may refuse a focus change, and an app
    // asked to close may be showing a "save changes?" prompt instead — so its own detail is
    // shown in preference to a blanket "done".
    final result = WindowActionResult.fromJson(response.map);
    final name = window.displayTitle;

    _toast(result.detail ?? (result.succeeded ? _done(action, name) : _failed(action, name)));
    await _load();
  }

  static String _done(WindowAction action, String name) => switch (action) {
        WindowAction.focus => 'Switched to $name.',
        WindowAction.minimize => 'Minimized $name.',
        WindowAction.maximize => 'Maximized $name.',
        WindowAction.restore => 'Restored $name.',
        WindowAction.close => 'Asked $name to close.',
      };

  static String _failed(WindowAction action, String name) => switch (action) {
        WindowAction.focus => 'Windows would not switch to $name.',
        WindowAction.minimize => 'Could not minimize $name.',
        WindowAction.maximize => 'Could not maximize $name.',
        WindowAction.restore => 'Could not restore $name.',
        WindowAction.close => '$name did not close. It may be showing a prompt on the PC.',
      };

  String _explain(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.permissionDenied =>
          'This device cannot manage open windows. Enable "Open and close apps" on the PC.',
        ErrorCodes.sessionUnavailable =>
          'Nobody is signed in to the PC, so its windows are unavailable.',
        ErrorCodes.desktopLocked => 'The PC is locked, so its windows cannot be changed.',
        ErrorCodes.secureDesktop =>
          'The PC is showing a Windows security prompt. Answer it at the PC first.',
        ErrorCodes.notFound => 'That window is no longer open.',
        ErrorCodes.unknownCommand ||
        ErrorCodes.notSupported =>
          'This PC cannot manage windows. It may need a newer version of PC-Remote.',
        _ => error?.message ?? 'The command failed.',
      };

  void _toast(String message) {
    ScaffoldMessenger.of(context)
      ..clearSnackBars()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

class _WindowTile extends StatelessWidget {
  const _WindowTile({required this.window, required this.onAction});

  final WindowInfo window;
  final ValueChanged<WindowAction> onAction;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    final state = [
      if (window.processName.isNotEmpty) window.processName,
      if (window.isForeground) 'In front',
      if (window.isMinimized) 'Minimized' else if (window.isMaximized) 'Maximized',
    ].join(' · ');

    return ListTile(
      leading: Icon(
        window.isMinimized ? Icons.minimize : Icons.web_asset,
        color: window.isForeground ? scheme.primary : null,
      ),
      title: Text(
        window.displayTitle,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: window.isForeground ? const TextStyle(fontWeight: FontWeight.w600) : null,
      ),
      subtitle: state.isEmpty ? null : Text(state, maxLines: 1, overflow: TextOverflow.ellipsis),

      // The thing one most often wants from a window list is to get to that window.
      onTap: () => onAction(WindowAction.focus),
      trailing: PopupMenuButton<WindowAction>(
        tooltip: 'Window actions',
        onSelected: onAction,
        itemBuilder: (context) => [
          const PopupMenuItem(
            value: WindowAction.focus,
            child: _MenuRow(icon: Icons.open_in_new, label: 'Switch to'),
          ),
          PopupMenuItem(
            value: WindowAction.minimize,
            enabled: !window.isMinimized,
            child: const _MenuRow(icon: Icons.minimize, label: 'Minimize'),
          ),
          PopupMenuItem(
            value: WindowAction.maximize,
            enabled: !window.isMaximized || window.isMinimized,
            child: const _MenuRow(icon: Icons.crop_square, label: 'Maximize'),
          ),
          PopupMenuItem(
            value: WindowAction.restore,
            enabled: window.isMinimized || window.isMaximized,
            child: const _MenuRow(icon: Icons.filter_none, label: 'Restore'),
          ),
          const PopupMenuDivider(),
          const PopupMenuItem(
            value: WindowAction.close,
            child: _MenuRow(icon: Icons.close, label: 'Close'),
          ),
        ],
      ),
    );
  }
}

class _MenuRow extends StatelessWidget {
  const _MenuRow({required this.icon, required this.label});

  final IconData icon;
  final String label;

  @override
  Widget build(BuildContext context) => Row(
        children: [
          Icon(icon, size: 20),
          const SizedBox(width: 12),
          Text(label),
        ],
      );
}
