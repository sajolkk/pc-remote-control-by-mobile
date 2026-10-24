using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;

namespace RemoteAgent.Ipc;

/// <summary>One connected session agent.</summary>
/// <param name="SessionId">The Windows session it serves.</param>
/// <param name="UserName">The user it runs as.</param>
/// <param name="Connection">The live IPC conversation.</param>
/// <param name="Commands">Commands this agent implements.</param>
/// <param name="Capabilities">Capabilities it probed in its session.</param>
/// <param name="ConnectedAt">When the handshake completed.</param>
/// <param name="Kind">Which share of the session's work it does.</param>
public sealed record ConnectedAgent(
    int SessionId,
    string UserName,
    IpcConnection Connection,
    IReadOnlySet<string> Commands,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ConnectedAt,
    SessionAgentKind Kind = SessionAgentKind.Full);

/// <summary>
/// Accepts session-agent connections and routes forwarded commands to them (§3).
/// </summary>
/// <remarks>
/// <para>The service is the pipe <em>server</em> even though it is the side that sends most
/// requests. That is deliberate: the service is the long-lived, privileged, always-present
/// process, so it owns the stable endpoint and agents come and go around it. An agent that
/// crashes simply reconnects; the service never has to rediscover anything.</para>
///
/// <para>Agents are tracked per session id rather than as a single "current agent", because
/// fast user switching and Remote Desktop can genuinely produce more than one interactive
/// session at a time. Forwarding always targets the <em>active console session</em>, which
/// the service tells this registry, so a command never lands on a disconnected session's
/// desktop where nobody would see it.</para>
///
/// <para>A session can have two agents, a user agent and a desktop agent (see
/// <see cref="SessionAgentKind"/>), which implement disjoint sets of commands. Each command goes to
/// whichever of the session's agents implements it.</para>
///
/// <para>Authentication is the one-time spawn token. The pipe must be openable by
/// interactive users (see <see cref="IpcPipe"/>), so the token is what actually proves the
/// peer is the process this service launched. A connection that does not present a valid,
/// unused token within the handshake window is dropped without being registered.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionAgentRegistry : ISessionBridge, IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<(int SessionId, SessionAgentKind Kind), ConnectedAgent> _agents = new();
    private readonly ConcurrentDictionary<(int SessionId, SessionAgentKind Kind), string> _spawnTokens = new();
    private readonly ILogger<SessionAgentRegistry> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    // Disposal happens twice: once from the ordered shutdown in the host, and again when
    // the DI container disposes its singletons. The second pass must be a no-op.
    private bool _disposed;

    private Task? _acceptLoop;
    private bool _firstInstanceCreated;

    // Stored as an int rather than int? because a nullable cannot be volatile, and this is
    // written from the service's session-change callback while being read by forwarding.
    // -1 means "no console session reported".
    private volatile int _activeSessionId = NoActiveSession;

    private const int NoActiveSession = -1;

    /// <summary>Creates the registry.</summary>
    public SessionAgentRegistry(ILogger<SessionAgentRegistry> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Raised when an agent completes its handshake.</summary>
    public event EventHandler<ConnectedAgent>? AgentConnected;

    /// <summary>Raised when an agent disconnects.</summary>
    public event EventHandler<int>? AgentDisconnected;

    /// <summary>Events the connected agent pushed, for the service to relay to clients.</summary>
    public event EventHandler<IpcEvent>? AgentEvent;

    /// <summary>
    /// Requests the service handles on an agent's behalf — currently only the pairing
    /// approval prompt, which needs the tray UI.
    /// </summary>
    public Func<IpcRequest, CancellationToken, Task<IpcResponse>>? AgentRequestHandler { get; set; }

    /// <inheritdoc />
    public bool IsConnected => GetActiveAgents().Count > 0;

    /// <summary>
    /// The agent that runs as the signed-in user in the active console session, if any: the one
    /// that can show the tray icon and pairing prompts. A desktop agent alone does not count.
    /// </summary>
    public ConnectedAgent? ActiveAgent =>
        GetActiveAgents().FirstOrDefault(static a => a.Kind != SessionAgentKind.Desktop);

    /// <summary>Every agent serving the active console session.</summary>
    public IReadOnlyList<ConnectedAgent> ActiveAgents => GetActiveAgents();

    /// <summary>Every connected agent, for the UI's status view.</summary>
    public IReadOnlyCollection<ConnectedAgent> All => _agents.Values.ToArray();

    /// <summary>
    /// Tells the registry which session is on the console, so forwarding targets the
    /// desktop the user is actually looking at.
    /// </summary>
    public void SetActiveSession(int? sessionId)
    {
        _activeSessionId = sessionId ?? NoActiveSession;
    }

    /// <summary>
    /// Issues a one-time token for an agent about to be spawned into a session.
    /// </summary>
    /// <remarks>
    /// Any previous unused token for that session is replaced, so a spawn that failed
    /// halfway cannot leave a valid credential lying around indefinitely.
    /// </remarks>
    public string IssueSpawnToken(int sessionId, SessionAgentKind kind = SessionAgentKind.Full)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _spawnTokens[(sessionId, kind)] = token;
        return token;
    }

    /// <summary>Whether an agent of that kind is connected for that session.</summary>
    public bool IsAgentConnected(int sessionId, SessionAgentKind kind) =>
        _agents.TryGetValue((sessionId, kind), out ConnectedAgent? agent) && agent.Connection.IsConnected;

    /// <summary>Starts accepting agent connections.</summary>
    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    /// <inheritdoc />
    public async Task<CommandResult> ForwardAsync(
        string command,
        JsonElement? args,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        IReadOnlyList<ConnectedAgent> agents = GetActiveAgents();
        if (agents.Count == 0)
        {
            return CommandResult.SessionUnavailable();
        }

        ConnectedAgent? agent = agents.FirstOrDefault(a => a.Commands.Contains(command)) ??
                                agents.FirstOrDefault(static a => a.Commands.Count == 0);

        if (agent is null)
        {
            // Half of a split pair is missing: the user agent while nobody is signed in, or either
            // one while it restarts. The command is not unsupported, just unavailable right now.
            bool complete = agents.Any(static a => a.Kind == SessionAgentKind.Full) ||
                            (agents.Any(static a => a.Kind == SessionAgentKind.User) &&
                             agents.Any(static a => a.Kind == SessionAgentKind.Desktop));

            // A command no agent implements is reported as unsupported rather than sent and
            // silently failed — this is how a PC with a reduced feature set stays honest about
            // what it can do (§0).
            return complete
                ? CommandResult.NotSupported($"the '{command}' command")
                : CommandResult.SessionUnavailable();
        }

        var request = new IpcRequest
        {
            Id = Guid.NewGuid().ToString("n"),
            Command = command,
            Args = args,
            Caller = new IpcCaller
            {
                DeviceId = caller.DeviceId ?? string.Empty,
                DeviceName = caller.DeviceName,
                Permissions = PermissionSet.ToNames(caller.Permissions),
                ConnectionId = caller.ConnectionId,
                RemoteAddress = caller.RemoteAddress,
            },
        };

        IpcResponse response = await agent.Connection
            .SendRequestAsync(request, ForwardTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (response.Ok)
        {
            return CommandResult.SuccessRaw(response.Data);
        }

        return response.Error is null
            ? CommandResult.Fail(ErrorCodes.Internal, "The session agent reported a failure.")
            : CommandResult.Fail(response.Error);
    }

    /// <summary>Asks the active session's agents to release every held key and mouse button.</summary>
    public async Task ReleaseInputAsync(CancellationToken cancellationToken)
    {
        foreach (ConnectedAgent agent in GetActiveAgents())
        {
            await agent.Connection.SendRequestAsync(
                new IpcRequest { Id = Guid.NewGuid().ToString("n"), Command = IpcCommands.ReleaseInput },
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells every connected agent that a control connection closed or its permissions changed.
    /// </summary>
    /// <remarks>
    /// Sent to every agent rather than only the active one: a stream started before a console
    /// session switch still belongs to the agent that started it, and must still be stopped.
    /// </remarks>
    public async Task NotifyConnectionChangedAsync(
        IpcConnectionChangedArgs change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        JsonElement args = JsonSerializer.SerializeToElement(change, ProtocolJson.Options);

        foreach (ConnectedAgent agent in _agents.Values)
        {
            if (!agent.Connection.IsConnected)
            {
                continue;
            }

            await agent.Connection.SendRequestAsync(
                new IpcRequest
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Command = IpcCommands.ConnectionChanged,
                    Args = args,
                },
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private List<ConnectedAgent> GetActiveAgents()
    {
        ConnectedAgent[] connected = _agents.Values.Where(static a => a.Connection.IsConnected).ToArray();
        int sessionId = _activeSessionId;

        if (sessionId != NoActiveSession)
        {
            List<ConnectedAgent> inSession = connected.Where(a => a.SessionId == sessionId).ToList();
            if (inSession.Count > 0)
            {
                return inSession;
            }
        }

        // Fallback: every connected agent serves one session and no console session is reported
        // yet. This covers the startup window before the first session notification arrives, and
        // console-less configurations. With agents in more than one session it is refused rather
        // than guessed — sending a keystroke to the wrong user's desktop is not a mistake worth
        // risking.
        if (connected.Length > 0 && connected.All(a => a.SessionId == connected[0].SessionId))
        {
            return [.. connected];
        }

        return [];
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                bool first = !_firstInstanceCreated;
                server = IpcPipe.CreateServerStream(first);
                _firstInstanceCreated = true;

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Handshake on a separate task so the loop returns immediately to accepting.
                NamedPipeServerStream connected = server;
                server = null;
                _ = Task.Run(() => HandshakeAsync(connected, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex) when (!_firstInstanceCreated)
            {
                // FirstPipeInstance failed: something else already owns the name. This is
                // the squatting case, and it must be loud — continuing would mean talking
                // to whatever created it.
                _logger.LogCritical(
                    ex,
                    "The IPC pipe name {PipeName} is already in use. Another process may be impersonating " +
                    "the agent endpoint. Session-scoped features will be unavailable.",
                    IpcPipe.PipeName);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The IPC accept loop failed; retrying shortly.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandshakeAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var connection = new IpcConnection(stream, _logger, "service");
        var handshake = new TaskCompletionSource<IpcHelloArgs?>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.RequestHandler = async (request, token) =>
        {
            if (string.Equals(request.Command, IpcCommands.Hello, StringComparison.Ordinal))
            {
                IpcHelloArgs? hello = null;
                if (request.Args is { } args && args.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        hello = args.Deserialize<IpcHelloArgs>(ProtocolJson.Options);
                    }
                    catch (JsonException)
                    {
                        hello = null;
                    }
                }

                if (hello is null || !ValidateSpawnToken(hello))
                {
                    handshake.TrySetResult(null);
                    return IpcResponse.Failure(
                        request.Id,
                        ErrorCodes.Unauthenticated,
                        "Invalid agent token.");
                }

                handshake.TrySetResult(hello);
                return IpcResponse.Success(request.Id);
            }

            // Anything other than hello before the handshake completes is refused. After
            // it, requests from the agent (currently only the pairing prompt) go to the
            // service's handler.
            if (!handshake.Task.IsCompletedSuccessfully || handshake.Task.Result is null)
            {
                return IpcResponse.Failure(
                    request.Id,
                    ErrorCodes.Unauthenticated,
                    "Complete the agent handshake first.");
            }

            Func<IpcRequest, CancellationToken, Task<IpcResponse>>? handler = AgentRequestHandler;
            return handler is null
                ? IpcResponse.Failure(request.Id, ErrorCodes.NotSupported, "Unsupported agent request.")
                : await handler(request, token).ConfigureAwait(false);
        };

        connection.EventHandler = (ipcEvent, _) =>
        {
            AgentEvent?.Invoke(this, ipcEvent);
            return Task.CompletedTask;
        };

        connection.Start();

        IpcHelloArgs? result;
        try
        {
            result = await handshake.Task.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("An IPC client connected but did not complete the agent handshake in time.");
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (result is null)
        {
            _logger.LogWarning("An IPC client failed the agent handshake and was dropped.");
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var agent = new ConnectedAgent(
            result.SessionId,
            result.UserName,
            connection,
            result.Commands.ToHashSet(StringComparer.Ordinal),
            result.Capabilities,
            DateTimeOffset.UtcNow,
            SessionAgentKinds.Parse(result.Kind));

        (int, SessionAgentKind) key = (agent.SessionId, agent.Kind);

        // Replace any stale agent for the same session: after a crash-and-respawn the old
        // connection may not have been noticed as dead yet.
        if (_agents.TryRemove(key, out ConnectedAgent? previous))
        {
            _logger.LogInformation(
                "Replacing a previous {Kind} agent connection for session {SessionId}.",
                agent.Kind,
                agent.SessionId);
            await previous.Connection.DisposeAsync().ConfigureAwait(false);
        }

        _agents[key] = agent;

        connection.Closed += (_, _) =>
        {
            if (_agents.TryGetValue(key, out ConnectedAgent? current) &&
                ReferenceEquals(current, agent))
            {
                _agents.TryRemove(key, out _);
                _logger.LogInformation(
                    "The {Kind} agent for session {SessionId} disconnected.",
                    agent.Kind,
                    agent.SessionId);
                AgentDisconnected?.Invoke(this, agent.SessionId);
            }
        };

        _logger.LogInformation(
            "Session agent connected. Kind={Kind} Session={SessionId} User={User} Version={Version} " +
            "Commands={CommandCount} Capabilities={Capabilities}",
            agent.Kind,
            agent.SessionId,
            agent.UserName,
            result.AgentVersion,
            agent.Commands.Count,
            string.Join(",", agent.Capabilities));

        AgentConnected?.Invoke(this, agent);
    }

    /// <summary>
    /// Validates and burns the one-time spawn token.
    /// </summary>
    /// <remarks>
    /// Compared in constant time, and removed on success so the same token cannot admit a
    /// second connection. A failed comparison leaves the token in place: an attacker
    /// guessing wrongly must not be able to invalidate the real agent's credential and
    /// thereby deny the feature.
    /// </remarks>
    private bool ValidateSpawnToken(IpcHelloArgs hello)
    {
        if (string.IsNullOrEmpty(hello.Token))
        {
            return false;
        }

        // Looked up by the kind the agent claims, so a token issued for the user's own agent can
        // never register a process as the SYSTEM desktop agent, or the other way round.
        (int, SessionAgentKind) key = (hello.SessionId, SessionAgentKinds.Parse(hello.Kind));

        if (!_spawnTokens.TryGetValue(key, out string? expected))
        {
            _logger.LogWarning(
                "An agent claimed session {SessionId} ({Kind}), for which no spawn token was issued.",
                hello.SessionId,
                key.Item2);
            return false;
        }

        bool match = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(hello.Token),
            System.Text.Encoding.UTF8.GetBytes(expected));

        if (!match)
        {
            _logger.LogWarning(
                "An agent presented an incorrect spawn token for session {SessionId}.",
                hello.SessionId);
            return false;
        }

        _spawnTokens.TryRemove(key, out _);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        foreach (ConnectedAgent agent in _agents.Values)
        {
            await agent.Connection.DisposeAsync().ConfigureAwait(false);
        }

        _agents.Clear();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Accept loop is blocked in WaitForConnectionAsync; cancellation ends it.
            }
        }

        _lifetime.Dispose();
    }
}
