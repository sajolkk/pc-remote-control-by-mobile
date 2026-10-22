import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/media/media_client.dart';

void main() {
  // The PC's answer as it was logged when the phone, on the same Wi-Fi as the PC, was connected
  // over Tailscale: a LAN candidate, and the Tailscale one the PC adds for a VPN peer.
  const answer = 'v=0\r\n'
      'o=- 1 1 IN IP4 127.0.0.1\r\n'
      'm=video 9 UDP/TLS/RTP/SAVPF 96\r\n'
      'a=candidate:1 1 udp 2113937151 192.168.30.10 47810 typ host generation 0\r\n'
      'a=candidate:2 1 udp 2113936895 100.82.238.72 47810 typ host generation 0\r\n'
      'a=candidate:3 1 udp 2113937151 fe80::1 47810 typ host generation 0\r\n'
      'a=end-of-candidates\r\n';

  test('keeps only the candidates on the control connection\'s address', () {
    final restricted = restrictCandidatesToHost(answer, '100.82.238.72');

    expect(restricted, contains('100.82.238.72 47810 typ host'));
    expect(restricted, isNot(contains('192.168.30.10')));
    expect(restricted, isNot(contains('fe80::1')));

    // Everything that is not a candidate is untouched.
    expect(restricted, contains('m=video 9 UDP/TLS/RTP/SAVPF 96\r\n'));
    expect(restricted, contains('a=end-of-candidates\r\n'));
  });

  test('connected on the LAN, the LAN candidate is the one kept', () {
    final restricted = restrictCandidatesToHost(answer, '192.168.30.10');

    expect(restricted, contains('192.168.30.10'));
    expect(restricted, isNot(contains('100.82.238.72')));
  });

  test('leaves the answer alone when no candidate is on that address', () {
    expect(restrictCandidatesToHost(answer, '10.0.0.5'), answer);
  });

  test('an IPv4 address seen through an IPv6 socket still matches', () {
    final restricted = restrictCandidatesToHost(answer, '::ffff:100.82.238.72');

    expect(restricted, contains('100.82.238.72'));
    expect(restricted, isNot(contains('192.168.30.10')));
  });
}
