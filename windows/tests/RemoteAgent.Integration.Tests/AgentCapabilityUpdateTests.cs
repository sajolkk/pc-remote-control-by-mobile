using System.IO.Pipes;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Service.Services;
using Xunit;

namespace RemoteAgent.Integration.Tests;

/// <summary>
/// A session agent that finds screen streaming after it connected — the PC booted before its
/// display driver or encoder was ready — reaches the service, and through it the phone.
/// </summary>
/// <remarks>
/// Uses the real registry and the real agent channel over a real named pipe. The pipe has a name
/// of its own, because the real one belongs to an installed PC-Remote service if there is one.
/// </remarks>
public sealed class AgentCapabilityUpdateTests : IAsyncDisposable
{
    private const int SessionId = 7;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly string[] BootCapabilities =
        [CapabilityNames.InputMouse, CapabilityNames.InputKeyboard];

    private static readonly string[] ReadyCapabilities =
    [
        CapabilityNames.InputMouse,
        CapabilityNames.InputKeyboard,
        CapabilityNames.ScreenCapture,
        CapabilityNames.SoftwareEncodeH264,
        CapabilityNames.SecureDesktop,
    ];

    private readonly string _pipeName = $"pcremote-test-{Guid.NewGuid():n}";
    private readonly SessionAgentRegistry _registry;
    private readonly CancellationTokenSource _agentLifetime = new();
    private ServiceChannelClient? _agent;
    private Task? _agentRun;

