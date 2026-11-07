/// The per-connection defences of the host's control channel, mirroring the PC's
/// `ReplayGuard`, `SessionTokenService` and `RequestRateLimiter` (§6.2, §7.2, §7.3).
library;

// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';

import '../protocol/protocol.dart';

/// A source of time, so expiry can be tested without waiting.
typedef Clock = DateTime Function();

DateTime systemClock() => DateTime.now().toUtc();

/// Why a request was refused by the [ReplayGuard].
enum ReplayVerdict { accepted, sequenceRepeated, sequenceGap, timestampOutOfWindow }

/// Refuses replayed or reordered requests on one connection.
///
/// The first request may start at any positive number; after that the sequence must advance by
/// exactly one. A gap or a repeat ends the connection, as it does on the PC: on an ordered,
/// encrypted transport either one is an attack or a broken client.
class ReplayGuard {
  ReplayGuard({int maxClockSkewSeconds = 30})
      : _maxSkewMs = maxClockSkewSeconds.clamp(5, 300) * 1000;

  final int _maxSkewMs;
  int? _lastSequence;

  int? get lastSequence => _lastSequence;

  ReplayVerdict evaluate(int sequence, int timestampMs, int nowMs) {
    if ((nowMs - timestampMs).abs() > _maxSkewMs) return ReplayVerdict.timestampOutOfWindow;

    final last = _lastSequence;
    if (last == null) {
      if (sequence < 1) return ReplayVerdict.sequenceRepeated;
      _lastSequence = sequence;
      return ReplayVerdict.accepted;
    }

    if (sequence <= last) return ReplayVerdict.sequenceRepeated;
    if (sequence != last + 1) return ReplayVerdict.sequenceGap;

    _lastSequence = sequence;
    return ReplayVerdict.accepted;
  }

  static String describe(ReplayVerdict verdict) => switch (verdict) {
        ReplayVerdict.accepted => 'accepted',
        ReplayVerdict.sequenceRepeated => 'the request sequence number repeated or went backwards',
        ReplayVerdict.sequenceGap => 'the request sequence number skipped a value',
        ReplayVerdict.timestampOutOfWindow =>
          "the request timestamp is outside the accepted window; check the device's clock",
      };
}

/// A token bucket: a burst of pointer moves is normal, a flood is not.
class RequestRateLimiter {
  RequestRateLimiter({required Clock clock, int maxRequestsPerMinute = 6000})
      : _clock = clock,
        _permitsPerSecond = maxRequestsPerMinute.clamp(60, 1 << 20) / 60.0,
        _burst = maxRequestsPerMinute.clamp(60, 1 << 20) / 6.0 {
    _available = _burst;
    _lastRefill = clock();
  }

  final Clock _clock;
  final double _permitsPerSecond;
  final double _burst;
  late double _available;
  late DateTime _lastRefill;

  bool tryAcquire() {
    final now = _clock();
    final elapsed = now.difference(_lastRefill).inMicroseconds / 1e6;
    _lastRefill = now;
    _available = min(_burst, _available + elapsed * _permitsPerSecond);

    if (_available < 1) return false;
    _available -= 1;
    return true;
  }
}

/// A live session credential, bound to one viewer and one connection.
class SessionToken {
  const SessionToken({
    required this.value,
    required this.deviceId,
    required this.connectionId,
    required this.expiresAt,
  });

  final String value;
  final String deviceId;
  final String connectionId;
  final DateTime expiresAt;
}

/// Issues and validates short-lived session tokens. In memory only: a token must not outlive the
/// process that issued it, and there is nothing on disk to steal.
class SessionTokenService {
  SessionTokenService({required Clock clock, int lifetimeSeconds = 600})
      : _clock = clock,
        lifetime = Duration(seconds: lifetimeSeconds.clamp(60, 3600));

  final Clock _clock;
  final Duration lifetime;
  final _tokens = <String, SessionToken>{};
  final _random = Random.secure();

  SessionToken issue(String deviceId, String connectionId) {
    final token = SessionToken(
      value: generateToken(32),
      deviceId: deviceId,
      connectionId: connectionId,
      expiresAt: _clock().add(lifetime),
    );
    _tokens[token.value] = token;
    return token;
  }

  /// Whether [token] is valid for [connectionId] right now. A token presented on another
  /// connection is refused: that is what makes a captured token useless elsewhere.
  SessionToken? validate(String? token, String connectionId) {
    if (token == null || token.isEmpty) return null;

    final found = _tokens[token];
    if (found == null) return null;

    if (!found.expiresAt.isAfter(_clock())) {
      _tokens.remove(token);
      return null;
    }

    return found.connectionId == connectionId ? found : null;
  }

  /// Replaces [current], invalidating it immediately.
  SessionToken renew(SessionToken current) {
    _tokens.remove(current.value);
    return issue(current.deviceId, current.connectionId);
  }

  int revokeDevice(String deviceId) => _removeWhere((t) => t.deviceId == deviceId);

  int revokeConnection(String connectionId) =>
      _removeWhere((t) => t.connectionId == connectionId);

  int purgeExpired() {
    final now = _clock();
    return _removeWhere((t) => !t.expiresAt.isAfter(now));
  }

  int _removeWhere(bool Function(SessionToken) predicate) {
    final before = _tokens.length;
    _tokens.removeWhere((_, token) => predicate(token));
    return before - _tokens.length;
  }

  /// [byteCount] CSPRNG bytes as unpadded base64url, like the PC's `Base64Url.GenerateToken`.
  String generateToken(int byteCount) => randomToken(byteCount, _random);
}

/// [byteCount] CSPRNG bytes as unpadded base64url.
String randomToken(int byteCount, [Random? random]) {
  final source = random ?? Random.secure();
  return Base64Url.encode(List<int>.generate(byteCount, (_) => source.nextInt(256)));
}

/// Constant-time comparison of two strings, for the pairing token: the token is the secret, so an
/// early return on the first differing byte would leak it a byte at a time.
bool fixedTimeEquals(String a, String b) {
  final left = utf8.encode(a);
  final right = utf8.encode(b);

  var difference = left.length ^ right.length;
  final length = max(left.length, right.length);

  for (var i = 0; i < length; i++) {
    final l = i < left.length ? left[i] : 0;
    final r = i < right.length ? right[i] : 0;
    difference |= l ^ r;
  }

  return difference == 0;
}

/// A short, non-reversible reference to a token, safe for logs.
String tokenReference(String token) =>
    sha256.convert(utf8.encode(token)).toString().substring(0, 8).toUpperCase();
