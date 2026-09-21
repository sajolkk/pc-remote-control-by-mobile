import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../data/discovery/discovery_service.dart';
import '../data/identity/device_identity.dart';
import '../data/storage/paired_pc_store.dart';
import '../domain/connection_controller.dart';
import '../protocol/protocol.dart';

/// This device's identity store.
final identityStoreProvider = Provider<DeviceIdentityStore>((ref) => DeviceIdentityStore());

/// The identity itself, created on first access.
final identityProvider = FutureProvider<DeviceIdentity>(
  (ref) => ref.watch(identityStoreProvider).getOrCreate(),
);

/// Paired PCs.
final pairedStoreProvider = Provider<PairedPcStore>((ref) => PairedPcStore());

/// The paired PC list. Invalidate after pairing or removing to refresh the UI.
final pairedPcsProvider = FutureProvider<List<PairedPc>>(
  (ref) => ref.watch(pairedStoreProvider).getAll(),
);

/// LAN discovery.
final discoveryProvider = Provider<DiscoveryService>((ref) => DiscoveryService());

/// A one-shot scan of the network.
///
/// Autodisposed so leaving the screen stops the scan rather than leaving a socket and a
/// repeating timer alive in the background.
final discoveredPcsProvider = StreamProvider.autoDispose<List<DiscoveryBeacon>>((ref) {
  final discovery = ref.watch(discoveryProvider);
  final found = <String, DiscoveryBeacon>{};

  return discovery.scan(duration: const Duration(seconds: 6)).map((beacon) {
    found[beacon.deviceId] = beacon;
    return found.values.toList(growable: false);
  });
});

/// The connection controller, shared across every screen.
final connectionProvider = ChangeNotifierProvider<ConnectionController>((ref) {
  final controller = ConnectionController(
    identityStore: ref.watch(identityStoreProvider),
    pairedStore: ref.watch(pairedStoreProvider),
    discovery: ref.watch(discoveryProvider),
  );

  ref.onDispose(controller.dispose);
  return controller;
});
