using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Streaming;
using RemoteAgent.Streaming.Video;

namespace RemoteAgent.Session.Handlers;

/// <summary>Lists the monitors this PC can stream.</summary>
public sealed class ScreenMonitorListHandler : ICommandHandler
{
    private readonly IMonitorProvider _monitors;
    private readonly MediaSessionManager _media;

    /// <summary>Creates the handler.</summary>
    public ScreenMonitorListHandler(IMonitorProvider monitors, MediaSessionManager media)
    {
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc />
    public string Command => CommandNames.ScreenMonitorList;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<MonitorInfo> monitors = _monitors.GetMonitors();

        return Task.FromResult(CommandResult.Success(new MonitorListResult
        {
            Monitors = [.. monitors],
            SelectedId = _media.GetSelectedMonitor(context.Caller.ConnectionId, monitors),
        }));
    }
}

/// <summary>Chooses which monitor this caller streams, switching a live stream immediately.</summary>
public sealed class ScreenSelectMonitorHandler : ICommandHandler
{
    private readonly MediaSessionManager _media;

    /// <summary>Creates the handler.</summary>
    public ScreenSelectMonitorHandler(MediaSessionManager media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc />
    public string Command => CommandNames.ScreenSelectMonitor;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out SelectMonitorArgs args, out CommandResult? failure))
        {
            return Task.FromResult(failure!);
        }

        return Task.FromResult(_media.SelectMonitor(context.Caller.ConnectionId, args.MonitorId) switch
        {
            MediaFailure.None => CommandResult.Success(new { monitorId = args.MonitorId }),
            MediaFailure.NotFound => CommandResult.NotFound("That monitor"),
            _ => CommandResult.InvalidArguments("A monitorId is required."),
        });
    }
}

/// <summary>
/// Captures one still image of a monitor.
/// </summary>
/// <remarks>
/// The image has to fit in a single control-channel frame (§6.2), so it is downscaled to the
/// requested width and then JPEG quality is lowered step by step until it does. A 1080p desktop
/// normally fits at the first attempt, in well under half the budget.
/// </remarks>
public sealed class ScreenScreenshotHandler : ICommandHandler
{
    /// <summary>
    /// Largest JPEG returned. Base64 inflates it by a third, which with the JSON envelope keeps the
    /// response comfortably under the 1 MiB frame limit.
    /// </summary>
    public const int MaxJpegBytes = 680 * 1024;

    private static readonly int[] Qualities = [80, 65, 50, 35];

