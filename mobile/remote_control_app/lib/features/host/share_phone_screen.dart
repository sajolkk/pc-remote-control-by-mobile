import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../../app/providers.dart';
import '../../host/host_controller.dart';
import '../../host/paired_viewer_store.dart';
import '../../protocol/protocol.dart';
import 'blocked_apps_screen.dart';
import 'pairing_approval_dialog.dart';

/// Lets another phone see and control this one.
///
/// The screen is the owner's view of host mode: a Start button that walks through the two things
/// Android requires (the accessibility service once, screen capture consent each time), the QR
/// code a viewer scans, the approval prompt when one pairs, who is connected right now, who is
/// paired, and Stop. Sharing itself lives in [HostController], so leaving this screen does not end
/// it; the notification does, or coming back here and tapping Stop.
class SharePhoneScreen extends ConsumerStatefulWidget {
  const SharePhoneScreen({super.key});

  @override
  ConsumerState<SharePhoneScreen> createState() => _SharePhoneScreenState();
}

class _SharePhoneScreenState extends ConsumerState<SharePhoneScreen> with WidgetsBindingObserver {
  PendingApproval? _shown;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      ref.read(hostControllerProvider).refreshAccessibility();
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Back from Settings → Accessibility: find out whether the service is on now.
    if (state == AppLifecycleState.resumed) ref.read(hostControllerProvider).refreshAccessibility();
  }

  @override
  Widget build(BuildContext context) {
    final host = ref.watch(hostControllerProvider);
    _maybeAsk(host);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Share this phone'),
        actions: [
          IconButton(
            tooltip: 'Blocked apps',
            icon: const Icon(Icons.block),
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(builder: (_) => const BlockedAppsScreen()),
            ),
          ),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(12),
        children: [
          _StatusCard(host: host),
          const SizedBox(height: 8),
          _AccessibilityCard(host: host),
          if (host.isSharing) ...[
            const SizedBox(height: 8),
            _QrCard(host: host),
            const SizedBox(height: 8),
            _ConnectedCard(host: host),
          ],
          const SizedBox(height: 8),
          _PairedCard(host: host),
          const SizedBox(height: 8),
          const _LimitsCard(),
        ],
      ),
    );
  }

  /// Shows the approval dialog once per pending request, and closes it if the request goes away.
  void _maybeAsk(HostController host) {
    final pending = host.pendingApproval;
    if (pending == _shown) return;
    _shown = pending;
    if (pending == null) return;

    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted) return;

      final decision = await showDialog<PairingApprovalChoice>(
        context: context,
        barrierDismissible: false,
        builder: (_) => PairingApprovalDialog(request: pending.request, pending: pending),
      );

      host.decide(approved: decision?.approved ?? false, fullControl: decision?.fullControl ?? false);
    });
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.host});

  final HostController host;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    final (IconData icon, String title, String detail) = switch (host.phase) {
      HostPhase.idle => (
          Icons.mobile_screen_share_outlined,
          'Not sharing',
          'Another phone with PC Remote can scan this phone\'s QR code, then see its screen and '
              'control it, exactly as it would a PC. You approve each new device here, and you can '
              'block apps it must never see.',
        ),
      HostPhase.starting => (Icons.hourglass_top, 'Starting…', host.message ?? ''),
      HostPhase.sharing => (
          Icons.mobile_screen_share,
          'Sharing as ${host.deviceName}',
          host.connectedViewers.isEmpty
              ? 'Waiting for a viewer. Scan the code below from the other phone.'
              : '${host.connectedViewers.length} device(s) connected.',
        ),
      HostPhase.failed => (Icons.error_outline, 'Could not start', host.message ?? ''),
    };

    return Card(
      color: host.phase == HostPhase.failed ? scheme.errorContainer : null,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, color: host.isSharing ? scheme.primary : null),
                const SizedBox(width: 12),
                Expanded(child: Text(title, style: theme.textTheme.titleMedium)),
              ],
            ),
            const SizedBox(height: 8),
            Text(detail, style: theme.textTheme.bodyMedium),
            if (host.isSharing && host.gate.isBlocked) ...[
              const SizedBox(height: 8),
              Row(
                children: [
                  Icon(Icons.block, size: 16, color: scheme.error),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      'A blocked app is in front: viewers see a placeholder and their touches are ignored.',
                      style: theme.textTheme.bodySmall?.copyWith(color: scheme.error),
                    ),
                  ),
                ],
              ),
            ],
            const SizedBox(height: 12),
            Row(
              children: [
                if (host.isSharing)
                  FilledButton.tonalIcon(
                    onPressed: host.stop,
                    icon: const Icon(Icons.stop),
                    label: const Text('Stop sharing'),
                  )
                else
                  FilledButton.icon(
                    onPressed: host.phase == HostPhase.starting ? null : host.start,
                    icon: const Icon(Icons.play_arrow),
                    label: const Text('Start sharing'),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _AccessibilityCard extends StatelessWidget {
  const _AccessibilityCard({required this.host});

  final HostController host;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final on = host.accessibilityEnabled;

    return Card(
      child: ListTile(
        leading: Icon(
          on ? Icons.check_circle : Icons.accessibility_new,
          color: on ? theme.colorScheme.primary : theme.colorScheme.error,
        ),
        title: Text(on ? 'Remote control is on' : 'Turn on remote control'),
        subtitle: Text(
          on
              ? 'Viewers can tap, swipe and type on this phone. Switch it off any time in Settings → '
                  'Accessibility.'
              : 'Viewers can only watch until you enable "PC Remote: remote control" in Settings → '
                  'Accessibility. Android asks for this once.',
        ),
        trailing: on ? null : const Icon(Icons.chevron_right),
        onTap: () => on ? host.openAccessibilitySettings() : _disclose(context, host),
      ),
    );
  }

  /// Says plainly what the accessibility service is for before Android's own settings page does,
  /// as Google Play requires of an app that uses one for something other than accessibility.
  Future<void> _disclose(BuildContext context, HostController host) async {
    final go = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: const Text('Remote control uses an accessibility service'),
        content: const SingleChildScrollView(
          child: Text(
            "To let a paired phone tap, swipe and type on this one, PC Remote needs Android's "
            'accessibility service "PC Remote: remote control".\n\n'
            'What it does: performs the touches and typing a viewer sends while you are sharing, '
            'reads which app is in front so your blocked apps stay hidden, and reads the text of '
            'the field being typed into so that typing lands at the cursor.\n\n'
            'What it never does: act while you are not sharing, touch an app you have blocked, '
            'or send anything about your screen to anyone but the viewer you approved.\n\n'
            'On the next page, turn on "PC Remote: remote control". You can turn it off again at '
            'any time.',
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.of(dialogContext).pop(false), child: const Text('Not now')),
          FilledButton(onPressed: () => Navigator.of(dialogContext).pop(true), child: const Text('Open settings')),
        ],
      ),
    );

    if (go == true) await host.openAccessibilitySettings();
  }
}

