import 'dart:async';
import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../protocol/protocol.dart';

/// A device allowed to view and control this phone: the host-side pairing record, mirroring the
/// PC's `PairedDevice`.
class PairedViewer {
  const PairedViewer({
    required this.deviceId,
    required this.deviceName,
    required this.fingerprint,
    required this.pairedAt,
    this.platform = '',
    this.model = '',
    this.permissions = defaultPermissions,
    this.lastAddress,
    this.lastSeenAt,
    this.blocked = false,
  });

  /// What a newly paired viewer may do, the same as on a PC: look, and drive the touch screen and
  /// keyboard. Opening apps is granted separately.
  static const defaultPermissions = <String>[Permissions.viewScreen, Permissions.controlInput];

  /// Everything a phone host can grant. Power controls cover only locking the phone.
  static const allPermissions = <String>[
    Permissions.viewScreen,
    Permissions.controlInput,
    Permissions.launchApps,
    Permissions.powerControls,
  ];

  factory PairedViewer.fromJson(Map<String, dynamic> json) => PairedViewer(
        deviceId: json['deviceId'] as String,
        deviceName: json['deviceName'] as String? ?? 'Phone',
        fingerprint: json['fingerprint'] as String,
        pairedAt: DateTime.tryParse(json['pairedAt'] as String? ?? '') ?? DateTime(2026),
        platform: json['platform'] as String? ?? '',
        model: json['model'] as String? ?? '',
        permissions:
            (json['permissions'] as List<dynamic>? ?? const []).whereType<String>().toList(),
        lastAddress: json['lastAddress'] as String?,
        lastSeenAt:
            json['lastSeenAt'] == null ? null : DateTime.tryParse(json['lastSeenAt'] as String),
        blocked: json['blocked'] as bool? ?? false,
      );

  /// The viewer's self-generated id, the key of this record.
  final String deviceId;
  final String deviceName;
  final String platform;
  final String model;

  /// The viewer's certificate fingerprint, learned from the TLS connection it paired on. The
  /// only thing that identifies it afterwards.
  final String fingerprint;

  final DateTime pairedAt;
  final List<String> permissions;
  final String? lastAddress;
  final DateTime? lastSeenAt;

  /// Disconnected by the owner without being forgotten: the viewer's handshake is refused with
  /// `E_POLICY_DENIED` until the owner allows it again, exactly as on a PC.
  final bool blocked;

  bool granted(String permission) => permissions.contains(permission);

  Map<String, dynamic> toJson() => {
        'deviceId': deviceId,
        'deviceName': deviceName,
        'fingerprint': fingerprint,
        'pairedAt': pairedAt.toIso8601String(),
        'platform': platform,
        'model': model,
        'permissions': permissions,
        if (lastAddress != null) 'lastAddress': lastAddress,
        if (lastSeenAt != null) 'lastSeenAt': lastSeenAt!.toIso8601String(),
        'blocked': blocked,
      };

  PairedViewer copyWith({
    String? deviceName,
    List<String>? permissions,
    String? lastAddress,
    DateTime? lastSeenAt,
    bool? blocked,
  }) =>
      PairedViewer(
        deviceId: deviceId,
        deviceName: deviceName ?? this.deviceName,
        fingerprint: fingerprint,
        pairedAt: pairedAt,
        platform: platform,
        model: model,
        permissions: permissions ?? this.permissions,
        lastAddress: lastAddress ?? this.lastAddress,
        lastSeenAt: lastSeenAt ?? this.lastSeenAt,
        blocked: blocked ?? this.blocked,
      );
}

/// Where paired viewers are kept: the PC's `IPairingStore`, on a phone.
///
/// Abstract so the host's pairing and handshake logic can be tested against an in-memory store.
abstract class PairedViewerStore {
  Future<List<PairedViewer>> getAll();
  Future<PairedViewer?> findByDeviceId(String deviceId);
  Future<PairedViewer?> findByFingerprint(String fingerprint);
  Future<void> save(PairedViewer viewer);
  Future<void> remove(String deviceId);
  Future<void> clear();

