using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Streaming;

/// <summary>
/// Finds out whether this PC can stream its screen, and keeps finding out until it can.
/// </summary>
/// <remarks>
/// <para><b>Screen streaming is reported only if every link in the chain works:</b> a monitor to
/// capture, Desktop Duplication on it, and an H.264 encoder that actually starts. A capture that
/// fails <em>transiently</em> — because the agent started while the PC was locked — still counts.
/// The capability describes the PC, not this moment; the locked state reaches the phone
/// separately and it explains the blank view (§6.4).</para>
///
/// <para><b>One probe is not enough at boot.</b> The service starts the desktop agent as the PC
/// starts, on the sign-in screen, and the display driver or the H.264 encoder is often not ready
/// yet. A single probe then finds no monitor or no encoder, and the phone said the PC could not
/// stream until the agent happened to be restarted. <see cref="WaitUntilAvailableAsync"/> keeps
/// probing for a while instead.</para>
/// </remarks>
public sealed class ScreenCapabilityProbe
{
    private readonly IMonitorProvider _monitors;
    private readonly IScreenCaptureFactory _captureFactory;
    private readonly IVideoEncoderFactory _encoders;
    private readonly ILogger<ScreenCapabilityProbe> _logger;

    /// <summary>Creates the probe.</summary>
    public ScreenCapabilityProbe(
        IMonitorProvider monitors,
        IScreenCaptureFactory captureFactory,
        IVideoEncoderFactory encoders,
        ILogger<ScreenCapabilityProbe> logger)
    {
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Waits between probes. Tests replace it to check the timing without waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// The screen capabilities that work right now: <see cref="CapabilityNames.ScreenCapture"/>,
    /// the encoder kinds and multi-monitor, or nothing.
    /// </summary>
    /// <param name="failureLevel">
    /// How loudly to log a failure. Repeated probes log quietly, so a PC that cannot stream does
    /// not fill the log while it is being retried.
    /// </param>
    public IReadOnlyList<string> Probe(LogLevel failureLevel = LogLevel.Warning)
    {
        var capabilities = new List<string>();

        try
        {
            IReadOnlyList<MonitorInfo> monitors = _monitors.GetMonitors();
            if (monitors.Count == 0)
            {
                _logger.Log(failureLevel, "No capturable monitor was found; screen streaming is not advertised.");
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
                _logger.Log(
                    failureLevel,
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
            _logger.Log(failureLevel, "Desktop Duplication does not work on this PC ({Reason}); screen streaming is not advertised.", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.Log(failureLevel, ex, "Screen capture could not be probed; screen streaming is not advertised.");
        }

        return capabilities;
    }

    /// <summary>
    /// Probes again, waiting longer each time, until screen streaming works or the waits add up
    /// to <paramref name="giveUpAfter"/>.
    /// </summary>
    /// <returns>The capabilities once screen streaming works, or null if it never did.</returns>
    /// <remarks>
    /// Gives up eventually because a PC that still cannot stream by then genuinely cannot.
    /// </remarks>
    public async Task<IReadOnlyList<string>?> WaitUntilAvailableAsync(
        TimeSpan firstDelay,
        TimeSpan maxDelay,
        TimeSpan giveUpAfter,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = firstDelay;
        TimeSpan waited = TimeSpan.Zero;

        while (waited < giveUpAfter)
        {
            await Delay(delay, cancellationToken).ConfigureAwait(false);
            waited += delay;
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));

            IReadOnlyList<string> capabilities = Probe(LogLevel.Debug);
            if (capabilities.Contains(CapabilityNames.ScreenCapture))
            {
                return capabilities;
            }
        }

        _logger.LogWarning(
            "Screen streaming still does not work after {Minutes:0.#} minutes; no longer probing for it.",
            giveUpAfter.TotalMinutes);
        return null;
    }
}
