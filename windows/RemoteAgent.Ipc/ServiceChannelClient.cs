using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RemoteAgent.Protocol;

namespace RemoteAgent.Ipc;

/// <summary>
/// The session agent's end of the IPC channel.
/// </summary>
/// <remarks>
/// <para>Connects to the service, proves its identity with the one-time spawn token it was
/// given in its environment, and then serves forwarded commands for as long as the service
/// keeps it alive.</para>
///
/// <para>Reconnection is the agent's responsibility, not the service's: if the service
/// restarts, the agent's pipe dies but the agent process survives. It retries with backoff
/// rather than exiting at once, because exiting would leave the desktop unreachable until the
/// service noticed and respawned it — a slower and more visible failure than simply
/// reconnecting (§6.4). The retrying is bounded by <see cref="GiveUpAfter"/>, though: the
/// service starts a fresh agent whenever it has none, so an agent that has been unreachable for
/// minutes is not keeping anything reachable, only keeping a process and a tray icon alive.</para>
///
/// <para>One subtlety: a reconnect needs a fresh spawn token, and the agent only has the one
/// it started with. So after the first successful handshake the token is kept for reuse,
/// and if the service rejects it (because that service instance never issued it) the agent
/// exits and lets the service spawn a replacement with a valid token. That keeps the
/// one-time property intact instead of weakening it to make reconnects convenient.</para>
/// </remarks>
public sealed class ServiceChannelClient : IAsyncDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<ServiceChannelClient> _logger;
    private readonly string _token;
    private readonly int _sessionId;
    private readonly string _userName;
    private readonly string _agentVersion;
    private readonly SessionAgentKind _kind;

    private IpcConnection? _connection;
    private IReadOnlyList<string> _capabilities = [];

    /// <summary>Creates the client from the environment the service provided.</summary>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="token">One-time spawn token.</param>
    /// <param name="sessionId">The session this agent serves.</param>
    /// <param name="userName">The user this agent runs as.</param>
    /// <param name="agentVersion">Build version, for mismatch detection.</param>
    /// <param name="kind">Which share of the session's work this agent does.</param>
    public ServiceChannelClient(
        ILogger<ServiceChannelClient> logger,
        string token,
        int sessionId,
        string userName,
        string agentVersion,
        SessionAgentKind kind = SessionAgentKind.Full)
    {
        _kind = kind;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _sessionId = sessionId;
        _userName = userName;
        _agentVersion = agentVersion;
    }

    /// <summary>
    /// Creates the client end of the pipe. Tests replace it with a private pipe name, because the
    /// real one belongs to the installed service.
    /// </summary>
    internal Func<NamedPipeClientStream> CreateClientStream { get; init; } = IpcPipe.CreateClientStream;

    /// <summary>
    /// How long the agent keeps trying to reach the service before <see cref="RunAsync"/> gives
    /// up and asks the host to exit.
    /// </summary>
    /// <remarks>
    /// The service is the only thing that starts agents, and it starts another whenever it finds
    /// none connected. While it cannot be reached, every agent it launched is stuck in this loop;
    /// unbounded, they accumulated by the dozen, each with its own tray icon. Two minutes is long
    /// enough to ride out a service restart and short enough that a stuck agent is gone before
    /// anyone goes looking for it.
    /// </remarks>
    internal TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Handles commands the service forwards. Set before connecting.</summary>
    public Func<IpcRequest, CancellationToken, Task<IpcResponse>>? RequestHandler { get; set; }

    /// <summary>Commands this agent implements, advertised at handshake.</summary>
    public IReadOnlyList<string> ImplementedCommands { get; set; } = [];

    /// <summary>Capabilities probed in this session, advertised at handshake.</summary>
    public IReadOnlyList<string> Capabilities
    {
        get => Volatile.Read(ref _capabilities);
        set => Volatile.Write(ref _capabilities, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Which share of the session's work this agent does.</summary>
    public SessionAgentKind Kind => _kind;

    /// <summary>Whether the channel is currently up.</summary>
    public bool IsConnected => _connection?.IsConnected == true;

    /// <summary>
    /// Reads the spawn parameters the service placed in the environment.
    /// </summary>
    /// <returns>Null when this process was not launched by the service.</returns>
    public static (string Token, int SessionId)? ReadSpawnEnvironment()
    {
        string? token = Environment.GetEnvironmentVariable(IpcPipe.SpawnTokenVariable);
        string? sessionText = Environment.GetEnvironmentVariable(IpcPipe.SessionIdVariable);

        if (string.IsNullOrEmpty(token) || !int.TryParse(sessionText, out int sessionId))
        {
            return null;
        }

        return (token, sessionId);
    }

    /// <summary>Reads the agent kind the service placed in the environment.</summary>
    public static SessionAgentKind ReadAgentKind() =>
        SessionAgentKinds.Parse(Environment.GetEnvironmentVariable(IpcPipe.AgentKindVariable));

    /// <summary>
    /// Connects and handshakes, retrying until the token is rejected, the service has been out of
    /// reach for <see cref="GiveUpAfter"/>, or cancellation.
    /// </summary>
    /// <returns>True once connected; false when the agent should exit.</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        TimeSpan maxBackoff = TimeSpan.FromSeconds(30);
        var unreachableFor = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            HandshakeOutcome outcome = await TryConnectOnceAsync(cancellationToken).ConfigureAwait(false);

            switch (outcome)
            {
                case HandshakeOutcome.Connected:
                    // Wait for the connection to end, then reconnect.
                    await WaitForCloseAsync(cancellationToken).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(1);
                    unreachableFor.Restart();
                    continue;

                case HandshakeOutcome.Rejected:
                    _logger.LogWarning(
                        "The service rejected this agent's token. Exiting so the service can start a " +
                        "replacement with a valid one.");
                    return false;

                case HandshakeOutcome.Unavailable:
                default:
                    if (unreachableFor.Elapsed >= GiveUpAfter)
                    {
                        _logger.LogWarning(
                            "The PC-Remote service could not be reached for {Seconds:F0}s. Exiting; the service " +
                            "starts a fresh agent once it can be reached again.",
                            unreachableFor.Elapsed.TotalSeconds);
                        return false;
                    }

                    try
                    {
                        await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
                    continue;
            }
        }

        return false;
    }

    private enum HandshakeOutcome
    {
        Connected,
        Unavailable,
        Rejected,
    }

    private async Task<HandshakeOutcome> TryConnectOnceAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream? stream = null;
        try
        {
            stream = CreateClientStream();
            await stream.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);

            var connection = new IpcConnection(stream, _logger, "agent")
            {
                RequestHandler = RequestHandler,
            };

            connection.Start();

            IReadOnlyList<string> advertised = Capabilities;

            var hello = new IpcHelloArgs
            {
                Token = _token,
                SessionId = _sessionId,
                UserName = _userName,
                AgentVersion = _agentVersion,
                Commands = ImplementedCommands.ToArray(),
                Capabilities = advertised.ToArray(),
                Kind = SessionAgentKinds.ToName(_kind),
            };

            IpcResponse response = await connection.SendRequestAsync(
                new IpcRequest
                {
                    Id = "hello",
                    Command = IpcCommands.Hello,
                    Args = JsonSerializer.SerializeToElement(hello, ProtocolJson.Options),
                },
                HelloTimeout,
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                bool rejected = string.Equals(
                    response.Error?.Code,
                    ErrorCodes.Unauthenticated,
                    StringComparison.Ordinal);

                await connection.DisposeAsync().ConfigureAwait(false);
                stream = null;

                return rejected ? HandshakeOutcome.Rejected : HandshakeOutcome.Unavailable;
            }

            _connection = connection;
            stream = null;

            _logger.LogInformation(
                "Connected to the PC-Remote service (session {SessionId}, user {User}).",
                _sessionId,
                _userName);

            // An update made while the hello was in flight found no connection to send on, and the
            // hello carried the list from before it. Sent now, or the service keeps the old one.
            IReadOnlyList<string> current = Capabilities;
            if (!ReferenceEquals(current, advertised))
            {
                await SendCapabilitiesAsync(current, cancellationToken).ConfigureAwait(false);
            }

            return HandshakeOutcome.Connected;
        }
        catch (TimeoutException)
        {
            // The service is not listening yet. Normal at logon, when the agent can start
            // before the service finishes initializing.
            return HandshakeOutcome.Unavailable;
        }
        catch (OperationCanceledException)
        {
            return HandshakeOutcome.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not connect to the service pipe.");
            return HandshakeOutcome.Unavailable;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WaitForCloseAsync(CancellationToken cancellationToken)
    {
        IpcConnection? connection = _connection;
        if (connection is null)
        {
            return;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += (_, _) => closed.TrySetResult();

        if (!connection.IsConnected)
        {
            closed.TrySetResult();
        }

        try
        {
            await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        _logger.LogInformation("The service connection closed; will attempt to reconnect.");
        await connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the connection to the service drops. Every control-connection id the agent was
    /// told about belongs to that service instance and is meaningless afterwards.
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>Replaces the advertised capabilities and tells the service, which tells clients.</summary>
    public Task UpdateCapabilitiesAsync(IReadOnlyList<string> capabilities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // Stored first: a handshake that happens instead of, or during, this event carries them.
        Capabilities = capabilities;

        return SendCapabilitiesAsync(capabilities, cancellationToken);
    }

    private Task SendCapabilitiesAsync(IReadOnlyList<string> capabilities, CancellationToken cancellationToken) =>
        SendEventAsync(
            IpcCommands.CapabilitiesChanged,
            new JsonObject { ["capabilities"] = new JsonArray([.. capabilities.Select(c => JsonValue.Create(c))]) },
            cancellationToken);

    /// <summary>Pushes a state-change event to the service for relay to clients.</summary>
    public Task SendEventAsync(string topic, JsonNode? data, CancellationToken cancellationToken) =>
        SendEventAsync(topic, data, connectionId: null, cancellationToken);

    /// <summary>
    /// Pushes an event to the service for one control connection only, or for everyone when
    /// <paramref name="connectionId"/> is null.
    /// </summary>
    public Task SendEventAsync(string topic, JsonNode? data, string? connectionId, CancellationToken cancellationToken)
    {
        IpcConnection? connection = _connection;
        if (connection is null || !connection.IsConnected)
        {
            return Task.CompletedTask;
        }

        return connection.SendEventAsync(
            new IpcEvent
            {
                Topic = topic,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = data,
                ConnectionId = connectionId,
            },
            cancellationToken);
    }

    /// <summary>
    /// Sends a request to the service and returns its response.
    /// </summary>
    /// <remarks>
    /// The generic agent -> service path, used by the tray menu. Returns a failure response rather
    /// than throwing when the channel is down, because the caller is a UI action and needs
    /// something to display either way.
    /// </remarks>
    public async Task<IpcResponse> RequestAsync(
        string command,
        JsonNode? args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IpcConnection? connection = _connection;
        if (connection is null || !connection.IsConnected)
        {
            return IpcResponse.Failure(
                "0",
                ErrorCodes.SessionUnavailable,
                "Not connected to the PC-Remote service.");
        }

        return await connection.SendRequestAsync(
            new IpcRequest
            {
                Id = Guid.NewGuid().ToString("n"),
                Command = command,
                Args = args is null ? null : JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
            },
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the service to have the tray UI prompt for pairing approval.
    /// </summary>
    public async Task<bool> RequestPairingApprovalAsync(
        JsonNode payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IpcConnection? connection = _connection;
        if (connection is null || !connection.IsConnected)
        {
            return false;
        }

        IpcResponse response = await connection.SendRequestAsync(
            new IpcRequest
            {
                Id = Guid.NewGuid().ToString("n"),
                Command = IpcCommands.ApprovePairing,
                Args = JsonSerializer.SerializeToElement(payload, ProtocolJson.Options),
            },
            timeout,
            cancellationToken).ConfigureAwait(false);

        return response.Ok && response.Data?["approved"]?.GetValue<bool>() == true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
