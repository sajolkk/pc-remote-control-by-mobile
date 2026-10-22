// The constructor takes public parameter names and assigns them to private fields, which the
// linter would prefer written as initializing formals. Private initializing formals are not
// valid Dart, so the assignment is deliberate here.
// ignore_for_file: prefer_initializing_formals

import 'package:flutter/foundation.dart';

import '../data/storage/paired_pc_store.dart';
import 'connection_controller.dart';

/// Every PC this phone is connected to, one [ConnectionController] each, and which of them the
/// System, Manage and Settings tabs are currently working with.
///
/// Each PC keeps its own connection, reconnects on its own and fails on its own: one PC going to
/// sleep says nothing about another. The screen is opened for a particular PC, so several can be
/// connected while one is being watched.
///
/// Notifies whenever any of the connections changes, or the selection does.
class ConnectionManager extends ChangeNotifier {
  ConnectionManager({
    required ConnectionController Function() create,
    PairedPcStore? store,
  })  : _create = create,
        _store = store;

  final ConnectionController Function() _create;

  /// Where the PCs to reconnect at start-up are remembered. Null in tests that do not care.
  final PairedPcStore? _store;

  final _byId = <String, ConnectionController>{};
  String? _activeId;
  bool _disposed = false;

  /// Stands in when no PC is selected, so the tabs always have a connection to ask — one that
  /// says "not connected".
  late final ConnectionController _none = _create();

  /// The PC the System, Manage and Settings tabs act on. Never null: with no PC selected it is a
  /// connection that is not connected.
  ConnectionController get active => _byId[_activeId] ?? _none;

  /// The id of the selected PC, or null when none is.
  String? get activeId => _activeId;

  /// The connection to [deviceId], or null when this phone has never tried to connect to it.
  ConnectionController? of(String deviceId) => _byId[deviceId];

  /// The status of the connection to [deviceId]; idle when there is none.
  ConnectionStatus statusOf(String deviceId) => _byId[deviceId]?.status ?? const ConnectionStatus();

  /// The status of every connection this phone has tried.
  Iterable<ConnectionStatus> get statuses => _byId.values.map((controller) => controller.status);

  /// Every connection that is up, with the selected one first.
  List<ConnectionController> get connected {
    final up = [
      for (final entry in _byId.entries)
        if (entry.value.status.isConnected) entry.value,
    ];

    final selected = _byId[_activeId];
    if (selected != null && up.remove(selected)) up.insert(0, selected);
    return up;
  }

  /// Connects to [pc], leaving any other PC connected, and keeps reconnecting to it until
  /// [disconnect]. With [select] it also becomes the PC the tabs work with.
  Future<void> connect(PairedPc pc, {bool select = true}) async {
    final controller = _controllerFor(pc.deviceId);
    if (select) this.select(pc.deviceId);

    _store?.setAutoConnect(pc.deviceId, true).ignore();
    await controller.connect(pc);
  }

  /// Disconnects from one PC. The others stay connected; if it was the selected one, another
  /// connected PC takes its place.
  Future<void> disconnect(String deviceId) async {
    _store?.setAutoConnect(deviceId, false).ignore();

    final controller = _byId[deviceId];
    if (controller == null) return;

    await controller.disconnect();
    if (_activeId == deviceId) _selectAnotherConnected();
  }

  /// Disconnects from the selected PC.
  Future<void> disconnectActive() async {
    final id = _activeId;
    if (id != null) await disconnect(id);
  }

  /// Disconnects from every PC without changing which ones reconnect at start-up — for when the
  /// pairings themselves are about to be wiped.
  Future<void> disconnectAll() async {
    await Future.wait([for (final controller in _byId.values) controller.disconnect()]);
  }

  /// Disconnects and drops a PC this phone has forgotten.
  Future<void> forget(String deviceId) async {
    final controller = _byId.remove(deviceId);
    if (_activeId == deviceId) _activeId = null;

    if (controller != null) {
      controller.removeListener(_changed);
      await controller.disconnect();
      controller.dispose();
    }

    _selectAnotherConnected();
    _changed();
  }

  /// Makes [deviceId] the PC the tabs work with.
  void select(String deviceId) {
    _controllerFor(deviceId);
    if (_activeId == deviceId) return;

    _activeId = deviceId;
    _changed();
  }

  /// Connects to the PCs that were connected when the app was last used.
  ///
  /// The first time — on an install from before several PCs could be connected — that is
  /// unknown, and the PC used most recently is connected, as it always was.
  Future<void> restore(List<PairedPc> pcs) async {
    if (pcs.isEmpty) return;

    final wanted = await _store?.getAutoConnect();
    final toConnect = wanted == null
        ? [_mostRecent(pcs)]
        : pcs.where((pc) => wanted.contains(pc.deviceId)).toList();

    // The most recently used first, so it is the one selected.
    toConnect.sort((a, b) => _seen(b).compareTo(_seen(a)));

    for (final pc in toConnect) {
      final existing = _byId[pc.deviceId];
      if (existing != null && existing.status.phase != ConnectionPhase.idle) continue;

      // connect() selects before it awaits anything, so only the first one is selected.
      connect(pc, select: _activeId == null).ignore();
    }
  }

  static DateTime _seen(PairedPc pc) => pc.lastSeenAt ?? pc.pairedAt;

  static PairedPc _mostRecent(List<PairedPc> pcs) =>
      pcs.reduce((a, b) => _seen(b).isAfter(_seen(a)) ? b : a);

  ConnectionController _controllerFor(String deviceId) => _byId.putIfAbsent(deviceId, () {
        final controller = _create();
        controller.addListener(_changed);
        return controller;
      });

  void _selectAnotherConnected() {
    final current = _byId[_activeId];
    if (current != null && current.status.isConnected) return;

    for (final entry in _byId.entries) {
      if (entry.value.status.isConnected) {
        _activeId = entry.key;
        _changed();
        return;
      }
    }
  }

  void _changed() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;

    for (final controller in _byId.values) {
      controller.removeListener(_changed);
      controller.dispose();
    }

    _byId.clear();
    _none.dispose();
    super.dispose();
  }
}
