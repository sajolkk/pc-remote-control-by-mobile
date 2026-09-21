import 'dart:convert';
import 'dart:typed_data';

import 'package:basic_utils/basic_utils.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:uuid/uuid.dart';

/// This device's cryptographic identity: one long-lived key pair and certificate.
///
/// The certificate is what the PC authenticates. Its fingerprint — `SHA-256` of the DER
/// encoding, base64url — is the value stored in the PC's pairing record, and possession of
/// the matching private key is proven by the TLS handshake itself. There is no password
/// and no shared secret anywhere in the system.
class DeviceIdentity {
  const DeviceIdentity({
    required this.deviceId,
    required this.certificatePem,
    required this.privateKeyPem,
    required this.fingerprint,
  });

  /// Stable id for this installation, sent to the PC and used as the pairing key.
  final String deviceId;

  /// The certificate presented to the PC, PEM encoded.
  final String certificatePem;

  /// The private key, PEM encoded.
  final String privateKeyPem;

  /// `SHA-256` of the DER certificate, base64url without padding.
  final String fingerprint;

  /// Colon-hex prefix, for comparing against what the PC displays.
  String get fingerprintShort => DeviceIdentityStore.shortFingerprint(fingerprint);
}

/// Creates and persists the device identity.
///
/// ## A limitation worth being explicit about
///
/// The architecture called for a hardware-backed, non-exportable key in the Android
/// Keystore or iOS Secure Enclave. That is not achievable together with Dart's TLS stack:
/// `SecurityContext.usePrivateKeyBytes` needs the key material as PEM, so the key must be
/// readable by this process. A truly non-exportable key would mean doing TLS natively
/// through OkHttp or NSURLSession with a keystore-backed identity, and bridging every byte
/// of the control channel over a platform channel.
///
/// What is done instead: the key is generated on the device, never transmitted, and stored
/// through `flutter_secure_storage`, which encrypts it with a hardware-backed key on
/// Android and stores it in the Keychain on iOS. So it is protected at rest by the OS
/// against another app or someone with the device, but it is extractable by this app's own
/// process and therefore by an attacker who achieves code execution inside it.
///
/// The consequence is bounded: stealing this key lets an attacker impersonate *this phone*
/// to a PC it is already paired with, and the PC's owner can revoke it. It does not expose
/// the PC's key, any Windows credential, or any other paired device. Moving to a native
/// TLS path is the fix if that trade stops being acceptable.
class DeviceIdentityStore {
  DeviceIdentityStore({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
              iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
            );

  static const _deviceIdKey = 'identity.device_id';
  static const _certificateKey = 'identity.certificate_pem';
  static const _privateKeyKey = 'identity.private_key_pem';

  /// Ten years. The certificate is pinned, not chain-validated, so a short lifetime buys
  /// nothing except forcing every paired PC to re-pair.
  static const _validityDays = 3650;

  final FlutterSecureStorage _storage;

  DeviceIdentity? _cached;

  /// Returns the identity, creating one on first launch.
  Future<DeviceIdentity> getOrCreate() async {
    final cached = _cached;
    if (cached != null) return cached;

    final deviceId = await _storage.read(key: _deviceIdKey);
    final certificatePem = await _storage.read(key: _certificateKey);
    final privateKeyPem = await _storage.read(key: _privateKeyKey);

    if (deviceId != null && certificatePem != null && privateKeyPem != null) {
      final identity = DeviceIdentity(
        deviceId: deviceId,
        certificatePem: certificatePem,
        privateKeyPem: privateKeyPem,
        fingerprint: fingerprintOfPem(certificatePem),
      );

      _cached = identity;
      return identity;
    }

    return _cached = await _create();
  }

  Future<DeviceIdentity> _create() async {
    // P-256, because it is the one modern curve both Windows SChannel and mobile BoringSSL
    // support for TLS. Ed25519 would be a better key but SChannel cannot use it for TLS,
    // which would force a hand-rolled handshake.
    final keyPair = CryptoUtils.generateEcKeyPair(curve: 'prime256v1');
    final privateKey = keyPair.privateKey as ECPrivateKey;
    final publicKey = keyPair.publicKey as ECPublicKey;

    final deviceId = const Uuid().v4().replaceAll('-', '');

    // The subject is informational only: the PC validates the fingerprint, never the name.
    final dn = {'CN': 'pc-remote-mobile', 'OU': 'PC-Remote'};

    final csrPem = X509Utils.generateEccCsrPem(dn, privateKey, publicKey);

    final certificatePem = X509Utils.generateSelfSignedCertificate(
      privateKey,
      csrPem,
      _validityDays,
      keyUsage: [KeyUsage.DIGITAL_SIGNATURE, KeyUsage.KEY_AGREEMENT],
      extKeyUsage: [ExtendedKeyUsage.CLIENT_AUTH],
      serialNumber: DateTime.now().millisecondsSinceEpoch.toString(),
    );

    final privateKeyPem = CryptoUtils.encodeEcPrivateKeyToPem(privateKey);

    await _storage.write(key: _deviceIdKey, value: deviceId);
    await _storage.write(key: _certificateKey, value: certificatePem);
    await _storage.write(key: _privateKeyKey, value: privateKeyPem);

    return DeviceIdentity(
      deviceId: deviceId,
      certificatePem: certificatePem,
      privateKeyPem: privateKeyPem,
      fingerprint: fingerprintOfPem(certificatePem),
    );
  }

  /// Computes the fingerprint the PC will store: `SHA-256` over the DER bytes.
  static String fingerprintOfPem(String certificatePem) =>
      fingerprintOfDer(_derFromPem(certificatePem));

  /// Computes the fingerprint from DER bytes, used for pinning the PC's certificate.
  static String fingerprintOfDer(List<int> der) {
    final digest = sha256.convert(der);
    return base64Url.encode(digest.bytes).replaceAll('=', '');
  }

  /// Formats the first bytes of a fingerprint as colon-hex, matching the PC's display.
  static String shortFingerprint(String fingerprint, {int bytes = 8}) {
    final padded = fingerprint.padRight((fingerprint.length + 3) & ~3, '=');
    try {
      final decoded = base64Url.decode(padded);
      final take = decoded.length < bytes ? decoded.length : bytes;
      return decoded
          .sublist(0, take)
          .map((b) => b.toRadixString(16).padLeft(2, '0').toUpperCase())
          .join(':');
    } on FormatException {
      return fingerprint.length > 16 ? fingerprint.substring(0, 16) : fingerprint;
    }
  }

  static Uint8List _derFromPem(String pem) {
    final body = pem
        .replaceAll('-----BEGIN CERTIFICATE-----', '')
        .replaceAll('-----END CERTIFICATE-----', '')
        .replaceAll(RegExp(r'\s'), '');

    return base64.decode(body);
  }

  /// Destroys the identity. Every existing pairing becomes unusable, so this is only for
  /// an explicit "forget everything" action.
  Future<void> reset() async {
    await _storage.delete(key: _deviceIdKey);
    await _storage.delete(key: _certificateKey);
    await _storage.delete(key: _privateKeyKey);
    _cached = null;
  }
}
