using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Forms;
using RemoteAgent.Session.Ui;
using Serilog;

namespace RemoteAgent.Session;

/// <summary>
/// What <c>RemoteAgent.Session.exe</c> does when a person opens it, e.g. from the Start menu.
/// </summary>
/// <remarks>
/// <para>It never becomes an agent. It asks the agent already running in this session to show its
/// pairing window, the same thing double-clicking the tray icon does.</para>
///
/// <para>When no agent answers, the service is usually still starting: it is registered for
/// delayed automatic start, so for a minute or two after boot there is nothing to talk to. The
/// user is offered to start it now. Starting a service needs administrator rights, so that goes
/// through the normal UAC prompt for <c>sc.exe</c>, with fixed arguments.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class LauncherMode
{
    private const string ServiceName = "PCRemoteAgent";
    private const string Title = "PC-Remote";

    private static readonly TimeSpan AgentStartWait = TimeSpan.FromSeconds(30);

    /// <summary>Runs the launcher. Returns a process exit code.</summary>
    /// <param name="args">
    /// <c>--after-install</c>, passed by the installer's "Open PC-Remote" option, waits quietly for
    /// the agent first: the service has only just been started and needs a few seconds.
    /// </param>
    public static int Run(string[] args)
    {
        bool afterInstall = args.Contains("--after-install", StringComparer.OrdinalIgnoreCase);

        if (afterInstall ? WaitForAgent() : ShowRequestSignal.TrySignal())
        {
            Log.Information("Launched by hand: asked the running session agent to show pairing.");
            return 0;
        }

        Log.Information("Launched by hand, but no session agent is running in this session.");

        DialogResult answer = MessageBox.Show(
            "PC-Remote is not running on this PC yet.\n\n" +
            "It starts by itself shortly after Windows starts. Start it now?",
            Title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (answer != DialogResult.Yes)
        {
            return 0;
        }

        if (!TryStartService())
        {
            return 1;
        }

        if (WaitForAgent())
        {
            return 0;
        }

        MessageBox.Show(
            "PC-Remote was started, but its tray icon has not appeared yet.\n\n" +
            "Wait a moment and open PC-Remote again. If it still does not appear, reinstall PC-Remote " +
            @"or look in %ProgramData%\PCRemote\logs.",
            Title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        return 1;
    }

    /// <summary>
    /// The service starts the agent a few seconds after it comes up; waits for it to listen.
    /// </summary>
    private static bool WaitForAgent()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < AgentStartWait)
        {
            if (ShowRequestSignal.TrySignal())
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return false;
    }

    private static bool TryStartService()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = $"start {ServiceName}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            process?.WaitForExit(15_000);

            // 1056: already running. That is fine; the agent may simply not be up yet.
            if (process is { HasExited: true, ExitCode: not 0 and not 1056 })
            {
                int code = process.ExitCode;
                Log.Warning("sc start {Service} exited with {Code}.", ServiceName, code);

                // The code is shown because the dialog is all a user on another PC can send back:
                // "not installed" and "installed but crashing at startup" need different repairs.
                MessageBox.Show(
                    $"PC-Remote could not be started: {DescribeStartFailure(code)} (error {code}).\n\n" +
                    "Run PC-Remote-Setup.exe again to repair it. If that does not help, look in " +
                    @"%ProgramData%\PCRemote\logs for the newest service-*.jsonl file.",
                    Title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return false;
            }

            return true;
        }
        catch (Win32Exception ex)
        {
            // 1223: the user declined the UAC prompt.
            Log.Information(ex, "Starting the service was cancelled.");
            return false;
        }
    }

    /// <summary>The service control manager's exit codes that a fresh installation can run into.</summary>
    private static string DescribeStartFailure(int code) => code switch
    {
        5 => "access was denied",
        1053 => "the service did not respond in time",
        1058 => "the service is disabled",
        1060 => "the PC-Remote service is not installed on this PC",
        1067 => "the service stopped right after starting",
        1069 => "the service account could not sign in",
        1072 => "the service is being removed and must be reinstalled",
        _ => "Windows reported an error",
    };
}
