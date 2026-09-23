import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../app/providers.dart';
import '../../data/control/control_client.dart';
import '../../data/identity/device_identity.dart';
import '../../data/storage/paired_pc_store.dart';
import '../../domain/connection_controller.dart';
import '../../protocol/protocol.dart';

/// Pairs this device with a PC by scanning its QR code.
///
/// ## What the QR code does and does not contain
///
/// It carries the PC's device id, display name, certificate fingerprint, address hints, and a
/// single-use pairing token. No password, no long-term secret, and no permission grant. The
/// fingerprint is public information whose only job is to close the trust-on-first-use window:
/// because this app learns the PC's certificate before it ever connects, there is no moment at
/// which a device on the same network could impersonate the PC.
///
/// ## Why the token alone is not enough
///
/// Somebody who photographed the QR code still cannot pair, because the PC also requires a
/// human to approve the request on its own screen. That is why this screen waits — sometimes
/// for many seconds — after submitting the token: it is waiting for a person.
class PairScreen extends ConsumerStatefulWidget {
  const PairScreen({super.key});

  @override
  ConsumerState<PairScreen> createState() => _PairScreenState();
}

enum _Stage { scanning, connecting, waitingForApproval, success, failed }

class _PairScreenState extends ConsumerState<PairScreen> {
  final MobileScannerController _scanner = MobileScannerController(
    detectionSpeed: DetectionSpeed.noDuplicates,
    formats: const [BarcodeFormat.qrCode],
  );

  _Stage _stage = _Stage.scanning;
  String? _message;
  String? _pcName;
  bool _handling = false;

