import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../../protocol/protocol.dart';

/// A PC this device has paired with.
class PairedPc {
  const PairedPc({
    required this.deviceId,
    required this.deviceName,
    required this.fingerprint,
    required this.pairedAt,
    this.lastAddress,
    this.lastPort = DiscoveryConstants.defaultControlPort,
    this.lastSeenAt,
    this.permissions = const [],
    this.remoteAddress,
    this.remotePort,
    this.nickname,
  });

  factory PairedPc.fromJson(Map<String, dynamic> json) => PairedPc(
        deviceId: json['deviceId'] as String,
        deviceName: json['deviceName'] as String? ?? 'Windows PC',
        fingerprint: json['fingerprint'] as String,
        pairedAt: DateTime.parse(json['pairedAt'] as String),
        lastAddress: json['lastAddress'] as String?,
        lastPort: json['lastPort'] as int? ?? DiscoveryConstants.defaultControlPort,
        lastSeenAt:
            json['lastSeenAt'] == null ? null : DateTime.tryParse(json['lastSeenAt'] as String),
        permissions:
            (json['permissions'] as List<dynamic>? ?? const []).whereType<String>().toList(),

        // Both absent from records written before remote addresses existed, which is the same
        // as the user never having entered one.
        remoteAddress: _nonEmpty(json['remoteAddress']),
        remotePort: _port(json['remotePort']),

        // Absent from records written before PCs could be renamed: the PC's own name is used.
        nickname: _nonEmpty(json['nickname']),
      );

  static String? _nonEmpty(Object? value) =>
      value is String && value.trim().isNotEmpty ? value.trim() : null;

  static int? _port(Object? value) => value is int && value > 0 && value <= 65535 ? value : null;

  /// The PC's stable id. This, not its address, is the identity — a PC keeps working after
  /// its IP changes.
  final String deviceId;

  /// The name the PC reports for itself (its Windows computer name unless changed on the PC).
  /// Refreshed at every connection, so it is not where a name chosen on this phone is kept.
  final String deviceName;

  /// A name the user gave this PC on this phone, e.g. "My home PC". Null means use [deviceName].
  ///
  /// Kept on the phone only: it changes nothing on the PC, needs no permission there, and
  /// survives the PC reporting its own name again at every connection.
  final String? nickname;

  /// The name to show for this PC everywhere in the app.
  String get displayName => nickname ?? deviceName;

  /// The PC's certificate fingerprint, learned from the QR code and pinned from then on.
  final String fingerprint;

  final DateTime pairedAt;

  /// The last address this PC answered on over the local network. A hint for a fast reconnect,
  /// never an identity. A connection made through [remoteAddress] does not change it.
  final String? lastAddress;

  final int lastPort;
  final DateTime? lastSeenAt;

  /// The permissions the PC reported at the last handshake, for showing what is available.
  final List<String> permissions;

  /// A host name or IP the user entered for reaching this PC when it is not on the phone's
  /// network, e.g. its address on a VPN such as Tailscale. Tried only after the local network
  /// has been, and held to the same pinned [fingerprint] as every other address.
  final String? remoteAddress;

  /// The port to use with [remoteAddress]. Null means the PC's usual port, [lastPort].
  final int? remotePort;

  /// The port a connection to [remoteAddress] is made on.
  int get effectiveRemotePort => remotePort ?? lastPort;

  /// The remote address as the user would type it, or null when none is set.
  String? get remoteAddressLabel {
    final host = remoteAddress;
    if (host == null) return null;

    return RemoteAddress(host: host, port: remotePort).toString();
  }

  Map<String, dynamic> toJson() => {
        'deviceId': deviceId,
        'deviceName': deviceName,
        'fingerprint': fingerprint,
        'pairedAt': pairedAt.toIso8601String(),
        if (lastAddress != null) 'lastAddress': lastAddress,
        'lastPort': lastPort,
        if (lastSeenAt != null) 'lastSeenAt': lastSeenAt!.toIso8601String(),
        'permissions': permissions,
        if (remoteAddress != null) 'remoteAddress': remoteAddress,
        if (remotePort != null) 'remotePort': remotePort,
        if (nickname != null) 'nickname': nickname,
      };

  PairedPc copyWith({
    String? deviceName,
    String? lastAddress,
    int? lastPort,
    DateTime? lastSeenAt,
    List<String>? permissions,
  }) =>
      PairedPc(
        deviceId: deviceId,
        deviceName: deviceName ?? this.deviceName,
        fingerprint: fingerprint,
        pairedAt: pairedAt,
        lastAddress: lastAddress ?? this.lastAddress,
        lastPort: lastPort ?? this.lastPort,
        lastSeenAt: lastSeenAt ?? this.lastSeenAt,
        permissions: permissions ?? this.permissions,
        remoteAddress: remoteAddress,
        remotePort: remotePort,
        nickname: nickname,
      );

