import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../data/storage/paired_pc_store.dart';
import '../../domain/connection_controller.dart';
import 'remote_desktop_screen.dart';

/// The Screen tab: the paired PCs, from which one is picked to watch and control.
///
/// A list first rather than straight into a stream, because with several PCs connected the
/// question "whose screen?" has to be asked — and answering it here also makes that PC the one
/// the other tabs work with.
class ScreenPickerScreen extends ConsumerWidget {
  const ScreenPickerScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final paired = ref.watch(pairedPcsProvider);
    final manager = ref.watch(connectionManagerProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Screen')),
      body: paired.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) => Center(child: Text('$error')),
        data: (pcs) {
          if (pcs.isEmpty) {
            return const _Empty();
          }

          // Connected PCs first: they are the ones that can be opened now.
          final sorted = [...pcs]..sort((a, b) {
              final aUp = manager.statusOf(a.deviceId).isConnected ? 0 : 1;
              final bUp = manager.statusOf(b.deviceId).isConnected ? 0 : 1;
              return aUp != bUp ? aUp - bUp : a.displayName.compareTo(b.displayName);
            });

          return ListView(
            padding: const EdgeInsets.symmetric(vertical: 8),
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
                child: Text(
                  'Choose a PC to see and control its screen.',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: Theme.of(context).colorScheme.onSurfaceVariant,
                      ),
                ),
              ),
              for (final pc in sorted)
                _PcTile(
                  pc: pc,
                  status: manager.statusOf(pc.deviceId),
                  selected: manager.activeId == pc.deviceId,
                  onOpen: () => openScreen(context, ref, pc),
                  onConnect: () => manager.connect(pc).ignore(),
                ),
            ],
          );
        },
      ),
    );
  }
}

/// Opens [pc]'s screen, making it the selected PC on the way.
Future<void> openScreen(BuildContext context, WidgetRef ref, PairedPc pc) async {
  final manager = ref.read(connectionManagerProvider)..select(pc.deviceId);
  final connection = manager.of(pc.deviceId);
  if (connection == null) return;

  await Navigator.of(context).push(
    MaterialPageRoute<void>(builder: (_) => RemoteDesktopScreen(connection: connection)),
  );
}

class _PcTile extends StatelessWidget {
  const _PcTile({
    required this.pc,
    required this.status,
    required this.selected,
    required this.onOpen,
    required this.onConnect,
  });

  final PairedPc pc;
  final ConnectionStatus status;
  final bool selected;
  final VoidCallback onOpen;
  final VoidCallback onConnect;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final connected = status.isConnected;
    final busy = !connected &&
        status.phase != ConnectionPhase.idle &&
        status.phase != ConnectionPhase.failed;

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
      shape: selected && connected
          ? RoundedRectangleBorder(
              side: BorderSide(color: scheme.primary, width: 1.5),
              borderRadius: BorderRadius.circular(12),
            )
          : null,
      child: ListTile(
        contentPadding: const EdgeInsets.fromLTRB(16, 6, 12, 6),
        leading: Icon(
          connected ? Icons.desktop_windows : Icons.desktop_windows_outlined,
          color: connected ? scheme.primary : scheme.outline,
          size: 32,
        ),
        title: Text(pc.displayName),
        subtitle: Text(_subtitle()),
        onTap: connected ? onOpen : null,
        trailing: busy
            ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
            : connected
                ? FilledButton.icon(
                    onPressed: onOpen,
                    icon: const Icon(Icons.cast_connected, size: 18),
                    label: const Text('Open'),
                  )
                : FilledButton.tonal(onPressed: onConnect, child: const Text('Connect')),
      ),
    );
  }

  String _subtitle() {
    if (status.isConnected) {
      if (status.isLocked) return 'Connected · locked';
      if (status.isLoggedOut) return 'Connected · nobody signed in';

      final route = status.viaRemoteAddress ? ' via remote address' : '';
      return 'Connected$route${status.userName != null ? ' · ${status.userName}' : ''}';
    }

    return switch (status.phase) {
      ConnectionPhase.idle => 'Not connected',
      ConnectionPhase.failed => status.message ?? 'Could not connect',
      _ => status.message ?? 'Connecting…',
    };
  }
}

class _Empty extends StatelessWidget {
  const _Empty();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.link_off, size: 48, color: theme.colorScheme.outline),
            const SizedBox(height: 16),
            Text('No paired PCs', style: theme.textTheme.titleMedium),
            const SizedBox(height: 8),
            Text(
              'Pair a PC from the Devices tab, then come back here to see its screen.',
              style: theme.textTheme.bodyMedium,
              textAlign: TextAlign.center,
            ),
          ],
        ),
      ),
    );
  }
}
