namespace RemoteAgent.Protocol;

/// <summary>
/// Which process is able to execute a command.
/// </summary>
public enum CommandTarget
{
    /// <summary>
    /// Executed by the Windows service itself: privileged operations and anything
    /// that must work with no interactive user logged on.
    /// </summary>
    Service,

    /// <summary>
    /// Executed by the user-session agent: anything touching the desktop, the
    /// shell, the clipboard, audio, or the user's files. Forwarded over IPC and
    /// unavailable when no session agent is running (§4).
    /// </summary>
    Session,

    /// <summary>
    /// Implemented in both hosts, with the service's version preferred because it can decide
    /// whether to forward.
    /// </summary>
    /// <remarks>
    /// Only for commands where each host does the job by a genuinely different mechanism.
    /// <c>system.lock</c> is the example: the session agent calls <c>LockWorkStation</c> on the
    /// real desktop, while the service can only disconnect the session — the same user-visible
    /// result, available when nobody is signed in. The service handler tries the agent first and
    /// falls back, so the client gets one command that works in both states.
    /// </remarks>
    Either,
}

/// <summary>
/// The declared contract for one command: who may call it, who executes it, and
/// whether it is reachable before authentication.
/// </summary>
/// <param name="Name">The wire name from <see cref="CommandNames"/>.</param>
/// <param name="RequiredPermission">
/// The permission group the caller must hold. <see cref="Permission.None"/> means
/// any paired device may call it.
/// </param>
/// <param name="Target">Which process executes it.</param>
/// <param name="Stage">
/// The point in the connection lifecycle at which the command becomes callable.
/// </param>
public sealed record CommandDescriptor(
    string Name,
    Permission RequiredPermission,
    CommandTarget Target,
    CommandStage Stage = CommandStage.Authenticated);

/// <summary>
/// How far a connection must have progressed before a command is accepted.
/// </summary>
public enum CommandStage
{
    /// <summary>
    /// Callable on a TLS connection whose certificate is NOT yet matched to a
    /// paired device. Only pairing uses this, and it is separately gated by
    /// pairing mode, a single-use token, and local user approval (§8.1).
    /// </summary>
    Unpaired,

    /// <summary>
    /// Callable by a paired device that has not yet completed <c>hello</c>, so no
    /// session token exists. Only the handshake itself uses this.
    /// </summary>
    Paired,

    /// <summary>Callable only with a valid session token bound to this connection.</summary>
    Authenticated,
}

