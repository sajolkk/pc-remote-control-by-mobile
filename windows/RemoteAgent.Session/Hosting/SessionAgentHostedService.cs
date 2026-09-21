using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
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
/// protocol declares. This build advertises applications, browser control and screen streaming
/// (each only when probing shows it works here); input and clipboard are absent until their phases
/// land, so a client hides those controls instead of offering something that returns "not
/// supported" (§0).</para>
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

    private bool _sessionSwitchHooked;
    private TrayIcon? _tray;
    private PairingQrWindow? _qrWindow;

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
        IVideoEncoderFactory encoders)
    {
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
            await CreateTrayAsync(stoppingToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Session agent starting. Commands={CommandCount} Capabilities={Capabilities}",
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

        _tray = tray;
        await UpdateTrayAsync(pairingOpen: false).ConfigureAwait(false);
    }

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
                // Nothing to release until input injection lands in Phase 4. Answered as a success
                // because the postcondition — no keys held — is satisfied.
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

        _logger.LogInformation(
            "Showing a pairing approval prompt for a device at {Address}.",
            address);

        // The fallback is false: if the dialog cannot be shown for any reason, the answer is no.
        bool approved = await _ui.InvokeAsync(
            () => PairingApprovalDialog.Show(deviceName, platform, model, address, fingerprint),
            fallback: false,
            cancellationToken).ConfigureAwait(false);

        return IpcResponse.Success(
            request.Id,
            JsonSerializer.SerializeToNode(new { approved }));
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

        capabilities.AddRange(ProbeScreenCapabilities());

        // Input, clipboard, files and volume are declared in the protocol but not implemented in
        // this build, so they are deliberately not advertised.
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
    private void OnServiceDisconnected(object? sender, EventArgs e) =>
        _ = _media.EndAllAsync("the PC-Remote service connection was lost");

    private void OnMediaTelemetry(string connectionId, MediaQualityEvent report)
    {
        _ = _channel.SendEventAsync(
            EventNames.MediaQuality,
            JsonSerializer.SerializeToNode(report, ProtocolJson.Options),
            connectionId,
            CancellationToken.None);
    }

    /// <summary>
    /// Ends a connection's stream when the connection closes or it loses <c>ViewScreen</c>.
    /// </summary>
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

        bool mayView = !change.Closed &&
                       PermissionSet.Allows(PermissionSet.FromNames(change.Permissions), Permission.ViewScreen);

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
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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

        _ = _channel.SendEventAsync(
            EventNames.MonitorsChanged,
            JsonSerializer.SerializeToNode(new { count }),
            CancellationToken.None);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,

            // Remote connect/disconnect and console switches change which session is interactive,
            // which the service tracks itself. Only lock state is ours to report.
            _ => null,
        };

        if (locked is null)
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
