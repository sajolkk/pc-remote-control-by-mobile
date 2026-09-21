using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Power;

/// <summary>
/// Power control for the Windows service (§3).
/// </summary>
/// <remarks>
/// <para>Runs in the service, in Session 0, for one reason: these operations must work
/// when nobody is logged on. A phone should be able to shut down or wake a PC sitting
/// at its logon screen, and a user-session process does not exist then.</para>
///
/// <para>Every method takes only a delay and a force flag. There is no path by which a
/// remote caller supplies a command line, an executable, or a script — the API shape
/// is the guarantee, not a validation step (§7.3).</para>
///
/// <para>Restart and shutdown go through <c>InitiateSystemShutdownEx</c> rather than
/// <c>ExitWindowsEx</c> so that Windows owns the grace period and shows its own
/// warning. That gives the person at the machine a real chance to cancel — which
/// matters for a remote command they did not initiate.</para>
///
/// <para>Sign-out targets the <em>active console session</em> explicitly via
/// <c>WTSLogoffSession</c>. Calling <c>ExitWindowsEx</c> from a service would act on
/// Session 0 and silently do nothing to the user's desktop.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerController : IPowerController
{
    private const string ShutdownMessage = "PC-Remote: a paired device requested this.";

    private readonly ISessionStateProvider _sessions;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<WindowsPowerController> _logger;
    private readonly bool _hasShutdownPrivilege;
    private readonly bool _sleepSupported;

    private volatile bool _shutdownPending;

    /// <summary>Creates the controller and probes what this machine can actually do.</summary>
    public WindowsPowerController(
        ISessionStateProvider sessions,
        IOptionsMonitor<AgentOptions> options,
        ILogger<WindowsPowerController> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _hasShutdownPrivilege = PrivilegeHelper.TryEnable(NativeMethods.SeShutdownName, logger);
        _sleepSupported = ProbeSleepSupport();
    }

    /// <inheritdoc />
    public bool IsSleepSupported => _sleepSupported && _hasShutdownPrivilege;

    /// <summary>Whether restart and shutdown are possible on this machine.</summary>
    public bool IsShutdownSupported => _hasShutdownPrivilege;

    /// <inheritdoc />
    public bool IsShutdownPending => _shutdownPending;

    /// <inheritdoc />
    public Task<PowerActionResult> LockAsync(CancellationToken cancellationToken)
    {
        // LockWorkStation only affects the caller's desktop, so a service cannot use
        // it. The service-side equivalent is disconnecting the session, which shows
        // the logon screen and requires credentials to return — the same user-visible
        // outcome. The session agent's direct lock is preferred when it is available;
        // this is the fallback.
        int? sessionId = _sessions.ActiveSessionId;
        if (sessionId is null)
        {
            _logger.LogInformation("Lock requested but no active console session exists.");
            return Task.FromResult(new PowerActionResult { Accepted = false });
        }

        bool ok = NativeMethods.WTSDisconnectSession(
            NativeMethods.WtsCurrentServerHandle,
            (uint)sessionId.Value,
            false);

        if (!ok)
        {
            _logger.LogWarning(
                "WTSDisconnectSession failed for session {SessionId}: Win32 error {Error}.",
                sessionId,
                Marshal.GetLastWin32Error());
        }

        return Task.FromResult(new PowerActionResult { Accepted = ok });
    }

    /// <inheritdoc />
    public Task<PowerActionResult> SignOutAsync(bool force, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Power.AllowSignOut)
        {
            return Task.FromResult(new PowerActionResult { Accepted = false });
        }

        int? sessionId = _sessions.ActiveSessionId;
        if (sessionId is null)
        {
            _logger.LogInformation("Sign-out requested but nobody is logged on.");
            return Task.FromResult(new PowerActionResult { Accepted = false });
        }

        bool ok = NativeMethods.WTSLogoffSession(
            NativeMethods.WtsCurrentServerHandle,
            (uint)sessionId.Value,
            false);

        if (!ok)
        {
            _logger.LogWarning(
                "WTSLogoffSession failed for session {SessionId}: Win32 error {Error}.",
                sessionId,
                Marshal.GetLastWin32Error());
        }
        else
        {
            _logger.LogInformation("Signed out session {SessionId}.", sessionId);
        }

        return Task.FromResult(new PowerActionResult { Accepted = ok });
    }

    /// <inheritdoc />
    public Task<PowerActionResult> SleepAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Power.AllowSleep)
        {
            return Task.FromResult(new PowerActionResult { Accepted = false });
        }

        if (!IsSleepSupported)
        {
            _logger.LogInformation("Sleep requested but this machine does not report sleep support.");
            return Task.FromResult(new PowerActionResult { Accepted = false });
        }

        // hibernate: false  -> S3 sleep, which is what wakes on a magic packet.
        // force: false      -> let drivers veto, rather than risking a dirty suspend.
        // disableWakeEvent: false -> wake sources such as WoL stay armed, which is the
        //                            entire point of allowing remote sleep (§12.2).
        bool ok = NativeMethods.SetSuspendState(false, false, false);

        if (!ok)
        {
            _logger.LogWarning("SetSuspendState failed: Win32 error {Error}.", Marshal.GetLastWin32Error());
        }

        return Task.FromResult(new PowerActionResult { Accepted = ok });
    }

    /// <inheritdoc />
    public Task<PowerActionResult> RestartAsync(int delaySeconds, bool force, CancellationToken cancellationToken)
    {
        PowerOptions power = _options.CurrentValue.Power;
        return power.AllowRestart
            ? Task.FromResult(InitiateShutdown(delaySeconds, force, reboot: true))
            : Task.FromResult(new PowerActionResult { Accepted = false });
    }

    /// <inheritdoc />
    public Task<PowerActionResult> ShutdownAsync(int delaySeconds, bool force, CancellationToken cancellationToken)
    {
        PowerOptions power = _options.CurrentValue.Power;
        return power.AllowShutdown
            ? Task.FromResult(InitiateShutdown(delaySeconds, force, reboot: false))
            : Task.FromResult(new PowerActionResult { Accepted = false });
    }

    /// <inheritdoc />
    public Task<bool> AbortShutdownAsync(CancellationToken cancellationToken)
    {
        bool ok = NativeMethods.AbortSystemShutdown(null);
        int error = Marshal.GetLastWin32Error();

        if (ok)
        {
            _shutdownPending = false;
            _logger.LogInformation("Pending shutdown cancelled.");
            return Task.FromResult(true);
        }

        if (error == NativeMethods.ErrorNoShutdownInProgress)
        {
            _shutdownPending = false;
            _logger.LogInformation("Abort requested but no shutdown was pending.");
            return Task.FromResult(false);
        }

        _logger.LogWarning("AbortSystemShutdown failed: Win32 error {Error}.", error);
        return Task.FromResult(false);
    }

    private PowerActionResult InitiateShutdown(int requestedDelaySeconds, bool force, bool reboot)
    {
        if (!_hasShutdownPrivilege)
        {
            _logger.LogWarning(
                "{Action} requested but this process holds no shutdown privilege.",
                reboot ? "Restart" : "Shutdown");
            return new PowerActionResult { Accepted = false };
        }

        PowerOptions power = _options.CurrentValue.Power;

        // A client-supplied delay is clamped rather than trusted: a hostile or buggy
        // value of int.MaxValue would otherwise leave a shutdown armed indefinitely.
        int delay = requestedDelaySeconds <= 0
            ? power.DefaultDelaySeconds
            : Math.Clamp(requestedDelaySeconds, 0, power.MaxDelaySeconds);

        bool ok = NativeMethods.InitiateSystemShutdownEx(
            machineName: null,
            message: ShutdownMessage,
            timeoutSeconds: (uint)delay,
            forceAppsClosed: force,
            rebootAfterShutdown: reboot,
            reason: NativeMethods.ShtdnReasonMajorApplication | NativeMethods.ShtdnReasonFlagPlanned);

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            _logger.LogWarning(
                "InitiateSystemShutdownEx failed for {Action}: Win32 error {Error}.",
                reboot ? "restart" : "shutdown",
                error);

            return new PowerActionResult { Accepted = false };
        }

        _shutdownPending = delay > 0;
        _logger.LogWarning(
            "{Action} initiated with a {Delay}s grace period (force={Force}).",
            reboot ? "Restart" : "Shutdown",
            delay,
            force);

        return new PowerActionResult
        {
            Accepted = true,
            DelaySeconds = delay,
            Cancellable = delay > 0,
        };
    }

    /// <summary>
    /// Asks Windows which sleep states exist, so sleep is advertised only where it
    /// works. A tablet in modern standby, a desktop with S3 disabled in firmware and
    /// a laptop all answer differently, and the same binary must be correct on all
    /// three (§0).
    /// </summary>
    private bool ProbeSleepSupport()
    {
        // A hand-written struct that is smaller than the native one lets the OS write past the end
        // of our stack buffer, which kills the process outright rather than raising an exception.
        // Verifying the size first turns a layout mistake into a logged degradation.
        if (!VerifyPowerCapabilitiesLayout())
        {
            return false;
        }

        try
        {
            if (!NativeMethods.GetPwrCapabilities(out NativeMethods.SystemPowerCapabilities capabilities))
            {
                _logger.LogDebug("GetPwrCapabilities failed; assuming sleep is unavailable.");
                return false;
            }

            bool supported = capabilities.SupportsS3 || capabilities.SupportsS4 || capabilities.SupportsModernStandby;
            _logger.LogInformation(
                "Power capabilities: S3={S3} S4={S4} ModernStandby={AoAc} FastStartup={Hiberboot}",
                capabilities.SupportsS3,
                capabilities.SupportsS4,
                capabilities.SupportsModernStandby,
                capabilities.FastStartupEnabled);

            return supported;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Power capabilities could not be read; treating sleep as unavailable.");
            return false;
        }
    }

    /// <summary>
    /// Confirms our declaration of <c>SYSTEM_POWER_CAPABILITIES</c> matches the documented native
    /// layout before passing it to the OS.
    /// </summary>
    /// <remarks>
    /// Expected size on both x64 and ARM64: 24 single-byte flags, a 6-byte spare, two more flags,
    /// three 8-byte battery scales, and five 4-byte power states = 76 bytes. Checked rather than
    /// trusted because the failure mode of getting it wrong is stack corruption, not a wrong answer.
    /// </remarks>
    private bool VerifyPowerCapabilitiesLayout()
    {
        const int expectedSize = 76;
        int actualSize = Marshal.SizeOf<NativeMethods.SystemPowerCapabilities>();

        if (actualSize == expectedSize)
        {
            return true;
        }

        _logger.LogError(
            "The SYSTEM_POWER_CAPABILITIES declaration is {Actual} bytes but Windows expects {Expected}. " +
            "Skipping the power-capability probe to avoid corrupting the stack; sleep will be reported " +
            "as unavailable.",
            actualSize,
            expectedSize);

        return false;
    }

    /// <summary>
    /// Whether Fast Startup is enabled, which usually prevents Wake-on-LAN from a
    /// shutdown state. Reported to the client so the limitation is visible up front
    /// rather than discovered as a button that does nothing (§12.2).
    /// </summary>
    public bool IsFastStartupEnabled()
    {
        if (!VerifyPowerCapabilitiesLayout())
        {
            return false;
        }

        try
        {
            return NativeMethods.GetPwrCapabilities(out NativeMethods.SystemPowerCapabilities capabilities) &&
                   capabilities.FastStartupEnabled;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "Fast Startup state could not be read.");
            return false;
        }
    }
}
