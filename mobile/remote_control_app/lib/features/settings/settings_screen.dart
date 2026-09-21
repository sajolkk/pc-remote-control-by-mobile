import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../protocol/protocol.dart';

/// This device's identity, and what it can do on each PC.
///
/// The fingerprint is shown prominently because it is the one value a cautious user can
/// compare against what the PC displays when approving a pairing. Making it easy to find is
/// the difference between that check being possible and being theoretical.
class SettingsScreen extends ConsumerWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final identity = ref.watch(identityProvider);
    final status = ref.watch(connectionProvider).status;

    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('This device', style: Theme.of(context).textTheme.titleSmall),
                  const SizedBox(height: 12),
                  identity.when(
                    loading: () => const Text('Preparing this device\'s identity…'),
                    error: (error, _) => Text(
                      'The device identity could not be created: $error',
                      style: TextStyle(color: Theme.of(context).colorScheme.error),
                    ),
                    data: (value) => Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          'Certificate fingerprint',
                          style: Theme.of(context).textTheme.labelSmall?.copyWith(
                                color: Theme.of(context).colorScheme.onSurfaceVariant,
                              ),
                        ),
                        const SizedBox(height: 4),
                        SelectableText(
                          value.fingerprintShort,
                          style: const TextStyle(fontFamily: 'monospace', fontSize: 15),
                        ),
                        const SizedBox(height: 12),
                        Text(
                          'Your PC shows this value when you approve a pairing. If it does not '
                          'match, decline — something else is asking for access.',
                          style: Theme.of(context).textTheme.bodySmall,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),

          if (status.isConnected)
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Permissions on ${status.pc?.deviceName ?? 'this PC'}',
                      style: Theme.of(context).textTheme.titleSmall,
                    ),
                    const SizedBox(height: 4),
                    Text(
                      'Permissions are granted on the PC, not here. That is deliberate: a phone '
                      'should not be able to widen its own access.',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                    const SizedBox(height: 12),
                    for (final permission in const [
                      Permissions.viewScreen,
                      Permissions.controlInput,
                      Permissions.launchApps,
                      Permissions.manageFiles,
                      Permissions.powerControls,
                      Permissions.clipboard,
                    ])
                      Padding(
                        padding: const EdgeInsets.symmetric(vertical: 2),
                        child: Row(
                          children: [
                            Icon(
                              status.granted(permission)
                                  ? Icons.check_circle
                                  : Icons.remove_circle_outline,
                              size: 18,
                              color: status.granted(permission)
                                  ? Theme.of(context).colorScheme.primary
                                  : Theme.of(context).disabledColor,
                            ),
                            const SizedBox(width: 10),
                            Text(Permissions.label(permission)),
                          ],
                        ),
                      ),
                  ],
                ),
              ),
            ),

          if (status.isConnected && status.capabilities.isNotEmpty)
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('What this PC supports', style: Theme.of(context).textTheme.titleSmall),
                    const SizedBox(height: 4),
                    Text(
                      'Reported by the PC itself. Features it cannot provide are hidden rather '
                      'than shown and then failing.',
                      style: Theme.of(context).textTheme.bodySmall,
                    ),
                    const SizedBox(height: 10),
                    Wrap(
                      spacing: 6,
                      runSpacing: 6,
                      children: [
                        for (final capability in status.capabilities)
                          Chip(
                            label: Text(_capabilityLabel(capability)),
                            visualDensity: VisualDensity.compact,
                          ),
                      ],
                    ),
                  ],
                ),
              ),
            ),

          Card(
            child: Column(
              children: [
                ListTile(
                  leading: const Icon(Icons.info_outline),
                  title: const Text('About remote unlock'),
                  subtitle: const Text('Why the PC cannot be unlocked from here'),
                  onTap: () => _showUnlockExplanation(context),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: Icon(Icons.delete_forever, color: Theme.of(context).colorScheme.error),
                  title: const Text('Reset this device'),
                  subtitle: const Text('Forget every PC and create a new identity'),
                  onTap: () => _reset(context, ref),
                ),
              ],
            ),
          ),

          const SizedBox(height: 16),
          Center(
            child: Text(
              'PC Remote 0.2.0 · LAN only',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
            ),
          ),
        ],
      ),
    );
  }

  static String _capabilityLabel(String capability) => switch (capability) {
        Capabilities.screenCapture => 'Screen view',
        Capabilities.multiMonitor => 'Multiple monitors',
        Capabilities.input => 'Mouse and keyboard',
        Capabilities.apps => 'Apps',
        Capabilities.browser => 'Browser',
        Capabilities.clipboard => 'Clipboard',
        Capabilities.files => 'Files',
        Capabilities.volume => 'Volume',
        Capabilities.powerSleep => 'Sleep',
        Capabilities.powerShutdown => 'Shut down / restart',
        Capabilities.powerSignOut => 'Sign out',
        Capabilities.wakeOnLan => 'Wake on LAN',
        Capabilities.sessionAgent => 'Desktop agent running',
        _ => capability,
      };

  /// Explains a limitation rather than leaving the user to wonder whether it is a bug.
  void _showUnlockExplanation(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Remote unlock'),
        content: const SingleChildScrollView(
          child: Text(
            'This app can lock your PC, but it cannot unlock it.\n\n'
            'Windows puts the lock screen on a separate, protected desktop that no ordinary '
            'application can draw on or send input to. That is a deliberate security boundary: '
            'it is what stops any program on your PC from capturing your password.\n\n'
            'The only sanctioned way to sign in from another device is a Windows Credential '
            'Provider — a component loaded by the Windows logon screen itself. Building one is a '
            'separate, higher-risk project, and this app deliberately does not attempt it.\n\n'
            'What this app will never do: store your Windows password, or type it for you. Both '
            'would defeat the protection above rather than work with it.\n\n'
            'So: lock, sleep, restart, shut down and wake all work remotely. Unlocking needs you '
            'at the PC.',
          ),
        ),
        actions: [
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('Got it'),
          ),
        ],
      ),
    );
  }

  Future<void> _reset(BuildContext context, WidgetRef ref) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Reset this device?'),
        content: const Text(
          'This forgets every paired PC and creates a new identity for this device.\n\n'
          'Every PC will refuse this device until you pair again, and the old pairings will '
          'remain listed on those PCs until you revoke them there.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Reset'),
          ),
        ],
      ),
    );

    if (confirmed != true) return;

    await ref.read(connectionProvider).disconnect();
    await ref.read(pairedStoreProvider).clear();
    await ref.read(identityStoreProvider).reset();

    ref.invalidate(identityProvider);
    ref.invalidate(pairedPcsProvider);

    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('This device has been reset.')),
      );
    }
  }
}
