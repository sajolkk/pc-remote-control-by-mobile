import 'dart:convert';

import 'package:crypto/crypto.dart';

/// The six-digit code both screens show during pairing without a QR code.
///
/// Derived from the PC's certificate fingerprint (as seen during this TLS handshake) and this
/// phone's own. The PC computes the same thing from its side, so matching codes prove nothing on
/// the network is sitting in between: an impostor would hold different keys and produce a
/// different code.
///
/// Must stay identical to `PairingCode` in `windows/RemoteAgent.Security`.
class PairingCode {
  const PairingCode._();

  static const String _domain = 'PC-Remote pairing code v1';

  /// The code as six digits, e.g. `042917`.
  static String compute(String pcFingerprint, String phoneFingerprint) {
    final digest = sha256.convert(utf8.encode('$_domain\n$pcFingerprint\n$phoneFingerprint')).bytes;

    final value = ((digest[0] << 24) | (digest[1] << 16) | (digest[2] << 8) | digest[3]) % 1000000;
    return value.toString().padLeft(6, '0');
  }

  /// The code split for reading, e.g. `042 917`.
  static String toDisplayForm(String code) =>
      code.length == 6 ? '${code.substring(0, 3)} ${code.substring(3)}' : code;
}
