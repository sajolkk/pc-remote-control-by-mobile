using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Input;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Session.Ui;
using RemoteAgent.Streaming;

namespace RemoteAgent.Session.Hosting;

/// <summary>
/// The session agent's main loop: serve the service, watch the desktop, show prompts.
/// </summary>
/// <remarks>
/// <para><b>Lock detection lives here, and only here.</b> Windows exposes lock and unlock as an
/// event delivered to processes <em>in the session</em>, never as state a service can query. So
/// the agent observes it and pushes it to the service, which is what lets the phone be told
/// "the PC is locked" rather than being shown a frozen frame (§12.1).</para>
///
/// <para><b>Capabilities are reported from what is actually implemented</b>, not from what the
/// protocol declares. This build advertises applications, browser control, screen streaming,
/// mouse and keyboard input, window management and file management (each only when probing shows
/// it works here); clipboard and volume are absent until their phase lands, so a client hides
/// those controls instead of offering something that returns "not supported" (§0).</para>
///
/// <para><b>Held input is released from here.</b> This class hears about the three events that
/// must let go of whatever a phone was holding down — its control connection closing, the session
/// locking, and the service itself going away — and passes each to the input controller. The
/// phone asking is the fourth, and the least important: the cases that matter are the ones where
/// it can no longer ask.</para>
///
/// <para><b>The desktop agent does less.</b> Running as SYSTEM for screen and input only (see
/// <see cref="SessionAgentKind"/>), it shows no tray icon or windows, reports only screen and input
/// capabilities, and does not refuse input while the PC is locked — the lock screen is something it
/// can show and type into. The user agent, in turn, reports no screen or input capabilities and
/// leaves display changes to the desktop agent.</para>
///
/// <para><b>Permissions are re-checked here</b> even though the service already authorized the
/// call. The service is trusted, but a second check costs nothing and means a routing bug in
/// another process cannot become an unauthorized action on this desktop.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SessionAgentHostedService : BackgroundService
{
    private readonly ServiceChannelClient _channel;
    private readonly ICommandDispatcher _dispatcher;
    private readonly CommandRegistry _registry;
    private readonly IAppCatalog _catalog;
    private readonly IBrowserController _browsers;
    private readonly UiThread _ui;
    private readonly IClock _clock;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SessionAgentHostedService> _logger;
    private readonly ILogger<TrayIcon> _trayLogger;
    private readonly MediaSessionManager _media;
    private readonly IMonitorProvider _monitors;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly IVideoEncoderFactory _encoders;
    private readonly InputController _input;
    private readonly IInputInjector _injector;
    private readonly IWindowManager _windows;
    private readonly ServiceFilePolicy _filePolicy;

    private bool _sessionSwitchHooked;
    private SessionAgentKind Kind => _channel.Kind;
    private bool DoesScreenAndInput => Kind != SessionAgentKind.User;
    private bool DoesUserWork => Kind != SessionAgentKind.Desktop;
    private TrayIcon? _tray;
    private PairingQrWindow? _qrWindow;
    private DevicesWindow? _devicesWindow;

    /// <summary>Creates the host service.</summary>
    public SessionAgentHostedService(
        ServiceChannelClient channel,
        ICommandDispatcher dispatcher,
        CommandRegistry registry,
        IAppCatalog catalog,
        IBrowserController browsers,
        UiThread ui,
        IClock clock,
        IHostApplicationLifetime lifetime,
        ILogger<SessionAgentHostedService> logger,
        ILogger<TrayIcon> trayLogger,
        MediaSessionManager media,
        IMonitorProvider monitors,
        IScreenCaptureFactory captureFactory,
        IVideoEncoderFactory encoders,
        InputController input,
        IInputInjector injector,
        IWindowManager windows,
        ServiceFilePolicy filePolicy)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _filePolicy = filePolicy ?? throw new ArgumentNullException(nameof(filePolicy));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _trayLogger = trayLogger ?? throw new ArgumentNullException(nameof(trayLogger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _channel.RequestHandler = HandleServiceRequestAsync;
            _channel.ImplementedCommands = _registry.ImplementedCommands.ToArray();
            _channel.Capabilities = await ProbeCapabilitiesAsync(stoppingToken).ConfigureAwait(false);

            // Telemetry goes to the one connection that owns the stream, never to everyone.
            _media.Telemetry += OnMediaTelemetry;
            _channel.Disconnected += OnServiceDisconnected;

            HookSessionSwitch();

            // The tray icon, the pairing windows and file access belong to the signed-in user. The
            // desktop agent runs as SYSTEM and must never put a window of its own in front of them.
            if (DoesUserWork)
            {
                await CreateTrayAsync(stoppingToken).ConfigureAwait(false);
                _ = ListenForShowRequestsAsync(stoppingToken);

                // Keeps the allowed file roots in step with the service's configuration. It waits
                // for the channel by itself, so it can start before the connection exists.
                _ = _filePolicy.RunAsync(stoppingToken);
            }

            _logger.LogInformation(
                "Session agent starting. Kind={Kind} Commands={CommandCount} Capabilities={Capabilities}",
                Kind,
                _channel.ImplementedCommands.Count,
                string.Join(",", _channel.Capabilities));

            // Runs until the token is rejected (meaning this agent is stale and the service should
            // start a fresh one) or the host shuts down.
            bool keepRunning = await _channel.RunAsync(stoppingToken).ConfigureAwait(false);

            if (!keepRunning && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Exiting so the service can start a replacement agent.");
                _lifetime.StopApplication();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The session agent failed.");
            throw;
        }
        finally
        {
            // Whatever a phone was holding must not outlive the process that was holding it.
            _input.ReleaseEverything("the session agent is stopping");

            _media.Telemetry -= OnMediaTelemetry;
            _channel.Disconnected -= OnServiceDisconnected;
            UnhookSessionSwitch();
            await DisposeTrayAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the tray icon on the UI thread and wires its menu.
    /// </summary>
    /// <remarks>
    /// The tray icon is the user's only direct control over the agent, and "Allow pairing" is the
    /// action that makes the whole system usable: without it there is no way to produce a QR code,
    /// so a fresh installation could never be paired at all.
    /// </remarks>
    private async Task CreateTrayAsync(CancellationToken cancellationToken)
    {
        TrayIcon? tray = await _ui.InvokeAsync<TrayIcon?>(
            () => new TrayIcon(_trayLogger),
            fallback: null,
            cancellationToken).ConfigureAwait(false);

        if (tray is null)
        {
            _logger.LogWarning(
                "The tray icon could not be created, so pairing cannot be started from this session.");
            return;
        }

        tray.PairingRequested += (_, _) => _ = OpenPairingAsync();
        tray.PairingCancelled += (_, _) => _ = ClosePairingAsync();
        tray.DevicesRequested += (_, _) => ShowDevices();

        _tray = tray;
        await UpdateTrayAsync(pairingOpen: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers the Start-menu shortcut: opens pairing, or brings its window forward if already open.
    /// </summary>
    /// <remarks>
    /// Waits on its own thread-pool thread, because the wait blocks. It ends with the agent.
    /// </remarks>
    private Task ListenForShowRequestsAsync(CancellationToken stoppingToken) => Task.Run(
        async () =>
        {
            EventWaitHandle signal;

            try
            {
                signal = ShowRequestSignal.CreateListener();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                _logger.LogWarning(ex, "The Start-menu shortcut will not be able to reach this agent.");
                return;
            }

            using EventWaitHandle owned = signal;
            WaitHandle[] handles = [signal, stoppingToken.WaitHandle];

            while (WaitHandle.WaitAny(handles) == 0)
            {
                _logger.LogInformation("PC-Remote was opened from the Start menu.");

                bool alreadyOpen = await _ui.InvokeAsync(
                    () =>
                    {
                        if (_qrWindow is null)
                        {
                            return false;
                        }

                        _qrWindow.Activate();
                        return true;
                    },
                    fallback: false,
                    CancellationToken.None).ConfigureAwait(false);

                if (!alreadyOpen)
                {
                    await OpenPairingAsync().ConfigureAwait(false);
                }
            }
        },
        CancellationToken.None);

    private async Task UpdateTrayAsync(bool pairingOpen)
    {
        TrayIcon? tray = _tray;
        if (tray is null)
        {
            return;
        }

        string status = !_channel.IsConnected
            ? "Service not connected"
            : pairingOpen
                ? "Pairing open"
                : "Ready";

        await _ui.InvokeAsync(
            () =>
            {
                tray.UpdateStatus(status, pairingOpen);
                return true;
            },
            fallback: false,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the service to open pairing, then shows the QR code it returns.
    /// </summary>
    /// <remarks>
    /// The QR payload is never logged: it carries a live single-use token, which section 7.5
    /// excludes from logs.
    /// </remarks>
    private async Task OpenPairingAsync()
    {
        try
        {
            IpcResponse response = await _channel.RequestAsync(
                IpcCommands.OpenPairing,
                null,
                TimeSpan.FromSeconds(15),
                CancellationToken.None).ConfigureAwait(false);

            if (!response.Ok || response.Data is null)
            {
                _logger.LogWarning("Could not open pairing: {Code}", response.Error?.Code);

                await NotifyAsync(
                    "Could not start pairing",
                    response.Error?.Message ?? "The PC-Remote service did not respond.",
                    isWarning: true).ConfigureAwait(false);

                return;
            }

            JsonNode data = response.Data;

            string qr = data["qr"]?.GetValue<string>() ?? string.Empty;
            string deviceName = data["deviceName"]?.GetValue<string>() ?? Environment.MachineName;
            string fingerprint = data["fingerprintShort"]?.GetValue<string>() ?? string.Empty;
            int port = data["port"]?.GetValue<int>() ?? 0;

            DateTimeOffset expiresAt = DateTimeOffset.TryParse(
                data["expiresAtUtc"]?.GetValue<string>(),
                out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.UtcNow.AddMinutes(5);

            var addresses = new List<string>();
            if (data["addresses"] is JsonArray array)
            {
                foreach (JsonNode? entry in array)
                {
                    string? address = entry?.GetValue<string>();
                    if (!string.IsNullOrEmpty(address))
                    {
                        addresses.Add(address);
                    }
                }
            }

            if (qr.Length == 0)
            {
                await NotifyAsync(
                    "Could not start pairing",
                    "The service returned no pairing code.",
                    isWarning: true).ConfigureAwait(false);

                return;
            }

            await _ui.InvokeAsync(
                () =>
                {
                    // Replace any window already open, so two live codes are never on screen.
                    _qrWindow?.Close();

                    PairingQrWindow window = PairingQrWindow.ShowFor(
                        qr,
                        deviceName,
                        fingerprint,
                        addresses,
                        port,
                        expiresAt);

                    window.Dismissed += (_, _) => _ = ClosePairingAsync();
                    _qrWindow = window;
                    return true;
                },
                fallback: false,
                CancellationToken.None).ConfigureAwait(false);

            await UpdateTrayAsync(pairingOpen: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening pairing failed.");
        }
    }

    private async Task ClosePairingAsync()
    {
        try
        {
            await _ui.InvokeAsync(
                () =>
                {
                    _qrWindow?.Close();
                    _qrWindow = null;
                    return true;
                },
                fallback: false,
                CancellationToken.None).ConfigureAwait(false);

            await _channel.RequestAsync(
                IpcCommands.ClosePairing,
                null,
                TimeSpan.FromSeconds(10),
                CancellationToken.None).ConfigureAwait(false);

            await UpdateTrayAsync(pairingOpen: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing pairing failed.");
        }
    }

    /// <summary>
    /// Opens the Devices window, or brings it forward. Runs on the UI thread (a tray menu click).
    /// </summary>
    private void ShowDevices()
    {
        try
        {
            if (_devicesWindow is not null)
            {
                _devicesWindow.Activate();
                return;
            }

            DevicesWindow window = DevicesWindow.ShowFor(new DevicesWindowActions(
                LoadDevicesAsync,
                (deviceId, blocked) => SendDeviceCommandAsync(
                    IpcCommands.SetDeviceBlocked,
                    new JsonObject { ["deviceId"] = deviceId, ["blocked"] = blocked }),
                (deviceId, permissions) => SendDeviceCommandAsync(
                    IpcCommands.SetDevicePermissions,
                    new JsonObject
                    {
                        ["deviceId"] = deviceId,
                        ["permissions"] = new JsonArray([.. permissions.Select(name => JsonValue.Create(name))]),
                    }),
                deviceId => SendDeviceCommandAsync(
                    IpcCommands.RemoveDevice,
                    new JsonObject { ["deviceId"] = deviceId })));

            window.Closed += (_, _) => _devicesWindow = null;
            _devicesWindow = window;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening the Devices window failed.");
        }
    }

    private async Task<(IReadOnlyList<DeviceRow>? Devices, string? Error)> LoadDevicesAsync()
    {
        IpcResponse response = await _channel.RequestAsync(
            IpcCommands.ListDevices,
            null,
            TimeSpan.FromSeconds(10),
            CancellationToken.None).ConfigureAwait(false);

        if (!response.Ok || response.Data?["devices"] is not JsonArray array)
        {
            return (null, response.Error?.Message ?? "The PC-Remote service did not respond.");
        }

        var rows = new List<DeviceRow>();
        foreach (JsonNode? node in array)
        {
            if (node?["deviceId"]?.GetValue<string>() is not { Length: > 0 } deviceId)
            {
                continue;
            }

            rows.Add(new DeviceRow(
                deviceId,
                node["deviceName"]?.GetValue<string>() ?? "Mobile device",
                node["model"]?.GetValue<string>() ?? string.Empty,
                node["connected"]?.GetValue<bool>() ?? false,
                node["blocked"]?.GetValue<bool>() ?? false,
                node["address"]?.GetValue<string>(),
                DateTimeOffset.TryParse(node["lastSeenAt"]?.GetValue<string>(), out DateTimeOffset seen) ? seen : null,
                ReadPermissions(node["permissions"])));
        }

        return (rows, null);
    }

    /// <summary>
    /// Reads a device's permission names. An older service that does not report them yields null,
    /// which the window shows as "unknown" rather than as "none".
    /// </summary>
    private static IReadOnlyList<string>? ReadPermissions(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var names = new List<string>(array.Count);
        foreach (JsonNode? entry in array)
        {
            if (entry is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private async Task<string?> SendDeviceCommandAsync(string command, JsonObject args)
    {
        IpcResponse response = await _channel.RequestAsync(
            command,
            args,
            TimeSpan.FromSeconds(10),
            CancellationToken.None).ConfigureAwait(false);

        if (!response.Ok)
        {
            _logger.LogWarning("{Command} failed: {Code}", command, response.Error?.Code);
            return response.Error?.Message ?? "The PC-Remote service did not respond.";
        }

        return null;
    }

    private async Task NotifyAsync(string title, string message, bool isWarning)
    {
        TrayIcon? tray = _tray;
        if (tray is null)
        {
            return;
        }

        await _ui.InvokeAsync(
            () =>
            {
                tray.Notify(title, message, isWarning);
                return true;
            },
            fallback: false,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DisposeTrayAsync()
    {
        TrayIcon? tray = _tray;
        _tray = null;

        if (tray is null)
        {
            return;
        }

        await _ui.InvokeAsync(
            () =>
            {
                _qrWindow?.Close();
                _qrWindow = null;
                _devicesWindow?.Close();
                _devicesWindow = null;
                tray.Dispose();
                return true;
            },
            fallback: false,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles one request forwarded by the service.
    /// </summary>
    private async Task<IpcResponse> HandleServiceRequestAsync(
        IpcRequest request,
        CancellationToken cancellationToken)
    {
        // IPC control commands are handled directly. They are deliberately absent from the command
        // catalog, so nothing a phone sends can ever reach them.
        switch (request.Command)
        {
            case IpcCommands.Ping:
                return IpcResponse.Success(request.Id, JsonSerializer.SerializeToNode(new
                {
                    ts = _clock.UnixTimeMilliseconds,
                }));

            case IpcCommands.ApprovePairing:
                return await HandleApprovalRequestAsync(request, cancellationToken).ConfigureAwait(false);

            case IpcCommands.ConnectionChanged:
                return await HandleConnectionChangedAsync(request).ConfigureAwait(false);

            case IpcCommands.ReleaseInput:
                // The service's blanket "let go of everything". Always a success: afterwards no
                // connection is holding anything, which is all the caller asked for.
                _input.ReleaseEverything("the service asked");
                return IpcResponse.Success(request.Id);

            case IpcCommands.Shutdown:
                _logger.LogInformation("The service asked this agent to stop.");
                _lifetime.StopApplication();
                return IpcResponse.Success(request.Id);
        }

        // Everything else is a catalog command. It goes through the same dispatcher, gate and
        // catalog as the service side, so the authorization rules are identical in both processes.
        if (request.Caller is null)
        {
            return IpcResponse.Failure(
                request.Id,
                ErrorCodes.Unauthenticated,
                "A forwarded command must carry its caller.");
        }

        var caller = new CallerIdentity(
            request.Caller.DeviceId,
            request.Caller.DeviceName,
            request.Caller.ToPermissions(),
            CommandStage.Authenticated,
            request.Caller.ConnectionId,
            request.Caller.RemoteAddress);

        CommandResult result = await _dispatcher
            .DispatchAsync(request.Command, request.Args, request.Id, caller, cancellationToken)
            .ConfigureAwait(false);

        return result.Ok
            ? IpcResponse.Success(request.Id, result.Data)
            : new IpcResponse { Id = request.Id, Ok = false, Error = result.Error };
    }

    private async Task<IpcResponse> HandleApprovalRequestAsync(
        IpcRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Args is not { } args || args.ValueKind != JsonValueKind.Object)
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidArguments, "Missing approval details.");
        }

        string deviceName = ReadString(args, "deviceName");
        string platform = ReadString(args, "platform");
        string model = ReadString(args, "model");
        string address = ReadString(args, "remoteAddress");
        string fingerprint = ReadString(args, "fingerprint");
        string code = ReadString(args, "code");

        _logger.LogInformation(
            "Showing a pairing approval prompt for a device at {Address}.",
            address);

        // The fallback is a refusal: if the dialog cannot be shown for any reason, the answer is no.
        PairingDialogResult answer = await _ui.InvokeAsync(
            () => PairingApprovalDialog.Show(deviceName, platform, model, address, fingerprint, code),
            fallback: PairingDialogResult.Declined,
            cancellationToken).ConfigureAwait(false);

        // "fullControl" is the state of the dialog's one checkbox. It is sent only alongside an
        // approval, and the service ignores it without one.
        return IpcResponse.Success(
            request.Id,
            JsonSerializer.SerializeToNode(new
            {
                approved = answer.Approved,
                fullControl = answer.Approved && answer.FullControl,
            }));
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Reports what this agent can actually do in this session.
    /// </summary>
    /// <remarks>
    /// Probed, not assumed. Browser control is advertised only when a browser was found, and
    /// application control only when the catalog produced entries — a PC whose Start Menu is empty
    /// and whose package query is blocked genuinely cannot launch anything, and saying so beats
    /// offering a screen full of nothing (§0).
    /// </remarks>
    private async Task<IReadOnlyList<string>> ProbeCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = new List<string>();

        if (DoesScreenAndInput)
        {
            List<string> screen = ProbeScreenCapabilities().ToList();
            capabilities.AddRange(screen);
            capabilities.AddRange(ProbeInputCapabilities());

            // The desktop agent follows Windows onto the lock screen, the sign-in screen and UAC
            // prompts. Said out loud so the phone can offer to unlock instead of explaining why
            // it cannot.
            if (Kind == SessionAgentKind.Desktop && screen.Contains(CapabilityNames.ScreenCapture))
            {
                capabilities.Add(CapabilityNames.SecureDesktop);
            }
        }

        if (!DoesUserWork)
        {
            return capabilities;
        }

        try
        {
            AppListResult apps = await _catalog
                .ListAsync(new AppListArgs { Limit = 1 }, cancellationToken)
                .ConfigureAwait(false);

            if (apps.Total > 0)
            {
                capabilities.Add(CapabilityNames.Apps);
            }
            else
            {
                _logger.LogWarning("No applications were discovered, so app control is not advertised.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The application catalog could not be built; app control is not advertised.");
        }

        try
        {
            BrowserListResult browsers = await _browsers.ListAsync(cancellationToken).ConfigureAwait(false);
            if (browsers.Browsers.Length > 0)
            {
                capabilities.Add(CapabilityNames.Browser);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browsers could not be detected; browser control is not advertised.");
        }

        capabilities.AddRange(ProbeShellCapabilities());

        // Clipboard and volume are declared in the protocol but not implemented in this build, so
        // they are deliberately not advertised.
        return capabilities;
    }

    /// <summary>
    /// Reports mouse and keyboard input, if it works here.
    /// </summary>
    /// <remarks>
    /// The probe is read-only: nothing is injected to find out. Input is advertised when the
    /// desktop reports a non-empty virtual screen to inject into — the same query every absolute
    /// move depends on. A locked desktop still counts, for the reason a locked desktop still counts
    /// for capture: the capability describes the PC, and the locked state reaches the phone
    /// separately.
    /// </remarks>
    private IEnumerable<string> ProbeInputCapabilities()
    {
        var capabilities = new List<string>();

        try
        {
            PixelRect screen = _injector.GetVirtualScreen();
            if (screen.Width > 0 && screen.Height > 0)
            {
                capabilities.Add(CapabilityNames.InputMouse);
                capabilities.Add(CapabilityNames.InputKeyboard);
            }
            else
            {
                _logger.LogWarning("Windows reports no desktop to inject input into; remote input is not advertised.");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "Input injection is unavailable; remote input is not advertised.");
        }

        return capabilities;
    }

    /// <summary>
    /// Reports window management and file management, each only if it works here.
    /// </summary>
    /// <remarks>
    /// Read-only, like the input probe: nothing is moved or opened to find out. File management is
    /// advertised because it is implemented; the service removes it again if configuration leaves
    /// it no roots.
    /// </remarks>
    private IEnumerable<string> ProbeShellCapabilities()
    {
        var capabilities = new List<string>();

        try
        {
            // Enumerating succeeds with an empty list on a desktop with nothing open, which is
            // still a desktop whose windows can be managed.
            _ = _windows.List();
            capabilities.Add(CapabilityNames.Windows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows could not be enumerated; window management is not advertised.");
        }

        capabilities.Add(CapabilityNames.Files);
        return capabilities;
    }

    /// <summary>
    /// Reports screen streaming only if every link in the chain works on this PC: a monitor to
    /// capture, Desktop Duplication on it, and an H.264 encoder that actually starts.
    /// </summary>
    /// <remarks>
    /// A capture that fails <em>transiently</em> — because the agent started while the PC was
    /// locked — still counts. The capability describes the PC, not this moment; the locked state
    /// reaches the phone separately and it explains the blank view (§6.4).
    /// </remarks>
    private IEnumerable<string> ProbeScreenCapabilities()
    {
        var capabilities = new List<string>();

        try
        {
            IReadOnlyList<MonitorInfo> monitors = _monitors.GetMonitors();
            if (monitors.Count == 0)
            {
                _logger.LogWarning("No capturable monitor was found; screen streaming is not advertised.");
                return capabilities;
            }

            MonitorInfo primary = monitors.FirstOrDefault(static m => m.Primary) ?? monitors[0];
            try
            {
                using IScreenCapture capture = _captureFactory.Open(primary.Id);
            }
            catch (ScreenCaptureUnavailableException ex) when (ex.Transient)
            {
                _logger.LogInformation("Capture is unavailable right now ({Reason}); it will be retried per stream.", ex.Message);
            }

            IReadOnlyList<VideoEncoderDescriptor> encoders = _encoders.Probe();
            if (encoders.Count == 0)
            {
                _logger.LogWarning(
                    "No H.264 encoder is usable on this PC (a Windows N edition without the Media Feature " +
                    "Pack has none); screen streaming is not advertised.");
                return capabilities;
            }

            capabilities.Add(CapabilityNames.ScreenCapture);

            if (encoders.Any(static e => e.IsHardware))
            {
                capabilities.Add(CapabilityNames.HardwareEncodeH264);
            }

            if (encoders.Any(static e => !e.IsHardware))
            {
                capabilities.Add(CapabilityNames.SoftwareEncodeH264);
            }

            if (monitors.Count > 1)
            {
                capabilities.Add(CapabilityNames.MultiMonitor);
            }
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            _logger.LogWarning("Desktop Duplication does not work on this PC ({Reason}); screen streaming is not advertised.", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screen capture could not be probed; screen streaming is not advertised.");
        }

        return capabilities;
    }

    /// <summary>
    /// The service went away, taking every control connection with it. Streams authorized by those
    /// connections end now rather than running on without anyone able to stop them.
    /// </summary>
    private void OnServiceDisconnected(object? sender, EventArgs e)
    {
        // Input first: it is synchronous and it is the one that leaves the desktop unusable.
        _input.ReleaseEverything("the PC-Remote service connection was lost");
        _ = _media.EndAllAsync("the PC-Remote service connection was lost");
    }

    private void OnMediaTelemetry(string connectionId, MediaQualityEvent report)
    {
        _ = _channel.SendEventAsync(
            EventNames.MediaQuality,
            JsonSerializer.SerializeToNode(report, ProtocolJson.Options),
            connectionId,
            CancellationToken.None);
    }

    /// <summary>
    /// Ends a connection's stream when the connection closes or it loses <c>ViewScreen</c>, and
    /// releases its held keys and buttons when it closes or loses <c>ControlInput</c>.
    /// </summary>
    /// <remarks>
    /// This is the dead-man release for the case that needs it most: a phone that vanished
    /// mid-drag. The service notices the dead connection (by error or by keepalive timeout) and
    /// sends this, and it arrives on the same ordered pipe as the commands themselves, so it
    /// cannot overtake the button-down it is cleaning up after.
    /// </remarks>
    private async Task<IpcResponse> HandleConnectionChangedAsync(IpcRequest request)
    {
        IpcConnectionChangedArgs? change = null;
        if (request.Args is { ValueKind: JsonValueKind.Object } args)
        {
            change = args.Deserialize<IpcConnectionChangedArgs>(ProtocolJson.Options);
        }

        if (change is null || string.IsNullOrEmpty(change.ConnectionId))
        {
            return IpcResponse.Failure(request.Id, ErrorCodes.InvalidArguments, "Missing connection id.");
        }

        Permission granted = PermissionSet.FromNames(change.Permissions);

        if (change.Closed || !PermissionSet.Allows(granted, Permission.ControlInput))
        {
            _input.ReleaseConnection(
                change.ConnectionId,
                change.Closed ? "the control connection closed" : "input control was revoked at the PC");
        }

        bool mayView = !change.Closed && PermissionSet.Allows(granted, Permission.ViewScreen);

        if (!mayView)
        {
            await _media.EndConnectionAsync(
                change.ConnectionId,
                change.Closed ? "the control connection closed" : "screen viewing was revoked at the PC")
                .ConfigureAwait(false);
        }

        return IpcResponse.Success(request.Id);
    }

    /// <summary>
    /// Subscribes to session lock and unlock notifications.
    /// </summary>
    /// <remarks>
    /// <c>SystemEvents</c> requires a message pump, which the UI thread provides. Without the hook
    /// the service would report a locked PC as "active" and the phone would show a stale frame
    /// with no explanation.
    /// </remarks>
    private void HookSessionSwitch()
    {
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;

            // Display changes matter to whoever captures and injects; with two agents only one of
            // them announces each change.
            if (DoesScreenAndInput)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }

            _sessionSwitchHooked = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            _logger.LogWarning(
                ex,
                "Could not subscribe to session lock notifications. The PC's locked state may be " +
                "reported inaccurately.");
        }
    }

    private void UnhookSessionSwitch()
    {
        if (!_sessionSwitchHooked)
        {
            return;
        }

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // Shutting down; nothing useful to do.
        }

        _sessionSwitchHooked = false;
    }

    /// <summary>
    /// Announces a monitor hot-plug or mode change (§6.4).
    /// </summary>
    /// <remarks>
    /// The event is broadcast but carries only a count. The monitor details stay behind
    /// <c>screen.monitor_list</c>, which requires <c>ViewScreen</c>, so a device without that
    /// permission learns that something changed and nothing more. A live stream on an unplugged
    /// monitor recovers by itself: its capture is lost and reopening it reports the monitor gone.
    /// </remarks>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        int count;
        try
        {
            count = _monitors.GetMonitors().Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monitors could not be re-enumerated after a display change.");
            return;
        }

        _logger.LogInformation("Display configuration changed; {Count} monitor(s).", count);

        // A point on "the second monitor" now means somewhere else. Drop the cached layout.
        _input.InvalidateMonitors();

        _ = _channel.SendEventAsync(
            EventNames.MonitorsChanged,
            JsonSerializer.SerializeToNode(new { count }),
            CancellationToken.None);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Leaving the desktop by any route releases held input before anything else is done about
        // it. A lock additionally refuses further input until the matching unlock — except in the
        // desktop agent, which follows input onto the lock screen so the phone can unlock the PC.
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock when Kind == SessionAgentKind.Desktop:
                _input.ReleaseEverything("the session locked");
                break;

            case SessionSwitchReason.SessionLock:
                _input.OnSessionLocked();
                break;

            case SessionSwitchReason.SessionUnlock when Kind == SessionAgentKind.Desktop:
                break;

            case SessionSwitchReason.SessionUnlock:
                _input.OnSessionUnlocked();
                break;

            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.SessionLogoff:
                _input.ReleaseEverything("the session left the console");
                break;
        }

        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,

            // Remote connect/disconnect and console switches change which session is interactive,
            // which the service tracks itself. Only lock state is ours to report.
            _ => null,
        };

        // Lock state is reported once per session, by the agent that runs as the user. The desktop
        // agent may be the only one (nobody signed in), and then there is no lock to report.
        if (locked is null || !DoesUserWork)
        {
            return;
        }

        _logger.LogInformation("Desktop {State}.", locked.Value ? "locked" : "unlocked");

        _ = _channel.SendEventAsync(
            EventNames.SessionStateChanged,
            JsonSerializer.SerializeToNode(new { locked = locked.Value }),
            CancellationToken.None);
    }
}
