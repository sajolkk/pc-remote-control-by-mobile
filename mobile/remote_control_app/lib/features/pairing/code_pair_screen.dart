import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../data/control/control_client.dart';
import '../../data/identity/pairing_code.dart';
import '../../data/storage/paired_pc_store.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';

/// Pairs with a PC found on the network, without a QR code.
///
/// The phone connects, and both screens show the same six-digit code. The user checks the codes
/// match and clicks Allow on the PC. That comparison is what the QR code's fingerprint otherwise
/// provides: proof that the phone is talking to that PC and not to something in between. See
/// [PairingCode].
class CodePairScreen extends ConsumerStatefulWidget {
  const CodePairScreen({super.key, required this.beacon});

  /// The PC to pair with, as it answered discovery.
  final DiscoveryBeacon beacon;

  @override
  ConsumerState<CodePairScreen> createState() => _CodePairScreenState();
}

enum _Stage { connecting, waitingForApproval, success, failed }

class _CodePairScreenState extends ConsumerState<CodePairScreen> {
  _Stage _stage = _Stage.connecting;
  String? _code;
  String? _message;
  ControlClient? _client;

  @override
  void initState() {
    super.initState();
    _pair();
  }

  @override
  void dispose() {
    // Leaving the screen abandons the request; the PC's dialog then times out as a refusal.
    _client?.dispose();
    super.dispose();
  }

  Future<void> _pair() async {
    final beacon = widget.beacon;
    final identity = await ref.read(identityStoreProvider).getOrCreate();
    final client = ControlClient(identity: identity);
    _client = client;

    try {
      // No pinned fingerprint: there is no QR code to learn it from. The code comparison below
      // is what establishes trust instead, before anything is stored.
      await client.connect(host: beacon.address, port: beacon.port, expectedFingerprint: null);

      final pcFingerprint = client.serverFingerprint;
      if (pcFingerprint == null) {
        _fail('The PC did not present a certificate.');
        return;
      }

      if (!mounted) return;
      setState(() {
        _stage = _Stage.waitingForApproval;
        _code = PairingCode.toDisplayForm(PairingCode.compute(pcFingerprint, identity.fingerprint));
      });

      final device = await describeThisDevice();

      // An empty token asks the PC for code pairing.
      final response = await client.pair(
        pairingToken: '',
        deviceId: identity.deviceId,
        deviceName: device.name,
        platform: device.platform,
        model: device.model,
      );

      if (!response.ok) {
        _fail(_explain(response.error));
        return;
      }

      final result = response.map;
      final reportedFingerprint = result['fingerprint'] as String?;

      if (reportedFingerprint != null && reportedFingerprint != pcFingerprint) {
        _fail('The PC reported a different certificate than it connected with. Pairing was cancelled.');
        return;
      }

      final pc = PairedPc(
        deviceId: result['deviceId'] as String? ?? beacon.deviceId,
        deviceName: result['deviceName'] as String? ?? beacon.deviceName,
        fingerprint: pcFingerprint,
        pairedAt: DateTime.now(),
        lastAddress: beacon.address,
        lastPort: beacon.port,
        lastSeenAt: DateTime.now(),
        permissions:
            (result['permissions'] as List<dynamic>? ?? const []).whereType<String>().toList(),
      );

      await ref.read(pairedStoreProvider).save(pc);
      ref.invalidate(pairedPcsProvider);

      if (!mounted) return;
      setState(() {
        _stage = _Stage.success;
        _message = 'Connecting…';
      });

      // Straight on to a live connection, without a second tap.
      ref.read(connectionProvider).connect(pc).ignore();
      await Future<void>.delayed(const Duration(milliseconds: 1200));
      if (mounted) Navigator.of(context).pop(true);
    } on ControlChannelException catch (error) {
      _fail(error.message);
    } on Object catch (error) {
      _fail('Could not reach ${beacon.deviceName}: $error');
    } finally {
      await client.dispose();
      _client = null;
    }
  }

