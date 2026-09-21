import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../data/storage/paired_pc_store.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';
import '../pairing/pair_screen.dart';

/// Lists paired PCs and PCs found on the network.
class DevicesScreen extends ConsumerWidget {
  const DevicesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final paired = ref.watch(pairedPcsProvider);
    final discovered = ref.watch(discoveredPcsProvider);
    final connection = ref.watch(connectionProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Devices'),
        actions: [
          IconButton(
            tooltip: 'Scan again',
            onPressed: () {
              ref.invalidate(discoveredPcsProvider);
              ref.invalidate(pairedPcsProvider);
            },
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => _startPairing(context, ref),
        icon: const Icon(Icons.qr_code_scanner),
        label: const Text('Pair a PC'),
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(discoveredPcsProvider);
          ref.invalidate(pairedPcsProvider);
        },
        child: ListView(
          padding: const EdgeInsets.only(bottom: 88),
          children: [
            if (connection.status.requiresRepair)
              _RepairNotice(message: connection.status.message ?? 'This device needs to pair again.'),

            _SectionHeader(
              title: 'Paired',
              subtitle: 'These PCs recognise this device',
            ),

            paired.when(
              loading: () => const _Loading(),
              error: (error, _) => _Error(message: '$error'),
              data: (devices) => devices.isEmpty
                  ? const _Empty(
                      icon: Icons.link_off,
                      title: 'No paired PCs yet',
                      message:
                          'On your PC, open PC-Remote, turn on "Allow pairing", then scan the QR code it shows.',
                    )
                  : Column(
                      children: [
                        for (final pc in devices)
                          _PairedTile(
                            pc: pc,
                            discovered: _findBeacon(discovered.valueOrNull, pc.deviceId),
                            status: connection.status,
                            onConnect: () => connection.connect(pc),
                            onDisconnect: connection.disconnect,
                            onForget: () => _forget(context, ref, pc),
                          ),
                      ],
                    ),
            ),

            const SizedBox(height: 8),
            _SectionHeader(
              title: 'Found on this network',
              subtitle: discovered.isLoading ? 'Scanning…' : 'Not paired with this device',
            ),

            discovered.when(
              loading: () => const _Loading(),
              error: (error, _) => _Error(
                message:
                    'Could not search the network. Check that Wi-Fi is on and that the app has '
                    'permission to use the local network.',
              ),
              data: (beacons) {
                final pairedIds =
                    (paired.valueOrNull ?? const <PairedPc>[]).map((pc) => pc.deviceId).toSet();

                final unpaired =
                    beacons.where((beacon) => !pairedIds.contains(beacon.deviceId)).toList();

                if (unpaired.isEmpty) {
                  return const _Empty(
                    icon: Icons.wifi_find,
                    title: 'No new PCs found',
                    message:
                        'Make sure the PC is on the same Wi-Fi network and that PC-Remote is running on it.',
                  );
                }

                return Column(
                  children: [
                    for (final beacon in unpaired)
                      ListTile(
                        leading: const Icon(Icons.computer_outlined),
                        title: Text(beacon.deviceName),
                        subtitle: Text(
                          beacon.isCompatible
                              ? '${beacon.address}:${beacon.port}'
                              : '${beacon.address} — needs a newer app or PC agent',
                        ),
                        trailing: beacon.isCompatible
                            ? TextButton(
                                onPressed: () => _startPairing(context, ref),
                                child: const Text('Pair'),
                              )
                            : const Icon(Icons.warning_amber_rounded),
                      ),
                  ],
                );
              },
            ),
          ],
        ),
      ),
    );
  }

  static DiscoveryBeacon? _findBeacon(List<DiscoveryBeacon>? beacons, String deviceId) {
    if (beacons == null) return null;
    for (final beacon in beacons) {
      if (beacon.deviceId == deviceId) return beacon;
    }
    return null;
  }

  Future<void> _startPairing(BuildContext context, WidgetRef ref) async {
    final paired = await Navigator.of(context).push<bool>(
      MaterialPageRoute(builder: (_) => const PairScreen()),
    );

    if (paired == true) {
      ref.invalidate(pairedPcsProvider);
      ref.invalidate(discoveredPcsProvider);
    }
  }

  Future<void> _forget(BuildContext context, WidgetRef ref, PairedPc pc) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('Forget ${pc.deviceName}?'),
        content: const Text(
          'This device will no longer connect to that PC. You will need to scan its QR code '
          'again to pair.\n\nThis does not remove the pairing on the PC itself — revoke it '
          'there as well if you no longer trust this phone.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Forget'),
          ),
        ],
      ),
    );

    if (confirmed != true) return;

    final connection = ref.read(connectionProvider);
    if (connection.status.pc?.deviceId == pc.deviceId) {
      await connection.disconnect();
    }

    await ref.read(pairedStoreProvider).remove(pc.deviceId);
    ref.invalidate(pairedPcsProvider);
  }
}

