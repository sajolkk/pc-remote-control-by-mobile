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
/// <para><b>Two agents per session when installed.</b> The service runs as SYSTEM, so it starts a
/// desktop agent as SYSTEM in the console session for screen and input — the only way the lock
/// screen and the sign-in screen can be seen and used remotely — beside the user agent that does
/// everything needing the user's own rights (see <see cref="SessionAgentKind"/>). Each kind has its
/// own backoff, so one failing does not stop the other.</para>
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

    private readonly bool _runningAsSystem;

    private readonly Dictionary<SessionAgentKind, LaunchState> _launchStates = new()
    {
        [SessionAgentKind.Full] = new LaunchState(),
        [SessionAgentKind.User] = new LaunchState(),
        [SessionAgentKind.Desktop] = new LaunchState(),
    };

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
        _runningAsSystem = IsLocalSystem();
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
        foreach (LaunchState launch in _launchStates.Values)
        {
            launch.Reset();
        }

        _registry.SetActiveSession(_sessionState.ActiveSessionId);
    }

    private void OnAgentDisconnected(object? sender, int sessionId)
    {
        _logger.LogInformation(
            "An agent for session {SessionId} disconnected; it will be restarted if that session is active.",
            sessionId);

        // A disconnect is definite information that an agent is no longer running, so the startup
        // grace no longer applies and the replacement can start immediately. The event does not
        // say which kind went, and clearing the grace for one that is still running costs nothing:
        // it is connected, so nothing is launched for it.
        foreach (LaunchState launch in _launchStates.Values)
        {
            launch.NextAttemptAfter = DateTimeOffset.MinValue;
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureAgentsAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Decides which agents the console session needs, and starts any that are missing.
    /// </summary>
    /// <remarks>
    /// <para>Installed as a service (SYSTEM, in Session 0) the work is split: a desktop agent that
    /// runs as SYSTEM for screen and input, so the lock screen and the sign-in screen can be seen
    /// and used, and a user agent for everything that must run with the user's own rights. The
    /// desktop agent is wanted even when nobody is signed in — the sign-in screen is exactly what
    /// someone needs after a restart.</para>
    ///
    /// <para>Anywhere else — the portable foreground process, or a console-mode run — nothing can
    /// cross into another session as SYSTEM, so one full agent does everything, as before.</para>
    /// </remarks>
    private async Task EnsureAgentsAsync(CancellationToken cancellationToken)
    {
        int? sessionId = _sessionState.ActiveSessionId;
        _registry.SetActiveSession(sessionId);

        if (sessionId is null)
        {
            // No console session at all. Not a problem to solve — the service still answers
            // power, status and wake commands, which is the whole reason it runs in Session 0 (§3).
            return;
        }

        bool signedIn = _sessionState.State != InteractiveSessionState.LoggedOut;

        // Crossing a session boundary needs SYSTEM and a duplicated token; launching into our own
        // session needs neither, and attempting the cross-session path there would fail with
        // access denied for an ordinary user. Deciding by comparing session ids means the
        // installed service and the portable foreground process both work without a mode flag.
        int currentSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        bool sameSession = currentSession == sessionId.Value;
        bool split = _runningAsSystem && !sameSession;

        if (split)
        {
            await EnsureAgentAsync(sessionId.Value, SessionAgentKind.Desktop, sameSession, cancellationToken)
                .ConfigureAwait(false);

            if (signedIn)
            {
                await EnsureAgentAsync(sessionId.Value, SessionAgentKind.User, sameSession, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (signedIn)
        {
            await EnsureAgentAsync(sessionId.Value, SessionAgentKind.Full, sameSession, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureAgentAsync(
        int sessionId,
        SessionAgentKind kind,
        bool sameSession,
        CancellationToken cancellationToken)
    {
        LaunchState state = _launchStates[kind];

        if (_registry.IsAgentConnected(sessionId, kind))
        {
            return;
        }

        if (state.ConsecutiveFailures >= MaxConsecutiveFailures)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < state.NextAttemptAfter)
        {
            return;
        }

        await _launchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: the monitor tick and a session-change event can both
            // arrive here, and launching two agents of one kind into one session would have them
            // fight over the same desktop resources.
            if (_registry.IsAgentConnected(sessionId, kind))
            {
                return;
            }

            string token = _registry.IssueSpawnToken(sessionId, kind);

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [IpcPipe.SpawnTokenVariable] = token,
                [IpcPipe.SessionIdVariable] = sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [IpcPipe.AgentKindVariable] = SessionAgentKinds.ToName(kind),
            };

            AgentLaunchResult result = kind switch
            {
                SessionAgentKind.Desktop => _launcher.LaunchAsSystem((uint)sessionId, _agentExecutablePath, environment),
                _ when sameSession => _launcher.LaunchInCurrentSession(_agentExecutablePath, environment),
                _ => _launcher.Launch((uint)sessionId, _agentExecutablePath, environment),
            };

            if (result.Launched)
            {
                state.ConsecutiveFailures = 0;
                state.Backoff = MinBackoff;

                // Give the new process time to connect before considering another launch, or the
                // next monitor tick spawns a second agent while the first is still starting.
                state.NextAttemptAfter = DateTimeOffset.UtcNow.Add(StartupGrace);
                return;
            }

            state.ConsecutiveFailures++;
            state.NextAttemptAfter = DateTimeOffset.UtcNow.Add(state.Backoff);
            state.Backoff = TimeSpan.FromTicks(Math.Min(state.Backoff.Ticks * 2, MaxBackoff.Ticks));

            if (state.ConsecutiveFailures >= MaxConsecutiveFailures)
            {
                _logger.LogError(
                    "Giving up on starting the {Kind} agent for session {SessionId} after {Count} attempts: " +
                    "{Reason} The features it provides stay unavailable until the session changes.",
                    kind,
                    sessionId,
                    state.ConsecutiveFailures,
                    result.FailureReason);
            }
            else
            {
                _logger.LogWarning(
                    "Could not start the {Kind} agent for session {SessionId} ({Reason}). " +
                    "Retrying in {Backoff}s.",
                    kind,
                    sessionId,
                    result.FailureReason,
                    state.Backoff.TotalSeconds);
            }
        }
        finally
        {
            _launchLock.Release();
        }
    }

    private static bool IsLocalSystem()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Launch bookkeeping for one kind of agent.</summary>
    private sealed class LaunchState
    {
        public DateTimeOffset NextAttemptAfter { get; set; } = DateTimeOffset.MinValue;

        public TimeSpan Backoff { get; set; } = MinBackoff;

        public int ConsecutiveFailures { get; set; }

        public void Reset()
        {
            ConsecutiveFailures = 0;
            Backoff = MinBackoff;
            NextAttemptAfter = DateTimeOffset.MinValue;
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
