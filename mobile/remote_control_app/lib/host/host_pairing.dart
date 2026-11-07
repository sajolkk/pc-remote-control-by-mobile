// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:async';

import 'package:flutter/foundation.dart';

import '../data/identity/device_identity.dart';
import '../data/identity/pairing_code.dart';
import '../protocol/protocol.dart';
import 'paired_viewer_store.dart';
import 'session_security.dart';

/// The outcome of a pairing attempt, as the PC names them.
enum PairingOutcome {
  paired,
  disabled,
  tokenInvalid,
  fingerprintMismatch,
  rejected,
  approvalTimeout,
  tooManyAttempts,
  invalidRequest,
  busy,
}

/// An open pairing window: the token in the QR code and when it stops working.
class PairingWindow {
  const PairingWindow({required this.token, required this.openedAt, required this.expiresAt});

  final String token;
  final DateTime openedAt;
  final DateTime expiresAt;
}

/// What the owner is shown before deciding.
@immutable
class PairingApprovalRequest {
  const PairingApprovalRequest({
    required this.deviceName,
    required this.platform,
    required this.model,
    required this.remoteAddress,
    required this.fingerprintShort,
    this.comparisonCode,
  });

  final String deviceName;
  final String platform;
  final String model;
  final String remoteAddress;

  /// Colon-hex prefix of the viewer's certificate fingerprint, to compare with its Settings tab.
  final String fingerprintShort;

  /// The six-digit code, in display form, when pairing without a QR code. Null for QR pairing.
  final String? comparisonCode;
}

/// The owner's answer.
class PairingDecision {
  const PairingDecision({required this.approved, this.grantFullControl = false});

  static const declined = PairingDecision(approved: false);

  final bool approved;

  /// Whether the owner ticked "also allow opening apps and locking". Decided only here: a pairing
  /// request has no field through which a viewer could ask for more than the default.
  final bool grantFullControl;
}

/// Asks the owner. Must complete within [timeout]; a timeout is a refusal.
typedef PairingApprover = Future<PairingDecision> Function(
  PairingApprovalRequest request,
  Duration timeout,
);

/// The result of [HostPairingService.tryPair].
class PairingAttemptResult {
  const PairingAttemptResult(this.outcome, [this.viewer]);

  final PairingOutcome outcome;
  final PairedViewer? viewer;

  bool get succeeded => outcome == PairingOutcome.paired;

  /// The wire error code the viewer receives, as `PairingService.ToErrorCode` maps it.
  String get errorCode => switch (outcome) {
        PairingOutcome.disabled => ErrorCodes.pairingDisabled,
        PairingOutcome.tokenInvalid => ErrorCodes.pairingTokenInvalid,
        PairingOutcome.fingerprintMismatch => ErrorCodes.pairingTokenInvalid,
        PairingOutcome.tooManyAttempts => ErrorCodes.rateLimited,
        PairingOutcome.busy => ErrorCodes.rateLimited,
        PairingOutcome.rejected => ErrorCodes.pairingRejected,
        PairingOutcome.approvalTimeout => ErrorCodes.pairingTimeout,
        PairingOutcome.invalidRequest => ErrorCodes.invalidArguments,
        PairingOutcome.paired => ErrorCodes.internal,
      };

  String get message => switch (outcome) {
        PairingOutcome.disabled =>
          'Pairing is not open on the phone. Open "Share this phone" there and scan its QR code.',
        PairingOutcome.tokenInvalid => 'The pairing code is invalid or has expired. Scan a new QR code.',
        PairingOutcome.tooManyAttempts =>
          'Too many failed pairing attempts. Start sharing again on the phone for a new QR code.',
        PairingOutcome.rejected => 'The request was declined on the phone.',
        PairingOutcome.approvalTimeout => 'Nobody approved the request on the phone in time.',
        PairingOutcome.fingerprintMismatch =>
          "The pairing request did not match this device's certificate and was refused.",
        PairingOutcome.invalidRequest => 'The pairing request was incomplete.',
        PairingOutcome.busy => 'Another device is waiting to be approved on the phone. Try again in a moment.',
        PairingOutcome.paired => 'Paired.',
      };
}

