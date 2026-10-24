using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Session;

/// <summary>
/// Moves the calling thread onto whichever desktop currently receives input (§12.1).
/// </summary>
/// <remarks>
/// <para>Windows draws the lock screen, the sign-in screen, Ctrl+Alt+Del and UAC consent prompts on
/// Winlogon's own desktop, not on the user's <c>Default</c> desktop. A thread captures and injects
/// only on the desktop it is attached to, and only SYSTEM may attach to Winlogon's. So the desktop
/// agent — the one process that runs as SYSTEM in the user's session — calls this before opening
/// a capture and before injecting input, and follows Windows from one desktop to the other.</para>
///
/// <para><b>Disabled everywhere else.</b> The ordinary user-session agent cannot open Winlogon's
/// desktop at all, and in portable mode there is nothing to follow, so with
/// <see cref="Enabled"/> false every call is a no-op and the existing behaviour is unchanged.</para>
///
/// <para>The desktop handle a thread is attached to is remembered per thread and closed once the
/// thread has moved off it: a desktop handle that some thread in the process is still using
/// cannot be closed, and leaking one per switch would slowly exhaust the desktop heap.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InputDesktopSwitcher
{
    [ThreadStatic]
    private static nint _attached;

    private readonly ILogger<InputDesktopSwitcher>? _logger;

    /// <summary>Creates the switcher.</summary>
    /// <param name="enabled">True only in the desktop agent, which runs as SYSTEM.</param>
    /// <param name="logger">Diagnostic sink.</param>
    public InputDesktopSwitcher(bool enabled, ILogger<InputDesktopSwitcher>? logger = null)
    {
        Enabled = enabled;
        _logger = logger;
    }

    /// <summary>Whether this process follows the input desktop at all.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Attaches the calling thread to the input desktop, if it is not on it already.
    /// </summary>
    /// <returns>False when following is enabled but the switch failed.</returns>
    public bool AttachCurrentThread()
    {
        if (!Enabled)
        {
            return true;
        }

        nint input = NativeMethods.OpenInputDesktop(0, false, NativeMethods.GenericAll);
        if (input == nint.Zero)
        {
            _logger?.LogDebug(
                "The input desktop could not be opened (Win32 error {Error}).",
                Marshal.GetLastPInvokeError());
            return false;
        }

        nint current = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        if (current != nint.Zero && SameName(current, input))
        {
            NativeMethods.CloseDesktop(input);
            return true;
        }

        if (!NativeMethods.SetThreadDesktop(input))
        {
            int error = Marshal.GetLastPInvokeError();
            NativeMethods.CloseDesktop(input);
            _logger?.LogDebug("Could not move a thread onto the input desktop (Win32 error {Error}).", error);
            return false;
        }

        nint previous = _attached;
        _attached = input;

        if (previous != nint.Zero)
        {
            NativeMethods.CloseDesktop(previous);
        }

        _logger?.LogDebug("Thread {ThreadId} moved to the {Desktop} desktop.", NativeMethods.GetCurrentThreadId(), NameOf(input));
        return true;
    }

    /// <summary>The name of the desktop receiving input, or null if it cannot be read.</summary>
    public static string? InputDesktopName()
    {
        nint desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DesktopReadObjects);
        if (desktop == nint.Zero)
        {
            return null;
        }

        try
        {
            return NameOf(desktop);
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }

    private static bool SameName(nint a, nint b)
    {
        string? first = NameOf(a);
        return first is not null && string.Equals(first, NameOf(b), StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string? NameOf(nint desktop)
    {
        const int capacity = 64;
        char* name = stackalloc char[capacity];

        if (!NativeMethods.GetUserObjectInformation(
                desktop,
                NativeMethods.UoiName,
                name,
                capacity * sizeof(char),
                out uint needed) || needed < sizeof(char))
        {
            return null;
        }

        return new string(name, 0, (int)(needed / sizeof(char)) - 1);
    }
}
