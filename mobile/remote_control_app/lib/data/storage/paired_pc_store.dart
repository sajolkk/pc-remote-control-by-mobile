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
      );

  /// The PC's stable id. This, not its address, is the identity — a PC keeps working after
  /// its IP changes.
  final String deviceId;

  final String deviceName;

  /// The PC's certificate fingerprint, learned from the QR code and pinned from then on.
  final String fingerprint;

  final DateTime pairedAt;

  /// The last address this PC answered on. A hint for a fast reconnect, never an identity.
  final String? lastAddress;

  final int lastPort;
  final DateTime? lastSeenAt;

  /// The permissions the PC reported at the last handshake, for showing what is available.
  final List<String> permissions;

  Map<String, dynamic> toJson() => {
        'deviceId': deviceId,
        'deviceName': deviceName,
        'fingerprint': fingerprint,
        'pairedAt': pairedAt.toIso8601String(),
        if (lastAddress != null) 'lastAddress': lastAddress,
        'lastPort': lastPort,
        if (lastSeenAt != null) 'lastSeenAt': lastSeenAt!.toIso8601String(),
        'permissions': permissions,
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
      );
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
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
              iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
            );

  static const _key = 'paired.pcs';

  final FlutterSecureStorage _storage;

  Future<List<PairedPc>> getAll() async {
    final raw = await _storage.read(key: _key);
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

  Future<void> save(PairedPc pc) async {
    final all = (await getAll()).where((existing) => existing.deviceId != pc.deviceId).toList()
      ..add(pc);

    await _write(all);
  }

  Future<void> remove(String deviceId) async {
    final all = (await getAll()).where((pc) => pc.deviceId != deviceId).toList();
    await _write(all);
  }

  Future<void> clear() => _storage.delete(key: _key);

  Future<void> _write(List<PairedPc> devices) => _storage.write(
        key: _key,
        value: jsonEncode(devices.map((pc) => pc.toJson()).toList()),
      );
}
