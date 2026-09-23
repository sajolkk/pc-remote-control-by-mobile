import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/identity/pairing_code.dart';

void main() {
  test('pairing code matches the PC derivation', () {
    // Same vector as PairingCodeMatchesTheMobileAppDerivation in the Windows tests; a change on
    // either side breaks both.
    expect(PairingCode.compute('pc-fingerprint', 'phone-fingerprint'), '792692');
    expect(PairingCode.toDisplayForm('792692'), '792 692');
  });

  test('pairing code is always six digits', () {
    for (var i = 0; i < 200; i++) {
      expect(PairingCode.compute('pc-$i', 'phone-$i'), matches(RegExp(r'^\d{6}$')));
    }
  });
}
