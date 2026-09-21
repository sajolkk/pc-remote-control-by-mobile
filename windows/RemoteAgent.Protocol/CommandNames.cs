namespace RemoteAgent.Protocol;

/// <summary>
/// Every command name the system understands (architecture doc, Appendix A).
/// </summary>
/// <remarks>
/// Names are lower-case dotted, <c>area.verb</c>. They are a wire contract and
/// never change once shipped. Commands are grouped by the phase that introduces
/// them so the MVP surface stays obvious.
/// </remarks>
public static class CommandNames
{
    // --- Handshake and identity (always available to a paired device) ---

    /// <summary>Latency probe and liveness check.</summary>
    public const string Ping = "ping";

    /// <summary>Opens the session: negotiates version, returns capabilities and a session token.</summary>
    public const string Hello = "hello";

    /// <summary>Static facts about the PC: name, OS, hardware summary, capability list.</summary>
    public const string DeviceInfo = "device.info";

    /// <summary>Live state: session status (active/locked/logged-out), uptime, volume, monitor count.</summary>
    public const string SystemState = "system.state";

    /// <summary>The permission groups this device has been granted.</summary>
    public const string PermissionsList = "permissions.list";

    /// <summary>Requests a fresh session token before the current one expires.</summary>
    public const string SessionRenew = "session.renew";

    // --- Pairing (pre-authentication; guarded by pairing mode, not by permissions) ---

    /// <summary>Submits a pairing token and waits for the user's approval at the PC.</summary>
    public const string PairRequest = "pair.request";

    // --- Power (Permission.PowerControls) ---

    /// <summary>Locks the workstation.</summary>
    public const string SystemLock = "system.lock";

    /// <summary>Signs the interactive user out.</summary>
    public const string SystemSignOut = "system.signout";

    /// <summary>Puts the PC to sleep (S3, or S4 when sleep is unavailable).</summary>
    public const string SystemSleep = "system.sleep";

    /// <summary>Restarts Windows.</summary>
    public const string SystemRestart = "system.restart";

    /// <summary>Shuts Windows down.</summary>
    public const string SystemShutdown = "system.shutdown";

    /// <summary>Cancels a pending restart or shutdown inside its grace period.</summary>
    public const string SystemAbortShutdown = "system.abort_shutdown";

    // --- Applications and browsers (Permission.LaunchApps) ---

    /// <summary>Lists known applications, optionally filtered to running ones.</summary>
    public const string AppList = "app.list";

    /// <summary>Launches an application by its registry id. Never accepts a path.</summary>
    public const string AppLaunch = "app.launch";

    /// <summary>Brings a running application's main window to the foreground.</summary>
    public const string AppFocus = "app.focus";

    /// <summary>Closes an application: a close request first, force-terminate only if asked.</summary>
    public const string AppClose = "app.close";

    /// <summary>Lists installed browsers and which one is the default.</summary>
    public const string BrowserList = "browser.list";

    /// <summary>Opens a browser at its home page.</summary>
    public const string BrowserOpen = "browser.open";

    /// <summary>Opens a validated http/https URL in a browser.</summary>
    public const string BrowserOpenUrl = "browser.open_url";

    /// <summary>Closes a browser.</summary>
    public const string BrowserClose = "browser.close";

    // --- Screen and media (Permission.ViewScreen) — Phase 3 ---

    /// <summary>Enumerates monitors with bounds, DPI and refresh rate.</summary>
    public const string ScreenMonitorList = "screen.monitor_list";

    /// <summary>Re-targets capture at a different monitor.</summary>
    public const string ScreenSelectMonitor = "screen.select_monitor";

    /// <summary>Captures a single still image of a monitor.</summary>
    public const string ScreenScreenshot = "screen.screenshot";

    /// <summary>Starts media negotiation with an SDP offer.</summary>
    public const string MediaOffer = "media.offer";

    /// <summary>Delivers an ICE candidate.</summary>
    public const string MediaIce = "media.ice";

    /// <summary>Tears the media session down.</summary>
    public const string MediaStop = "media.stop";

    /// <summary>Requests a streaming quality change (resolution, FPS or bitrate ceiling).</summary>
    public const string MediaSetQuality = "media.set_quality";

    // --- Input (Permission.ControlInput) — Phase 4 ---

    /// <summary>Moves the pointer, absolute or relative.</summary>
    public const string InputMouseMove = "input.mouse_move";

    /// <summary>Presses or releases a single mouse button.</summary>
    public const string InputMouseButton = "input.mouse_button";

    /// <summary>A complete click: press and release, optionally a double click.</summary>
    public const string InputMouseClick = "input.mouse_click";

    /// <summary>Scrolls vertically or horizontally.</summary>
    public const string InputScroll = "input.scroll";

    /// <summary>Presses or releases a single key by virtual-key code.</summary>
    public const string InputKey = "input.key";

    /// <summary>Types a Unicode string.</summary>
    public const string InputText = "input.text";

    /// <summary>Sends a modifier + key shortcut as one atomic sequence.</summary>
    public const string InputShortcut = "input.shortcut";

    /// <summary>Releases every held key and button. Sent on disconnect and on focus loss.</summary>
    public const string InputReleaseAll = "input.release_all";

    // --- Clipboard (Permission.Clipboard) — Phase 5 ---

    /// <summary>Reads clipboard text.</summary>
    public const string ClipboardGet = "clipboard.get";

    /// <summary>Writes clipboard text.</summary>
    public const string ClipboardSet = "clipboard.set";

    // --- Files (Permission.ManageFiles) — Phase 5 ---

    /// <summary>Lists entries inside one configured allowed root.</summary>
    public const string FileList = "file.list";

    /// <summary>Begins a mobile-to-PC transfer.</summary>
    public const string FileUpload = "file.upload";

    /// <summary>Begins a PC-to-mobile transfer.</summary>
    public const string FileDownload = "file.download";

    /// <summary>Cancels an in-flight transfer.</summary>
    public const string FileCancel = "file.cancel";

    // --- Audio and power info (Phase 5) ---

    /// <summary>Reads master volume and mute state.</summary>
    public const string VolumeGet = "volume.get";

    /// <summary>Sets master volume.</summary>
    public const string VolumeSet = "volume.set";

    /// <summary>Sets or toggles mute.</summary>
    public const string VolumeMute = "volume.mute";

    /// <summary>Reports whether Wake-on-LAN will actually work, and the MACs to target.</summary>
    public const string WolInfo = "wol.info";
}

/// <summary>
/// Event topics pushed from the PC to subscribed clients.
/// </summary>
public static class EventNames
{
    /// <summary>Power or system-level state changed.</summary>
    public const string SystemStateChanged = "system.state_changed";

    /// <summary>The interactive session locked, unlocked, logged on or logged off.</summary>
    public const string SessionStateChanged = "session.state_changed";

    /// <summary>Monitor topology changed (hot-plug, resolution or arrangement).</summary>
    public const string MonitorsChanged = "monitors.changed";

    /// <summary>Clipboard content changed on the PC.</summary>
    public const string ClipboardChanged = "clipboard.changed";

    /// <summary>An application started or exited.</summary>
    public const string AppStateChanged = "app.state_changed";

    /// <summary>Progress of a file transfer.</summary>
    public const string TransferProgress = "transfer.progress";

    /// <summary>Streaming quality telemetry.</summary>
    public const string MediaQuality = "media.quality";

    /// <summary>This device's permissions were changed at the PC.</summary>
    public const string PermissionsChanged = "permissions.changed";

    /// <summary>This device's pairing was revoked. The connection closes immediately after.</summary>
    public const string PairingRevoked = "pairing.revoked";
}
