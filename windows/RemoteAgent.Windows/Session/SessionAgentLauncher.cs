using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Session;

/// <summary>The outcome of attempting to launch a session agent.</summary>
/// <param name="Launched">Whether a process was created.</param>
/// <param name="ProcessId">The new process id, when launched.</param>
/// <param name="FailureReason">A displayable reason, when not.</param>
public readonly record struct AgentLaunchResult(bool Launched, uint ProcessId, string? FailureReason);

/// <summary>
/// Launches the user-session agent from the Windows service (§3).
/// </summary>
/// <remarks>
/// <para>This is the mechanism that crosses Session 0 isolation. A service cannot capture a
/// screen, inject input, or read a clipboard — but it can obtain the interactive user's
/// token and start a process that can. Everything about the service/agent split follows from
/// this one capability.</para>
///
/// <para><b>The sequence matters.</b> <c>WTSQueryUserToken</c> yields an impersonation-grade
/// token, which <c>CreateProcessAsUser</c> will not accept, so it is duplicated to a primary
/// token first. The user's environment block is built explicitly, because a process created
/// from a duplicated token otherwise inherits the service's Session 0 environment where
/// <c>%USERPROFILE%</c> points at the system profile — which would silently make the
/// file-transfer roots resolve to the wrong directories.</para>
///
/// <para><b>The desktop assignment matters too.</b> <c>lpDesktop</c> is set to
/// <c>winsta0\default</c>, the interactive window station and desktop. Leaving it null
/// would place the process on the service's invisible desktop, where capture would see
/// nothing and injected input would go nowhere — a failure that looks like a bug in the
/// capture code rather than a launch mistake.</para>
///
/// <para><b>The spawn token</b> is injected into the environment rather than the command
/// line, because any local process can read another process's command line while an
/// environment block is far better protected. That token is what lets the service recognise
/// the agent it started, since the pipe itself must be openable by any interactive user.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionAgentLauncher
{
    private const string InteractiveDesktop = @"winsta0\default";

    private readonly ILogger<SessionAgentLauncher> _logger;

    /// <summary>Creates the launcher.</summary>
    public SessionAgentLauncher(ILogger<SessionAgentLauncher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the agent executable in the given session.
    /// </summary>
    /// <param name="sessionId">The target Windows session.</param>
    /// <param name="executablePath">Full path to the agent executable.</param>
    /// <param name="extraEnvironment">Variables to add to the user's environment block.</param>
    public AgentLaunchResult Launch(
        uint sessionId,
        string executablePath,
        IReadOnlyDictionary<string, string> extraEnvironment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(extraEnvironment);

        if (!File.Exists(executablePath))
        {
            return new AgentLaunchResult(false, 0, $"The agent executable was not found at {executablePath}.");
        }

        nint userToken = nint.Zero;
        nint primaryToken = nint.Zero;
        nint environment = nint.Zero;
        nint customEnvironment = nint.Zero;

        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken))
            {
                int error = Marshal.GetLastWin32Error();

                // Error 1008 (no token) is the ordinary case at the logon screen: the session
                // exists for Winlogon but nobody is signed in. Not an error worth alarming
                // about, just a reason there is no desktop to serve (§6.4).
                if (error == NativeMethods.ErrorNoToken)
                {
                    return new AgentLaunchResult(false, 0, "No user is signed in to that session.");
                }

                return new AgentLaunchResult(
                    false,
                    0,
                    $"Could not obtain the user token for session {sessionId} (Win32 error {error}).");
            }

            if (!NativeMethods.DuplicateTokenEx(
                    userToken,
                    NativeMethods.TokenAllAccess,
                    nint.Zero,
                    NativeMethods.SecurityImpersonationLevel.Impersonation,
                    NativeMethods.TokenType.Primary,
                    out primaryToken))
            {
                return new AgentLaunchResult(
                    false,
                    0,
                    $"Could not duplicate the user token (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (!NativeMethods.CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                _logger.LogWarning(
                    "Could not build the user environment block (Win32 error {Error}). The agent will be " +
                    "started with only the variables it strictly needs, which may affect file-transfer roots.",
                    Marshal.GetLastWin32Error());

                environment = nint.Zero;
            }

            customEnvironment = BuildEnvironmentBlock(environment, extraEnvironment);

            var startupInfo = new NativeMethods.StartupInfo
            {
                Cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                Desktop = InteractiveDesktop,
                Flags = NativeMethods.StartfUseShowWindow,
                ShowWindow = NativeMethods.SwHide,
            };

            string workingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;

            // The command line is quoted explicitly. The path comes from this process's own
            // location, never from configuration or the network, but quoting it correctly is
            // free and removes any question about paths containing spaces.
            string commandLine = $"\"{executablePath}\"";

            bool created = NativeMethods.CreateProcessAsUser(
                primaryToken,
                executablePath,
                commandLine,
                nint.Zero,
                nint.Zero,
                false,
                NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateNoWindow |
                NativeMethods.CreateBreakawayFromJob,
                customEnvironment,
                workingDirectory,
                ref startupInfo,
                out NativeMethods.ProcessInformation processInfo);

            if (!created)
            {
                return new AgentLaunchResult(
                    false,
                    0,
                    $"CreateProcessAsUser failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            // The handles are not needed: the agent's lifetime is tracked through its IPC
            // connection, not by waiting on a handle. Holding them would keep a zombie
            // process entry alive after the agent exits.
            NativeMethods.CloseHandle(processInfo.Thread);
            NativeMethods.CloseHandle(processInfo.Process);

            _logger.LogInformation(
                "Started the session agent in session {SessionId} (pid {ProcessId}).",
                sessionId,
                processInfo.ProcessId);

            return new AgentLaunchResult(true, processInfo.ProcessId, null);
        }
        finally
        {
            if (customEnvironment != nint.Zero)
            {
                Marshal.FreeHGlobal(customEnvironment);
            }

            if (environment != nint.Zero)
            {
                NativeMethods.DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != nint.Zero)
            {
                NativeMethods.CloseHandle(primaryToken);
            }

            if (userToken != nint.Zero)
            {
                NativeMethods.CloseHandle(userToken);
            }
        }
    }

    /// <summary>
    /// Starts the agent in the caller's own session, without token duplication.
    /// </summary>
    /// <remarks>
    /// <para>Used when the host is already running as the interactive user — console mode and the
    /// portable deployment. There is no session boundary to cross, so the whole
    /// <c>WTSQueryUserToken</c> dance is unnecessary <em>and</em> unavailable: obtaining another
    /// session's token requires SYSTEM, which an ordinary user process does not have.</para>
    ///
    /// <para>Without this path, portable mode would run the service half only and silently lack
    /// every desktop feature. Having both paths is what lets one binary work as an installed
    /// service and as a no-install foreground process (§0).</para>
    /// </remarks>
    public AgentLaunchResult LaunchInCurrentSession(
        string executablePath,
        IReadOnlyDictionary<string, string> extraEnvironment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(extraEnvironment);

        if (!File.Exists(executablePath))
        {
            return new AgentLaunchResult(false, 0, $"The agent executable was not found at {executablePath}.");
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            };

            // The spawn token travels in the environment, not the command line, for the same reason
            // as in the cross-session path: command lines are readable by other local processes.
            foreach (KeyValuePair<string, string> pair in extraEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return new AgentLaunchResult(false, 0, "The agent process could not be started.");
            }

            _logger.LogInformation(
                "Started the session agent in this process's own session (pid {ProcessId}).",
                process.Id);

            return new AgentLaunchResult(true, (uint)process.Id, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException)
        {
            return new AgentLaunchResult(false, 0, $"Could not start the agent: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the user's environment block and appends our own variables.
    /// </summary>
    /// <remarks>
    /// A Windows environment block is a sequence of null-terminated <c>NAME=VALUE</c> strings
    /// ending in an extra null. There is no API to add one variable to an existing block, so
    /// it is parsed, extended and rebuilt. Our variables are appended last, which means they
    /// win if the user happens to have a variable of the same name.
    /// </remarks>
    private static nint BuildEnvironmentBlock(nint userEnvironment, IReadOnlyDictionary<string, string> extra)
    {
        var entries = new List<string>();

        if (userEnvironment != nint.Zero)
        {
            nint cursor = userEnvironment;
            while (true)
            {
                string? entry = Marshal.PtrToStringUni(cursor);
                if (string.IsNullOrEmpty(entry))
                {
                    break;
                }

                entries.Add(entry);

                // Advance past this string and its terminator: (length + 1) UTF-16 units.
                cursor += (entry.Length + 1) * sizeof(char);
            }
        }

        foreach (KeyValuePair<string, string> pair in extra)
        {
            entries.Add($"{pair.Key}={pair.Value}");
        }

        var builder = new StringBuilder();
        foreach (string entry in entries)
        {
            builder.Append(entry).Append('\0');
        }

        builder.Append('\0');

        string block = builder.ToString();
        nint unmanaged = Marshal.StringToHGlobalUni(block);
        return unmanaged;
    }
}