  /// A copy with the nickname replaced, or removed when [name] is null or blank, so the PC's
  /// own name shows again.
  ///
  /// Separate from [copyWith] for the reason [withRemoteAddress] is.
  PairedPc withNickname(String? name) => PairedPc(
        deviceId: deviceId,
        deviceName: deviceName,
        fingerprint: fingerprint,
        pairedAt: pairedAt,
        lastAddress: lastAddress,
        lastPort: lastPort,
        lastSeenAt: lastSeenAt,
        permissions: permissions,
        remoteAddress: remoteAddress,
        remotePort: remotePort,
        nickname: _nonEmpty(name),
      );

  /// A copy with the remote address replaced, or removed when [remote] is null.
  ///
  /// Separate from [copyWith] because "leave it alone" and "clear it" both look like null
  /// there, and silently dropping an address the user typed is the mistake to avoid.
  PairedPc withRemoteAddress(RemoteAddress? remote) => PairedPc(
        deviceId: deviceId,
        deviceName: deviceName,
        fingerprint: fingerprint,
        pairedAt: pairedAt,
        lastAddress: lastAddress,
        lastPort: lastPort,
        lastSeenAt: lastSeenAt,
        permissions: permissions,
        remoteAddress: remote?.host,
        remotePort: remote?.port,
        nickname: nickname,
      );
}

/// A user-entered address: a host name or IP, and optionally a port.
class RemoteAddress {
  const RemoteAddress({required this.host, this.port});

  /// Parses `host`, `host:port`, an IPv6 address, or `[ipv6]:port`. Null when it is none of
  /// those.
  ///
  /// Deliberately strict about what a host may contain. This is typed by hand, usually
  /// copied out of a VPN app, and "http://..." or a trailing path is far more likely a paste
  /// mistake than a host name.
  static RemoteAddress? tryParse(String input) {
    final text = input.trim();
    if (text.isEmpty) return null;

    String host;
    String? portText;

    if (text.startsWith('[')) {
      final close = text.indexOf(']');
      if (close < 0) return null;

      host = text.substring(1, close);
      final rest = text.substring(close + 1);

      if (rest.isNotEmpty) {
        if (!rest.startsWith(':')) return null;
        portText = rest.substring(1);
      }

      if (!_ipv6.hasMatch(host) || !host.contains(':')) return null;
    } else if (':'.allMatches(text).length > 1) {
      // More than one colon without brackets can only be a bare IPv6 address.
      host = text;
      if (!_ipv6.hasMatch(host)) return null;
    } else {
      final colon = text.indexOf(':');
      host = colon < 0 ? text : text.substring(0, colon);
      portText = colon < 0 ? null : text.substring(colon + 1);

      if (!_hostName.hasMatch(host)) return null;
    }

    if (portText == null) return RemoteAddress(host: host);

    final port = int.tryParse(portText);
    if (port == null || port < 1 || port > 65535) return null;

    return RemoteAddress(host: host, port: port);
  }

  /// Letters, digits, dots and hyphens: an IPv4 address or a DNS name such as a MagicDNS one.
  static final _hostName = RegExp(r'^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$');

  static final _ipv6 = RegExp(r'^[0-9A-Fa-f:.]{2,45}(%[A-Za-z0-9._-]+)?$');

  final String host;

  /// Null means "the PC's usual port".
  final int? port;

  @override
  String toString() {
    if (port == null) return host;
    return host.contains(':') ? '[$host]:$port' : '$host:$port';
  }
}

