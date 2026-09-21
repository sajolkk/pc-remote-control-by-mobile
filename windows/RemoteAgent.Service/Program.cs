using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Security;
using RemoteAgent.Service.Configuration;
using RemoteAgent.Service.Discovery;
using RemoteAgent.Service.Handlers;
using RemoteAgent.Service.Hosting;
using RemoteAgent.Diagnostics;
using RemoteAgent.Service.Network;
using RemoteAgent.Service.Services;
using RemoteAgent.Windows.Network;
using RemoteAgent.Windows.Power;
using RemoteAgent.Windows.Security;
using RemoteAgent.Windows.Session;
using RemoteAgent.Windows.SystemInfo;
using Serilog;

namespace RemoteAgent.Service;

/// <summary>
/// Entry point and composition root for the privileged host.
/// </summary>
/// <remarks>
/// <para>The same executable runs three ways, which is what makes the system both installable
/// and portable (§0):</para>
/// <list type="bullet">
/// <item>as a Windows service, the normal deployment;</item>
/// <item>with <c>--console</c>, as an ordinary foreground process — for development, for a
/// machine where installing a service is not wanted, and for the no-install portable mode;</item>
/// <item>with <c>--install</c> / <c>--uninstall</c>, to register or remove the service.</item>
/// </list>
///
/// <para>Console mode is not a reduced mode. It runs the same listener, the same pairing and the
/// same command pipeline. What it loses is what actually requires SYSTEM: launching a session
/// agent into another user's session, and power privileges a standard user does not hold. Those
/// degrade to reported-as-unavailable rather than failing at request time.</para>
/// </remarks>
public static class Program
{
    private const string ServiceName = "PCRemoteAgent";
    private const string ServiceDisplayName = "PC-Remote Agent";

    private const string ServiceDescription =
        "Provides secure LAN remote control of this PC for paired mobile devices.";

