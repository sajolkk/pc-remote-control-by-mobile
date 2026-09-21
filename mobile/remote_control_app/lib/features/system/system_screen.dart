import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';

/// PC status and power controls.
///
/// Every control here is rendered from what the PC reported, not from what the protocol can
/// express: a machine that cannot sleep shows no sleep button, and a device without the
/// `PowerControls` permission sees the section explained rather than silently broken. That is
/// what lets one app build serve any PC honestly.
class SystemScreen extends ConsumerStatefulWidget {
  const SystemScreen({super.key});

  @override
  ConsumerState<SystemScreen> createState() => _SystemScreenState();
}

class _SystemScreenState extends ConsumerState<SystemScreen> {
  Map<String, dynamic>? _deviceInfo;
  bool _busy = false;

  @override
  Widget build(BuildContext context) {
    final connection = ref.watch(connectionProvider);
    final status = connection.status;

    if (!status.isConnected) {
      return const _NotConnected();
    }

    final hasPower = status.granted(Permissions.powerControls);

    return Scaffold(
      appBar: AppBar(
        title: Text(status.pc?.deviceName ?? 'System'),
        actions: [
          IconButton(
            tooltip: 'Refresh',
            onPressed: _busy ? null : _refresh,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          _StatusCard(status: status, info: _deviceInfo),

          if (status.isLocked)
            const _Notice(
              icon: Icons.lock_outline,
              title: 'The PC is locked',
              message:
                  'Windows protects the lock screen, so the desktop cannot be viewed or controlled '
                  'while it is locked. Unlocking has to happen at the PC itself — this is a Windows '
                  'security boundary, not a limitation of this app. Power controls still work.',
            ),

          if (status.isLoggedOut)
            const _Notice(
              icon: Icons.person_off_outlined,
              title: 'Nobody is signed in',
              message:
                  'Screen, input, apps and clipboard need a signed-in user. Power controls and '
                  'status still work.',
            ),

          const SizedBox(height: 8),
          _SectionTitle('Power'),

          if (!hasPower)
            const _Notice(
              icon: Icons.lock_person_outlined,
              title: 'Power controls are not enabled for this device',
              message:
                  'Open PC-Remote on the PC, find this device under Paired Devices, and turn on '
                  'Power controls.',
            )
          else
            Column(
              children: [
                _ActionTile(
                  icon: Icons.lock_outline,
                  title: 'Lock',
                  subtitle: 'Show the lock screen',
                  enabled: !_busy,
                  onTap: () => _run(Commands.systemLock, 'Locked the PC.'),
                ),
                if (status.has(Capabilities.powerSignOut))
                  _ActionTile(
                    icon: Icons.logout,
                    title: 'Sign out',
                    subtitle: 'Close the session and return to the logon screen',
                    enabled: !_busy && !status.isLoggedOut,
                    destructive: true,
                    onTap: () => _confirmAndRun(
                      title: 'Sign out?',
                      message: 'Unsaved work in open apps may be lost.',
                      command: Commands.systemSignOut,
                      success: 'Signing out.',
                    ),
                  ),
                if (status.has(Capabilities.powerSleep))
                  _ActionTile(
                    icon: Icons.bedtime_outlined,
                    title: 'Sleep',
                    subtitle: status.has(Capabilities.wakeOnLan)
                        ? 'This PC can be woken from sleep'
                        : 'Waking may require pressing a key at the PC',
                    enabled: !_busy,
                    onTap: () => _run(Commands.systemSleep, 'Going to sleep.'),
                  ),
                if (status.has(Capabilities.powerShutdown)) ...[
                  _ActionTile(
                    icon: Icons.restart_alt,
                    title: 'Restart',
                    subtitle: 'With a short countdown you can cancel',
                    enabled: !_busy,
                    destructive: true,
                    onTap: () => _confirmAndRun(
                      title: 'Restart the PC?',
                      message:
                          'Windows will warn on screen first and you can cancel during the countdown.',
                      command: Commands.systemRestart,
                      args: const {'delaySec': 15},
                      success: 'Restart scheduled. You can cancel it below.',
                    ),
                  ),
                  _ActionTile(
                    icon: Icons.power_settings_new,
                    title: 'Shut down',
                    subtitle: 'With a short countdown you can cancel',
                    enabled: !_busy,
                    destructive: true,
                    onTap: () => _confirmAndRun(
                      title: 'Shut down the PC?',
                      message:
                          'Windows will warn on screen first and you can cancel during the countdown.'
                          '\n\nIf Fast Startup is enabled, a shut-down PC usually cannot be woken '
                          'remotely — use Sleep instead if you want to wake it later.',
                      command: Commands.systemShutdown,
                      args: const {'delaySec': 15},
                      success: 'Shutdown scheduled. You can cancel it below.',
                    ),
                  ),
                  _ActionTile(
                    icon: Icons.cancel_outlined,
                    title: 'Cancel pending shutdown',
                    subtitle: 'Stops a restart or shutdown still in its countdown',
                    enabled: !_busy,
                    onTap: () => _run(Commands.systemAbortShutdown, 'Cancelled.'),
                  ),
                ],
              ],
            ),

          const SizedBox(height: 24),
          Center(
            child: TextButton.icon(
              onPressed: () => ref.read(connectionProvider).disconnect(),
              icon: const Icon(Icons.link_off),
              label: const Text('Disconnect'),
            ),
          ),
        ],
      ),
    );
  }

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _refresh());
  }

  Future<void> _refresh() async {
    final connection = ref.read(connectionProvider);
    if (!connection.status.isConnected) return;

    await connection.refreshState();

    final response = await connection.send(Commands.deviceInfo);
    if (response != null && response.ok && mounted) {
      setState(() => _deviceInfo = response.map);
    }
  }

  Future<void> _confirmAndRun({
    required String title,
    required String message,
    required String command,
    required String success,
    Map<String, dynamic>? args,
  }) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(title),
        content: Text(message),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Continue'),
          ),
        ],
      ),
    );

    if (confirmed == true) {
      await _run(command, success, args: args);
    }
  }

  Future<void> _run(String command, String success, {Map<String, dynamic>? args}) async {
    setState(() => _busy = true);

    try {
      final response = await ref.read(connectionProvider).send(command, args: args);

      if (!mounted) return;

      if (response == null) {
        _toast('Not connected to the PC.');
        return;
      }

      if (response.ok) {
        _toast(success);
        await ref.read(connectionProvider).refreshState();
        return;
      }

      _toast(_explain(response.error));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Turns an error code into something a person can act on.
  String _explain(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.permissionDenied =>
          'This device does not have permission for that. Enable it on the PC.',
        ErrorCodes.policyDenied => error?.message ?? 'That action is disabled on the PC.',
        ErrorCodes.sessionUnavailable =>
          'Nobody is signed in to the PC, so that is unavailable right now.',
        ErrorCodes.notSupported => error?.message ?? 'This PC cannot do that.',
        ErrorCodes.notFound => error?.message ?? 'Nothing to do.',
        _ => error?.message ?? 'The command failed.',
      };

  void _toast(String message) {
    ScaffoldMessenger.of(context)
      ..clearSnackBars()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.status, this.info});

  final ConnectionStatus status;
  final Map<String, dynamic>? info;

  @override
  Widget build(BuildContext context) {
    final rows = <(String, String)>[
      ('Session', _sessionLabel(status.sessionState)),
      if (status.userName != null) ('Signed in', status.userName!),
      ('Desktop agent', status.sessionAgentConnected ? 'Connected' : 'Not running'),
      if (info?['osVersion'] != null) ('Windows', info!['osVersion'] as String),
      if (info?['hostName'] != null) ('Host name', info!['hostName'] as String),
      if (info?['architecture'] != null) ('Architecture', info!['architecture'] as String),
      if (info?['cpuCount'] != null) ('Processors', '${info!['cpuCount']}'),
      if (info?['totalMemoryMb'] != null && (info!['totalMemoryMb'] as int) > 0)
        ('Memory', '${((info!['totalMemoryMb'] as int) / 1024).toStringAsFixed(1)} GB'),
      if (info?['tlsVersion'] != null) ('Connection', info!['tlsVersion'] as String),
      if (info?['localAddresses'] is List && (info!['localAddresses'] as List).isNotEmpty)
        ('Address', (info!['localAddresses'] as List).join(', ')),
      if (info?['agentVersion'] != null) ('Agent version', info!['agentVersion'] as String),
    ];

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (final (label, value) in rows)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 3),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    SizedBox(
                      width: 120,
                      child: Text(
                        label,
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                              color: Theme.of(context).colorScheme.onSurfaceVariant,
                            ),
                      ),
                    ),
                    Expanded(
                      child: Text(value, style: Theme.of(context).textTheme.bodyMedium),
                    ),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }

  static String _sessionLabel(String state) => switch (state) {
        'Active' => 'Active',
        'Locked' => 'Locked',
        'LoggedOut' => 'Nobody signed in',
        _ => 'Unknown',
      };
}

