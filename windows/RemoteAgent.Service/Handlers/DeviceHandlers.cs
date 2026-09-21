using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Service.Network;

namespace RemoteAgent.Service.Handlers;

/// <summary>
/// Reports facts about the PC that change rarely.
/// </summary>
/// <remarks>
/// Everything returned is read from the OS at call time. Nothing is compiled in, which is what
/// lets the same binary describe any machine accurately (§0). The negotiated TLS version is
/// included deliberately: it varies by Windows build, and a user who wants to know whether
/// their connection is 1.3 or 1.2 should be able to see it rather than infer it (§7.1).
/// </remarks>
public sealed class DeviceInfoHandler : ICommandHandler
{
    private readonly ISystemInfoProvider _systemInfo;
    private readonly ICapabilityProvider _capabilities;
    private readonly ClientSessionManager _sessions;
    private readonly ListenerEndpointStatus _endpoint;
    private readonly IOptionsMonitor<AgentOptions> _options;

    /// <summary>Creates the handler.</summary>
    public DeviceInfoHandler(
        ISystemInfoProvider systemInfo,
        ICapabilityProvider capabilities,
        ClientSessionManager sessions,
        ListenerEndpointStatus endpoint,
        IOptionsMonitor<AgentOptions> options)
    {
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Command => CommandNames.DeviceInfo;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AgentOptions options = _options.CurrentValue;
        IReadOnlyList<string> capabilities = await _capabilities.GetCapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);

        string tlsDescription = _sessions.TryGet(context.Caller.ConnectionId, out ClientSession session)
            ? session.TlsDescription
            : "unknown";

        return CommandResult.Success(new DeviceInfoResult
        {
            DeviceId = options.Device.Id,
            DeviceName = options.Device.Name,
            HostName = _systemInfo.HostName,
            OsVersion = _systemInfo.OsVersion,
            Architecture = _systemInfo.Architecture,
            AgentVersion = AgentVersion.Current,
            CpuCount = _systemInfo.CpuCount,
            TotalMemoryMb = _systemInfo.TotalMemoryMb,
            LocalAddresses = _systemInfo.GetLocalAddresses().ToArray(),
            ControlPort = _endpoint.Port,
            TlsVersion = tlsDescription,
            Capabilities = capabilities.ToArray(),
        });
    }
}

/// <summary>
/// Reports live state: who is signed in, whether the desktop is locked, volume, power.
/// </summary>
/// <remarks>
/// <para>Session state is the honest answer to "why is my screen view blank". A locked
/// workstation cannot be captured or driven — that is Windows protecting the secure desktop,
/// not a failure in this system — and reporting it plainly lets the client say so instead of
/// showing a frozen frame (§12.1).</para>
///
/// <para>Volume and monitor count come from the session agent, so they are null when nobody is
/// signed in. Null means "unknown", never zero: a muted PC and a PC with no session are
/// different states and the client treats them differently.</para>
/// </remarks>
public sealed class SystemStateHandler : ICommandHandler
{
    private readonly ISessionStateProvider _sessionState;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IPowerController _power;
    private readonly SessionAgentRegistry _agents;

    /// <summary>Creates the handler.</summary>
    public SystemStateHandler(
        ISessionStateProvider sessionState,
        ISystemInfoProvider systemInfo,
        IPowerController power,
        SessionAgentRegistry agents)
    {
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <inheritdoc />
    public string Command => CommandNames.SystemState;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.Success(new SystemStateResult
        {
            SessionState = _sessionState.State,
            UserName = _sessionState.UserName,
            SessionAgentConnected = _agents.IsConnected,
            UptimeSeconds = _systemInfo.UptimeSeconds,
            ShutdownPending = _power.IsShutdownPending,
            BatteryPercent = _systemInfo.BatteryPercent,
            OnAcPower = _systemInfo.OnAcPower,

            // Owned by the session agent; the client fetches them separately when a desktop
            // is available rather than having this command block on IPC.
            Volume = null,
            Muted = null,
            MonitorCount = null,
        }));
}

/// <summary>
/// Reports whether Wake-on-LAN will work and what the phone needs to send.
/// </summary>
public sealed class WolInfoHandler : ICommandHandler
{
    private readonly IWakeOnLanInfoProvider _provider;

    /// <summary>Creates the handler.</summary>
    public WolInfoHandler(IWakeOnLanInfoProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <inheritdoc />
    public string Command => CommandNames.WolInfo;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        WolInfoResult info = await _provider.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        return CommandResult.Success(info);
    }
}

/// <summary>The agent's build version, read from the assembly rather than hardcoded.</summary>
public static class AgentVersion
{
    /// <summary>The informational version of the running build.</summary>
    public static string Current { get; } =
        typeof(AgentVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(AgentVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