class _PairedTile extends StatelessWidget {
  const _PairedTile({
    required this.pc,
    required this.discovered,
    required this.status,
    required this.onConnect,
    required this.onDisconnect,
    required this.onForget,
  });

  final PairedPc pc;
  final DiscoveryBeacon? discovered;
  final ConnectionStatus status;
  final VoidCallback onConnect;
  final Future<void> Function() onDisconnect;
  final VoidCallback onForget;

  @override
  Widget build(BuildContext context) {
    final isThisPc = status.pc?.deviceId == pc.deviceId;
    final isConnected = isThisPc && status.isConnected;
    final isBusy = isThisPc &&
        status.phase != ConnectionPhase.idle &&
        status.phase != ConnectionPhase.ready &&
        status.phase != ConnectionPhase.degraded &&
        status.phase != ConnectionPhase.failed;

    // Online means the PC answered a discovery probe during the last scan. Shown separately
    // from "connected", because a reachable PC we are not connected to is a useful state.
    final isOnline = discovered != null;

    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
      child: ListTile(
        leading: Icon(
          isConnected ? Icons.desktop_windows : Icons.desktop_windows_outlined,
          color: isConnected
              ? Theme.of(context).colorScheme.primary
              : isOnline
                  ? null
                  : Theme.of(context).disabledColor,
        ),
        title: Text(pc.deviceName),
        subtitle: Text(_subtitle(isConnected, isOnline)),
        trailing: isBusy
            ? const SizedBox(
                width: 20,
                height: 20,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (isConnected)
                    TextButton(onPressed: () => onDisconnect(), child: const Text('Disconnect'))
                  else
                    FilledButton.tonal(onPressed: onConnect, child: const Text('Connect')),
                  IconButton(
                    tooltip: 'Forget this PC',
                    onPressed: onForget,
                    icon: const Icon(Icons.delete_outline),
                  ),
                ],
              ),
      ),
    );
  }

  String _subtitle(bool isConnected, bool isOnline) {
    if (isConnected) {
      if (status.isLocked) return 'Connected · locked';
      if (status.isLoggedOut) return 'Connected · nobody signed in';
      return 'Connected${status.userName != null ? ' · ${status.userName}' : ''}';
    }

    if (isOnline) return 'Online · ${discovered!.address}';

    final lastSeen = pc.lastSeenAt;
    if (lastSeen == null) return 'Offline';

    return 'Offline · last seen ${_ago(lastSeen)}';
  }

  static String _ago(DateTime time) {
    final difference = DateTime.now().difference(time);

    if (difference.inMinutes < 1) return 'just now';
    if (difference.inHours < 1) return '${difference.inMinutes}m ago';
    if (difference.inDays < 1) return '${difference.inHours}h ago';
    return '${difference.inDays}d ago';
  }
}

class _RepairNotice extends StatelessWidget {
  const _RepairNotice({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Container(
      margin: const EdgeInsets.all(12),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: scheme.errorContainer,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        children: [
          Icon(Icons.gpp_maybe_outlined, color: scheme.onErrorContainer),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              message,
              style: TextStyle(color: scheme.onErrorContainer),
            ),
          ),
        ],
      ),
    );
  }
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.title, this.subtitle});

  final String title;
  final String? subtitle;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 20, 16, 6),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: Theme.of(context).textTheme.titleSmall),
            if (subtitle != null)
              Text(
                subtitle!,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
              ),
          ],
        ),
      );
}

class _Loading extends StatelessWidget {
  const _Loading();

  @override
  Widget build(BuildContext context) => const Padding(
        padding: EdgeInsets.all(24),
        child: Center(child: CircularProgressIndicator()),
      );
}

class _Error extends StatelessWidget {
  const _Error({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.all(16),
        child: Text(
          message,
          style: TextStyle(color: Theme.of(context).colorScheme.error),
        ),
      );
}

class _Empty extends StatelessWidget {
  const _Empty({required this.icon, required this.title, required this.message});

  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 20),
        child: Column(
          children: [
            Icon(icon, size: 36, color: Theme.of(context).colorScheme.onSurfaceVariant),
            const SizedBox(height: 10),
            Text(title, style: Theme.of(context).textTheme.titleSmall),
            const SizedBox(height: 6),
            Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
            ),
          ],
        ),
      );
}
