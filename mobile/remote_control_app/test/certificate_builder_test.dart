import 'package:basic_utils/basic_utils.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/identity/certificate_builder.dart';
import 'package:remote_control_app/data/identity/device_identity.dart';

void main() {
  test('the TLS stack accepts the generated certificate and key', () {
    // Regression: basic_utils' certificates were rejected by BoringSSL with
    // CANNOT_PARSE_LEAF_CERT, so pairing failed at the handshake.
    for (var i = 0; i < 20; i++) {
      final pair = CryptoUtils.generateEcKeyPair(curve: 'prime256v1');
      final privateKey = pair.privateKey as ECPrivateKey;
      final publicKey = pair.publicKey as ECPublicKey;

      final certificate = CertificateBuilder.selfSigned(
        privateKey: privateKey,
        publicKey: publicKey,
        commonName: 'pc-remote-mobile',
        validity: const Duration(days: 3650),
      );
      final key = CertificateBuilder.privateKeyPem(privateKey, publicKey);

      expect(DeviceIdentityStore.isUsable(certificate, key), isTrue);
    }
  });
}
