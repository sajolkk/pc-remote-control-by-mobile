using System.Runtime.InteropServices;
using System.Text;

namespace RemoteAgent.Windows.Interop;

/// <summary>
/// Window enumeration, focus and close, plus packaged-app activation.
/// </summary>
internal static partial class NativeMethods
{
    // ---------------------------------------------------------------------
    // user32 — top-level windows
    // ---------------------------------------------------------------------

    internal const uint WmClose = 0x0010;

    internal const int SwRestore = 9;
    internal const int SwShow = 5;

    /// <summary>Callback for <see cref="EnumWindows"/>. Return false to stop enumerating.</summary>
    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    /// <remarks>
    /// Declared with <c>DllImport</c> rather than <c>LibraryImport</c>: the delegate
    /// parameter is not supported by source-generated marshalling.
    /// </remarks>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    internal static partial int GetWindowTextLength(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    /// <summary>
    /// Grants another process the right to take the foreground.
    /// </summary>
    /// <remarks>
    /// Windows refuses <c>SetForegroundWindow</c> from a process that does not own the
    /// foreground, to stop applications stealing focus. A remote-control agent is
    /// exactly the case the restriction was not designed for, so focus requests are
    /// best-effort and report failure honestly rather than pretending to work (§12.2).
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    // ---------------------------------------------------------------------
    // shell32 — packaged (Store/UWP) app activation
    // ---------------------------------------------------------------------

    /// <summary>
    /// Activates a packaged application by its Application User Model ID.
    /// </summary>
    /// <remarks>
    /// Used in preference to spawning <c>explorer.exe shell:AppsFolder\{aumid}</c>:
    /// activation happens in-process through a documented COM interface, returns the new
    /// process id, and never involves building a command line — so there is no string
    /// concatenation for an attacker to aim at, even though the AUMID always comes from
    /// this PC's own catalog rather than from the network.
    /// </remarks>
    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        /// <summary>Launches the app and returns the process id.</summary>
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);

        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            nint itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string? verb,
            out uint processId);

        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            nint itemArray,
            out uint processId);
    }

    [Flags]
    internal enum ActivateOptions
    {
        None = 0,
        DesignMode = 0x1,
        NoErrorUI = 0x2,
        NoSplashScreen = 0x4,
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManager
    {
    }
}