    private readonly IMonitorProvider _monitors;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly IImageEncoder _imageEncoder;
    private readonly MediaSessionManager _media;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<ScreenScreenshotHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public ScreenScreenshotHandler(
        IMonitorProvider monitors,
        IScreenCaptureFactory captureFactory,
        IImageEncoder imageEncoder,
        MediaSessionManager media,
        IOptionsMonitor<AgentOptions> options,
        ILogger<ScreenScreenshotHandler> logger)
    {
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _imageEncoder = imageEncoder ?? throw new ArgumentNullException(nameof(imageEncoder));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Command => CommandNames.ScreenScreenshot;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out ScreenshotArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        IReadOnlyList<MonitorInfo> monitors = _monitors.GetMonitors();
        string? monitorId = args.MonitorId ?? _media.GetSelectedMonitor(context.Caller.ConnectionId, monitors);

        if (monitorId is null)
        {
            return CommandResult.NotSupported("screen capture: no monitor is available");
        }

        int maxWidth = Math.Clamp(args.MaxWidth ?? 1280, 320, _options.CurrentValue.Streaming.ScreenshotMaxWidth);

        // Capture blocks in the display driver; keep it off the IPC dispatch thread.
        return await Task.Run(() => Capture(monitorId, maxWidth, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private CommandResult Capture(string monitorId, int maxWidth, CancellationToken cancellationToken)
    {
        IScreenCapture capture;
        try
        {
            capture = _captureFactory.Open(monitorId);
        }
        catch (ScreenCaptureUnavailableException ex)
        {
            return ex.Transient
                ? CommandResult.Fail(ErrorCodes.SecureDesktopActive, ex.Message, retryable: true)
                : CommandResult.NotFound("That monitor");
        }

        using (capture)
        {
            // A new duplication delivers the whole desktop on its first frame, but may take a
            // moment to do so. A few attempts cover a slow driver without hanging the request.
            bool captured = false;
            for (int attempt = 0; attempt < 10 && !captured; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CaptureStatus status = capture.Acquire(100);
                if (status == CaptureStatus.Lost)
                {
                    return CommandResult.Fail(
                        ErrorCodes.SessionUnavailable,
                        "The desktop cannot be captured right now.",
                        retryable: true);
                }

                captured = capture.Frame.ContentVersion > 0;
            }

            if (!captured)
            {
                return CommandResult.Fail(ErrorCodes.Timeout, "The display did not produce a frame.", retryable: true);
            }

            CapturedFrame frame = capture.Frame;
            var cursor = new CursorCompositor();
            if (_options.CurrentValue.Streaming.CaptureCursor)
            {
                cursor.Draw(frame);
            }

            try
            {
                return Encode(capture.Monitor.Id, frame, maxWidth);
            }
            finally
            {
                cursor.Restore(frame);
            }
        }
    }

    private CommandResult Encode(string monitorId, CapturedFrame frame, int maxWidth)
    {
        int width = frame.Width;
        int height = frame.Height;
        byte[] pixels = frame.Pixels;
        int stride = frame.Stride;

        // Halve the width each time quality alone is not enough — a very detailed 4K desktop can
        // exceed the budget even at low quality.
        for (int targetWidth = Math.Min(maxWidth, frame.Width); targetWidth >= 320; targetWidth /= 2)
        {
            if (targetWidth < frame.Width)
            {
                width = targetWidth;
                height = Math.Max(1, (int)((long)frame.Height * targetWidth / frame.Width));
                pixels = BgraScaler.Downscale(frame.Pixels, frame.Width, frame.Height, frame.Stride, width, height);
                stride = width * 4;
            }

            foreach (int quality in Qualities)
            {
                byte[] jpeg = _imageEncoder.EncodeJpeg(pixels, width, height, stride, quality);
                if (jpeg.Length <= MaxJpegBytes)
                {
                    return CommandResult.Success(new ScreenshotResult
                    {
                        MonitorId = monitorId,
                        Width = width,
                        Height = height,
                        Data = Convert.ToBase64String(jpeg),
                    });
                }
            }
        }

        _logger.LogWarning("A screenshot of {Monitor} could not be made small enough to send.", monitorId);
        return CommandResult.Fail(ErrorCodes.Internal, "The screenshot is too large to send.");
    }
}

/// <summary>
/// Starts a screen stream from the phone's WebRTC offer (§1.4 step 6).
/// </summary>
public sealed class MediaOfferHandler : ICommandHandler
{
    private readonly MediaSessionManager _media;

    /// <summary>Creates the handler.</summary>
    public MediaOfferHandler(MediaSessionManager media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc />
    public string Command => CommandNames.MediaOffer;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out MediaOfferArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        (MediaAnswerResult? answer, MediaFailure error, string? detail) = await _media
            .StartAsync(context.Caller.ConnectionId, context.Caller.RemoteAddress, args, cancellationToken)
            .ConfigureAwait(false);

        return error switch
        {
            MediaFailure.None => CommandResult.Success(answer!),
            MediaFailure.NotFound => CommandResult.NotFound("That monitor"),
            MediaFailure.Busy => CommandResult.Fail(ErrorCodes.Busy, detail ?? "The PC is busy.", retryable: true),
            MediaFailure.NotSupported => CommandResult.NotSupported(detail ?? "screen streaming"),
            _ => CommandResult.InvalidArguments(detail ?? "The offer was not accepted."),
        };
    }
}

/// <summary>Adds an ICE candidate the phone trickled after its offer.</summary>
public sealed class MediaIceHandler : ICommandHandler
{
    private readonly MediaSessionManager _media;

    /// <summary>Creates the handler.</summary>
    public MediaIceHandler(MediaSessionManager media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc />
    public string Command => CommandNames.MediaIce;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out MediaIceArgs args, out CommandResult? failure))
        {
            return Task.FromResult(failure!);
        }

        return Task.FromResult(_media.AddCandidate(context.Caller.ConnectionId, args) == MediaFailure.None
            ? CommandResult.Success()
            : CommandResult.NotFound("That media session"));
    }
}

/// <summary>Stops this caller's stream.</summary>
public sealed class MediaStopHandler : ICommandHandler
{
    private readonly MediaSessionManager _media;

    /// <summary>Creates the handler.</summary>
    public MediaStopHandler(MediaSessionManager media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc />
    public string Command => CommandNames.MediaStop;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out MediaStopArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        await _media.StopAsync(context.Caller.ConnectionId, args.SessionId).ConfigureAwait(false);
        return CommandResult.Success();
    }
}

/// <summary>Changes the quality ceiling of this caller's stream.</summary>
public sealed class MediaSetQualityHandler : ICommandHandler
{
    private readonly MediaSessionManager _media;

    /// <summary>Creates the handler.</summary>
    public MediaSetQualityHandler(MediaSessionManager media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc />
    public string Command => CommandNames.MediaSetQuality;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out MediaQualityArgs args, out CommandResult? failure))
        {
            return Task.FromResult(failure!);
        }

        (MediaQualityResult? result, MediaFailure error) = _media.SetQuality(context.Caller.ConnectionId, args);

        return Task.FromResult(error == MediaFailure.None
            ? CommandResult.Success(result!)
            : CommandResult.NotFound("That media session"));
    }
}
