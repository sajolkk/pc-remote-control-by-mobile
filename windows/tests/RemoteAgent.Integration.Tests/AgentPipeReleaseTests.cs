using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Ipc;
using Xunit;

namespace RemoteAgent.Integration.Tests;

/// <summary>
/// The service's end of the pipe is released when its agent goes, and an agent that cannot reach
/// the service stops trying.
/// </summary>
/// <remarks>
/// <para>Found on a PC that had run the service for a week. Each sleep and wake disconnected both
/// agents, and each disconnected agent kept one of the pipe's server instances, because the
/// registry forgot the agent but never disposed its stream. Once every instance was taken, no agent
/// could connect; the service launched a new pair every twenty seconds regardless, and each ran on
/// forever waiting for a service it could not reach. There were 149 of them, and as many tray
/// icons.</para>
///
/// <para>Uses the real registry and the real agent channel over a private named pipe, as the
/// capability tests do. This pipe allows a single server instance, so one leaked instance would
/// block the next agent outright instead of the eighth.</para>
/// </remarks>
public sealed class AgentPipeReleaseTests : IAsyncDisposable
{
    private const int SessionId = 9;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _pipeName = $"pcremote-test-{Guid.NewGuid():n}";
    private readonly SessionAgentRegistry _registry;

    public AgentPipeReleaseTests()
    {
        _registry = new SessionAgentRegistry(NullLogger<SessionAgentRegistry>.Instance)
        {
            CreateServerStream = first => new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None)),
        };

        _registry.SetActiveSession(SessionId);
        _registry.Start();
    }

    [Fact]
    public async Task AnAgentCanConnectAfterThePreviousOneDisconnected()
    {
        for (int round = 0; round < 3; round++)
        {
            string token = _registry.IssueSpawnToken(SessionId, SessionAgentKind.User);
            ServiceChannelClient agent = CreateAgent(token);

            var connected = new TaskCompletionSource<ConnectedAgent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ConnectedAgent> onConnected = (_, a) => connected.TrySetResult(a);
            EventHandler<int> onDisconnected = (_, s) => disconnected.TrySetResult(s);
            _registry.AgentConnected += onConnected;
            _registry.AgentDisconnected += onDisconnected;

            using var lifetime = new CancellationTokenSource();
            Task<bool> run = agent.RunAsync(lifetime.Token);

            try
            {
                ConnectedAgent registered = await connected.Task.WaitAsync(Timeout);
                Assert.Equal(SessionAgentKind.User, registered.Kind);
                Assert.True(_registry.IsAgentConnected(SessionId, SessionAgentKind.User));
            }
            finally
            {
                await lifetime.CancelAsync();
                await run.WaitAsync(Timeout);
                await agent.DisposeAsync();
            }

            Assert.Equal(SessionId, await disconnected.Task.WaitAsync(Timeout));
            Assert.False(_registry.IsAgentConnected(SessionId, SessionAgentKind.User));

            _registry.AgentConnected -= onConnected;
            _registry.AgentDisconnected -= onDisconnected;
        }
    }

    /// <summary>
    /// Nobody serves this pipe, as nothing served the real one while it was full. Two minutes is
    /// the real limit; a second is enough to show that the agent exits rather than retrying forever.
    /// </summary>
    [Fact]
    public async Task AnAgentThatCannotReachTheServiceExitsInsteadOfWaitingForever()
    {
        string nobody = $"pcremote-nobody-{Guid.NewGuid():n}";

        await using var agent = new ServiceChannelClient(
            NullLogger<ServiceChannelClient>.Instance,
            "token",
            SessionId,
            "tester",
            "test",
            SessionAgentKind.User)
        {
            CreateClientStream = () => new NamedPipeClientStream(".", nobody, PipeDirection.InOut, PipeOptions.Asynchronous),
            GiveUpAfter = TimeSpan.FromSeconds(1),
        };

        bool keepRunning = await agent.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.False(keepRunning);
    }

    private ServiceChannelClient CreateAgent(string token) =>
        new(NullLogger<ServiceChannelClient>.Instance, token, SessionId, "tester", "test", SessionAgentKind.User)
        {
            CreateClientStream = () => new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous),
        };

    public async ValueTask DisposeAsync()
    {
        await _registry.DisposeAsync();
    }
}
