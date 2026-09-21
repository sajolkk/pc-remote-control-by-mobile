using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Service.Services;

/// <summary>
/// Assembles the capability list this PC advertises (§0).
/// </summary>
/// <remarks>
/// <para>Capabilities come from three places and are combined here: what the service itself
/// can do (power actions, Wake-on-LAN), what the session agent reports from inside the user's
/// desktop (capture, input, clipboard, apps), and what local configuration permits.</para>
///
/// <para>The configuration layer matters as much as the probing. A PC that is perfectly capable
/// of shutting down but whose owner disabled remote shutdown should not advertise the
/// capability, because a client that renders a shutdown button from this list would then offer
/// a control that always fails. Capability means "this will work if you have permission", not
/// "the hardware supports it".</para>
///
/// <para>Recomputed per call rather than cached: the session agent connects and disconnects as
/// users sign in and out, and a stale list is exactly how a client ends up showing a screen
/// view for a PC with nobody logged on.</para>
/// </remarks>
public sealed class ServiceCapabilityProvider : ICapabilityProvider
{
    private readonly IPowerController _power;
    private readonly IWakeOnLanInfoProvider _wol;
    private readonly SessionAgentRegistry _agents;
    private readonly IOptionsMonitor<AgentOptions> _options;

    /// <summary>Creates the provider.</summary>
    public ServiceCapabilityProvider(
        IPowerController power,
        IWakeOnLanInfoProvider wol,
        SessionAgentRegistry agents,
        IOptionsMonitor<AgentOptions> options)
    {
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _wol = wol ?? throw new ArgumentNullException(nameof(wol));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        AgentOptions options = _options.CurrentValue;

        // --- Service-side: power. Both the machine's ability and the owner's consent. ---

        if (options.Power.AllowSleep && _power.IsSleepSupported)
        {
            capabilities.Add(CapabilityNames.PowerSleep);
        }

        if (options.Power.AllowShutdown || options.Power.AllowRestart)
        {
            capabilities.Add(CapabilityNames.PowerShutdown);
        }

        if (options.Power.AllowSignOut)
        {
            capabilities.Add(CapabilityNames.PowerSignOut);
        }

        // --- Service-side: wake. Reported only when it is actually expected to work. ---

        WolInfoResult wol = await _wol.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (wol.Supported)
        {
            capabilities.Add(CapabilityNames.WakeOnLan);
        }

        // --- Session-side: whatever the agent probed in the user's desktop. ---

        ConnectedAgent? agent = _agents.ActiveAgent;
        if (agent is not null)
        {
            capabilities.Add(CapabilityNames.SessionAgent);

            foreach (string capability in agent.Capabilities)
            {
                capabilities.Add(capability);
            }
        }

        // --- Configuration gates that remove capabilities regardless of ability. ---

        if (options.Files.AllowedRoots.Length == 0)
        {
            capabilities.Remove(CapabilityNames.Files);
        }

        return capabilities.OrderBy(static c => c, StringComparer.Ordinal).ToArray();
    }
}