    /// <summary>Process entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            return ServiceInstaller.Install(
                ServiceName,
                ServiceDisplayName,
                ServiceDescription,
                ReadBootstrapOptions(AgentPaths.Resolve()));
        }

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            return ServiceInstaller.Uninstall(ServiceName);
        }

        bool consoleMode = args.Contains("--console", StringComparer.OrdinalIgnoreCase) ||
                           !WindowsServiceHelpers.IsWindowsService();

        AgentPaths paths = AgentPaths.Resolve();
        paths.EnsureCreated();

        // Configuration has to be read before the logger exists, because it says how the logger
        // should be configured. A bootstrap logger covers that gap so a failure during
        // configuration load is still recorded somewhere.
        AgentOptions bootstrapOptions = ReadBootstrapOptions(paths);

        Log.Logger = AgentLogging.Create(
            paths,
            minimumLevel: "Information",
            retainDays: 14,
            toConsole: consoleMode,
            componentName: "service");

        try
        {
            Log.Information(
                "PC-Remote service host starting. Mode={Mode} DataDirectory={Path}",
                consoleMode ? "console" : "windows-service",
                paths.Root);

            IHost host = BuildHost(args, paths, bootstrapOptions, consoleMode);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The service host terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static AgentOptions ReadBootstrapOptions(AgentPaths paths)
    {
        using ILoggerFactory factory = LoggerFactory.Create(static builder => builder.AddSimpleConsole());
        var store = new AgentConfigurationStore(paths, factory.CreateLogger<AgentConfigurationStore>());
        return store.LoadOrDefault();
    }

    private static IHost BuildHost(
        string[] args,
        AgentPaths paths,
        AgentOptions bootstrapOptions,
        bool consoleMode)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();

        if (!consoleMode)
        {
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = ServiceName;
            });
        }

        // --- Configuration ---

        builder.Configuration.AddJsonFile(paths.ConfigFile, optional: true, reloadOnChange: true);
        builder.Configuration.AddJsonFile(paths.LocalConfigFile, optional: true, reloadOnChange: true);

        builder.Services.AddSingleton(paths);
        builder.Services
            .AddOptions<AgentOptions>()
            .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
            .ValidateDataAnnotations();

        builder.Services.AddSingleton<AgentConfigurationStore>(provider => new AgentConfigurationStore(
            paths,
            provider.GetRequiredService<ILogger<AgentConfigurationStore>>()));

        // --- Platform services (the only place Windows-specific types are constructed) ---

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<DpapiSecretProtector>(provider => new DpapiSecretProtector(
            provider.GetRequiredService<ILogger<DpapiSecretProtector>>(),

            // Machine scope for a service; user scope for a portable instance whose data lives
            // in a user-owned directory and which may not be able to write machine-scoped state.
            useMachineScope: !consoleMode || !paths.IsPortable));

        builder.Services.AddSingleton<ISecretProtector>(
            provider => provider.GetRequiredService<DpapiSecretProtector>());

        builder.Services.AddSingleton<WindowsSystemInfoProvider>();
        builder.Services.AddSingleton<ISystemInfoProvider>(
            provider => provider.GetRequiredService<WindowsSystemInfoProvider>());

        builder.Services.AddSingleton<WtsSessionStateProvider>();
        builder.Services.AddSingleton<ISessionStateProvider>(
            provider => provider.GetRequiredService<WtsSessionStateProvider>());

        builder.Services.AddSingleton<WindowsPowerController>();
        builder.Services.AddSingleton<IPowerController>(
            provider => provider.GetRequiredService<WindowsPowerController>());

        builder.Services.AddSingleton<IWakeOnLanInfoProvider, WakeOnLanInfoProvider>();
        builder.Services.AddSingleton<SessionAgentLauncher>();

        // --- Security ---

        builder.Services.AddSingleton<IIdentityStore>(provider => new IdentityStore(
            paths,
            provider.GetRequiredService<ISecretProtector>(),
            provider.GetRequiredService<ILogger<IdentityStore>>(),
            provider.GetRequiredService<IOptionsMonitor<AgentOptions>>()
                .CurrentValue.Security.CertificateLifetimeYears,
            subjectName: $"PC-Remote Agent on {Environment.MachineName}"));

        builder.Services.AddSingleton<JsonPairingStore>();
        builder.Services.AddSingleton<IPairingStore>(
            provider => provider.GetRequiredService<JsonPairingStore>());

        builder.Services.AddSingleton<ISessionTokenService>(provider => new SessionTokenService(
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<SessionTokenService>>(),
            provider.GetRequiredService<IOptionsMonitor<AgentOptions>>()
                .CurrentValue.Security.SessionTokenTtlSeconds));

        builder.Services.AddSingleton<AbuseLimiter>(provider =>
        {
            SecurityOptions security = provider.GetRequiredService<IOptionsMonitor<AgentOptions>>()
                .CurrentValue.Security;

            return new AbuseLimiter(
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<ILogger<AbuseLimiter>>(),
                security.MaxAuthFailuresPerAddress,
                security.AuthBlockMinutes);
        });

        builder.Services.AddSingleton<IPairingApprovalService, SessionAgentApprovalService>();

        builder.Services.AddSingleton<IPairingService>(provider => new PairingService(
            provider.GetRequiredService<IPairingStore>(),
            provider.GetRequiredService<IPairingApprovalService>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<PairingService>>(),
            provider.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Pairing));

        // --- IPC and session agent supervision ---

        builder.Services.AddSingleton<SessionAgentRegistry>();
        builder.Services.AddSingleton<AgentConfigurationWriter>();
        builder.Services.AddSingleton<AgentRequestRouter>();
        builder.Services.AddSingleton<ISessionBridge>(
            provider => provider.GetRequiredService<SessionAgentRegistry>());

        builder.Services.AddSingleton<SessionAgentSupervisor>(provider => new SessionAgentSupervisor(
            provider.GetRequiredService<SessionAgentLauncher>(),
            provider.GetRequiredService<SessionAgentRegistry>(),
            provider.GetRequiredService<ISessionStateProvider>(),
            provider.GetRequiredService<ILogger<SessionAgentSupervisor>>(),
            ResolveAgentExecutablePath(
                provider.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Startup)));

        // --- Command pipeline ---

        builder.Services.AddSingleton<IAuthorizationGate, AuthorizationGate>();
        builder.Services.AddSingleton<ICapabilityProvider, ServiceCapabilityProvider>();

        RegisterHandlers(builder.Services);

        builder.Services.AddSingleton(provider => new CommandRegistry(
            AgentRole.Service,
            provider.GetServices<ICommandHandler>()));

        builder.Services.AddSingleton<ICommandDispatcher>(provider => new CommandDispatcher(
            provider.GetRequiredService<CommandRegistry>(),
            provider.GetRequiredService<IAuthorizationGate>(),
            provider.GetRequiredService<ILogger<CommandDispatcher>>(),
            provider.GetRequiredService<ISessionBridge>()));

        // --- Network ---

        // Registered before the listener and the handlers that read it: a plain shared holder,
        // which is what keeps the command layer from depending on the transport (see the type's
        // remarks for the dependency cycle this avoids).
        builder.Services.AddSingleton<ListenerEndpointStatus>();
        builder.Services.AddSingleton<ClientSessionManager>();
        builder.Services.AddSingleton<ControlChannelListener>();
        builder.Services.AddSingleton<UdpDiscoveryResponder>();

        builder.Services.AddHostedService<AgentHostedService>();

        return builder.Build();
    }

    /// <summary>
    /// Registers every command handler this host implements.
    /// </summary>
    /// <remarks>
    /// Explicit rather than assembly-scanned. Scanning would make it possible to add a remotely
    /// reachable command by dropping in a type, which is exactly the kind of accident an
    /// allowlist design should prevent: adding a command should require a deliberate edit in two
    /// places — the catalog and here (§13 rule 5).
    /// </remarks>
    private static void RegisterHandlers(IServiceCollection services)
    {
        services.AddSingleton<ICommandHandler, PingHandler>();
        services.AddSingleton<ICommandHandler, HelloHandler>();
        services.AddSingleton<ICommandHandler, SessionRenewHandler>();
        services.AddSingleton<ICommandHandler, PermissionsListHandler>();
        services.AddSingleton<ICommandHandler, PairRequestHandler>();

        services.AddSingleton<ICommandHandler, DeviceInfoHandler>();
        services.AddSingleton<ICommandHandler, SystemStateHandler>();
        services.AddSingleton<ICommandHandler, WolInfoHandler>();

        services.AddSingleton<ICommandHandler, SystemLockHandler>();
        services.AddSingleton<ICommandHandler, SystemSignOutHandler>();
        services.AddSingleton<ICommandHandler, SystemSleepHandler>();
        services.AddSingleton<ICommandHandler, SystemRestartHandler>();
        services.AddSingleton<ICommandHandler, SystemShutdownHandler>();
        services.AddSingleton<ICommandHandler, SystemAbortShutdownHandler>();
    }

    /// <summary>
    /// Locates the session agent executable next to this one.
    /// </summary>
    /// <remarks>
    /// Derived from this process's own directory rather than configured, so the two halves always
    /// match and there is no path to get wrong on a different machine (§0).
    /// </remarks>
    private static string ResolveAgentExecutablePath(StartupOptions startup)
    {
        if (!string.IsNullOrWhiteSpace(startup.SessionAgentPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(startup.SessionAgentPath.Trim()));
        }

        return Path.Combine(AppContext.BaseDirectory, "RemoteAgent.Session.exe");
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            {ServiceDisplayName} {AgentVersion.Current}

            Usage:
              RemoteAgent.Service.exe                 Run as a Windows service (or console if not installed)
              RemoteAgent.Service.exe --console       Run in the foreground, logging to the console
              RemoteAgent.Service.exe --install       Register the Windows service (requires elevation)
              RemoteAgent.Service.exe --uninstall     Remove the Windows service (requires elevation)
              RemoteAgent.Service.exe --help          Show this message

            Data directory:
              {AgentPaths.DataDirectoryVariable}       Overrides where configuration, keys and logs live
              {AgentPaths.PortableMarkerFileName}      Place beside the executable for portable mode
            """);
    }
}