  String _explain(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.pairingDisabled =>
          'Pairing without a QR code is switched off on this PC. Use "Pair a PC" to scan its QR code instead.',
        ErrorCodes.pairingRejected => 'The request was declined on the PC.',
        ErrorCodes.pairingTimeout =>
          'Nobody clicked Allow on the PC in time. Somebody has to be signed in there to accept it.',
        ErrorCodes.rateLimited => error?.message ??
            'The PC is not accepting a new request right now. Wait a minute and try again.',
        _ => error?.message ?? 'The PC refused the request.',
      };

  void _fail(String message) {
    if (!mounted) return;
    setState(() {
      _stage = _Stage.failed;
      _message = message;
    });
  }

  @override
  Widget build(BuildContext context) {
    final name = widget.beacon.deviceName;

    return Scaffold(
      appBar: AppBar(title: Text('Pair with $name')),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(28),
          child: switch (_stage) {
            _Stage.connecting => _progress('Connecting to $name…'),
            _Stage.waitingForApproval => _waiting(context, name),
            _Stage.success => _result(
                context,
                icon: Icons.verified_user,
                title: 'Paired with $name',
                message: _message ??
                    'This phone can now view the screen. Other permissions can be enabled on the PC.',
                isError: false,
              ),
            _Stage.failed => _result(
                context,
                icon: Icons.error_outline,
                title: 'Pairing failed',
                message: _message ?? 'The PC refused the request.',
                isError: true,
              ),
          },
        ),
      ),
    );
  }

  Widget _progress(String title) => Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const CircularProgressIndicator(),
          const SizedBox(height: 22),
          Text(title, style: Theme.of(context).textTheme.titleMedium, textAlign: TextAlign.center),
        ],
      );

  Widget _waiting(BuildContext context, String name) {
    final theme = Theme.of(context);

    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Text('On $name, click Allow', style: theme.textTheme.titleLarge, textAlign: TextAlign.center),
        const SizedBox(height: 12),
        const Text(
          'Only allow it if the PC shows exactly this code:',
          textAlign: TextAlign.center,
        ),
        const SizedBox(height: 20),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 14),
          decoration: BoxDecoration(
            color: theme.colorScheme.surfaceContainerHighest,
            borderRadius: BorderRadius.circular(12),
          ),
          child: Text(
            _code ?? '',
            style: theme.textTheme.displaySmall?.copyWith(
              fontFamily: 'monospace',
              fontWeight: FontWeight.bold,
              letterSpacing: 2,
            ),
          ),
        ),
        const SizedBox(height: 24),
        const LinearProgressIndicator(),
        const SizedBox(height: 24),
        Text(
          'If the codes are different, click Decline on the PC and cancel here: something else on '
          'the network may be answering for your PC.',
          textAlign: TextAlign.center,
          style: theme.textTheme.bodySmall,
        ),
        const SizedBox(height: 12),
        TextButton(
          onPressed: () => Navigator.of(context).pop(false),
          child: const Text('Cancel'),
        ),
      ],
    );
  }

  Widget _result(
    BuildContext context, {
    required IconData icon,
    required String title,
    required String message,
    required bool isError,
  }) {
    final scheme = Theme.of(context).colorScheme;

    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Icon(icon, size: 56, color: isError ? scheme.error : scheme.primary),
        const SizedBox(height: 18),
        Text(title, style: Theme.of(context).textTheme.titleLarge, textAlign: TextAlign.center),
        const SizedBox(height: 12),
        Text(message, textAlign: TextAlign.center),
        const SizedBox(height: 28),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            if (isError)
              OutlinedButton(
                onPressed: () {
                  setState(() {
                    _stage = _Stage.connecting;
                    _message = null;
                    _code = null;
                  });
                  _pair();
                },
                child: const Text('Try again'),
              ),
            const SizedBox(width: 12),
            FilledButton(
              onPressed: () => Navigator.of(context).pop(!isError),
              child: Text(isError ? 'Close' : 'Done'),
            ),
          ],
        ),
      ],
    );
  }
}
