using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Shell;

/// <summary>One top-level window belonging to a process.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="ProcessId">Owning process.</param>
/// <param name="Title">Window title, for diagnostics.</param>
/// <param name="IsMinimized">Whether the window is currently minimized.</param>
public readonly record struct TopLevelWindow(nint Handle, uint ProcessId, string Title, bool IsMinimized);

/// <summary>
/// Enumerates and manipulates top-level windows (§4).
/// </summary>
/// <remarks>
/// A single enumeration pass serves the whole application catalog: building the list
/// per application would mean one <c>EnumWindows</c> sweep per app, which on a machine
/// with hundreds of installed programs is a visible stall.
/// <para>
/// "Main window" here means a visible top-level window with a title and no owner —
/// the same heuristic the taskbar uses. It is a heuristic: some applications keep a
/// hidden message-only window, and some (notably packaged apps hosted by
/// <c>ApplicationFrameHost</c>) attribute their window to a different process than the
/// one doing the work. Where the heuristic misses, the catalog reports
/// <c>hasWindow: false</c> and focus fails honestly rather than silently doing nothing.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowInspector
{
    private const uint GwOwner = 4;

    private readonly ILogger<WindowInspector> _logger;

    /// <summary>Creates the inspector.</summary>
    public WindowInspector(ILogger<WindowInspector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Enumerates candidate main windows, grouped by owning process id.
    /// </summary>
    public IReadOnlyDictionary<uint, List<TopLevelWindow>> EnumerateByProcess()
    {
        var result = new Dictionary<uint, List<TopLevelWindow>>();

        bool Callback(nint window, nint _)
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(window))
                {
                    return true;
                }

                // A window with an owner is a dialog or tool window, not the app's main
                // window; the taskbar applies the same rule.
                if (NativeMethods.GetWindow(window, GwOwner) != nint.Zero)
                {
                    return true;
                }

                int length = NativeMethods.GetWindowTextLength(window);
                if (length <= 0)
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(window, out uint processId);
                if (processId == 0)
                {
                    return true;
                }

                var title = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(window, title, title.Capacity);

                if (!result.TryGetValue(processId, out List<TopLevelWindow>? windows))
                {
                    windows = [];
                    result[processId] = windows;
                }

                windows.Add(new TopLevelWindow(
                    window,
                    processId,
                    title.ToString(),
                    NativeMethods.IsIconic(window)));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // A window can be destroyed mid-enumeration; that is normal, not an error.
                _logger.LogTrace(ex, "Skipped a window that disappeared during enumeration.");
            }

            return true;
        }

        if (!NativeMethods.EnumWindows(Callback, nint.Zero))
        {
            int error = Marshal.GetLastWin32Error();

            // EnumWindows reports failure when the callback stops it early, which we
            // never do, so a non-zero error here is worth noting but not fatal: a
            // partial window list degrades focus, it does not break anything.
            if (error != NativeMethods.ErrorSuccess)
            {
                _logger.LogDebug("EnumWindows returned false with error {Error}.", error);
            }
        }

        return result;
    }

    /// <summary>
    /// Enumerates the windows Alt+Tab would show, front to back.
    /// </summary>
    /// <remarks>
    /// Starts from the same "main window" heuristic as <see cref="EnumerateByProcess"/> — visible,
    /// titled, unowned — and then removes what a person would not call an open window: tool
    /// windows and no-activate overlays, cloaked windows (suspended Store apps, windows on another
    /// virtual desktop), and the shell's own desktop surfaces. <c>EnumWindows</c> walks in Z order,
    /// so the result is already ordered with the most recently used window first.
    /// </remarks>
    public IReadOnlyList<TopLevelWindow> EnumerateAltTabWindows()
    {
        var result = new List<TopLevelWindow>();

        bool Callback(nint window, nint _)
        {
            if (TryDescribeAltTabWindow(window, out TopLevelWindow described))
            {
                result.Add(described);
            }

            return true;
        }

        NativeMethods.EnumWindows(Callback, nint.Zero);
        return result;
    }

    /// <summary>
    /// Describes one window if it is a live, visible, top-level application window right now.
    /// </summary>
    /// <remarks>
    /// This is also the validation applied to a window handle that came back from a phone: a
    /// handle is just a number, numbers get reused, and the phone's list may be seconds old. A
    /// handle that no longer satisfies every rule here is treated as gone.
    /// </remarks>
    public bool TryDescribeAltTabWindow(nint window, out TopLevelWindow described)
    {
        described = default;

        try
        {
            if (window == nint.Zero || !NativeMethods.IsWindow(window) || !NativeMethods.IsWindowVisible(window))
            {
                return false;
            }

            // Top-level and unowned: not a child control, not a dialog hanging off another window.
            if (NativeMethods.GetAncestor(window, NativeMethods.GaRoot) != window ||
                NativeMethods.GetWindow(window, GwOwner) != nint.Zero)
            {
                return false;
            }

            long exStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle);
            bool appWindow = (exStyle & NativeMethods.WsExAppWindow) != 0;

            if ((exStyle & NativeMethods.WsExToolWindow) != 0 ||
                (!appWindow && (exStyle & NativeMethods.WsExNoActivate) != 0))
            {
                return false;
            }

            if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmwaCloaked, out int cloaked, sizeof(int)) == 0 &&
                cloaked != 0)
            {
                return false;
            }

            int length = NativeMethods.GetWindowTextLength(window);
            if (length <= 0)
            {
                return false;
            }

            var className = new StringBuilder(64);
            NativeMethods.GetClassName(window, className, className.Capacity);

            // The desktop itself and the taskbar are visible, titled, unowned windows too.
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0)
            {
                return false;
            }

            var title = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(window, title, title.Capacity);

            described = new TopLevelWindow(window, processId, title.ToString(), NativeMethods.IsIconic(window));
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogTrace(ex, "Skipped a window that disappeared while it was being examined.");
            return false;
        }
    }

    /// <summary>
    /// Brings a window to the foreground, restoring it if minimized.
    /// </summary>
    /// <remarks>
    /// Windows deliberately blocks focus theft, so this can fail even when everything is
    /// correct. <c>AllowSetForegroundWindow</c> asks for permission first, and the
    /// result is returned rather than swallowed so the client can say "couldn't focus"
    /// instead of appearing to succeed (§12.2).
    /// </remarks>
    public bool TryFocus(TopLevelWindow window)
    {
        if (window.Handle == nint.Zero)
        {
            return false;
        }

        NativeMethods.AllowSetForegroundWindow(window.ProcessId);

        if (window.IsMinimized)
        {
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SwRestore);
        }

        bool ok = NativeMethods.SetForegroundWindow(window.Handle);
        if (!ok)
        {
            _logger.LogInformation(
                "SetForegroundWindow was refused for process {ProcessId}. Windows blocks focus changes from " +
                "background processes; the window may have been restored but not focused.",
                window.ProcessId);
        }

        return ok;
    }

    /// <summary>
    /// Asks a window to close, the same way clicking its X does.
    /// </summary>
    /// <remarks>
    /// <c>WM_CLOSE</c> is posted rather than sent so that an unresponsive application
    /// cannot block the caller, and the application keeps its chance to prompt about
    /// unsaved work. That prompt appears on the PC's screen, which is the correct
    /// outcome: a remote close request must not discard someone's document silently.
    /// </remarks>
    public bool RequestClose(TopLevelWindow window)
    {
        if (window.Handle == nint.Zero)
        {
            return false;
        }

        bool posted = NativeMethods.PostMessage(window.Handle, NativeMethods.WmClose, nint.Zero, nint.Zero);
        if (!posted)
        {
            _logger.LogDebug(
                "PostMessage(WM_CLOSE) failed for process {ProcessId}: error {Error}.",
                window.ProcessId,
                Marshal.GetLastWin32Error());
        }

        return posted;
    }
}
