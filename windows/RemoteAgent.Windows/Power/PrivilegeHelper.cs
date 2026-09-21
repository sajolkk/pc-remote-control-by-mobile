using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Power;

/// <summary>
/// Enables a privilege in the current process token.
/// </summary>
/// <remarks>
/// Windows grants a process's token privileges but leaves most of them
/// <em>disabled</em>: holding <c>SeShutdownPrivilege</c> is not the same as being able
/// to use it. Shutdown, restart and sleep all fail with a misleading access error
/// until the privilege is explicitly enabled, which is what this does.
/// <para>
/// The service enables it once at startup rather than per request, and the result is
/// cached so that a machine where the privilege is genuinely absent reports "shutdown
/// unavailable" as a capability instead of failing every command (§0).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class PrivilegeHelper
{
    /// <summary>
    /// Attempts to enable a named privilege for this process.
    /// </summary>
    /// <returns>True when the privilege is enabled and usable.</returns>
    public static bool TryEnable(string privilegeName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);
        ArgumentNullException.ThrowIfNull(logger);

        nint token = nint.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(
                    NativeMethods.GetCurrentProcess(),
                    NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
                    out token))
            {
                logger.LogWarning(
                    "Could not open the process token to enable {Privilege}: Win32 error {Error}.",
                    privilegeName,
                    Marshal.GetLastWin32Error());
                return false;
            }

            if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out NativeMethods.Luid luid))
            {
                logger.LogWarning(
                    "The privilege {Privilege} is not recognised on this system: Win32 error {Error}.",
                    privilegeName,
                    Marshal.GetLastWin32Error());
                return false;
            }

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = NativeMethods.SePrivilegeEnabled,
                },
            };

            bool adjusted = NativeMethods.AdjustTokenPrivileges(
                token,
                false,
                ref privileges,
                (uint)Marshal.SizeOf<NativeMethods.TokenPrivileges>(),
                nint.Zero,
                nint.Zero);

            int lastError = Marshal.GetLastWin32Error();

            // AdjustTokenPrivileges reports success even when it changed nothing, so
            // the error code must be checked separately. This is the exact case where
            // the process holds no such privilege — typically a standard user account.
            if (!adjusted || lastError == NativeMethods.ErrorNotAllAssigned)
            {
                logger.LogInformation(
                    "The privilege {Privilege} is not held by this process, so the related power actions " +
                    "will be reported as unavailable rather than failing at request time.",
                    privilegeName);
                return false;
            }

            logger.LogDebug("Enabled the {Privilege} privilege.", privilegeName);
            return true;
        }
        finally
        {
            if (token != nint.Zero)
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }
}
