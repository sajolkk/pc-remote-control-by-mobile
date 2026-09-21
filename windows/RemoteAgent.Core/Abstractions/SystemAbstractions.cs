using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// Privileged power operations, implemented by the service (§3).
/// </summary>
/// <remarks>
/// Every method is argument-free or takes only a delay and a force flag: there is
/// no path by which a remote caller supplies a command line, a path, or a script.
/// Implementations report capability honestly rather than throwing, so a PC that
/// cannot sleep simply advertises no sleep capability (§0).
/// </remarks>
public interface IPowerController
{
    /// <summary>Whether this machine reports sleep (S3/S4) as available.</summary>
    bool IsSleepSupported { get; }

    /// <summary>Locks the workstation.</summary>
    Task<PowerActionResult> LockAsync(CancellationToken cancellationToken);

    /// <summary>Signs the interactive user out.</summary>
    Task<PowerActionResult> SignOutAsync(bool force, CancellationToken cancellationToken);

    /// <summary>Suspends the machine.</summary>
    Task<PowerActionResult> SleepAsync(CancellationToken cancellationToken);

    /// <summary>Restarts Windows after an optional grace period.</summary>
    Task<PowerActionResult> RestartAsync(int delaySeconds, bool force, CancellationToken cancellationToken);

    /// <summary>Shuts Windows down after an optional grace period.</summary>
    Task<PowerActionResult> ShutdownAsync(int delaySeconds, bool force, CancellationToken cancellationToken);

    /// <summary>Cancels a pending restart or shutdown. Returns false if none is pending.</summary>
    Task<bool> AbortShutdownAsync(CancellationToken cancellationToken);

    /// <summary>Whether a restart or shutdown is pending and still cancellable.</summary>
    bool IsShutdownPending { get; }
}

/// <summary>
/// Facts about the machine, all read at runtime so no value is ever compiled in (§0).
/// </summary>
public interface ISystemInfoProvider
{
    /// <summary>The machine's host name.</summary>
    string HostName { get; }

    /// <summary>Windows edition and build, formatted for display.</summary>
    string OsVersion { get; }

    /// <summary>Process architecture.</summary>
    string Architecture { get; }

    /// <summary>Logical processor count.</summary>
    int CpuCount { get; }

    /// <summary>Installed physical memory in megabytes, or 0 when it cannot be read.</summary>
    long TotalMemoryMb { get; }

    /// <summary>Seconds since Windows booted.</summary>
    long UptimeSeconds { get; }

    /// <summary>
    /// LAN addresses currently bound to up, non-loopback interfaces. Re-read on
    /// every call because DHCP, docking and Wi-Fi switching all change it (§6.4).
    /// </summary>
    IReadOnlyList<string> GetLocalAddresses();

    /// <summary>Battery percentage, or null on a machine without one.</summary>
    int? BatteryPercent { get; }

    /// <summary>Whether the machine is on AC power, or null when unknown.</summary>
    bool? OnAcPower { get; }
}

/// <summary>
/// State of the interactive Windows session, observed by the service via WTS
/// notifications (§3).
/// </summary>
public interface ISessionStateProvider
{
    /// <summary>Whether a user is logged on, locked out, or absent.</summary>
    InteractiveSessionState State { get; }

    /// <summary>The interactive user's name, or null when nobody is logged on.</summary>
    string? UserName { get; }

    /// <summary>The active console session id, or null when there is none.</summary>
    int? ActiveSessionId { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<InteractiveSessionState>? StateChanged;
}

/// <summary>
/// Wake-on-LAN capability reporting (§12.2).
/// </summary>
public interface IWakeOnLanInfoProvider
{
    /// <summary>
    /// Reports whether wake is expected to work, the MACs to target, and any
    /// caveat worth showing the user.
    /// </summary>
    Task<WolInfoResult> GetInfoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The set of capabilities this PC actually has, probed rather than assumed.
/// </summary>
/// <remarks>
/// This is the other half of the portability rule: the same binary reports a
/// different capability list on a laptop with no discrete GPU than on a desktop
/// with three monitors, and the mobile app renders from that list (§0).
/// </remarks>
public interface ICapabilityProvider
{
    /// <summary>Capability names from <c>CapabilityNames</c> that currently apply.</summary>
    Task<IReadOnlyList<string>> GetCapabilitiesAsync(CancellationToken cancellationToken);
}
