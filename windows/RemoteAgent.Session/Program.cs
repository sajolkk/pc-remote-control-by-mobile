using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Session.Handlers;
using RemoteAgent.Session.Hosting;
using RemoteAgent.Session.Ui;
using RemoteAgent.Streaming;
using RemoteAgent.Streaming.Adaptation;
using RemoteAgent.Windows.Display;
using RemoteAgent.Windows.Media;
using RemoteAgent.Windows.Shell;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RemoteAgent.Diagnostics;
using Serilog;

namespace RemoteAgent.Session;

/// <summary>
/// Entry point for the user-session agent.
/// </summary>
/// <remarks>
/// <para>The agent is started by the service, never by the user, and it refuses to run without
/// the one-time spawn token the service places in its environment. That refusal is the point: the
/// IPC pipe has to be openable by interactive users (the agent is one), so the token is what
/// distinguishes the real agent from any other process the user could run. A copy launched by
/// hand, such as from the Start-menu shortcut, has no token and never becomes an agent: it only
/// asks the running agent to show its pairing window, then exits (<see cref="LauncherMode"/>).</para>
///
/// <para>It runs at the user's own integrity level and never asks for elevation. The visible
/// consequence — that injected input cannot reach elevated windows — is documented in the
/// manifest rather than worked around (§12.1).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>Process entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        AgentPaths paths = AgentPaths.Resolve();

        // The agent shares the service's data directory but writes its own log file, so the two
        // halves can be read separately when diagnosing which one misbehaved.
        Directory.CreateDirectory(paths.LogsDirectory);

        // Same logging setup and same redaction rules as the service, from the shared project:
        // a separate file so the two halves can be read independently, identical rules so neither
        // can start logging something §7.5 forbids.
        Log.Logger = AgentLogging.Create(
            paths,
            minimumLevel: "Information",
            retainDays: 14,
            toConsole: false,
            componentName: "session");

        try
        {
            (string Token, int SessionId)? spawn = ServiceChannelClient.ReadSpawnEnvironment();

            if (spawn is null)
            {
                // Launched by hand, normally from the Start-menu shortcut. Never becomes an agent:
                // it asks the real one to show itself and exits.
                return LauncherMode.Run(args);
            }

            string userName = ResolveUserName();

            Log.Information(
                "Session agent starting for session {SessionId} as {User}.",
                spawn.Value.SessionId,
                userName);

            IHost host = BuildHost(args, spawn.Value.Token, spawn.Value.SessionId, userName);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The session agent terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static IHost BuildHost(string[] args, string token, int sessionId, string userName)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();

        builder.Services.AddSingleton<IClock, SystemClock>();

        // --- Desktop services. All of these need the interactive session to work at all. ---

        builder.Services.AddSingleton<WindowInspector>();

        builder.Services.AddSingleton<WindowsAppCatalog>();
        builder.Services.AddSingleton<IAppCatalog>(
            provider => provider.GetRequiredService<WindowsAppCatalog>());

        builder.Services.AddSingleton<WindowsBrowserController>();
        builder.Services.AddSingleton<IBrowserController>(
            provider => provider.GetRequiredService<WindowsBrowserController>());

        builder.Services.AddSingleton<WindowsWorkstationLocker>();
        builder.Services.AddSingleton<IWorkstationLocker>(
            provider => provider.GetRequiredService<WindowsWorkstationLocker>());

        builder.Services.AddSingleton<UiThread>();

        // --- Configuration: streaming limits and the media port range. ---

        AddConfiguration(builder);

        // --- Screen streaming (Phase 3). Capture and encode are platform code; the pipeline and
        //     WebRTC transport are platform-neutral and live in RemoteAgent.Streaming. ---

        builder.Services.AddSingleton<IMonitorProvider, DxgiMonitorProvider>();
        builder.Services.AddSingleton<IScreenCaptureFactory, DesktopDuplicationCaptureFactory>();
        builder.Services.AddSingleton<IVideoEncoderFactory, MediaFoundationH264EncoderFactory>();
        builder.Services.AddSingleton<IImageEncoder, JpegImageEncoder>();

        builder.Services.AddSingleton(provider =>
        {
            IOptionsMonitor<AgentOptions> options = provider.GetRequiredService<IOptionsMonitor<AgentOptions>>();

            return new MediaSessionManager(
                () => BuildMediaOptions(options.CurrentValue),
                () => options.CurrentValue.Streaming.MaxConcurrentStreams,
                () => options.CurrentValue.Streaming.DefaultMonitorId,
                provider.GetRequiredService<IMonitorProvider>(),
                provider.GetRequiredService<IScreenCaptureFactory>(),
                provider.GetRequiredService<IVideoEncoderFactory>(),
                provider.GetRequiredService<ILogger<MediaSessionManager>>(),
                provider.GetRequiredService<ILogger<MediaSession>>());
        });

        // --- Command pipeline. Same gate, same catalog, same dispatcher as the service. ---

        builder.Services.AddSingleton<IAuthorizationGate, AuthorizationGate>();

        RegisterHandlers(builder.Services);

        builder.Services.AddSingleton(provider => new CommandRegistry(
            AgentRole.Session,
            provider.GetServices<ICommandHandler>()));

        builder.Services.AddSingleton<ICommandDispatcher>(provider => new CommandDispatcher(
            provider.GetRequiredService<CommandRegistry>(),
            provider.GetRequiredService<IAuthorizationGate>(),
            provider.GetRequiredService<ILogger<CommandDispatcher>>(),

            // No session bridge: this host IS the session. A session command with no handler here
            // is genuinely unsupported rather than something to forward onward.
            sessionBridge: null));

        // --- IPC back to the service ---

        builder.Services.AddSingleton(provider => new ServiceChannelClient(
            provider.GetRequiredService<ILogger<ServiceChannelClient>>(),
            token,
            sessionId,
            userName,
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"));

        builder.Services.AddHostedService<SessionAgentHostedService>();

        IHost host = builder.Build();

        // Route the WebRTC stack's own diagnostics (ICE checks, DTLS) into the agent's log, so a
        // stream that fails to connect on some network leaves something to read afterwards.
        SIPSorcery.LogFactory.Set(host.Services.GetRequiredService<ILoggerFactory>());

        return host;
    }

    /// <summary>
    /// Registers the commands this host implements.
    /// </summary>
    /// <remarks>
    /// Explicit, like the service's list, so that a remotely reachable command cannot appear merely
    /// because a type was added to the assembly (§13 rule 5).
    /// </remarks>
    private static void RegisterHandlers(IServiceCollection services)
    {
        services.AddSingleton<ICommandHandler, AppListHandler>();
        services.AddSingleton<ICommandHandler, AppLaunchHandler>();
        services.AddSingleton<ICommandHandler, AppFocusHandler>();
        services.AddSingleton<ICommandHandler, AppCloseHandler>();

        services.AddSingleton<ICommandHandler, BrowserListHandler>();
        services.AddSingleton<ICommandHandler, BrowserOpenHandler>();
        services.AddSingleton<ICommandHandler, BrowserOpenUrlHandler>();
        services.AddSingleton<ICommandHandler, BrowserCloseHandler>();

        services.AddSingleton<ICommandHandler, SessionLockHandler>();

        services.AddSingleton<ICommandHandler, ScreenMonitorListHandler>();
        services.AddSingleton<ICommandHandler, ScreenSelectMonitorHandler>();
        services.AddSingleton<ICommandHandler, ScreenScreenshotHandler>();
        services.AddSingleton<ICommandHandler, MediaOfferHandler>();
        services.AddSingleton<ICommandHandler, MediaIceHandler>();
        services.AddSingleton<ICommandHandler, MediaStopHandler>();
        services.AddSingleton<ICommandHandler, MediaSetQualityHandler>();
    }

    /// <summary>
    /// Reads the same <c>config.json</c> as the service, when this user is allowed to.
    /// </summary>
    /// <remarks>
    /// Installed as a service, the data directory belongs to SYSTEM and Administrators, and an
    /// ordinary signed-in user may not be able to read the file. That is not an error: every setting
    /// the agent uses has a safe default, so an unreadable file means "defaults", never a crash.
    /// </remarks>
    private static void AddConfiguration(HostApplicationBuilder builder)
    {
        AgentPaths paths = AgentPaths.Resolve();

        foreach (string file in new[] { paths.ConfigFile, paths.LocalConfigFile })
        {
            try
            {
                using (File.OpenRead(file))
                {
                }

                builder.Configuration.AddJsonFile(file, optional: true, reloadOnChange: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Absent or unreadable: defaults apply.
            }
        }

        builder.Services
            .AddOptions<AgentOptions>()
            .Bind(builder.Configuration.GetSection(AgentOptions.SectionName));

        // Not re-validated here: the service validates this same file at startup and refuses to
        // run with an invalid one, and every consumer below clamps what it reads anyway.
    }

    private static MediaSessionOptions BuildMediaOptions(AgentOptions options)
    {
        StreamingOptions streaming = options.Streaming;

        return new MediaSessionOptions
        {
            Limits = new StreamingLimits(
                streaming.MaxWidth,
                streaming.MaxHeight,
                streaming.FpsLimit,
                streaming.MinFps,
                Math.Min(streaming.MinBitrateKbps, streaming.MaxBitrateKbps),
                streaming.MaxBitrateKbps),
            CaptureCursor = streaming.CaptureCursor,
            PreferHardwareEncode = streaming.PreferHardwareEncode,
            PortRangeStart = Math.Min(options.Network.MediaPortRangeStart, options.Network.MediaPortRangeEnd),
            PortRangeEnd = Math.Max(options.Network.MediaPortRangeStart, options.Network.MediaPortRangeEnd),
        };
    }

    private static string ResolveUserName()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Environment.UserName;
        }
    }
}