/// The pairing state machine of a phone host: the PC's `PairingService`, in Dart.
///
/// Three things must all hold before a viewer is paired, and each alone is useless to an
/// attacker: pairing is open (only while the owner has the Share screen up, and the window
/// closes itself), the single-use token from the QR code matches, and the owner approves on this
/// phone after seeing the viewer's name, address and fingerprint. The token is checked before the
/// owner is asked, so a wrong token never costs the owner a dialog.
///
/// Code pairing, for a viewer that found this phone on the network without scanning, carries no
/// token: the owner compares a six-digit code derived from both certificates instead, one request
/// at a time, with a cooldown after a refusal.
class HostPairingService extends ChangeNotifier {
  HostPairingService({
    required PairedViewerStore store,
    required PairingApprover approver,
    required String hostFingerprint,
    Clock clock = systemClock,
    this.windowDuration = const Duration(minutes: 5),
    this.maxAttempts = 5,
    this.approvalTimeout = const Duration(seconds: 60),
    this.allowCodePairing = true,
  })  : _store = store,
        _approver = approver,
        _hostFingerprint = hostFingerprint,
        _clock = clock;

  static const _tokenBytes = 16;
  static const _maxNameLength = 64;
  static const _codeCooldown = Duration(minutes: 1);

  final PairedViewerStore _store;
  final PairingApprover _approver;
  final String _hostFingerprint;
  final Clock _clock;
  final Duration windowDuration;
  final int maxAttempts;
  final Duration approvalTimeout;
  final bool allowCodePairing;

  final _codeCooldownUntil = <String, DateTime>{};

  PairingWindow? _window;
  int _failedAttempts = 0;
  bool _codePromptActive = false;
  bool _qrPromptActive = false;

  /// The open window, or null when pairing is closed or the window has expired.
  PairingWindow? get currentWindow => _isWindowLive ? _window : null;

  bool get isOpen => currentWindow != null;

  int get failedAttempts => _failedAttempts;

  bool get _isWindowLive => _window != null && _window!.expiresAt.isAfter(_clock());

  /// Opens a window with a fresh single-use token, invalidating any previous one.
  PairingWindow openWindow() {
    final now = _clock();
    final window = PairingWindow(
      token: randomToken(_tokenBytes),
      openedAt: now,
      expiresAt: now.add(windowDuration),
    );

    _window = window;
    _failedAttempts = 0;
    notifyListeners();
    return window;
  }

  /// Closes the window and burns its token.
  void closeWindow() {
    final wasOpen = _window != null;
    _window = null;
    _failedAttempts = 0;
    if (wasOpen) notifyListeners();
  }

  /// Attempts to pair the viewer that sent [args] on a connection whose TLS peer certificate has
  /// [peerFingerprint].
  Future<PairingAttemptResult> tryPair(
    Map<String, dynamic> args, {
    required String peerFingerprint,
    required String remoteAddress,
  }) async {
    if (peerFingerprint.isEmpty) {
      // The transport let through a connection without a client certificate. Refuse rather than
      // trust the claim.
      return const PairingAttemptResult(PairingOutcome.fingerprintMismatch);
    }

    final deviceId = args['deviceId'] as String? ?? '';
    if (deviceId.trim().isEmpty) return const PairingAttemptResult(PairingOutcome.invalidRequest);

    final token = args['token'] as String? ?? '';
    if (token.isEmpty) {
      return _tryPairWithCode(args, peerFingerprint: peerFingerprint, remoteAddress: remoteAddress);
    }

    // Gate 1: is pairing open?
    if (!_isWindowLive) return const PairingAttemptResult(PairingOutcome.disabled);
    if (_failedAttempts >= maxAttempts) {
      return const PairingAttemptResult(PairingOutcome.tooManyAttempts);
    }

    // Gate 2: is the token the one in the QR code?
    if (!fixedTimeEquals(token, _window!.token)) {
      _failedAttempts++;
      if (_failedAttempts >= maxAttempts) {
        closeWindow();
        return const PairingAttemptResult(PairingOutcome.tooManyAttempts);
      }
      notifyListeners();
      return const PairingAttemptResult(PairingOutcome.tokenInvalid);
    }

    // Gate 3: does the claimed identity match the TLS peer? Closes the relay case.
    if (!_claimMatchesPeer(args, peerFingerprint)) {
      return const PairingAttemptResult(PairingOutcome.fingerprintMismatch);
    }

    // Only one approval dialog at a time; a second device with the same token waits.
    if (_qrPromptActive) return const PairingAttemptResult(PairingOutcome.busy);
    _qrPromptActive = true;

    try {
      // Gate 4: does the owner approve?
      final decision = await _askOwner(args, peerFingerprint, remoteAddress, comparisonCode: null);
      if (decision.outcome != PairingOutcome.paired) return PairingAttemptResult(decision.outcome);

      final viewer = await _save(args, peerFingerprint, remoteAddress, decision.fullControl);

      // The token is single-use: closing the window burns it.
      closeWindow();
      return PairingAttemptResult(PairingOutcome.paired, viewer);
    } finally {
      _qrPromptActive = false;
    }
  }

