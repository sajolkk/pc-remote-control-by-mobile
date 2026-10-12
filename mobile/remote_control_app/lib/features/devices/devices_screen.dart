import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../data/storage/paired_pc_store.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';
import '../pairing/code_pair_screen.dart';
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
        onPressed: () => _choosePairing(context, ref),
        icon: const Icon(Icons.add_link),
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
              _RepairNotice(
                message: connection.status.message ?? 'This device needs to pair again.',
                onPairAgain: connection.status.pc == null
                    ? null
                    : () => _pairAgain(context, ref, connection.status.pc!),
              ),

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
                          'Tap Pair next to your PC below, then click Allow on the PC when the codes match. '
                          'Or tap "Pair a PC" to scan the QR code PC-Remote shows.',
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
                            onEditRemoteAddress: () => _editRemoteAddress(context, ref, pc),
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
                // A PC that has dropped this device is listed here again: it has to be paired
                // afresh, and this list is where pairing starts.
                final pairedIds = (paired.valueOrNull ?? const <PairedPc>[])
                    .map((pc) => pc.deviceId)
                    .where((id) => id != _needsPairing(connection.status))
                    .toSet();

                final unpaired =
                    beacons.where((beacon) => !pairedIds.contains(beacon.deviceId)).toList();

                if (unpaired.isEmpty) {
                  return const _Empty(
                    icon: Icons.wifi_find,
                    title: 'No new PCs found',
                    message:
                        'Install PC-Remote on the PC (PC-Remote-Setup.exe) and check its icon is in '
                        'the taskbar tray. The phone and PC must be on the same Wi-Fi, and the PC\'s '
                        'network must be set to Private. Pull down to search again.',
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
                                onPressed: () => _pairFound(context, ref, beacon),
                                child: const Text('Pair'),
                              )
                            : const Icon(Icons.warning_amber_rounded),
                      ),
                  ],
                );
              },
            ),

            ListTile(
              leading: const Icon(Icons.edit_location_alt_outlined),
              title: const Text("Can't see your PC?"),
              subtitle: const Text("Enter the PC's address instead"),
              onTap: () => _enterAddress(context, ref),
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

  /// The two ways to pair: scan the PC's QR code, which connects straight to the address in it,
  /// or search the network and confirm a code on both screens.
  Future<void> _choosePairing(BuildContext context, WidgetRef ref) async {
    final choice = await showModalBottomSheet<String>(
      context: context,
      showDragHandle: true,
      builder: (sheetContext) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.qr_code_scanner),
              title: const Text('Scan QR code'),
              subtitle: const Text(
                'Connects directly, no search needed. On the PC, open PC-Remote from the Start menu.',
              ),
              onTap: () => Navigator.of(sheetContext).pop('scan'),
            ),
            ListTile(
              leading: const Icon(Icons.wifi_find),
              title: const Text('Search this network'),
              subtitle: const Text('Find the PC, then confirm the same code on both screens.'),
              onTap: () => Navigator.of(sheetContext).pop('search'),
            ),
            ListTile(
              leading: const Icon(Icons.edit_location_alt_outlined),
              title: const Text("Enter the PC's address"),
              subtitle: const Text('When the search cannot see the PC.'),
              onTap: () => Navigator.of(sheetContext).pop('address'),
            ),
            const SizedBox(height: 8),
          ],
        ),
      ),
    );

    if (!context.mounted) return;

    switch (choice) {
      case 'scan':
        await _startPairing(context, ref);
      case 'search':
        await _searchAndPair(context, ref);
      case 'address':
        await _enterAddress(context, ref);
    }
  }

  /// Searches, then pairs straight away when exactly one new PC answers. With several, the list
  /// on the Devices screen is the place to choose.
  Future<void> _searchAndPair(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger.showSnackBar(const SnackBar(content: Text('Searching for PCs…')));

    final needsPairing = _needsPairing(ref.read(connectionProvider).status);
    final pairedIds = (ref.read(pairedPcsProvider).valueOrNull ?? const <PairedPc>[])
        .map((pc) => pc.deviceId)
        .where((id) => id != needsPairing)
        .toSet();

    final found = <String, DiscoveryBeacon>{};
    try {
      await for (final beacon in ref.read(discoveryProvider).scan(duration: const Duration(seconds: 4))) {
        if (beacon.isCompatible && !pairedIds.contains(beacon.deviceId)) {
          found[beacon.deviceId] = beacon;
        }
      }
    } on Object {
      // Reported below as nothing found.
    }

    messenger.hideCurrentSnackBar();
    ref.invalidate(discoveredPcsProvider);
    if (!context.mounted) return;

    if (found.length == 1) {
      await _pairFound(context, ref, found.values.single);
      return;
    }

    messenger.showSnackBar(
      SnackBar(
        content: Text(
          found.isEmpty
              ? 'No new PC found. Check PC-Remote is installed on the PC, or scan its QR code instead.'
              : 'Found ${found.length} PCs. Tap Pair next to the one you want.',
        ),
      ),
    );
  }

  /// The id of the PC that refused this device as unpaired, if that is the current state.
  static String? _needsPairing(ConnectionStatus status) =>
      status.requiresRepair ? status.pc?.deviceId : null;

  /// Pairs again with a PC that no longer recognises this device: straight to the code screen
  /// when the PC answers on this network, otherwise by its address.
  Future<void> _pairAgain(BuildContext context, WidgetRef ref, PairedPc pc) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger.showSnackBar(SnackBar(content: Text('Looking for ${pc.deviceName}…')));

    DiscoveryBeacon? found;
    try {
      await for (final beacon in ref.read(discoveryProvider).scan(duration: const Duration(seconds: 3))) {
        if (beacon.deviceId == pc.deviceId) {
          found = beacon;
          break;
        }
      }
    } on Object {
      // Falls through to asking for the address.
    }

    messenger.hideCurrentSnackBar();
    if (!context.mounted) return;

    if (found != null) {
      await _pairFound(context, ref, found);
    } else {
      await _enterAddress(context, ref);
    }
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

  Future<void> _pairFound(BuildContext context, WidgetRef ref, DiscoveryBeacon beacon) async {
    final paired = await Navigator.of(context).push<bool>(
      MaterialPageRoute(builder: (_) => CodePairScreen(beacon: beacon)),
    );

    if (paired == true) {
      ref.invalidate(pairedPcsProvider);
      ref.invalidate(discoveredPcsProvider);
    }
  }

  /// For networks where the search cannot see the PC but can still reach it, e.g. when the
  /// phone's Wi-Fi and the PC's cable are on different segments of the same router.
  Future<void> _enterAddress(BuildContext context, WidgetRef ref) async {
    final controller = TextEditingController();

    final host = await showDialog<String>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text("PC's address"),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              'On the PC, open PC-Remote from the Start menu: the pairing window lists its '
              'address, for example 192.168.1.20.',
            ),
            const SizedBox(height: 12),
            TextField(
              controller: controller,
              autofocus: true,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: const InputDecoration(hintText: '192.168.1.20'),
              onSubmitted: (value) => Navigator.of(dialogContext).pop(value.trim()),
            ),
          ],
        ),
        actions: [
          TextButton(onPressed: () => Navigator.of(dialogContext).pop(), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(controller.text.trim()),
            child: const Text('Find'),
          ),
        ],
      ),
    );

    controller.dispose();
    if (host == null || host.isEmpty || !context.mounted) return;

    if (InternetAddress.tryParse(host) == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('"$host" is not an address like 192.168.1.20.')),
      );
      return;
    }

    final beacon = await ref.read(discoveryProvider).probe(host, timeout: const Duration(seconds: 3));
    if (!context.mounted) return;

    if (beacon == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            'PC-Remote did not answer at $host. Check the address, that PC-Remote is installed, '
            'and that the phone is on the same network as the PC.',
          ),
        ),
      );
      return;
    }

    await _pairFound(context, ref, beacon);
  }

  /// Adds, changes or removes the address used to reach a PC that is not on this network.
  ///
  /// Only ever an extra place to look. The PC that answers there is held to the certificate
  /// pinned at pairing exactly as it is on the local network, so a wrong or hijacked address
  /// gets a refusal, not a connection.
  Future<void> _editRemoteAddress(BuildContext context, WidgetRef ref, PairedPc pc) async {
    final result = await showDialog<({RemoteAddress? remote})>(
      context: context,
      builder: (_) => _RemoteAddressDialog(pc: pc),
    );

    if (result == null) return;

    final updated = await ref.read(pairedStoreProvider).setRemoteAddress(pc.deviceId, result.remote);
    ref.invalidate(pairedPcsProvider);

    if (updated == null || !context.mounted) return;

    ScaffoldMessenger.of(context)
      ..clearSnackBars()
      ..showSnackBar(
        SnackBar(
          content: Text(
            result.remote == null
                ? 'Remote address removed for ${pc.deviceName}.'
                : 'Remote address saved for ${pc.deviceName}.',
          ),
        ),
      );

    // If the app is in the middle of failing to reach this very PC, the new address is most
    // likely the fix, so try it now rather than at the end of a back-off.
    final connection = ref.read(connectionProvider);
    final status = connection.status;

    final isSearching = status.pc?.deviceId == pc.deviceId &&
        !status.isConnected &&
        status.phase != ConnectionPhase.idle &&
        status.phase != ConnectionPhase.failed;

    if (isSearching && result.remote != null) {
      connection.connect(updated).ignore();
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
    required this.onEditRemoteAddress,
    required this.onForget,
  });

  final PairedPc pc;
  final DiscoveryBeacon? discovered;
  final ConnectionStatus status;
  final VoidCallback onConnect;
  final Future<void> Function() onDisconnect;
  final VoidCallback onEditRemoteAddress;
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

    final remote = pc.remoteAddressLabel;

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
        subtitle: Text(
          remote == null
              ? _subtitle(isConnected, isOnline)
              : '${_subtitle(isConnected, isOnline)}\nRemote address: $remote',
        ),
        isThreeLine: remote != null,
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
                  PopupMenuButton<VoidCallback>(
                    tooltip: 'More',
                    onSelected: (action) => action(),
                    itemBuilder: (context) => [
                      PopupMenuItem(
                        value: onEditRemoteAddress,
                        child: Text(remote == null ? 'Add remote address…' : 'Remote address…'),
                      ),
                      PopupMenuItem(value: onForget, child: const Text('Forget this PC')),
                    ],
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

      final route = status.viaRemoteAddress ? ' via remote address' : '';
      return 'Connected$route${status.userName != null ? ' · ${status.userName}' : ''}';
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

/// Edits a paired PC's remote address.
///
/// Pops a record holding the new address, or holding null to remove it. Popping nothing means
/// the user cancelled, which is why the two cannot share a plain nullable result.
class _RemoteAddressDialog extends StatefulWidget {
  const _RemoteAddressDialog({required this.pc});

  final PairedPc pc;

  @override
  State<_RemoteAddressDialog> createState() => _RemoteAddressDialogState();
}

class _RemoteAddressDialogState extends State<_RemoteAddressDialog> {
  late final _controller = TextEditingController(text: widget.pc.remoteAddressLabel ?? '');

  String? _problem;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final pc = widget.pc;

    return AlertDialog(
      title: Text('Remote address for ${pc.deviceName}'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text(
            'Used when the PC cannot be found on this network, e.g. over a VPN such as Tailscale.',
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _controller,
            autofocus: true,
            autocorrect: false,
            enableSuggestions: false,
            keyboardType: TextInputType.url,
            textInputAction: TextInputAction.done,
            onChanged: (_) {
              if (_problem != null) setState(() => _problem = null);
            },
            onSubmitted: (_) => _save(),
            decoration: InputDecoration(
              labelText: 'Host name or IP address',
              hintText: '100.101.102.103',
              helperText: 'Add :port only if it is not ${pc.lastPort}.',
              errorText: _problem,
              errorMaxLines: 2,
            ),
          ),
        ],
      ),
      actions: [
        if (pc.remoteAddress != null)
          TextButton(
            onPressed: () => Navigator.of(context).pop((remote: null)),
            child: const Text('Remove'),
          ),
        TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Cancel')),
        FilledButton(onPressed: _save, child: const Text('Save')),
      ],
    );
  }

  void _save() {
    final remote = RemoteAddress.tryParse(_controller.text);

    if (remote == null) {
      setState(() {
        _problem = 'Enter a host name or IP address, optionally followed by :port.';
      });
      return;
    }

    Navigator.of(context).pop((remote: remote));
  }
}

class _RepairNotice extends StatelessWidget {
  const _RepairNotice({required this.message, this.onPairAgain});

  final String message;

  /// Starts pairing with the PC again. Null when there is no PC to pair with.
  final VoidCallback? onPairAgain;

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
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
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
          if (onPairAgain != null)
            Align(
              alignment: Alignment.centerRight,
              child: TextButton.icon(
                onPressed: onPairAgain,
                icon: const Icon(Icons.add_link),
                label: const Text('Pair again'),
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
