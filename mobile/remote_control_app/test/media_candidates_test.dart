import 'package:flutter_test/flutter_test.dart';
import 'package:remote_control_app/data/media/media_client.dart';

void main() {
  group('congestion feedback', congestionFeedbackTests);
  group('freeze detector', freezeDetectorTests);

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

void congestionFeedbackTests() {
  // The video section of a libwebrtc offer, trimmed.
  const offer = 'm=video 9 UDP/TLS/RTP/SAVPF 96\r\n'
      'a=extmap:3 http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01\r\n'
      'a=extmap:4 urn:3gpp:video-orientation\r\n'
      'a=rtpmap:96 H264/90000\r\n'
      'a=rtcp-fb:96 goog-remb\r\n'
      'a=rtcp-fb:96 transport-cc\r\n'
      'a=rtcp-fb:96 ccm fir\r\n'
      'a=rtcp-fb:96 nack\r\n'
      'a=rtcp-fb:96 nack pli\r\n';

  test('the offer no longer asks for REMB or transport-cc', () {
    final stripped = withoutCongestionFeedback(offer);

    expect(stripped, isNot(contains('goog-remb')));
    expect(stripped, isNot(contains('transport-cc')));
    expect(stripped, isNot(contains('transport-wide-cc')));
  });

  test('keyframe requests and everything else stay', () {
    final stripped = withoutCongestionFeedback(offer);

    expect(stripped, contains('a=rtcp-fb:96 nack pli\r\n'));
    expect(stripped, contains('a=rtcp-fb:96 ccm fir\r\n'));
    expect(stripped, contains('a=rtcp-fb:96 nack\r\n'));
    expect(stripped, contains('a=extmap:4 urn:3gpp:video-orientation\r\n'));
    expect(stripped, contains('a=rtpmap:96 H264/90000\r\n'));
  });
}

void freezeDetectorTests() {
  final t0 = DateTime(2026, 10, 22, 12);
  DateTime at(int ms) => t0.add(Duration(milliseconds: ms));

  test('a picture that stops while the PC keeps sending is restarted after 3 s', () {
    final detector = FreezeDetector();

    expect(detector.sample(at(0), sentFps: 30, shownFps: 0), isFalse);
    expect(detector.sample(at(2000), sentFps: 30, shownFps: 0), isFalse);
    expect(detector.sample(at(3000), sentFps: 30, shownFps: 0), isTrue);
  });

  test('a still screen is not a frozen one', () {
    final detector = FreezeDetector();

    for (var ms = 0; ms <= 10000; ms += 1000) {
      expect(detector.sample(at(ms), sentFps: 0.5, shownFps: 0), isFalse);
    }
  });

  test('one frame shown starts the wait again', () {
    final detector = FreezeDetector();

    detector.sample(at(0), sentFps: 30, shownFps: 0);
    detector.sample(at(2500), sentFps: 30, shownFps: 12);

    expect(detector.sample(at(3000), sentFps: 30, shownFps: 0), isFalse);
    expect(detector.sample(at(6000), sentFps: 30, shownFps: 0), isTrue);
  });

  test('restarts are spaced out, so an undecodable stream is not restarted in a loop', () {
    final detector = FreezeDetector();

    detector.sample(at(0), sentFps: 30, shownFps: 0);
    expect(detector.sample(at(3000), sentFps: 30, shownFps: 0), isTrue);

    detector.sample(at(4000), sentFps: 30, shownFps: 0);
    expect(detector.sample(at(7000), sentFps: 30, shownFps: 0), isFalse);
    expect(detector.sample(at(11000), sentFps: 30, shownFps: 0), isTrue);
  });
}