/// Persists the PCs this device is paired with.
///
/// Stored through `flutter_secure_storage` rather than plain preferences. The contents are
/// not secret — a fingerprint is a public value — but the pinned fingerprint is what stops
/// an impostor being accepted, so it must not be modifiable by another app on a rooted or
/// jailbroken device.
class PairedPcStore {
  PairedPcStore({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              // resetOnError: data the Android Keystore can no longer decrypt (its key is gone,
              // e.g. after a reinstall) is wiped instead of failing every read. It cannot be
              // recovered anyway; starting clean means pairing again rather than a stuck app.
              aOptions: AndroidOptions(encryptedSharedPreferences: true, resetOnError: true),
              iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
            );

  static const _key = 'paired.pcs';
  static const _autoConnectKey = 'paired.autoConnect';

  final FlutterSecureStorage _storage;

  /// The tail of the queue every read-modify-write goes through.
  ///
  /// Several PCs can be connected at once, and each connection saves its PC's record as it comes
  /// up. Without the queue two of those saves read the same list and the second write drops the
  /// first — which, if the first was a pairing, loses a PC.
  Future<void> _queue = Future.value();

  Future<T> _serialized<T>(Future<T> Function() action) {
    final result = _queue.then((_) => action());
    _queue = result.then<void>((_) {}, onError: (_) {});
    return result;
  }

  Future<List<PairedPc>> getAll() async {
    String? raw;
    try {
      raw = await _storage.read(key: _key);
    } on Object {
      // Unreadable storage (see the constructor) is the same as an empty one: the user pairs
      // again, rather than facing a platform stack trace where the list of PCs should be.
      return const [];
    }

    if (raw == null || raw.isEmpty) return const [];

    try {
      final decoded = jsonDecode(raw);
      if (decoded is! List) return const [];

      return decoded
          .whereType<Map<String, dynamic>>()
          .map(PairedPc.fromJson)
          .toList(growable: false);
    } on Object {
      // A corrupt store must not brick the app. Returning empty means the user re-pairs,
      // which is recoverable; throwing on launch is not.
      return const [];
    }
  }

  Future<PairedPc?> find(String deviceId) async {
    final all = await getAll();
    for (final pc in all) {
      if (pc.deviceId == deviceId) return pc;
    }
    return null;
  }

  /// Saves a record, replacing the one with the same device id.
  ///
  /// A record arriving without a remote address or a nickname keeps the one already stored.
  /// Pairing again builds a fresh record that knows nothing about either, and that must not
  /// throw away what the user typed. Removing them is [setRemoteAddress]'s and [setNickname]'s
  /// job.
  Future<void> save(PairedPc pc) => _serialized(() async {
    final all = await getAll();

    var toSave = pc;
    for (final existing in all) {
      if (existing.deviceId != pc.deviceId) continue;

      if (toSave.remoteAddress == null && existing.remoteAddress != null) {
        toSave = toSave.withRemoteAddress(
          RemoteAddress(host: existing.remoteAddress!, port: existing.remotePort),
        );
      }

      if (toSave.nickname == null && existing.nickname != null) {
        toSave = toSave.withNickname(existing.nickname);
      }
    }

    await _write(
      all.where((existing) => existing.deviceId != pc.deviceId).toList()..add(toSave),
    );
  });

  /// Sets, replaces or (with null) clears a PC's remote address, leaving everything else as
  /// stored. Returns the updated record, or null when that PC is not paired.
  Future<PairedPc?> setRemoteAddress(String deviceId, RemoteAddress? remote) =>
      _serialized(() async {
    final all = await getAll();

    PairedPc? updated;
    final next = <PairedPc>[];

    for (final existing in all) {
      if (existing.deviceId == deviceId) {
        updated = existing.withRemoteAddress(remote);
        next.add(updated);
      } else {
        next.add(existing);
      }
    }

    if (updated != null) await _write(next);
    return updated;
  });

  /// Sets, replaces or (with null or blank) clears the name this phone shows for a PC, leaving
  /// everything else as stored. Returns the updated record, or null when that PC is not paired.
  Future<PairedPc?> setNickname(String deviceId, String? nickname) => _serialized(() async {
    final all = await getAll();

    PairedPc? updated;
    final next = <PairedPc>[];

    for (final existing in all) {
      if (existing.deviceId == deviceId) {
        updated = existing.withNickname(nickname);
        next.add(updated);
      } else {
        next.add(existing);
      }
    }

    if (updated != null) await _write(next);
    return updated;
  });

  Future<void> remove(String deviceId) => _serialized(() async {
    final all = (await getAll()).where((pc) => pc.deviceId != deviceId).toList();
    await _write(all);
    await _writeAutoConnect((await _readAutoConnect() ?? const {})..remove(deviceId));
  });

  Future<void> clear() => _serialized(() async {
    await _storage.delete(key: _key);
    await _storage.delete(key: _autoConnectKey);
  });

  /// The PCs to connect to when the app starts: the ones the user left connected.
  ///
  /// Null when nothing has been recorded yet — an install from before several PCs could be
  /// connected at once — so the caller can fall back to the PC used most recently.
  Future<Set<String>?> getAutoConnect() => _serialized(_readAutoConnect);

  /// Records whether [deviceId] should be connected when the app starts.
  Future<void> setAutoConnect(String deviceId, bool enabled) => _serialized(() async {
    final ids = await _readAutoConnect() ?? <String>{};
    final changed = enabled ? ids.add(deviceId) : ids.remove(deviceId);

    // Written even when unchanged the first time, so "never recorded" becomes "recorded".
    if (changed || !(await _storage.containsKey(key: _autoConnectKey))) {
      await _writeAutoConnect(ids);
    }
  });

  Future<Set<String>?> _readAutoConnect() async {
    final raw = await _storage.read(key: _autoConnectKey);
    if (raw == null) return null;

    try {
      final decoded = jsonDecode(raw);
      return decoded is List ? decoded.whereType<String>().toSet() : <String>{};
    } on Object {
      return <String>{};
    }
  }

  Future<void> _writeAutoConnect(Set<String> ids) =>
      _storage.write(key: _autoConnectKey, value: jsonEncode(ids.toList()));

  Future<void> _write(List<PairedPc> devices) => _storage.write(
        key: _key,
        value: jsonEncode(devices.map((pc) => pc.toJson()).toList()),
      );
}
