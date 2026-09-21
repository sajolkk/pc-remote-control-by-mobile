using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using RemoteAgent.Core.Configuration;

namespace RemoteAgent.Service.Hosting;

/// <summary>
/// Registers and removes the Windows service (§3).
/// </summary>
/// <remarks>
/// <para>Uses <c>sc.exe</c> rather than P/Invoking the service control manager. The arguments are
/// fixed and derived from this process's own path — nothing here comes from configuration or the
/// network — and <c>sc.exe</c> handles the delayed-start and failure-action configuration that
/// would otherwise be several more native calls to get right. Every argument is passed through
/// <see cref="ProcessStartInfo.ArgumentList"/>, so the runtime quotes them and a path containing
/// spaces cannot turn into extra arguments.</para>
///
/// <para>The service is registered as <c>LocalSystem</c> with delayed automatic start, and with
/// failure actions that restart it. Delayed start because there is nothing urgent about being
/// reachable in the first few seconds of boot, and yielding to the rest of startup makes boot
/// measurably smoother. Restart-on-failure because a crashed agent should recover without
/// anyone noticing.</para>
///
/// <para>This is the one operation that genuinely needs elevation, which is why it is a separate
/// command-line switch rather than something the GUI does silently (§1.1).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ServiceInstaller
{
    /// <summary>Registers the service. Returns a process exit code.</summary>
    public static int Install(string serviceName, string displayName, string description, AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "Installing the service requires administrator rights. " +
                "Re-run this command from an elevated prompt.");
            return 5;
        }

        string executablePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(executablePath))
        {
            Console.Error.WriteLine("Could not determine this executable's path.");
            return 1;
        }

        Console.WriteLine($"Registering {displayName} from {executablePath}");

        // binPath must be a single argument containing the quoted executable path. sc.exe is
        // particular about the space after "binPath=".
        int result = RunSc(
            "create",
            serviceName,
            $"binPath= \"{executablePath}\"",
            "DisplayName= " + displayName,
            "start= delayed-auto",
            "obj= LocalSystem");

        if (result != 0)
        {
            Console.Error.WriteLine($"sc create failed with exit code {result}.");
            return result;
        }

        RunSc("description", serviceName, description);

        // Restart after 5s, then 10s, then every 60s; reset the counter after a day without
        // failures so a long-running instance is not one crash away from "give up".
        RunSc("failure", serviceName, "reset= 86400", "actions= restart/5000/restart/10000/restart/60000");

        ConfigureFirewall(executablePath, options);

        Console.WriteLine($"{displayName} registered. Starting it now.");

        int startResult = RunSc("start", serviceName);
        if (startResult != 0)
        {
            Console.Error.WriteLine(
                $"The service was registered but did not start (exit code {startResult}). " +
                "Check the log directory for details.");
        }

        return 0;
    }

    /// <summary>Stops and removes the service. Returns a process exit code.</summary>
    public static int Uninstall(string serviceName)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "Removing the service requires administrator rights. " +
                "Re-run this command from an elevated prompt.");
            return 5;
        }

        // Stop first, and tolerate failure: the service may already be stopped, which sc.exe
        // reports as an error but is exactly the state we want.
        RunSc("stop", serviceName);

        int result = RunSc("delete", serviceName);
        if (result != 0)
        {
            Console.Error.WriteLine($"sc delete failed with exit code {result}.");
            return result;
        }

        foreach (string rule in FirewallRuleNames)
        {
            RunTool("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={rule}");
        }

        Console.WriteLine(
            $"{serviceName} removed. Configuration, keys and pairings were left in place; " +
            "delete the data directory manually to remove them.");

        return 0;
    }

    private const string ControlRule = "PC-Remote control channel (TCP)";
    private const string DiscoveryRule = "PC-Remote discovery (UDP)";
    private const string MediaRule = "PC-Remote screen streaming (UDP)";

    private static readonly string[] FirewallRuleNames = [ControlRule, DiscoveryRule, MediaRule];

    /// <summary>
    /// Opens exactly the ports PC-Remote listens on, for exactly its own executables, on private
    /// networks only.
    /// </summary>
    /// <remarks>
    /// <para>Each rule is bound to a program path as well as a port range, so another process
    /// cannot borrow the opening by binding the same port, and to the Private profile, so the
    /// PC is not reachable on a network Windows was told is public (a café, an airport).</para>
    ///
    /// <para>Without the media rule streaming still usually works — the PC sends ICE checks to the
    /// phone first, and Windows Firewall admits the replies to that outbound traffic — but not on
    /// every network, and Windows may put up a prompt on the user's desktop the first time the
    /// session agent binds a UDP port. The rule makes it deterministic.</para>
    ///
    /// <para>Failures are reported and do not fail the installation: a PC whose firewall is managed
    /// by group policy may refuse local rules, and the owner can still add them by hand.</para>
    /// </remarks>
    private static void ConfigureFirewall(string serviceExecutable, AgentOptions options)
    {
        string directory = Path.GetDirectoryName(serviceExecutable) ?? string.Empty;
        string sessionExecutable = string.IsNullOrWhiteSpace(options.Startup.SessionAgentPath)
            ? Path.Combine(directory, "RemoteAgent.Session.exe")
            : options.Startup.SessionAgentPath;

        NetworkOptions network = options.Network;
        int controlEnd = Math.Min(65535, network.ControlPort + network.ControlPortFallbackRange - 1);

        var rules = new (string Name, string Program, string Protocol, string Ports)[]
        {
            (ControlRule, serviceExecutable, "TCP", $"{network.ControlPort}-{controlEnd}"),
            (DiscoveryRule, serviceExecutable, "UDP", options.Discovery.UdpFallbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (MediaRule, sessionExecutable, "UDP",
                $"{Math.Min(network.MediaPortRangeStart, network.MediaPortRangeEnd)}-{Math.Max(network.MediaPortRangeStart, network.MediaPortRangeEnd)}"),
        };

        foreach ((string name, string program, string protocol, string ports) in rules)
        {
            // Delete first so a reinstall with different ports replaces the rule instead of
            // leaving the old opening behind.
            RunTool("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={name}");

            int exit = RunTool(
                "netsh.exe",
                "advfirewall", "firewall", "add", "rule",
                $"name={name}",
                "dir=in",
                "action=allow",
                $"program={program}",
                $"protocol={protocol}",
                $"localport={ports}",
                "profile=private",
                "enable=yes");

            if (exit != 0)
            {
                Console.Error.WriteLine(
                    $"Could not add the firewall rule \"{name}\" (exit code {exit}). " +
                    $"Allow {protocol} {ports} for {program} on private networks manually.");
            }
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RunSc(params string[] arguments) => RunTool("sc.exe", arguments);

    private static int RunTool(string tool, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine($"Could not start {tool}.");
                return 1;
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine(output.Trim());
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.Error.WriteLine(error.Trim());
            }

            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not run {tool}: {ex.Message}");
            return 1;
        }
    }
}
