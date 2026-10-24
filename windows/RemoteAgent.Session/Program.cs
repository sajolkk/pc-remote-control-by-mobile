using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Core.Files;
using RemoteAgent.Core.Input;
using RemoteAgent.Ipc;
using RemoteAgent.Session.Handlers;
using RemoteAgent.Session.Hosting;
using RemoteAgent.Session.Ui;
using RemoteAgent.Streaming;
using RemoteAgent.Streaming.Adaptation;
using RemoteAgent.Windows.Display;
using RemoteAgent.Windows.Files;
using RemoteAgent.Windows.Input;
using RemoteAgent.Windows.Media;
using RemoteAgent.Windows.Session;
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
/// <para>Installed, the service starts this executable twice per session (see
/// <see cref="SessionAgentKind"/>). The <em>user</em> agent runs at the user's own integrity level,
/// never asks for elevation, and does applications, files, windows, the browser, the tray icon and
/// pairing prompts. The <em>desktop</em> agent runs as SYSTEM and does screen and input only, which
/// is what lets the lock screen, the sign-in screen and UAC prompts be seen and used from the phone
/// (§12.1). In portable mode one <em>full</em> agent does everything at the user's rights, and the
/// secure desktop stays out of reach there.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Program
{
    /// <summary>Process entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        AgentPaths paths = AgentPaths.Resolve();
        SessionAgentKind kind = ServiceChannelClient.ReadAgentKind();

        // Same logging setup and same redaction rules as the service, from the shared project:
        // a separate file so the two halves can be read independently, identical rules so neither
        // can start logging something §7.5 forbids.
        Log.Logger = AgentLogging.Create(
            paths,
            minimumLevel: "Information",
            retainDays: 14,
            toConsole: false,
            componentName: kind == SessionAgentKind.Desktop ? "desktop" : "session",
            logsDirectory: ResolveLogsDirectory(paths));

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
                "Session agent ({Kind}) starting for session {SessionId} as {User}.",
                kind,
                spawn.Value.SessionId,
                userName);

            IHost host = BuildHost(args, spawn.Value.Token, spawn.Value.SessionId, userName, kind);
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

    private static IHost BuildHost(string[] args, string token, int sessionId, string userName, SessionAgentKind kind)
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

        // Only the desktop agent runs as SYSTEM, and only it can follow input onto the lock screen.
        builder.Services.AddSingleton(provider => new InputDesktopSwitcher(
            kind == SessionAgentKind.Desktop,
            provider.GetRequiredService<ILogger<InputDesktopSwitcher>>()));

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

        // --- Remote input (Phase 4). The injector is the only platform piece; the controller that
        //     validates, maps coordinates and tracks held keys is platform-neutral. ---

        builder.Services.AddSingleton<IInputInjector, SendInputInjector>();

        builder.Services.AddSingleton(provider =>
        {
            MediaSessionManager media = provider.GetRequiredService<MediaSessionManager>();

            return new InputController(
                provider.GetRequiredService<IInputInjector>(),
                provider.GetRequiredService<IMonitorProvider>(),

                // A point with no monitor named refers to the monitor this connection is watching.
                media.GetSelectedMonitor,
                provider.GetRequiredService<ILogger<InputController>>());
        });

        // --- Window management. ---

        builder.Services.AddSingleton<IWindowManager, WindowsWindowManager>();

        // --- File management. The allowed roots come from the service (see ServiceFilePolicy);
        //     the built-in roots and the two shell operations come from the platform layer. ---

        builder.Services.AddSingleton<ServiceFilePolicy>();
        builder.Services.AddSingleton<IDefaultFileRoots, WindowsDefaultFileRoots>();
        builder.Services.AddSingleton<IFileShell, WindowsFileShell>();

        builder.Services.AddSingleton<IFileRootProvider>(provider =>
        {
            ServiceFilePolicy policy = provider.GetRequiredService<ServiceFilePolicy>();
            return new ConfiguredFileRoots(() => policy.Current, provider.GetRequiredService<IDefaultFileRoots>());
        });

        builder.Services.AddSingleton(provider =>
        {
            ServiceFilePolicy policy = provider.GetRequiredService<ServiceFilePolicy>();

            return new FileManager(
                provider.GetRequiredService<IFileRootProvider>(),
                provider.GetRequiredService<IFileShell>(),
                provider.GetRequiredService<ILogger<FileManager>>(),
                maxCopyBytes: () => (policy.Current?.MaxFileSizeMb ?? 2048) * 1024L * 1024L);
        });

        // --- Command pipeline. Same gate, same catalog, same dispatcher as the service. ---

        builder.Services.AddSingleton<IAuthorizationGate, AuthorizationGate>();

        RegisterHandlers(builder.Services, kind);

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
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            kind));

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
    /// <para>Explicit, like the service's list, so that a remotely reachable command cannot appear
    /// merely because a type was added to the assembly (§13 rule 5).</para>
    ///
    /// <para>Split by kind, and the two halves never overlap: the service routes each command to the
    /// one agent that implements it. Screen and input go to the desktop agent; everything else stays
    /// at the user's rights, because a file opened or an app launched by a SYSTEM process would run
    /// with rights the signed-in user does not have.</para>
    /// </remarks>
    private static void RegisterHandlers(IServiceCollection services, SessionAgentKind kind)
    {
        if (kind != SessionAgentKind.User)
        {
            RegisterDesktopHandlers(services);
        }

        if (kind != SessionAgentKind.Desktop)
        {
            RegisterUserHandlers(services);
        }
    }

    private static void RegisterDesktopHandlers(IServiceCollection services)
    {
        services.AddSingleton<ICommandHandler, ScreenMonitorListHandler>();
        services.AddSingleton<ICommandHandler, ScreenSelectMonitorHandler>();
        services.AddSingleton<ICommandHandler, ScreenScreenshotHandler>();
        services.AddSingleton<ICommandHandler, MediaOfferHandler>();
        services.AddSingleton<ICommandHandler, MediaIceHandler>();
        services.AddSingleton<ICommandHandler, MediaStopHandler>();
        services.AddSingleton<ICommandHandler, MediaSetQualityHandler>();

        services.AddSingleton<ICommandHandler, InputMouseMoveHandler>();
        services.AddSingleton<ICommandHandler, InputMouseButtonHandler>();
        services.AddSingleton<ICommandHandler, InputMouseClickHandler>();
        services.AddSingleton<ICommandHandler, InputScrollHandler>();
        services.AddSingleton<ICommandHandler, InputKeyHandler>();
        services.AddSingleton<ICommandHandler, InputTextHandler>();
        services.AddSingleton<ICommandHandler, InputShortcutHandler>();
        services.AddSingleton<ICommandHandler, InputReleaseAllHandler>();
    }

    private static void RegisterUserHandlers(IServiceCollection services)
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

        services.AddSingleton<ICommandHandler, WindowListHandler>();
        services.AddSingleton<ICommandHandler, WindowActionHandler>();

        // file.upload, file.download and file.cancel stay declared-but-unimplemented: a phone
        // that calls one is told this PC does not support it.
        services.AddSingleton<ICommandHandler, FileListHandler>();
        services.AddSingleton<ICommandHandler, FileOpenHandler>();
        services.AddSingleton<ICommandHandler, FileCreateHandler>();
        services.AddSingleton<ICommandHandler, FileRenameHandler>();
        services.AddSingleton<ICommandHandler, FileCopyHandler>();
        services.AddSingleton<ICommandHandler, FileMoveHandler>();
        services.AddSingleton<ICommandHandler, FileDeleteHandler>();
    }

    /// <summary>
    /// Where this process writes its log: the shared logs directory when this user may write there,
    /// otherwise <c>%LOCALAPPDATA%\PCRemote\logs</c>.
    /// </summary>
    /// <remarks>
    /// Installed as a service, the data directory is restricted to SYSTEM and Administrators, and the
    /// agent runs as the ordinary signed-in user. Failing to open the shared directory used to crash
    /// the agent before it could log anything, so the service restarted it in a loop.
    /// </remarks>
    private static string ResolveLogsDirectory(AgentPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            string probe = Path.Combine(paths.LogsDirectory, $".write-test-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return paths.LogsDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AgentPaths.ProductFolderName,
                "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
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