class _ActionTile extends StatelessWidget {
  const _ActionTile({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.enabled,
    required this.onTap,
    this.destructive = false,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final bool enabled;
  final VoidCallback onTap;
  final bool destructive;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Card(
      margin: const EdgeInsets.symmetric(vertical: 4),
      child: ListTile(
        leading: Icon(icon, color: destructive ? scheme.error : null),
        title: Text(title),
        subtitle: Text(subtitle),
        enabled: enabled,
        onTap: enabled ? onTap : null,
        trailing: const Icon(Icons.chevron_right),
      ),
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle(this.title);

  final String title;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(4, 12, 4, 6),
        child: Text(title, style: Theme.of(context).textTheme.titleSmall),
      );
}

class _Notice extends StatelessWidget {
  const _Notice({required this.icon, required this.title, required this.message});

  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Container(
      margin: const EdgeInsets.symmetric(vertical: 8),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: scheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 20, color: scheme.onSurfaceVariant),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 4),
                Text(message, style: Theme.of(context).textTheme.bodySmall),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Shown on every feature screen when there is no live connection.
class _NotConnected extends StatelessWidget {
  const _NotConnected();

  @override
  Widget build(BuildContext context) => Scaffold(
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(
                  Icons.link_off,
                  size: 44,
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
                const SizedBox(height: 16),
                Text('Not connected', style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: 8),
                Text(
                  'Choose a PC on the Devices tab to connect.',
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
            ),
          ),
        ),
      );
}

/// Exported so other feature screens show the same empty state.
class NotConnectedView extends StatelessWidget {
  const NotConnectedView({super.key});

  @override
  Widget build(BuildContext context) => const _NotConnected();
}