/// <summary>
/// The authoritative command registry (architecture doc §13 rule 5).
/// </summary>
/// <remarks>
/// Every command that exists is declared here exactly once, with its permission
/// and its executing process. The dispatcher consults this catalog before any
/// argument is deserialized, which is what makes "no arbitrary command execution"
/// a structural property rather than a promise: a name absent from this table
/// cannot be dispatched, and a name present here cannot be dispatched with the
/// wrong permission.
/// <para>
/// Declaring a command here does not mean this build implements it. Whether a
/// handler is actually registered is reported through capability negotiation, and
/// an unimplemented-but-declared command returns
/// <see cref="ErrorCodes.NotSupported"/>. That separation is what lets one binary
/// serve PCs with different capabilities (§0).
/// </para>
/// </remarks>
public static class CommandCatalog
{
    private static readonly CommandDescriptor[] Descriptors =
    [
        // Handshake and identity.
        new(CommandNames.Ping, Permission.None, CommandTarget.Service, CommandStage.Paired),
        new(CommandNames.Hello, Permission.None, CommandTarget.Service, CommandStage.Paired),
        new(CommandNames.SessionRenew, Permission.None, CommandTarget.Service),
        new(CommandNames.DeviceInfo, Permission.None, CommandTarget.Service),
        new(CommandNames.SystemState, Permission.None, CommandTarget.Service),
        new(CommandNames.PermissionsList, Permission.None, CommandTarget.Service),

        // Pairing: the only command reachable without a pairing record.
        new(CommandNames.PairRequest, Permission.None, CommandTarget.Service, CommandStage.Unpaired),

        // Power. Executed by the service so they work with nobody logged on.
        new(CommandNames.SystemLock, Permission.PowerControls, CommandTarget.Either),
        new(CommandNames.SystemSignOut, Permission.PowerControls, CommandTarget.Service),
        new(CommandNames.SystemSleep, Permission.PowerControls, CommandTarget.Service),
        new(CommandNames.SystemRestart, Permission.PowerControls, CommandTarget.Service),
        new(CommandNames.SystemShutdown, Permission.PowerControls, CommandTarget.Service),
        new(CommandNames.SystemAbortShutdown, Permission.PowerControls, CommandTarget.Service),

        // Applications and browsers. Session-scoped: they need the user's shell.
        new(CommandNames.AppList, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.AppLaunch, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.AppFocus, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.AppClose, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.BrowserList, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.BrowserOpen, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.BrowserOpenUrl, Permission.LaunchApps, CommandTarget.Session),
        new(CommandNames.BrowserClose, Permission.LaunchApps, CommandTarget.Session),

        // Screen and media.
        new(CommandNames.ScreenMonitorList, Permission.ViewScreen, CommandTarget.Session),
        new(CommandNames.ScreenSelectMonitor, Permission.ViewScreen, CommandTarget.Session),
        new(CommandNames.ScreenScreenshot, Permission.ViewScreen, CommandTarget.Session),
        new(CommandNames.MediaOffer, Permission.ViewScreen, CommandTarget.Session),
        new(CommandNames.MediaIce, Permission.ViewScreen, CommandTarget.Session),
        new(CommandNames.MediaStop, Permission.ViewScreen, CommandTarget.Session),
        new(CommandNames.MediaSetQuality, Permission.ViewScreen, CommandTarget.Session),

        // Input.
        new(CommandNames.InputMouseMove, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputMouseButton, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputMouseClick, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputScroll, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputKey, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputText, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputShortcut, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.InputReleaseAll, Permission.ControlInput, CommandTarget.Session),

        // Clipboard.
        new(CommandNames.ClipboardGet, Permission.Clipboard, CommandTarget.Session),
        new(CommandNames.ClipboardSet, Permission.Clipboard, CommandTarget.Session),

        // Files.
        new(CommandNames.FileList, Permission.ManageFiles, CommandTarget.Session),
        new(CommandNames.FileUpload, Permission.ManageFiles, CommandTarget.Session),
        new(CommandNames.FileDownload, Permission.ManageFiles, CommandTarget.Session),
        new(CommandNames.FileCancel, Permission.ManageFiles, CommandTarget.Session),

        // Audio and wake info.
        new(CommandNames.VolumeGet, Permission.None, CommandTarget.Session),
        new(CommandNames.VolumeSet, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.VolumeMute, Permission.ControlInput, CommandTarget.Session),
        new(CommandNames.WolInfo, Permission.None, CommandTarget.Service),
    ];

    private static readonly Dictionary<string, CommandDescriptor> ByName =
        Descriptors.ToDictionary(static d => d.Name, StringComparer.Ordinal);

    /// <summary>Every declared command.</summary>
    public static IReadOnlyList<CommandDescriptor> All { get; } = Descriptors;

    /// <summary>
    /// Looks up a command by wire name. Returns <c>false</c> for any name not
    /// declared above — the structural guarantee against arbitrary execution.
    /// </summary>
    public static bool TryGet(string? name, out CommandDescriptor descriptor)
    {
        if (name is null)
        {
            descriptor = null!;
            return false;
        }

        return ByName.TryGetValue(name, out descriptor!);
    }

    /// <summary>Commands executed by the given process.</summary>
    public static IEnumerable<CommandDescriptor> ForTarget(CommandTarget target) =>
        Descriptors.Where(d => d.Target == target);
}
