using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Shell;

/// <summary>
/// Lists the windows Alt+Tab shows and focuses, resizes or closes one of them (§4).
/// </summary>
/// <remarks>
/// <para><b>The id is the handle and the process that owned it.</b> A window id on the wire is
/// <c>w{handle}-{pid}</c> in hex. A bare handle would not be enough to act on safely: Windows reuses
/// handle values, so the number a phone was given a minute ago may by now belong to a different
/// window of a different program. Pairing it with the owning process id, and re-checking on every
/// action that the handle is still a visible top-level application window of that same process,
/// means a stale id is reported as gone rather than acted on.</para>
///
/// <para><b>Close is a request.</b> <c>WM_CLOSE</c> is posted, which is what the X button does: the
/// application may prompt about unsaved work on the PC's screen, and may decline. Nothing here
/// terminates a process — <c>app.close</c> with <c>force</c> is the only route to that, and it is
/// a separate, deliberate command.</para>
///
/// <para><b>Elevated windows are listed but not controllable.</b> User Interface Privilege
/// Isolation stops this medium-integrity process from posting to or re-showing a window that runs
/// as administrator. Such a window still appears in the list — the user can see it on the stream —
/// and an action on it reports the reason instead of pretending to work (§12.1).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowManager : IWindowManager
{
    private readonly WindowInspector _inspector;
    private readonly ILogger<WindowsWindowManager> _logger;

    /// <summary>Creates the manager.</summary>
    public WindowsWindowManager(WindowInspector inspector, ILogger<WindowsWindowManager> logger)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<WindowInfo> List()
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        var processNames = new Dictionary<uint, string>();
        var windows = new List<WindowInfo>();

        foreach (TopLevelWindow window in _inspector.EnumerateAltTabWindows())
        {
            if (!processNames.TryGetValue(window.ProcessId, out string? processName))
            {
                processName = ReadProcessName(window.ProcessId);
                processNames[window.ProcessId] = processName;
            }

            windows.Add(new WindowInfo
            {
                Id = MakeId(window.Handle, window.ProcessId),
                Title = window.Title,
                ProcessName = processName,
                IsMinimized = window.IsMinimized,
                IsMaximized = NativeMethods.IsZoomed(window.Handle),
                IsForeground = window.Handle == foreground,
            });
        }

        // Enumeration is already front to back. A stable sort lifts the foreground window to the
        // top without disturbing that order — it normally is the top, but a topmost overlay can
        // sit above it in Z order.
        return [.. windows.OrderByDescending(static w => w.IsForeground)];
    }

    /// <inheritdoc />
    public WindowActionResult? Act(string windowId, WindowAction action)
    {
        if (!TryParseId(windowId, out nint handle, out uint processId) ||
            !_inspector.TryDescribeAltTabWindow(handle, out TopLevelWindow window) ||
            window.ProcessId != processId)
        {
            return null;
        }

        bool ok;
        int error = 0;

        switch (action)
        {
            case WindowAction.Focus:
                ok = Focus(window);
                break;

            case WindowAction.Minimize:
                ok = NativeMethods.ShowWindowAsync(handle, NativeMethods.SwMinimize);
                error = ok ? 0 : Marshal.GetLastPInvokeError();
                break;

            case WindowAction.Maximize:
                ok = NativeMethods.ShowWindowAsync(handle, NativeMethods.SwMaximize);
                error = ok ? 0 : Marshal.GetLastPInvokeError();
                break;

            case WindowAction.Restore:
                ok = NativeMethods.ShowWindowAsync(handle, NativeMethods.SwRestore);
                error = ok ? 0 : Marshal.GetLastPInvokeError();
                break;

            case WindowAction.Close:
                ok = NativeMethods.PostMessage(handle, NativeMethods.WmClose, nint.Zero, nint.Zero);
                error = ok ? 0 : Marshal.GetLastPInvokeError();
                break;

            default:
                return new WindowActionResult { Succeeded = false, Detail = "Unknown action." };
        }

        if (ok)
        {
            return new WindowActionResult { Succeeded = true };
        }

        _logger.LogInformation(
            "Window action {Action} was refused by Windows for process {ProcessId} (error {Error}).",
            action,
            processId,
            error);

        return new WindowActionResult
        {
            Succeeded = false,
            Detail = error == NativeMethods.ErrorAccessDenied
                ? "That window belongs to an app running as administrator, which PC-Remote cannot control."
                : action == WindowAction.Focus
                    ? "Windows did not let PC-Remote bring that window to the front."
                    : "Windows did not accept the request for that window.",
        };
    }

    /// <summary>
    /// Brings a window forward, with one extra attempt when Windows refuses.
    /// </summary>
    /// <remarks>
    /// Windows only lets a process take the foreground if it owns it already or has just received
    /// input, to stop applications stealing focus (§12.2). The well-known way to qualify is to have
    /// a key event of your own in the input stream: a tap of Alt lifts the lock for the caller. It
    /// is used only as a second attempt, because a bare Alt tap also highlights the menu bar of
    /// whatever is in front at that moment.
    /// </remarks>
    private unsafe bool Focus(TopLevelWindow window)
    {
        if (_inspector.TryFocus(window))
        {
            return true;
        }

        NativeMethods.Input* tap = stackalloc NativeMethods.Input[2];
        tap[0] = default;
        tap[0].Type = NativeMethods.InputKeyboard;
        tap[0].Data.Keyboard.VirtualKey = (ushort)RemoteAgent.Core.Input.VirtualKeys.Menu;
        tap[1] = tap[0];
        tap[1].Data.Keyboard.Flags = NativeMethods.KeyEventKeyUp;

        if (NativeMethods.SendInput(2, tap, sizeof(NativeMethods.Input)) != 2)
        {
            return false;
        }

        return NativeMethods.SetForegroundWindow(window.Handle);
    }

    private static string MakeId(nint handle, uint processId) =>
        string.Create(CultureInfo.InvariantCulture, $"w{(long)handle:x}-{processId:x}");

    private static bool TryParseId(string? id, out nint handle, out uint processId)
    {
        handle = nint.Zero;
        processId = 0;

        if (string.IsNullOrEmpty(id) || id.Length > 40 || id[0] != 'w')
        {
            return false;
        }

        int dash = id.IndexOf('-', StringComparison.Ordinal);
        if (dash < 2 ||
            !long.TryParse(id.AsSpan(1, dash - 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long rawHandle) ||
            !uint.TryParse(id.AsSpan(dash + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out processId) ||
            rawHandle == 0)
        {
            return false;
        }

        handle = (nint)rawHandle;
        return true;
    }

    private string ReadProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited, or belongs to another user. The window is still worth listing.
            _logger.LogTrace(ex, "Could not read the name of process {ProcessId}.", processId);
            return string.Empty;
        }
    }
}
