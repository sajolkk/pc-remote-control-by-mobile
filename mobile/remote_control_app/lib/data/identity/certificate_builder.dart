import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:pointycastle/export.dart';

/// Builds the self-signed P-256 client certificate this app authenticates with.
///
/// Encoded by hand, in strict DER, instead of through `basic_utils`: its certificates carry a NULL
/// parameter on `ecdsa-with-SHA256` and a Key Usage bit string with a set padding bit. OpenSSL
/// tolerates both; the BoringSSL inside Flutter does not, and `useCertificateChainBytes` fails
/// with `CANNOT_PARSE_LEAF_CERT`, so pairing could never get past the TLS handshake.
class CertificateBuilder {
  CertificateBuilder._();

  static const _oidEcPublicKey = [1, 2, 840, 10045, 2, 1];
  static const _oidPrime256v1 = [1, 2, 840, 10045, 3, 1, 7];
  static const _oidEcdsaWithSha256 = [1, 2, 840, 10045, 4, 3, 2];
  static const _oidCommonName = [2, 5, 4, 3];
  static const _oidKeyUsage = [2, 5, 29, 15];
  static const _oidExtKeyUsage = [2, 5, 29, 37];
  static const _oidClientAuth = [1, 3, 6, 1, 5, 5, 7, 3, 2];

  /// Returns the certificate as PEM.
  static String selfSigned({
    required ECPrivateKey privateKey,
    required ECPublicKey publicKey,
    required String commonName,
    required Duration validity,
    DateTime? now,
  }) {
    final notBefore = (now ?? DateTime.now()).toUtc().subtract(const Duration(days: 1));
    final notAfter = notBefore.add(validity);

    final algorithm = _seq([_oid(_oidEcdsaWithSha256)]);
    final name = _seq([
      _set([
        _seq([_oid(_oidCommonName), _tlv(0x0C, utf8.encode(commonName))]),
      ]),
    ]);

    final tbs = _seq([
      _tlv(0xA0, _integer(BigInt.two)), // version: v3
      _integer(_randomSerial()),
      algorithm,
      name,
      _seq([_time(notBefore), _time(notAfter)]),
      name,
      _seq([
        _seq([_oid(_oidEcPublicKey), _oid(_oidPrime256v1)]),
        _bitString(publicKey.Q!.getEncoded(false)),
      ]),
      _tlv(0xA3, _seq([
        // Key Usage, critical: digitalSignature only. 7 unused bits, all zero.
        _seq([
          _oid(_oidKeyUsage),
          _tlv(0x01, [0xFF]),
          _tlv(0x04, _tlv(0x03, [0x07, 0x80])),
        ]),
        _seq([
          _oid(_oidExtKeyUsage),
          _tlv(0x04, _seq([_oid(_oidClientAuth)])),
        ]),
      ])),
    ]);

    // Deterministic ECDSA (RFC 6979): no random source to get wrong.
    final signer = ECDSASigner(SHA256Digest(), HMac(SHA256Digest(), 64))
      ..init(true, PrivateKeyParameter<ECPrivateKey>(privateKey));
    final signature = signer.generateSignature(tbs) as ECSignature;

    final certificate = _seq([
      tbs,
      algorithm,
      _bitString(_seq([_integer(signature.r), _integer(signature.s)])),
    ]);

    return _pem('CERTIFICATE', certificate);
  }

  /// Returns the private key as unencrypted PKCS#8 PEM (`BEGIN PRIVATE KEY`).
  static String privateKeyPem(ECPrivateKey privateKey, ECPublicKey publicKey) {
    final ecPrivateKey = _seq([
      _integer(BigInt.one),
      _tlv(0x04, _unsigned(privateKey.d!, 32)),
      _tlv(0xA1, _bitString(publicKey.Q!.getEncoded(false))),
    ]);

    final pkcs8 = _seq([
      _integer(BigInt.zero),
      _seq([_oid(_oidEcPublicKey), _oid(_oidPrime256v1)]),
      _tlv(0x04, ecPrivateKey),
    ]);

    return _pem('PRIVATE KEY', pkcs8);
  }

  static BigInt _randomSerial() {
    final random = Random.secure();
    final bytes = List<int>.generate(16, (_) => random.nextInt(256));
    bytes[0] &= 0x7F; // Positive.
    bytes[0] |= 0x40; // Non-zero leading byte, so the length is always 16.
    return _toBigInt(bytes);
  }

  static Uint8List _time(DateTime utc) {
    String two(int v) => v.toString().padLeft(2, '0');
    final body = '${two(utc.month)}${two(utc.day)}${two(utc.hour)}${two(utc.minute)}${two(utc.second)}Z';

    // RFC 5280: UTCTime through 2049, GeneralizedTime from 2050.
    return utc.year < 2050
        ? _tlv(0x17, ascii.encode('${two(utc.year % 100)}$body'))
        : _tlv(0x18, ascii.encode('${utc.year.toString().padLeft(4, '0')}$body'));
  }

  static Uint8List _seq(List<List<int>> items) => _tlv(0x30, items.expand((e) => e).toList());

  static Uint8List _set(List<List<int>> items) => _tlv(0x31, items.expand((e) => e).toList());

  static Uint8List _bitString(List<int> bytes) => _tlv(0x03, [0x00, ...bytes]);

  static Uint8List _integer(BigInt value) {
    // Minimal two's complement for a non-negative value: a leading zero only when the high bit
    // would otherwise make it negative.
    var bytes = value == BigInt.zero ? <int>[0] : _magnitude(value);
    if (bytes.first & 0x80 != 0) bytes = [0x00, ...bytes];
    return _tlv(0x02, bytes);
  }

  static Uint8List _oid(List<int> arcs) {
    final out = <int>[40 * arcs[0] + arcs[1]];
    for (final arc in arcs.skip(2)) {
      final chunk = <int>[arc & 0x7F];
      var rest = arc >> 7;
      while (rest > 0) {
        chunk.insert(0, (rest & 0x7F) | 0x80);
        rest >>= 7;
      }
      out.addAll(chunk);
    }
    return _tlv(0x06, out);
  }

  static Uint8List _tlv(int tag, List<int> value) {
    final length = value.length;
    final List<int> encodedLength;
    if (length < 0x80) {
      encodedLength = [length];
    } else {
      final bytes = <int>[];
      for (var rest = length; rest > 0; rest >>= 8) {
        bytes.insert(0, rest & 0xFF);
      }
      encodedLength = [0x80 | bytes.length, ...bytes];
    }
    return Uint8List.fromList([tag, ...encodedLength, ...value]);
  }

  static List<int> _magnitude(BigInt value) {
    final bytes = <int>[];
    for (var rest = value; rest > BigInt.zero; rest >>= 8) {
      bytes.insert(0, (rest & BigInt.from(0xFF)).toInt());
    }
    return bytes;
  }

  static List<int> _unsigned(BigInt value, int width) {
    final bytes = _magnitude(value);
    return [...List<int>.filled(width - bytes.length, 0), ...bytes];
  }

  static BigInt _toBigInt(List<int> bytes) =>
      bytes.fold(BigInt.zero, (acc, b) => (acc << 8) | BigInt.from(b));

  static String _pem(String label, List<int> der) {
    final body = base64.encode(der);
    final lines = [for (var i = 0; i < body.length; i += 64) body.substring(i, min(i + 64, body.length))];
    return '-----BEGIN $label-----\n${lines.join('\n')}\n-----END $label-----\n';
  }
}
