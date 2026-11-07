import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../data/discovery/discovery_service.dart';
import '../data/identity/device_identity.dart';
import '../data/storage/paired_pc_store.dart';
import '../domain/connection_controller.dart';
import '../domain/connection_manager.dart';
import '../host/android_host_platform.dart';
import '../host/host_controller.dart';
import '../host/host_identity.dart';
import '../host/paired_viewer_store.dart';
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
///
/// Emits an empty list when the scan ends with no answers. Without it the provider would never
/// leave its loading state and the screen would say "Scanning…" forever.
final discoveredPcsProvider = StreamProvider.autoDispose<List<DiscoveryBeacon>>((ref) async* {
  final discovery = ref.watch(discoveryProvider);
  final found = <String, DiscoveryBeacon>{};

  await for (final beacon in discovery.scan(duration: const Duration(seconds: 6))) {
    found[beacon.deviceId] = beacon;
    yield found.values.toList(growable: false);
  }

  if (found.isEmpty) yield const <DiscoveryBeacon>[];
});

/// Every PC connection, one per PC, and which PC is selected.
final connectionManagerProvider = ChangeNotifierProvider<ConnectionManager>((ref) {
  final identityStore = ref.watch(identityStoreProvider);
  final pairedStore = ref.watch(pairedStoreProvider);
  final discovery = ref.watch(discoveryProvider);

  return ConnectionManager(
    store: pairedStore,
    create: () => ConnectionController(
      identityStore: identityStore,
      pairedStore: pairedStore,
      discovery: discovery,
    ),
  );
});

/// The certificate this phone presents when it is the one being controlled (host mode).
final hostIdentityStoreProvider = Provider<HostIdentityStore>(
  (ref) => HostIdentityStore(deviceIdentity: ref.watch(identityStoreProvider)),
);

/// The devices allowed to view and control this phone.
final pairedViewerStoreProvider = Provider<PairedViewerStore>((ref) => SecurePairedViewerStore());

/// The native half of host mode.
final hostPlatformProvider = Provider<AndroidHostPlatform>((ref) => AndroidHostPlatform());

/// This phone as a host: sharing state, the QR code, viewers and the block list. One for the
/// whole app, because sharing outlives the screen that started it.
final hostControllerProvider = ChangeNotifierProvider<HostController>(
  (ref) => HostController(
    identityStore: ref.watch(hostIdentityStoreProvider),
    viewerStore: ref.watch(pairedViewerStoreProvider),
    platform: ref.watch(hostPlatformProvider),
    deviceName: defaultHostName,
  ),
);

/// The selected PC's connection: the one the System, Manage and Settings tabs act on.
///
/// Changes only when another PC is selected. Watch [connectionStatusProvider] to follow what
/// the connection is doing.
final connectionProvider = Provider<ConnectionController>(
  (ref) => ref.watch(connectionManagerProvider.select((manager) => manager.active)),
);

/// The selected PC's connection status, updated as it changes.
final connectionStatusProvider = Provider<ConnectionStatus>((ref) {
  final connection = ref.watch(connectionProvider);

  void update() => ref.state = connection.status;
  connection.addListener(update);
  ref.onDispose(() => connection.removeListener(update));

  return connection.status;
});
