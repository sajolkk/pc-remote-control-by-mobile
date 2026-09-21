using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Shell;

/// <summary>
/// Locks the workstation from inside the interactive session.
/// </summary>
/// <remarks>
/// <c>LockWorkStation</c> acts on the desktop of the calling thread, so it only works
/// from a process running in the user's session — a service in Session 0 would lock an
/// invisible desktop and appear to do nothing. That is why locking is a session-agent
/// operation with a service-side fallback (session disconnect) rather than a service
/// operation (§10).
/// <para>
/// Locking is the one half of the lock/unlock pair that a normal application can do.
/// Unlocking is architecturally out of reach: the logon UI lives on a separate, secure
/// desktop that no ordinary process can draw on or inject input into, and the only
/// sanctioned route is a Credential Provider DLL loaded by <c>LogonUI.exe</c> — a
/// deliberate non-goal for this project (§12.1).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsWorkstationLocker : IWorkstationLocker
{
    private readonly ILogger<WindowsWorkstationLocker> _logger;

    /// <summary>Creates the locker.</summary>
    public WindowsWorkstationLocker(ILogger<WindowsWorkstationLocker> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> LockAsync(CancellationToken cancellationToken)
    {
        bool ok = NativeMethods.LockWorkStation();

        if (ok)
        {
            _logger.LogInformation("Workstation locked at a paired device's request.");
        }
        else
        {
            _logger.LogWarning("LockWorkStation failed: Win32 error {Error}.", Marshal.GetLastWin32Error());
        }

        return Task.FromResult(ok);
    }
}