class _QrCard extends StatelessWidget {
  const _QrCard({required this.host});

  final HostController host;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final payload = host.qrPayload;
    final window = host.pairingWindow;
    final identity = host.identity;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          children: [
            Text('Scan from the other phone', style: theme.textTheme.titleSmall),
            const SizedBox(height: 4),
            Text(
              'Devices → Pair a PC → Scan QR code. The code works once and expires in '
              '${window == null ? 'a few minutes' : _remaining(window.expiresAt)}.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall,
            ),
            const SizedBox(height: 12),
            if (payload != null)
              Container(
                color: Colors.white,
                padding: const EdgeInsets.all(8),
                child: QrImageView(data: payload, size: 220, backgroundColor: Colors.white),
              )
            else
              Padding(
                padding: const EdgeInsets.all(24),
                child: Column(
                  children: [
                    const Icon(Icons.timer_off_outlined, size: 40),
                    const SizedBox(height: 8),
                    const Text('The code expired or was used.'),
                    const SizedBox(height: 8),
                    FilledButton.tonal(onPressed: host.newPairingCode, child: const Text('New code')),
                  ],
                ),
              ),
            const SizedBox(height: 12),
            Text(
              '${host.addresses.join(', ')} · port ${host.port}',
              style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurfaceVariant),
              textAlign: TextAlign.center,
            ),
            if (identity != null) ...[
              const SizedBox(height: 4),
              Text(
                'Fingerprint ${identity.fingerprintShort}',
                style: const TextStyle(fontFamily: 'monospace', fontSize: 12),
              ),
            ],
            if (payload != null)
              TextButton.icon(
                onPressed: host.newPairingCode,
                icon: const Icon(Icons.refresh, size: 16),
                label: const Text('New code'),
              ),
          ],
        ),
      ),
    );
  }

  static String _remaining(DateTime until) {
    final left = until.difference(DateTime.now().toUtc());
    if (left.isNegative) return 'a moment';
    if (left.inMinutes >= 1) return '${left.inMinutes} min';
    return '${left.inSeconds} s';
  }
}