  Future<PairingAttemptResult> _tryPairWithCode(
    Map<String, dynamic> args, {
    required String peerFingerprint,
    required String remoteAddress,
  }) async {
    if (!allowCodePairing || _hostFingerprint.isEmpty) {
      return const PairingAttemptResult(PairingOutcome.disabled);
    }

    if (!_claimMatchesPeer(args, peerFingerprint)) {
      return const PairingAttemptResult(PairingOutcome.fingerprintMismatch);
    }

    final now = _clock();
    final until = _codeCooldownUntil[remoteAddress];
    if (until != null && until.isAfter(now)) {
      return const PairingAttemptResult(PairingOutcome.tooManyAttempts);
    }

    if (_codePromptActive) return const PairingAttemptResult(PairingOutcome.busy);
    _codePromptActive = true;

    try {
      final code = PairingCode.compute(_hostFingerprint, peerFingerprint);
      final decision = await _askOwner(
        args,
        peerFingerprint,
        remoteAddress,
        comparisonCode: PairingCode.toDisplayForm(code),
      );

      if (decision.outcome != PairingOutcome.paired) {
        final at = _clock();
        _codeCooldownUntil.removeWhere((_, value) => !value.isAfter(at));
        _codeCooldownUntil[remoteAddress] = at.add(_codeCooldown);
        return PairingAttemptResult(decision.outcome);
      }

      final viewer = await _save(args, peerFingerprint, remoteAddress, decision.fullControl);
      return PairingAttemptResult(PairingOutcome.paired, viewer);
    } finally {
      _codePromptActive = false;
    }
  }

  bool _claimMatchesPeer(Map<String, dynamic> args, String peerFingerprint) {
    final claimed = args['clientFingerprint'] as String? ?? '';
    return claimed.isEmpty || claimed == peerFingerprint;
  }

  Future<({PairingOutcome outcome, bool fullControl})> _askOwner(
    Map<String, dynamic> args,
    String peerFingerprint,
    String remoteAddress, {
    required String? comparisonCode,
  }) async {
    final request = PairingApprovalRequest(
      deviceName: sanitizeDisplayName(args['deviceName'] as String?),
      platform: sanitizeDisplayName(args['platform'] as String?),
      model: sanitizeDisplayName(args['model'] as String?),
      remoteAddress: remoteAddress,
      fingerprintShort: DeviceIdentityStore.shortFingerprint(peerFingerprint),
      comparisonCode: comparisonCode,
    );

    try {
      final decision = await _approver(request, approvalTimeout).timeout(approvalTimeout);
      return decision.approved
          ? (outcome: PairingOutcome.paired, fullControl: decision.grantFullControl)
          : (outcome: PairingOutcome.rejected, fullControl: false);
    } on TimeoutException {
      return (outcome: PairingOutcome.approvalTimeout, fullControl: false);
    }
  }

  Future<PairedViewer> _save(
    Map<String, dynamic> args,
    String peerFingerprint,
    String remoteAddress,
    bool fullControl,
  ) async {
    final name = sanitizeDisplayName(args['deviceName'] as String?);

    final viewer = PairedViewer(
      deviceId: (args['deviceId'] as String).trim(),
      deviceName: name.isEmpty ? 'Mobile device' : name,
      platform: sanitizeDisplayName(args['platform'] as String?),
      model: sanitizeDisplayName(args['model'] as String?),
      fingerprint: peerFingerprint,
      pairedAt: _clock(),
      lastAddress: remoteAddress,
      lastSeenAt: _clock(),
      permissions: fullControl ? PairedViewer.allPermissions : PairedViewer.defaultPermissions,
    );

    await _store.save(viewer);
    return viewer;
  }

  /// Makes a viewer-supplied display string safe to show: control characters removed, length
  /// capped, so it cannot push the fingerprint off the screen.
  static String sanitizeDisplayName(String? value) {
    if (value == null) return '';

    var trimmed = value.trim();
    if (trimmed.length > _maxNameLength) trimmed = trimmed.substring(0, _maxNameLength);

    return String.fromCharCodes(trimmed.runes.where((rune) => rune >= 0x20 && rune != 0x7F));
  }
}
