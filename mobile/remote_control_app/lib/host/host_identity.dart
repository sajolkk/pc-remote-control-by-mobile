// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'package:basic_utils/basic_utils.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../data/identity/certificate_builder.dart';
import '../data/identity/device_identity.dart';

/// The certificate this phone presents when it is the one being controlled.
///
/// Separate from [DeviceIdentity], the certificate it presents to PCs, for two reasons. The
/// viewer certificate was issued for client authentication only, and a TLS server should present
/// one issued for server authentication. And the viewer certificate's fingerprint is pinned by
/// every PC this phone is paired with; replacing it would mean pairing with each of them again.
///
/// The device id is shared: this is still one phone, and the id is how a viewer remembers it.
class HostIdentity {
  const HostIdentity({
    required this.deviceId,
    required this.certificatePem,
    required this.privateKeyPem,
    required this.fingerprint,
  });

  final String deviceId;
  final String certificatePem;
  final String privateKeyPem;

  /// `SHA-256` of the DER certificate, base64url without padding: what a viewer pins.
  final String fingerprint;

  String get fingerprintShort => DeviceIdentityStore.shortFingerprint(fingerprint);
}

/// Creates and persists the host identity. See [DeviceIdentityStore] for the storage trade-off,
/// which is the same here.
class HostIdentityStore {
  HostIdentityStore({required DeviceIdentityStore deviceIdentity, FlutterSecureStorage? storage})
      : _deviceIdentity = deviceIdentity,
        _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true, resetOnError: true),
              iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
            );

  static const _certificateKey = 'host.certificate_pem';
  static const _privateKeyKey = 'host.private_key_pem';
  static const _validityDays = 3650;

  final DeviceIdentityStore _deviceIdentity;
  final FlutterSecureStorage _storage;

  HostIdentity? _cached;

  Future<HostIdentity> getOrCreate() async {
    final cached = _cached;
    if (cached != null) return cached;

    final device = await _deviceIdentity.getOrCreate();
    final certificatePem = await _storage.read(key: _certificateKey);
    final privateKeyPem = await _storage.read(key: _privateKeyKey);

    if (certificatePem != null &&
        privateKeyPem != null &&
        DeviceIdentityStore.isUsable(certificatePem, privateKeyPem)) {
      return _cached = HostIdentity(
        deviceId: device.deviceId,
        certificatePem: certificatePem,
        privateKeyPem: privateKeyPem,
        fingerprint: DeviceIdentityStore.fingerprintOfPem(certificatePem),
      );
    }

    final keyPair = CryptoUtils.generateEcKeyPair(curve: 'prime256v1');
    final privateKey = keyPair.privateKey as ECPrivateKey;
    final publicKey = keyPair.publicKey as ECPublicKey;

    final certificate = CertificateBuilder.selfSigned(
      privateKey: privateKey,
      publicKey: publicKey,
      commonName: 'pc-remote-phone-host',
      validity: const Duration(days: _validityDays),
      serverAuth: true,
    );
    final key = CertificateBuilder.privateKeyPem(privateKey, publicKey);

    await _storage.write(key: _certificateKey, value: certificate);
    await _storage.write(key: _privateKeyKey, value: key);

    return _cached = HostIdentity(
      deviceId: device.deviceId,
      certificatePem: certificate,
      privateKeyPem: key,
      fingerprint: DeviceIdentityStore.fingerprintOfPem(certificate),
    );
  }

  /// Destroys the host identity. Every viewer paired with this phone must pair again.
  Future<void> reset() async {
    await _storage.delete(key: _certificateKey);
    await _storage.delete(key: _privateKeyKey);
    _cached = null;
  }
}