class _ConnectedCard extends StatelessWidget {
  const _ConnectedCard({required this.host});

  final HostController host;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final viewers = host.connectedViewers;

    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
              child: Text('Connected now', style: theme.textTheme.titleSmall),
            ),
            if (viewers.isEmpty)
              const Padding(
                padding: EdgeInsets.fromLTRB(16, 4, 16, 8),
                child: Text('Nobody is connected.'),
              ),
            for (final viewer in viewers)
              ListTile(
                leading: Icon(viewer.streaming ? Icons.cast_connected : Icons.smartphone),
                title: Text(viewer.name),
                subtitle: Text(
                  '${viewer.address} · ${viewer.authenticated ? (viewer.streaming ? 'watching' : 'connected') : 'pairing'}',
                ),
                trailing: TextButton(
                  onPressed: () => host.disconnectViewer(viewer.connectionId),
                  child: const Text('Disconnect'),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _PairedCard extends StatelessWidget {
  const _PairedCard({required this.host});

  final HostController host;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final paired = host.pairedViewers;

    return Card(
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 4),
              child: Text('Paired devices', style: theme.textTheme.titleSmall),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 4),
              child: Text(
                'These devices may connect whenever this phone is sharing. Changes apply at once.',
                style: theme.textTheme.bodySmall,
              ),
            ),
            if (paired.isEmpty)
              const Padding(
                padding: EdgeInsets.fromLTRB(16, 4, 16, 8),
                child: Text('No device has paired with this phone yet.'),
              ),
            for (final viewer in paired) _PairedViewerTile(viewer: viewer, host: host),
          ],
        ),
      ),
    );
  }
}

class _PairedViewerTile extends StatelessWidget {
  const _PairedViewerTile({required this.viewer, required this.host});

  final PairedViewer viewer;
  final HostController host;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return ExpansionTile(
      leading: Icon(viewer.blocked ? Icons.phonelink_off : Icons.phonelink),
      title: Text(viewer.deviceName),
      subtitle: Text(
        viewer.blocked
            ? 'Disconnected · will not be let in until allowed again'
            : '${viewer.model.isEmpty ? viewer.platform : viewer.model} · paired ${_ago(viewer.pairedAt)}',
      ),
      childrenPadding: const EdgeInsets.only(left: 16, right: 8, bottom: 8),
      children: [
        for (final permission in PairedViewer.allPermissions)
          SwitchListTile(
            dense: true,
            title: Text(_label(permission)),
            value: viewer.granted(permission),
            onChanged: (value) => host.setViewerPermission(viewer.deviceId, permission, value),
          ),
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            TextButton(
              onPressed: () => host.setViewerBlocked(viewer.deviceId, !viewer.blocked),
              child: Text(viewer.blocked ? 'Allow again' : 'Disconnect'),
            ),
            TextButton(
              style: TextButton.styleFrom(foregroundColor: theme.colorScheme.error),
              onPressed: () => host.forgetViewer(viewer.deviceId),
              child: const Text('Forget'),
            ),
          ],
        ),
      ],
    );
  }

  static String _label(String permission) => switch (permission) {
        Permissions.viewScreen => 'See the screen',
        Permissions.controlInput => 'Touch and type',
        Permissions.launchApps => 'Open apps',
        Permissions.powerControls => 'Lock the phone',
        _ => Permissions.label(permission),
      };

  static String _ago(DateTime time) {
    final difference = DateTime.now().toUtc().difference(time.toUtc());
    if (difference.inMinutes < 1) return 'just now';
    if (difference.inHours < 1) return '${difference.inMinutes}m ago';
    if (difference.inDays < 1) return '${difference.inHours}h ago';
    return '${difference.inDays}d ago';
  }
}

class _LimitsCard extends StatelessWidget {
  const _LimitsCard();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('What viewers cannot do', style: theme.textTheme.titleSmall),
            const SizedBox(height: 6),
            Text(
              '• See apps that hide themselves from screen capture (banking apps, private browsing): '
              'Android shows them black.\n'
              '• See or touch any app you have blocked: they get a placeholder instead.\n'
              '• Unlock this phone, or switch it off. They can lock it.\n'
              '• Keep watching after you stop: sharing ends when you tap Stop, from here or from the '
              'notification.',
              style: theme.textTheme.bodySmall,
            ),
          ],
        ),
      ),
    );
  }
}