  @override
  void dispose() {
    _scanner.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Pair a PC')),
      body: switch (_stage) {
        _Stage.scanning => _buildScanner(),
        _Stage.connecting => _buildProgress('Connecting to the PC…'),
        _Stage.waitingForApproval => _buildProgress(
            'Waiting for approval on ${_pcName ?? 'the PC'}…',
            detail:
                'Go to the PC and choose Allow. Check that the fingerprint shown there matches '
                'the one below.',
          ),
        _Stage.success => _buildResult(
            icon: Icons.verified_user,
            title: 'Paired with ${_pcName ?? 'your PC'}',
            message: _message ??
                'This device can now view the screen and control input. Other permissions can be '
                'enabled on the PC.',
            isError: false,
          ),
        _Stage.failed => _buildResult(
            icon: Icons.error_outline,
            title: 'Pairing failed',
            message: _message ?? 'The PC refused the request.',
            isError: true,
          ),
      },
    );
  }

  Widget _buildScanner() => Stack(
        children: [
          MobileScanner(controller: _scanner, onDetect: _onDetect),
          Positioned(
            left: 0,
            right: 0,
            bottom: 0,
            child: Container(
              color: Colors.black.withValues(alpha: 0.65),
              padding: const EdgeInsets.all(20),
              child: SafeArea(
                top: false,
                child: Column(
                  children: [
                    const Text(
                      'Scan the QR code shown by PC-Remote on your PC',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: Colors.white, fontSize: 16),
                    ),
                    const SizedBox(height: 8),
                    Text(
                      'On the PC: open PC-Remote from the Start menu to show the QR code.',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: Colors.white.withValues(alpha: 0.75), fontSize: 13),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ],
      );

  Widget _buildProgress(String title, {String? detail}) => Center(
        child: Padding(
          padding: const EdgeInsets.all(28),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              const CircularProgressIndicator(),
              const SizedBox(height: 22),
              Text(title, style: Theme.of(context).textTheme.titleMedium, textAlign: TextAlign.center),
              if (detail != null) ...[
                const SizedBox(height: 10),
                Text(
                  detail,
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ],
              if (_fingerprint != null) ...[
                const SizedBox(height: 18),
                _FingerprintChip(fingerprint: _fingerprint!),
              ],
            ],
          ),
        ),
      );

  Widget _buildResult({
    required IconData icon,
    required String title,
    required String message,
    required bool isError,
  }) {
    final scheme = Theme.of(context).colorScheme;

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: Column(
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
                    onPressed: () => setState(() {
                      _stage = _Stage.scanning;
                      _message = null;
                      _handling = false;
                    }),
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
        ),
      ),
    );
  }

  String? _fingerprint;

  Future<void> _onDetect(BarcodeCapture capture) async {
    // The scanner fires repeatedly while the code is in frame; one pairing attempt is enough.
    if (_handling) return;

    final raw = capture.barcodes
        .map((barcode) => barcode.rawValue)
        .whereType<String>()
        .firstOrNull;

    if (raw == null) return;

    final payload = PairingQrPayload.tryParse(raw);
    if (payload == null) {
      // Not our QR code. Left scanning rather than shown as an error, because the camera will
      // pick up all sorts of codes while the user lines up the right one.
      return;
    }

    _handling = true;
    await _scanner.stop();

    setState(() {
      _stage = _Stage.connecting;
      _pcName = payload.deviceName;
      _fingerprint = DeviceIdentityStore.shortFingerprint(payload.fingerprint);
    });

    await _pair(payload);
  }

  Future<void> _pair(PairingQrPayload payload) async {
    final identity = await ref.read(identityStoreProvider).getOrCreate();
    final client = ControlClient(identity: identity);

    try {
      final endpoint = await _connectDirect(client, payload);
      if (endpoint == null) {
        _fail(
          'Could not reach ${payload.deviceName}. Check that the phone and the PC are on the '
          'same Wi-Fi network.',
        );
        return;
      }

      if (!mounted) return;
      setState(() => _stage = _Stage.waitingForApproval);

      final device = await describeThisDevice();

      final response = await client.pair(
        pairingToken: payload.token,
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

      // The PC echoes its own fingerprint. If it does not match what the QR code claimed,
      // something substituted one of the two and the pairing must not be stored.
      if (reportedFingerprint != null && reportedFingerprint != payload.fingerprint) {
        _fail(
          'The PC reported a different certificate than its QR code. Pairing was cancelled.',
        );
        return;
      }

      final pc = PairedPc(
        deviceId: result['deviceId'] as String? ?? payload.deviceId,
        deviceName: result['deviceName'] as String? ?? payload.deviceName,
        fingerprint: payload.fingerprint,
        pairedAt: DateTime.now(),
        lastAddress: endpoint,
        lastPort: payload.port,
        lastSeenAt: DateTime.now(),
        permissions:
            (result['permissions'] as List<dynamic>? ?? const []).whereType<String>().toList(),
      );

      await ref.read(pairedStoreProvider).save(pc);
      ref.invalidate(pairedPcsProvider);

      if (!mounted) return;

      setState(() {
        _stage = _Stage.success;
        _pcName = pc.deviceName;
        _message = 'Connecting…';
      });

      // Straight on to a live connection: no second tap, and no search, because the address
      // that just worked is remembered.
      ref.read(connectionProvider).connect(pc).ignore();
      await Future<void>.delayed(const Duration(milliseconds: 1200));
      if (mounted) Navigator.of(context).pop(true);
    } on CertificatePinMismatch catch (error) {
      _fail(error.toString());
    } on ControlChannelException catch (error) {
      _fail(error.message);
    } on Object catch (error) {
      _fail('Pairing failed: $error');
    } finally {
      await client.dispose();
    }
  }

  /// Connects straight to the addresses in the QR code, with no network search.
  ///
  /// Returns the address that worked. Only when none of them answers does it fall back to
  /// searching for the PC by its id: the QR code may be from before the PC changed networks.
  /// Every attempt is pinned to the QR code's fingerprint, so trying several addresses can
  /// never connect to the wrong machine.
  Future<String?> _connectDirect(ControlClient client, PairingQrPayload payload) async {
    for (final host in payload.hosts) {
      try {
        await client.connect(
          host: host,
          port: payload.port,
          expectedFingerprint: payload.fingerprint,
          timeout: const Duration(seconds: 3),
        );
        return host;
      } on CertificatePinMismatch {
        // Another device answers at this address. Try the next one.
      } on Object {
        // Not reachable from here, e.g. an address on the PC's other network adapter.
      }
    }

    try {
      await for (final beacon in ref.read(discoveryProvider).scan(duration: const Duration(seconds: 4))) {
        if (beacon.deviceId != payload.deviceId) continue;

        await client.connect(host: beacon.address, port: beacon.port, expectedFingerprint: payload.fingerprint);
        return beacon.address;
      }
    } on CertificatePinMismatch {
      rethrow;
    } on Object {
      // Nothing else to try.
    }

    return null;
  }

  String _explain(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.pairingDisabled =>
          'Pairing is not switched on at the PC. Turn on "Allow pairing" and generate a new QR code.',
        ErrorCodes.pairingTokenInvalid =>
          'That QR code is no longer valid. Generate a new one on the PC.',
        ErrorCodes.pairingRejected => 'The request was declined on the PC.',
        ErrorCodes.pairingTimeout =>
          'Nobody approved the request at the PC. Somebody has to be signed in there to accept it.',
        ErrorCodes.rateLimited =>
          'Too many failed attempts. Generate a new QR code on the PC and try again.',
        _ => error?.message ?? 'The PC refused the request.',
      };

  void _fail(String message) {
    if (!mounted) return;

    setState(() {
      _stage = _Stage.failed;
      _message = message;
    });
  }
}

class _FingerprintChip extends StatelessWidget {
  const _FingerprintChip({required this.fingerprint});

  final String fingerprint;

  @override
  Widget build(BuildContext context) => Column(
        children: [
          Text(
            'Fingerprint',
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  color: Theme.of(context).colorScheme.onSurfaceVariant,
                ),
          ),
          const SizedBox(height: 4),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.surfaceContainerHighest,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Text(
              fingerprint,
              style: const TextStyle(fontFamily: 'monospace', fontSize: 13),
            ),
          ),
        ],
      );
}