  /// Fires after every change, so live connections can re-read their viewer's record: a
  /// permission revoked by the owner applies to the next command, not the next connection.
  Stream<void> get changes;
}

/// Keeps the records in memory. For tests, and as the base of the persistent store.
class InMemoryPairedViewerStore implements PairedViewerStore {
  final _byId = <String, PairedViewer>{};
  final _changes = StreamController<void>.broadcast();

  @override
  Stream<void> get changes => _changes.stream;

  @override
  Future<List<PairedViewer>> getAll() async => _byId.values.toList(growable: false);

  @override
  Future<PairedViewer?> findByDeviceId(String deviceId) async => _byId[deviceId];

  @override
  Future<PairedViewer?> findByFingerprint(String fingerprint) async {
    for (final viewer in _byId.values) {
      if (viewer.fingerprint == fingerprint) return viewer;
    }
    return null;
  }

  @override
  Future<void> save(PairedViewer viewer) async {
    _byId[viewer.deviceId] = viewer;
    _notify();
  }

  @override
  Future<void> remove(String deviceId) async {
    if (_byId.remove(deviceId) != null) _notify();
  }

  @override
  Future<void> clear() async {
    _byId.clear();
    _notify();
  }

  void _notify() {
    if (!_changes.isClosed) _changes.add(null);
  }
}

/// Persists paired viewers through `flutter_secure_storage`, for the reason `PairedPcStore`
/// does: the fingerprints are what keep an impostor out, so another app must not be able to
/// edit them.
class SecurePairedViewerStore implements PairedViewerStore {
  SecurePairedViewerStore({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true, resetOnError: true),
              iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
            );

  static const _key = 'host.paired_viewers';

  final FlutterSecureStorage _storage;
  final _changes = StreamController<void>.broadcast();

  /// Every read-modify-write goes through here, so two saves cannot drop each other's record.
  Future<void> _queue = Future.value();

  Future<T> _serialized<T>(Future<T> Function() action) {
    final result = _queue.then((_) => action());
    _queue = result.then<void>((_) {}, onError: (_) {});
    return result;
  }

  @override
  Stream<void> get changes => _changes.stream;

  @override
  Future<List<PairedViewer>> getAll() async {
    String? raw;
    try {
      raw = await _storage.read(key: _key);
    } on Object {
      return const [];
    }

    if (raw == null || raw.isEmpty) return const [];

    try {
      final decoded = jsonDecode(raw);
      if (decoded is! List) return const [];
      return decoded.whereType<Map<String, dynamic>>().map(PairedViewer.fromJson).toList();
    } on Object {
      // A corrupt store means the owner approves viewers again; crashing on launch would not.
      return const [];
    }
  }

  @override
  Future<PairedViewer?> findByDeviceId(String deviceId) async {
    for (final viewer in await getAll()) {
      if (viewer.deviceId == deviceId) return viewer;
    }
    return null;
  }

  @override
  Future<PairedViewer?> findByFingerprint(String fingerprint) async {
    for (final viewer in await getAll()) {
      if (viewer.fingerprint == fingerprint) return viewer;
    }
    return null;
  }

  @override
  Future<void> save(PairedViewer viewer) => _serialized(() async {
        final all = (await getAll()).where((v) => v.deviceId != viewer.deviceId).toList()
          ..add(viewer);
        await _write(all);
      });

  @override
  Future<void> remove(String deviceId) => _serialized(() async {
        await _write((await getAll()).where((v) => v.deviceId != deviceId).toList());
      });

  @override
  Future<void> clear() => _serialized(() async {
        await _storage.delete(key: _key);
        if (!_changes.isClosed) _changes.add(null);
      });

  Future<void> _write(List<PairedViewer> viewers) async {
    await _storage.write(key: _key, value: jsonEncode(viewers.map((v) => v.toJson()).toList()));
    if (!_changes.isClosed) _changes.add(null);
  }
}
