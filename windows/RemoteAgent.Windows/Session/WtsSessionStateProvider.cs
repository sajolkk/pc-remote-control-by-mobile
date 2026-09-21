using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Session;

/// <summary>
/// Reports the state of the interactive Windows session (§3).
/// </summary>
/// <remarks>
/// <para>The service needs this for two things: to know where to launch the session
/// agent, and to tell the phone honestly why the screen is blank. "Locked" is not an
/// error state to hide — it is the OS enforcing that a remote viewer cannot see the
/// secure desktop (§12.1), and the client shows a clear explanation rather than a
/// frozen frame.</para>
///
/// <para>Lock state is <em>not</em> obtainable from <c>WTSEnumerateSessions</c>: a
/// locked session still reports <c>Active</c>. The service therefore learns about
/// locking from <c>SERVICE_ACCEPT_SESSIONCHANGE</c> notifications and pushes it in
/// through <see cref="SetLocked"/>, while this class owns the parts that can be
/// polled. Keeping the two sources separate avoids a provider that appears to know
/// something it cannot actually observe.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WtsSessionStateProvider : ISessionStateProvider
{
    private readonly ILogger<WtsSessionStateProvider> _logger;
    private readonly object _sync = new();

    private InteractiveSessionState _state = InteractiveSessionState.Unknown;
    private string? _userName;
    private int? _activeSessionId;
    private bool _locked;

    /// <summary>Creates the provider and takes an initial reading.</summary>
    public WtsSessionStateProvider(ILogger<WtsSessionStateProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Refresh();
    }

    /// <inheritdoc />
    public event EventHandler<InteractiveSessionState>? StateChanged;

    /// <inheritdoc />
    public InteractiveSessionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public string? UserName
    {
        get
        {
            lock (_sync)
            {
                return _userName;
            }
        }
    }

    /// <inheritdoc />
    public int? ActiveSessionId
    {
        get
        {
            lock (_sync)
            {
                return _activeSessionId;
            }
        }
    }

    /// <summary>
    /// Re-reads the active console session and who owns it. Called at startup and on
    /// every session-change notification.
    /// </summary>
    public void Refresh()
    {
        uint consoleSessionId = NativeMethods.WTSGetActiveConsoleSessionId();

        // 0xFFFFFFFF means there is no console session attached at all, which happens
        // during fast user switching transitions and on some headless configurations.
        if (consoleSessionId == uint.MaxValue)
        {
            Update(InteractiveSessionState.LoggedOut, null, null);
            return;
        }

        string? user = QuerySessionString(consoleSessionId, NativeMethods.WtsInfoClass.UserName);
        if (string.IsNullOrWhiteSpace(user))
        {
            // A console session with no user name is the logon screen: the session
            // exists so that Winlogon can run, but nobody is signed in.
            Update(InteractiveSessionState.LoggedOut, null, (int)consoleSessionId);
            return;
        }

        bool locked;
        lock (_sync)
        {
            locked = _locked;
        }

        Update(
            locked ? InteractiveSessionState.Locked : InteractiveSessionState.Active,
            user,
            (int)consoleSessionId);
    }

    /// <summary>
    /// Records a lock or unlock reported by a session-change notification.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Refresh"/> because Windows exposes lock state only as
    /// an event, never as queryable state.
    /// </remarks>
    public void SetLocked(bool locked)
    {
        lock (_sync)
        {
            if (_locked == locked)
            {
                return;
            }

            _locked = locked;
        }

        _logger.LogInformation("Interactive session {State}.", locked ? "locked" : "unlocked");
        Refresh();
    }

    private void Update(InteractiveSessionState state, string? userName, int? sessionId)
    {
        bool changed;
        lock (_sync)
        {
            changed = _state != state ||
                      !string.Equals(_userName, userName, StringComparison.Ordinal) ||
                      _activeSessionId != sessionId;

            _state = state;
            _userName = userName;
            _activeSessionId = sessionId;

            if (state == InteractiveSessionState.LoggedOut)
            {
                // Nobody is signed in, so a stale "locked" flag from the previous user
                // must not survive to mislabel the next logon.
                _locked = false;
            }
        }

        if (changed)
        {
            _logger.LogInformation(
                "Session state: {State} Session={SessionId} User={User}",
                state,
                sessionId,
                userName is null ? "(none)" : userName);

            StateChanged?.Invoke(this, state);
        }
    }

    private string? QuerySessionString(uint sessionId, NativeMethods.WtsInfoClass infoClass)
    {
        nint buffer = nint.Zero;
        try
        {
            if (!NativeMethods.WTSQuerySessionInformation(
                    NativeMethods.WtsCurrentServerHandle,
                    sessionId,
                    infoClass,
                    out buffer,
                    out uint bytes))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ErrorAccessDenied)
                {
                    _logger.LogDebug(
                        "WTSQuerySessionInformation({InfoClass}) failed for session {SessionId}: error {Error}.",
                        infoClass,
                        sessionId,
                        error);
                }

                return null;
            }

            return bytes == 0 ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
    }

    /// <summary>
    /// Enumerates sessions for diagnostics and for the UI's status view.
    /// </summary>
    public IReadOnlyList<(int SessionId, string? UserName, string State)> EnumerateSessions()
    {
        nint buffer = nint.Zero;
        var results = new List<(int, string?, string)>();

        try
        {
            if (!NativeMethods.WTSEnumerateSessions(
                    NativeMethods.WtsCurrentServerHandle,
                    0,
                    1,
                    out buffer,
                    out uint count))
            {
                _logger.LogDebug("WTSEnumerateSessions failed: error {Error}.", Marshal.GetLastWin32Error());
                return results;
            }

            int structSize = Marshal.SizeOf<NativeMethods.WtsSessionInfo>();
            for (uint i = 0; i < count; i++)
            {
                nint entry = buffer + (int)(i * structSize);
                var info = Marshal.PtrToStructure<NativeMethods.WtsSessionInfo>(entry);
                results.Add((
                    (int)info.SessionId,
                    QuerySessionString(info.SessionId, NativeMethods.WtsInfoClass.UserName),
                    info.State.ToString()));
            }

            return results;
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                NativeMethods.WTSFreeMemory(buffer);
            }
        }
    }
}
