using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// The application registry and launcher, implemented by the session agent (§4).
/// </summary>
/// <remarks>
/// The interface takes ids, never paths. A remote caller can only name something
/// this PC already enumerated, so "no arbitrary executable paths from the network"
/// is enforced by the shape of the API rather than by a validation step someone
/// could forget to call.
/// </remarks>
public interface IAppCatalog
{
    /// <summary>Lists applications, filtered and optionally with icons.</summary>
    Task<AppListResult> ListAsync(AppListArgs args, CancellationToken cancellationToken);

    /// <summary>Looks up one entry by id.</summary>
    Task<AppInfo?> FindAsync(string appId, CancellationToken cancellationToken);

    /// <summary>Launches an application by id.</summary>
    Task<AppActionResult> LaunchAsync(string appId, CancellationToken cancellationToken);

    /// <summary>Brings an application's main window to the foreground.</summary>
    Task<AppActionResult> FocusAsync(string appId, CancellationToken cancellationToken);

    /// <summary>
    /// Closes an application. Implementations must request a graceful close first
    /// and only terminate when <paramref name="force"/> is set, so unsaved work is
    /// never discarded without the user having asked for it.
    /// </summary>
    Task<AppActionResult> CloseAsync(string appId, bool force, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>Rebuilds the catalog from the Start Menu, registry and package manager.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Browser detection and control, implemented by the session agent (§4).
/// </summary>
public interface IBrowserController
{
    /// <summary>Lists detected browsers, default first.</summary>
    Task<BrowserListResult> ListAsync(CancellationToken cancellationToken);

    /// <summary>Opens a browser at its start page.</summary>
    Task<AppActionResult> OpenAsync(string? browserId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a URL. The URL must already have passed
    /// <c>UrlValidator</c>; implementations pass it as a single argument and never
    /// build a shell command line from it.
    /// </summary>
    Task<AppActionResult> OpenUrlAsync(string url, string? browserId, CancellationToken cancellationToken);

    /// <summary>Closes a browser.</summary>
    Task<AppActionResult> CloseAsync(string? browserId, bool force, CancellationToken cancellationToken);
}

/// <summary>
/// Master volume control, implemented by the session agent over Core Audio (§4).
/// </summary>
public interface IVolumeController
{
    /// <summary>Whether an audio endpoint exists to control.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads volume 0-100 and mute state.</summary>
    Task<(int Volume, bool Muted)> GetAsync(CancellationToken cancellationToken);

    /// <summary>Sets volume. Values outside 0-100 are clamped by the implementation.</summary>
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken);

    /// <summary>Sets mute, or toggles it when <paramref name="muted"/> is null.</summary>
    Task SetMuteAsync(bool? muted, CancellationToken cancellationToken);
}

/// <summary>
/// Locks the workstation from inside the interactive session.
/// </summary>
/// <remarks>
/// Separate from <see cref="IPowerController"/> because <c>LockWorkStation</c> only
/// works when called on the interactive desktop — a service in Session 0 cannot
/// lock a user's screen directly, so the service delegates here (§10).
/// </remarks>
public interface IWorkstationLocker
{
    /// <summary>Locks the workstation. Returns false if Windows refused.</summary>
    Task<bool> LockAsync(CancellationToken cancellationToken);
}

/// <summary>What <c>window.action</c> can ask of a window.</summary>
public enum WindowAction
{
    /// <summary>Bring it to the foreground, restoring it first if minimized.</summary>
    Focus,

    /// <summary>Minimize it to the taskbar.</summary>
    Minimize,

    /// <summary>Maximize it.</summary>
    Maximize,

    /// <summary>Return it to its normal size and position.</summary>
    Restore,

    /// <summary>Ask it to close, as clicking its X does. Never a kill.</summary>
    Close,
}

/// <summary>
/// Lists and manipulates top-level application windows, implemented by the session agent (§4).
/// </summary>
/// <remarks>
/// Ids only, like <see cref="IAppCatalog"/>: a caller can act on a window this PC listed and on
/// nothing else, and the implementation re-validates the id on every use because a window can
/// close — and its handle be reused by an unrelated one — between the list and the action.
/// </remarks>
public interface IWindowManager
{
    /// <summary>The visible top-level application windows, foreground first.</summary>
    IReadOnlyList<WindowInfo> List();

    /// <summary>
    /// Performs an action on one window. Returns null when the id no longer names a live, visible
    /// top-level window.
    /// </summary>
    WindowActionResult? Act(string windowId, WindowAction action);
}