    public AgentCapabilityUpdateTests()
    {
        _registry = new SessionAgentRegistry(NullLogger<SessionAgentRegistry>.Instance)
        {
            CreateServerStream = first => new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None)),
        };

        _registry.SetActiveSession(SessionId);
        _registry.Start();
    }

    [Fact]
    public async Task ScreenCaptureFoundAfterConnectingReachesTheServiceAndItsCapabilityList()
    {
        ServiceCapabilityProvider provider = CreateCapabilityProvider();
        ConnectedAgent connected = await ConnectDesktopAgentAsync(BootCapabilities);

        Assert.DoesNotContain(CapabilityNames.ScreenCapture, connected.Capabilities);
        Assert.DoesNotContain(CapabilityNames.ScreenCapture, await provider.GetCapabilitiesAsync(CancellationToken.None));

        ConnectedAgent updated = await UpdateAndWaitAsync(ReadyCapabilities);

        Assert.Equal(SessionAgentKind.Desktop, updated.Kind);
        Assert.Equal(ReadyCapabilities, updated.Capabilities);
        Assert.Same(connected.Connection, updated.Connection);

        // What device.info and the handshake send the phone.
        IReadOnlyList<string> advertised = await provider.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Contains(CapabilityNames.ScreenCapture, advertised);
        Assert.Contains(CapabilityNames.SecureDesktop, advertised);
        Assert.Contains(CapabilityNames.SessionAgent, advertised);
    }

    [Fact]
    public async Task TheUpdateIsNotRelayedToPhonesAsARawEvent()
    {
        var relayed = new List<string>();
        _registry.AgentEvent += (_, e) => relayed.Add(e.Topic);

        await ConnectDesktopAgentAsync(BootCapabilities);
        await UpdateAndWaitAsync(ReadyCapabilities);

        // An ordinary event sent afterwards arrives, which proves the update had its chance to.
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.AgentEvent += (_, e) =>
        {
            if (e.Topic == EventNames.MonitorsChanged)
            {
                marker.TrySetResult();
            }
        };

        await _agent!.SendEventAsync(EventNames.MonitorsChanged, new JsonObject { ["count"] = 1 }, CancellationToken.None);
        await marker.Task.WaitAsync(Timeout);

        Assert.DoesNotContain(IpcCommands.CapabilitiesChanged, relayed);
    }

    /// <summary>
    /// The registry used to match the closing connection to the record it registered. An update
    /// replaces that record, so the agent's exit would no longer have been noticed and the service
    /// would never have started a replacement.
    /// </summary>
    [Fact]
    public async Task AnAgentThatUpdatedItsCapabilitiesIsStillRemovedWhenItExits()
    {
        await ConnectDesktopAgentAsync(BootCapabilities);
        await UpdateAndWaitAsync(ReadyCapabilities);

        var disconnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.AgentDisconnected += (_, session) => disconnected.TrySetResult(session);

        await StopAgentAsync();

        Assert.Equal(SessionId, await disconnected.Task.WaitAsync(Timeout));
        Assert.False(_registry.IsAgentConnected(SessionId, SessionAgentKind.Desktop));
        Assert.Empty(_registry.ActiveAgents);
    }

    [Fact]
    public async Task OnlyTheSendingAgentChanges()
    {
        string userToken = _registry.IssueSpawnToken(SessionId, SessionAgentKind.User);
        await using ServiceChannelClient user = CreateAgent(userToken, SessionAgentKind.User);
        user.Capabilities = [CapabilityNames.Apps, CapabilityNames.Files];

        using var userLifetime = new CancellationTokenSource();
        Task<ConnectedAgent> userConnected = WaitForAsync(h => _registry.AgentConnected += h, h => _registry.AgentConnected -= h);
        Task userRun = user.RunAsync(userLifetime.Token);
        await userConnected;

        await ConnectDesktopAgentAsync(BootCapabilities);
        await UpdateAndWaitAsync(ReadyCapabilities);

        ConnectedAgent userAgent = _registry.ActiveAgents.Single(static a => a.Kind == SessionAgentKind.User);
        Assert.Equal([CapabilityNames.Apps, CapabilityNames.Files], userAgent.Capabilities);

        await userLifetime.CancelAsync();
        await userRun.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AMalformedUpdateChangesNothing()
    {
        ConnectedAgent connected = await ConnectDesktopAgentAsync(BootCapabilities);

        await _agent!.SendEventAsync(IpcCommands.CapabilitiesChanged, new JsonObject { ["capabilities"] = "screen.capture" }, CancellationToken.None);
        await _agent.SendEventAsync(IpcCommands.CapabilitiesChanged, data: null, CancellationToken.None);

        // Non-strings inside a well-formed list are dropped; the strings are kept.
        Task<ConnectedAgent> changed = WaitForAsync(h => _registry.AgentCapabilitiesChanged += h, h => _registry.AgentCapabilitiesChanged -= h);
        await _agent.SendEventAsync(
            IpcCommands.CapabilitiesChanged,
            new JsonObject { ["capabilities"] = new JsonArray(CapabilityNames.ScreenCapture, 42, null) },
            CancellationToken.None);

        ConnectedAgent updated = await changed.WaitAsync(Timeout);
        Assert.Equal([CapabilityNames.ScreenCapture], updated.Capabilities);
        Assert.Same(connected.Connection, updated.Connection);
    }

    /// <summary>
    /// Found while the agent was between connections: the next handshake carries it instead.
    /// </summary>
    [Fact]
    public async Task AnUpdateMadeBeforeConnectingIsSentWithTheHandshake()
    {
        string token = _registry.IssueSpawnToken(SessionId, SessionAgentKind.Desktop);
        _agent = CreateAgent(token, SessionAgentKind.Desktop);
        _agent.Capabilities = BootCapabilities;

        await _agent.UpdateCapabilitiesAsync(ReadyCapabilities, CancellationToken.None);

        Task<ConnectedAgent> connected = WaitForAsync(h => _registry.AgentConnected += h, h => _registry.AgentConnected -= h);
        _agentRun = _agent.RunAsync(_agentLifetime.Token);

        Assert.Equal(ReadyCapabilities, (await connected.WaitAsync(Timeout)).Capabilities);
    }

    /// <summary>
    /// Sent the moment the service registered the agent, before the agent has heard the hello
    /// succeed: the event used to find no connection and vanish, leaving the service with the
    /// list the hello carried.
    /// </summary>
    [Fact]
    public async Task AnUpdateMadeWhileTheHandshakeCompletesIsNotLost()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string token = _registry.IssueSpawnToken(SessionId, SessionAgentKind.Desktop);
            ServiceChannelClient agent = CreateAgent(token, SessionAgentKind.Desktop);
            agent.Capabilities = BootCapabilities;

            EventHandler<ConnectedAgent> updateOnConnect = (_, _) =>
                _ = agent.UpdateCapabilitiesAsync(ReadyCapabilities, CancellationToken.None);
            _registry.AgentConnected += updateOnConnect;

            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ConnectedAgent> onChanged = (_, a) =>
            {
                if (a.Capabilities.Contains(CapabilityNames.ScreenCapture))
                {
                    changed.TrySetResult();
                }
            };
            _registry.AgentCapabilitiesChanged += onChanged;

            using var lifetime = new CancellationTokenSource();
            Task run = agent.RunAsync(lifetime.Token);
            try
            {
                await changed.Task.WaitAsync(Timeout);

                ConnectedAgent registered = _registry.ActiveAgents.Single(static a => a.Kind == SessionAgentKind.Desktop);
                Assert.Contains(CapabilityNames.ScreenCapture, registered.Capabilities);
            }
            finally
            {
                _registry.AgentConnected -= updateOnConnect;
                _registry.AgentCapabilitiesChanged -= onChanged;
                await lifetime.CancelAsync();
                await run.WaitAsync(Timeout);
                await agent.DisposeAsync();
            }
        }
    }

    private async Task<ConnectedAgent> ConnectDesktopAgentAsync(string[] capabilities)
    {
        string token = _registry.IssueSpawnToken(SessionId, SessionAgentKind.Desktop);
        _agent = CreateAgent(token, SessionAgentKind.Desktop);
        _agent.Capabilities = capabilities;

        Task<ConnectedAgent> connected = WaitForAsync(h => _registry.AgentConnected += h, h => _registry.AgentConnected -= h);
        _agentRun = _agent.RunAsync(_agentLifetime.Token);
        return await connected.WaitAsync(Timeout);
    }

    private async Task<ConnectedAgent> UpdateAndWaitAsync(string[] capabilities)
    {
        Task<ConnectedAgent> changed = WaitForAsync(h => _registry.AgentCapabilitiesChanged += h, h => _registry.AgentCapabilitiesChanged -= h);
        await _agent!.UpdateCapabilitiesAsync(capabilities, CancellationToken.None);
        return await changed.WaitAsync(Timeout);
    }

    private ServiceChannelClient CreateAgent(string token, SessionAgentKind kind) =>
        new(NullLogger<ServiceChannelClient>.Instance, token, SessionId, "tester", "test", kind)
        {
            CreateClientStream = () => new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous),
        };

    private ServiceCapabilityProvider CreateCapabilityProvider() => new(
        new NoPower(),
        new NoWake(),
        _registry,
        new StaticOptionsMonitor<AgentOptions>(new AgentOptions()));

    private static Task<ConnectedAgent> WaitForAsync(
        Action<EventHandler<ConnectedAgent>> subscribe,
        Action<EventHandler<ConnectedAgent>> unsubscribe)
    {
        var raised = new TaskCompletionSource<ConnectedAgent>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConnectedAgent>? handler = null;
        handler = (_, agent) =>
        {
            unsubscribe(handler!);
            raised.TrySetResult(agent);
        };

        subscribe(handler);
        return raised.Task;
    }

    private async Task StopAgentAsync()
    {
        await _agentLifetime.CancelAsync();
        if (_agentRun is not null)
        {
            await _agentRun.WaitAsync(Timeout);
        }

        if (_agent is not null)
        {
            await _agent.DisposeAsync();
            _agent = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        await _registry.DisposeAsync();
        _agentLifetime.Dispose();
    }

    private sealed class NoPower : IPowerController
    {
        public bool IsSleepSupported => false;

        public bool IsShutdownPending => false;

        public Task<PowerActionResult> LockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PowerActionResult> SignOutAsync(bool force, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PowerActionResult> SleepAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PowerActionResult> RestartAsync(int delaySeconds, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PowerActionResult> ShutdownAsync(int delaySeconds, bool force, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> AbortShutdownAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoWake : IWakeOnLanInfoProvider
    {
        public Task<WolInfoResult> GetInfoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WolInfoResult());
    }
}
