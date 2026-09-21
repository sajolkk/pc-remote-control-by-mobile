using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Session;

namespace RemoteAgent.Service.Services;

/// <summary>
/// Keeps a session agent running in the active console session (§3).
/// </summary>
/// <remarks>
/// <para>This is the supervisor that makes the two-process design self-healing. It launches an
/// agent when a user signs in, relaunches it if it dies, and does nothing at all when nobody is
/// signed in — because there is no desktop to serve then, and repeatedly failing to launch one
/// would just fill the log.</para>
///
/// <para><b>Backoff matters more than it looks.</b> If the agent crashes on startup — a bad
/// GPU driver, a missing dependency — an unthrottled supervisor would spawn processes in a
/// tight loop and make a broken feature into a broken machine. Failures back off
/// exponentially, and after a run of them the supervisor stops trying until the session
/// changes, so a permanently broken agent costs one log entry rather than a spin.</para>
///
/// <para><b>Why not a scheduled task at logon?</b> Because then nothing owns the agent's
/// lifetime: a crash would leave the desktop unreachable until the next logon, and the service
/// would have no way to know why. Spawning from the service means the process that needs the
/// agent is the process responsible for it.</para>
/// </remarks>
public sealed class SessionAgentSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a freshly launched agent is given to complete its IPC handshake before the
    /// supervisor considers launching another.
    /// </summary>
    /// <remarks>
    /// Without this the supervisor spawns duplicates. The check for "is an agent connected" cannot
    /// see an agent that has started but not yet handshaked, and startup is not instant: the agent
    /// builds its application catalog first, which took over two and a half seconds on a busy
    /// machine during testing. The monitor's three-second tick then fired again and launched a
    /// second agent, and a third. Duplicate agents compete for the same desktop, and each adds its
    /// own tray icon.
    ///
    /// Generous on purpose. Waiting too long to retry a genuinely failed launch costs a few
    /// seconds; launching duplicates costs correctness.
    /// </remarks>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    private const int MaxConsecutiveFailures = 5;

    private readonly SessionAgentLauncher _launcher;
    private readonly SessionAgentRegistry _registry;
    private readonly ISessionStateProvider _sessionState;
    private readonly ILogger<SessionAgentSupervisor> _logger;
    private readonly string _agentExecutablePath;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // Disposal happens twice: once from the ordered shutdown in the host, and again when
    // the DI container disposes its singletons. The second pass must be a no-op.
    private bool _disposed;

    private DateTimeOffset _nextAttemptAfter = DateTimeOffset.MinValue;
    private TimeSpan _backoff = MinBackoff;
    private int _consecutiveFailures;
    private Task? _monitor;

    /// <summary>Creates the supervisor.</summary>
    /// <param name="launcher">Cross-session process launcher.</param>
    /// <param name="registry">Where connected agents register.</param>
    /// <param name="sessionState">Which session is on the console.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="agentExecutablePath">
    /// Full path to the agent executable, resolved from this process's own directory so the
    /// pair always match and no path is configured (§0).
    /// </param>
    public SessionAgentSupervisor(
        SessionAgentLauncher launcher,
        SessionAgentRegistry registry,
        ISessionStateProvider sessionState,
        ILogger<SessionAgentSupervisor> logger,
        string agentExecutablePath)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _agentExecutablePath = agentExecutablePath ?? throw new ArgumentNullException(nameof(agentExecutablePath));
    }

    /// <summary>Starts watching for sessions that need an agent.</summary>
    public void Start()
    {
        if (!File.Exists(_agentExecutablePath))
        {
            // Worth being loud about: everything session-scoped will be unavailable, and the
            // cause is a deployment problem rather than anything the user did.
            _logger.LogError(
                "The session agent executable was not found at {Path}. Screen, input, apps, clipboard " +
                "and file features will be unavailable until the installation is repaired.",
                _agentExecutablePath);
            return;
        }

        _sessionState.StateChanged += OnSessionStateChanged;
        _registry.AgentDisconnected += OnAgentDisconnected;

        _monitor ??= Task.Run(() => MonitorAsync(_lifetime.Token), CancellationToken.None);
    }

    private void OnSessionStateChanged(object? sender, InteractiveSessionState state)
    {
        // A session change is a fresh start: whatever went wrong before may not apply to this
        // user, so the failure count and backoff are reset. The startup grace is cleared too,
        // because an agent launched into the previous session is no longer the one we need.
        _consecutiveFailures = 0;
        _backoff = MinBackoff;
        _nextAttemptAfter = DateTimeOffset.MinValue;

        _registry.SetActiveSession(_sessionState.ActiveSessionId);
    }

    private void OnAgentDisconnected(object? sender, int sessionId)
    {
        _logger.LogInformation(
            "The agent for session {SessionId} disconnected; it will be restarted if that session is active.",
            sessionId);

        // A disconnect is definite information that no agent is running, so the startup grace no
        // longer applies and the replacement can start immediately.
        _nextAttemptAfter = DateTimeOffset.MinValue;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The session agent supervisor loop failed; continuing.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task EnsureAgentAsync(CancellationToken cancellationToken)
    {
        int? sessionId = _sessionState.ActiveSessionId;
        _registry.SetActiveSession(sessionId);

        if (sessionId is null || _sessionState.State == InteractiveSessionState.LoggedOut)
        {
            // Nobody signed in. Not a problem to solve — the service still answers power,
            // status and wake commands, which is the whole reason it runs in Session 0 (§3).
            return;
        }

        if (_registry.All.Any(a => a.SessionId == sessionId.Value && a.Connection.IsConnected))
        {
            return;
        }

        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _nextAttemptAfter)
        {
            return;
        }

        await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: the monitor tick and a session-change event can both
            // arrive here, and launching two agents into one session would have them fight
            // over the same desktop resources.
            if (_registry.All.Any(a => a.SessionId == sessionId.Value && a.Connection.IsConnected))
            {
                return;
            }

            string token = _registry.IssueSpawnToken(sessionId.Value);

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [IpcPipe.SpawnTokenVariable] = token,
                [IpcPipe.SessionIdVariable] = sessionId.Value.ToString(),
            };

            // Crossing a session boundary needs SYSTEM and a duplicated user token; launching into
            // our own session needs neither, and attempting the cross-session path there would fail
            // with access denied for an ordinary user. Deciding by comparing session ids means the
            // installed service and the portable foreground process both work without a mode flag.
            int currentSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            bool sameSession = currentSession == sessionId.Value;

            AgentLaunchResult result = sameSession
                ? _launcher.LaunchInCurrentSession(_agentExecutablePath, environment)
                : _launcher.Launch((uint)sessionId.Value, _agentExecutablePath, environment);

            if (result.Launched)
            {
                _consecutiveFailures = 0;
                _backoff = MinBackoff;

                // Give the new process time to connect before considering another launch, or the
                // next monitor tick spawns a second agent while the first is still starting.
                _nextAttemptAfter = DateTimeOffset.UtcNow.Add(StartupGrace);
                return;
            }

            _consecutiveFailures++;
            _nextAttemptAfter = DateTimeOffset.UtcNow.Add(_backoff);
            _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));

            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _logger.LogError(
                    "Giving up on starting the session agent for session {SessionId} after {Count} attempts: " +
                    "{Reason} Session-scoped features stay unavailable until the next sign-in.",
                    sessionId.Value,
                    _consecutiveFailures,
                    result.FailureReason);
            }
            else
            {
                _logger.LogWarning(
                    "Could not start the session agent for session {SessionId} ({Reason}). " +
                    "Retrying in {Backoff}s.",
                    sessionId.Value,
                    result.FailureReason,
                    _backoff.TotalSeconds);
            }
        }
        finally
        {
            _launchLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _sessionState.StateChanged -= OnSessionStateChanged;
        _registry.AgentDisconnected -= OnAgentDisconnected;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_monitor is not null)
        {
            try
            {
                await _monitor.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Loop ends with cancellation.
            }
        }

        _lifetime.Dispose();
        _launchLock.Dispose();
    }
}
