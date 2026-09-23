import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/apps/apps_screen.dart';
import '../features/devices/devices_screen.dart';
import '../features/remote_desktop/remote_desktop_screen.dart';
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

  /// Reconnects to the PC used most recently, so opening the app is enough to be connected.
  Future<void> _autoConnect() async {
    final pcs = await ref.read(pairedStoreProvider).getAll();
    if (!mounted || pcs.isEmpty) return;

    final connection = ref.read(connectionProvider);
    if (connection.status.phase != ConnectionPhase.idle) return;

    final latest = pcs.reduce((a, b) {
      final aSeen = a.lastSeenAt ?? a.pairedAt;
      final bSeen = b.lastSeenAt ?? b.pairedAt;
      return bSeen.isAfter(aSeen) ? b : a;
    });

    await connection.connect(latest);
  }

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(connectionProvider).status;

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
      const NavigationDestination(
        icon: Icon(Icons.apps_outlined),
        selectedIcon: Icon(Icons.apps),
        label: 'Apps',
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
          if (status.phase != ConnectionPhase.idle) _ConnectionBanner(status: status),
          Expanded(
            child: switch (_index) {
              0 => const DevicesScreen(),

              // Built only while selected, so leaving the tab disposes it and stops the stream.
              1 => const RemoteDesktopScreen(),
              2 => const SystemScreen(),
              3 => const AppsScreen(),
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
}

/// A strip showing what the connection is doing, and why it is not working when it is not.
class _ConnectionBanner extends StatelessWidget {
  const _ConnectionBanner({required this.status});

  final ConnectionStatus status;

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
            ],
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

    return tls == null ? 'Connected to $name' : 'Connected to $name · $tls';
  }
}
