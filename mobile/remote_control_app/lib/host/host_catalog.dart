import '../protocol/input_messages.dart';
import '../protocol/protocol.dart';

/// How far a connection must have got before a command is accepted. Mirrors the PC's
/// `CommandStage`.
enum CommandStage {
  /// A TLS connection whose certificate matches no paired viewer. Only pairing.
  unpaired,

  /// A paired viewer that has not completed `hello`, so no token exists. Only the handshake.
  paired,

  /// A valid session token bound to this connection.
  authenticated,
}

/// The declared contract of one command on a phone host.
class HostCommandDescriptor {
  const HostCommandDescriptor(this.name, {this.permission, this.stage = CommandStage.authenticated});

  final String name;

  /// The permission group the caller must hold, or null for any paired viewer.
  final String? permission;
  final CommandStage stage;
}

/// Every command a phone host will dispatch, declared once with its permission and stage.
///
/// The same rule as the PC's `CommandCatalog`: a name absent from this table cannot be dispatched,
/// and a name present cannot be dispatched with the wrong permission. Commands a PC has and a phone
/// does not (files, windows, browsers, power beyond lock) are simply not here, so they fail as
/// unknown rather than being half-implemented.
class HostCommandCatalog {
  const HostCommandCatalog._();

  static const _descriptors = <HostCommandDescriptor>[
    HostCommandDescriptor(Commands.pairRequest, stage: CommandStage.unpaired),
    HostCommandDescriptor(Commands.ping, stage: CommandStage.paired),
    HostCommandDescriptor(Commands.hello, stage: CommandStage.paired),
    HostCommandDescriptor(Commands.sessionRenew),
    HostCommandDescriptor(Commands.deviceInfo),
    HostCommandDescriptor(Commands.systemState),
    HostCommandDescriptor(Commands.permissionsList),

    HostCommandDescriptor(Commands.systemLock, permission: Permissions.powerControls),

    HostCommandDescriptor(Commands.appList, permission: Permissions.launchApps),
    HostCommandDescriptor(Commands.appLaunch, permission: Permissions.launchApps),
    HostCommandDescriptor(Commands.appFocus, permission: Permissions.launchApps),

    HostCommandDescriptor(Commands.screenMonitorList, permission: Permissions.viewScreen),
    HostCommandDescriptor(Commands.screenSelectMonitor, permission: Permissions.viewScreen),
    HostCommandDescriptor(Commands.screenScreenshot, permission: Permissions.viewScreen),
    HostCommandDescriptor(Commands.mediaOffer, permission: Permissions.viewScreen),
    HostCommandDescriptor(Commands.mediaIce, permission: Permissions.viewScreen),
    HostCommandDescriptor(Commands.mediaStop, permission: Permissions.viewScreen),
    HostCommandDescriptor(Commands.mediaSetQuality, permission: Permissions.viewScreen),

    HostCommandDescriptor(InputCommands.mouseMove, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.mouseButton, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.mouseClick, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.scroll, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.key, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.text, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.shortcut, permission: Permissions.controlInput),
    HostCommandDescriptor(InputCommands.releaseAll, permission: Permissions.controlInput),
    HostCommandDescriptor(Commands.phoneNav, permission: Permissions.controlInput),
  ];

  static final Map<String, HostCommandDescriptor> _byName = {
    for (final descriptor in _descriptors) descriptor.name: descriptor,
  };

  static Iterable<HostCommandDescriptor> get all => _descriptors;

  static HostCommandDescriptor? find(String? name) => name == null ? null : _byName[name];

  /// The input commands: the ones the block list drops while a blocked app is in front.
  static bool isInput(String name) =>
      name.startsWith('input.') || name == Commands.phoneNav;
}
