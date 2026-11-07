import 'package:flutter/material.dart';

import '../../host/host_controller.dart';
import '../../host/host_pairing.dart';

/// The owner's answer, as the dialog pops it.
class PairingApprovalChoice {
  const PairingApprovalChoice({required this.approved, this.fullControl = false});

  final bool approved;
  final bool fullControl;
}

/// Asks the owner whether a device may pair: the phone's version of the PC's approval dialog.
///
/// Shows what the PC shows, so the owner can make the same check: the device's name, where it
/// connected from, its certificate fingerprint (to compare with the Settings tab of the other
/// phone) and, for pairing without a QR code, the six-digit code both screens must agree on.
class PairingApprovalDialog extends StatefulWidget {
  const PairingApprovalDialog({super.key, required this.request, required this.pending});

  final PairingApprovalRequest request;

  /// Closes the dialog by itself if the request times out or sharing stops.
  final PendingApproval pending;

  @override
  State<PairingApprovalDialog> createState() => _PairingApprovalDialogState();
}

class _PairingApprovalDialogState extends State<PairingApprovalDialog> {
  bool _fullControl = false;

  @override
  void initState() {
    super.initState();
    widget.pending.completer.future.whenComplete(() {
      if (mounted) Navigator.of(context).maybePop();
    });
  }

  @override
  Widget build(BuildContext context) {
    final request = widget.request;
    final theme = Theme.of(context);
    final code = request.comparisonCode;

    return AlertDialog(
      title: const Text('Allow this device to control your phone?'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _row(theme, 'Device', request.deviceName.isEmpty ? 'Unnamed device' : request.deviceName),
            if (request.model.isNotEmpty) _row(theme, 'Model', request.model),
            _row(theme, 'From', request.remoteAddress),
            _row(theme, 'Fingerprint', request.fingerprintShort, mono: true),
            if (code != null) ...[
              const SizedBox(height: 12),
              Text('Only allow it if the other phone shows exactly this code:', style: theme.textTheme.bodySmall),
              const SizedBox(height: 6),
              Center(
                child: Text(
                  code,
                  style: theme.textTheme.headlineMedium?.copyWith(
                    fontFeatures: const [FontFeature.tabularFigures()],
                    letterSpacing: 2,
                  ),
                ),
              ),
            ] else ...[
              const SizedBox(height: 12),
              Text(
                'Check that the fingerprint matches the one on the other phone\'s Settings tab. If it '
                'does not, decline: something else is asking.',
                style: theme.textTheme.bodySmall,
              ),
            ],
            const SizedBox(height: 8),
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              dense: true,
              value: _fullControl,
              onChanged: (value) => setState(() => _fullControl = value ?? false),
              title: const Text('Also let it open apps and lock this phone'),
              subtitle: const Text('Otherwise it can see the screen, tap and type only.'),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(const PairingApprovalChoice(approved: false)),
          child: const Text('Decline'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(
            PairingApprovalChoice(approved: true, fullControl: _fullControl),
          ),
          child: const Text('Allow'),
        ),
      ],
    );
  }

  Widget _row(ThemeData theme, String label, String value, {bool mono = false}) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(
              width: 88,
              child: Text(label, style: theme.textTheme.labelMedium?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
            ),
            Expanded(
              child: Text(
                value,
                style: mono ? const TextStyle(fontFamily: 'monospace') : theme.textTheme.bodyMedium,
              ),
            ),
          ],
        ),
      );
}
