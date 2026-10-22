import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/apps/manage_screen.dart';
import '../features/devices/devices_screen.dart';
import '../features/remote_desktop/screen_picker_screen.dart';
import '../features/settings/settings_screen.dart';
import '../features/system/system_screen.dart';
import '../domain/connection_controller.dart';
import 'providers.dart';

/// The application shell.
class RemoteControlApp extends StatelessWidget {
  const RemoteControlApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'PC Remote',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF0F6CBD)),
        useMaterial3: true,
      ),
      darkTheme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xFF0F6CBD),
          brightness: Brightness.dark,
        ),
        useMaterial3: true,
      ),
      home: const HomeShell(),
    );
  }
}

/// Bottom-navigation shell.
///
/// The screens that need a live connection are disabled rather than hidden when there is
/// none: hiding them leaves the user wondering where a feature went, while a disabled tab
/// with a reason attached explains itself.
class HomeShell extends ConsumerStatefulWidget {
  const HomeShell({super.key});

  @override
  ConsumerState<HomeShell> createState() => _HomeShellState();
}

class _HomeShellState extends ConsumerState<HomeShell> {
  int _index = 0;

  @override
  void initState() {
    super.initState();
    _autoConnect();
  }

  /// Reconnects to the PCs that were connected last time, so opening the app is enough to be
  /// connected.
  Future<void> _autoConnect() async {
    final pcs = await ref.read(pairedStoreProvider).getAll();
    if (!mounted || pcs.isEmpty) return;

    await ref.read(connectionManagerProvider).restore(pcs);
  }

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(connectionStatusProvider);
    final connectedCount =
        ref.watch(connectionManagerProvider.select((manager) => manager.connected.length));

    final destinations = <NavigationDestination>[
      const NavigationDestination(
        icon: Icon(Icons.devices_outlined),
        selectedIcon: Icon(Icons.devices),
        label: 'Devices',
      ),
      const NavigationDestination(
        icon: Icon(Icons.desktop_windows_outlined),
        selectedIcon: Icon(Icons.desktop_windows),
        label: 'Screen',
      ),
      const NavigationDestination(
        icon: Icon(Icons.power_settings_new_outlined),
        selectedIcon: Icon(Icons.power_settings_new),
        label: 'System',
      ),

      // Apps, open windows and files share this one destination: the bar is full at five.
      const NavigationDestination(
        icon: Icon(Icons.dashboard_outlined),
        selectedIcon: Icon(Icons.dashboard),
        label: 'Manage',
      ),
      const NavigationDestination(
        icon: Icon(Icons.settings_outlined),
        selectedIcon: Icon(Icons.settings),
        label: 'Settings',
      ),
    ];

    return Scaffold(
      body: Column(
        children: [
          if (status.phase != ConnectionPhase.idle)
            _ConnectionBanner(
              status: status,
              connectedCount: connectedCount,
              onSwitch: connectedCount > 1 ? () => _switchPc(context) : null,
            ),
          Expanded(
            child: switch (_index) {
              0 => const DevicesScreen(),

              // A list of PCs; the stream itself opens on top for the one picked, and stops when
              // it is closed.
              1 => const ScreenPickerScreen(),
              2 => const SystemScreen(),
              3 => const ManageScreen(),
              _ => const SettingsScreen(),
            },
          ),
        ],
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: (index) => setState(() => _index = index),
        destinations: destinations,
      ),
    );
  }

  /// Picks which connected PC the System, Manage and Settings tabs work with.
  Future<void> _switchPc(BuildContext context) async {
    final manager = ref.read(connectionManagerProvider);

    final chosen = await showModalBottomSheet<String>(
      context: context,
      showDragHandle: true,
      builder: (sheetContext) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const ListTile(title: Text('Work with which PC?')),
            for (final connection in manager.connected)
              if (connection.status.pc case final pc?)
                ListTile(
                  leading: Icon(
                    manager.activeId == pc.deviceId
                        ? Icons.radio_button_checked
                        : Icons.radio_button_unchecked,
                  ),
                  title: Text(pc.deviceName),
                  subtitle: Text(connection.status.userName ?? 'Connected'),
                  onTap: () => Navigator.of(sheetContext).pop(pc.deviceId),
                ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );

    if (chosen != null) manager.select(chosen);
  }
}

/// A strip showing what the connection is doing, and why it is not working when it is not.
class _ConnectionBanner extends StatelessWidget {
  const _ConnectionBanner({required this.status, this.connectedCount = 0, this.onSwitch});

  final ConnectionStatus status;

  /// How many PCs are connected, the one shown included.
  final int connectedCount;

  /// Picks another connected PC to work with; null when there is no other.
  final VoidCallback? onSwitch;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    final (Color background, Color foreground, IconData icon) = switch (status.phase) {
      ConnectionPhase.ready => (scheme.primaryContainer, scheme.onPrimaryContainer, Icons.link),
      ConnectionPhase.degraded =>
        (scheme.tertiaryContainer, scheme.onTertiaryContainer, Icons.link),
      ConnectionPhase.failed => (scheme.errorContainer, scheme.onErrorContainer, Icons.error_outline),
      _ => (scheme.surfaceContainerHighest, scheme.onSurfaceVariant, Icons.sync),
    };

    final label = switch (status.phase) {
      ConnectionPhase.ready => _readyLabel(),
      ConnectionPhase.degraded => status.message ?? 'Connected with limits',
      _ => status.message ?? status.phase.name,
    };

    return Material(
      color: background,
      child: SafeArea(
        bottom: false,
        child: InkWell(
          onTap: onSwitch,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
            child: Row(
              children: [
                Icon(icon, size: 18, color: foreground),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    label,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(color: foreground),
                  ),
                ),
                if (status.latencyMs != null)
                  Text(
                    '${status.latencyMs} ms',
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(color: foreground),
                  ),

                // With several PCs connected the tabs act on one of them; this says there are
                // others and opens the choice.
                if (onSwitch != null) ...[
                  const SizedBox(width: 8),
                  Text(
                    '$connectedCount PCs',
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(color: foreground),
                  ),
                  Icon(Icons.unfold_more, size: 18, color: foreground),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }

  String _readyLabel() {
    final name = status.pc?.deviceName ?? 'PC';
    final tls = status.tlsDescription;

    // The lock state is surfaced here because it explains, without the user having to ask,
    // why the screen and input features are unavailable.
    if (status.isLocked) {
      return '$name — locked. Unlock at the PC to view or control the screen.';
    }

    if (status.isLoggedOut) {
      return '$name — nobody signed in. Power controls still work.';
    }

    // Worth saying, because it explains a slower connection and confirms the VPN route works.
    final route = status.viaRemoteAddress ? ' via its remote address' : '';

    return tls == null ? 'Connected to $name$route' : 'Connected to $name$route · $tls';
  }
}
