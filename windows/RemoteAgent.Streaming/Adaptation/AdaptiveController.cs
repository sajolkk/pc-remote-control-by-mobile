namespace RemoteAgent.Streaming.Adaptation;

/// <summary>The PC's configured streaming limits, which nothing a client asks for can exceed.</summary>
/// <param name="MaxWidth">Largest streamed width.</param>
/// <param name="MaxHeight">Largest streamed height.</param>
/// <param name="FpsLimit">Highest frame rate.</param>
/// <param name="MinFps">Frame rate below which resolution is reduced instead.</param>
/// <param name="MinBitrateKbps">Bitrate floor.</param>
/// <param name="MaxBitrateKbps">Bitrate ceiling.</param>
public sealed record StreamingLimits(
    int MaxWidth,
    int MaxHeight,
    int FpsLimit,
    int MinFps,
    int MinBitrateKbps,
    int MaxBitrateKbps);

/// <summary>One rung of the degradation ladder.</summary>
/// <param name="MaxHeight">Height cap at this rung.</param>
/// <param name="Fps">Frame rate at this rung.</param>
public readonly record struct LadderStep(int MaxHeight, int Fps);

/// <summary>
/// Chooses bitrate, frame rate and resolution from what the network and the encoder report (§9.5).
/// </summary>
/// <remarks>
/// <para><b>Preference order: bitrate, then frame rate, then resolution.</b> Bitrate is adjusted
/// continuously and invisibly. Only when bitrate alone cannot relieve congestion — loss persists
/// with the bitrate already low for the current rung — does the controller step down the ladder:
/// 1080p60 → 1080p30 → 900p30 → 720p30 → 720p20 → the explicitly-labelled degraded rung.</para>
///
/// <para><b>Two independent reasons to step down.</b> The network (RTCP loss and RTT inflation) and
/// the PC itself (the capture-to-send time exceeding the frame budget, which is what a software
/// encoder at 1080p60 on a slow CPU does). Lowering bitrate does nothing for the second, so it goes
/// straight to the ladder.</para>
///
/// <para><b>Tuned for a LAN, not the internet.</b> That is why the pipeline owns this rather than
/// using libwebrtc's congestion controller (§9.4): loss on Wi-Fi is bursty and usually brief, RTT
/// is a few milliseconds, and the right response is a fast, shallow back-off with an equally fast
/// recovery. Step-up waits for ten clean seconds, so a flapping link settles at the rung it can
/// sustain instead of oscillating.</para>
///
/// <para>Time is passed in explicitly, in milliseconds, so every decision is deterministic under
/// test.</para>
/// </remarks>
public sealed class AdaptiveController
{
    /// <summary>Loss above which bitrate is cut hard.</summary>
    public const double HeavyLoss = 0.10;

    /// <summary>Loss above which bitrate is trimmed.</summary>
    public const double LightLoss = 0.03;

    /// <summary>Clean time required before stepping back up a rung.</summary>
    public const long StepUpAfterMs = 10_000;

    /// <summary>Time after a decrease before bitrate may rise again.</summary>
    public const long IncreaseHoldMs = 3_000;

    private readonly StreamingLimits _limits;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;

    private LadderStep[] _ladder = [];
    private int _ceilingHeight;
    private int _ceilingFps;
    private int _ceilingBitrate;

    private double? _rttBaselineMs;
    private long _lastDecreaseMs = long.MinValue / 2;
    private long _lastIncreaseMs = long.MinValue / 2;
    private long _cleanSinceMs;
    private int _congestedReports;

    private double _pipelineEwmaMs;
    private long _pipelineWindowStartMs = -1;
    private int _overloadedWindows;

    /// <summary>Creates a controller for a source of the given size.</summary>
    public AdaptiveController(StreamingLimits limits, int sourceWidth, int sourceHeight, long nowMs)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _sourceWidth = Math.Max(2, sourceWidth);
        _sourceHeight = Math.Max(2, sourceHeight);

        _ceilingHeight = limits.MaxHeight;
        _ceilingFps = limits.FpsLimit;
        _ceilingBitrate = limits.MaxBitrateKbps;
        _cleanSinceMs = nowMs;

        RebuildLadder();
        Level = 0;
        BitrateKbps = Math.Clamp(LevelCeilingKbps(0) / 4, _limits.MinBitrateKbps, LevelCeilingKbps(0));
    }

    /// <summary>Current rung, 0 being full quality.</summary>
    public int Level { get; private set; }

    /// <summary>The ladder in force, from full quality down.</summary>
    public IReadOnlyList<LadderStep> Ladder => _ladder;

    /// <summary>
    /// The rung from which the stream is labelled degraded: the last one, when the ladder has more
    /// than one. A ladder of one rung cannot degrade.
    /// </summary>
    public int DegradedLevel => _ladder.Length > 1 ? _ladder.Length - 1 : int.MaxValue;

    /// <summary>Encoder target bitrate.</summary>
    public int BitrateKbps { get; private set; }

    /// <summary>Frame-rate target for the current rung.</summary>
    public int Fps => _ladder[Level].Fps;

    /// <summary>Height cap for the current rung.</summary>
    public int MaxHeight => _ladder[Level].MaxHeight;

    /// <summary>Latest loss fraction reported by the receiver.</summary>
    public double LastLoss { get; private set; }

    /// <summary>Latest round-trip time, or null before the first report that carries one.</summary>
    public double? LastRttMs { get; private set; }

    /// <summary>Smoothed capture-to-send time.</summary>
    public double PipelineMs => _pipelineEwmaMs;

    /// <summary>Increments on every change a pipeline has to act on.</summary>
    public int Version { get; private set; }

    /// <summary>A one-word summary for the UI.</summary>
    public string Indicator =>
        Level >= DegradedLevel || LastLoss > 0.08
            ? "poor"
            : Level >= 2 || LastLoss > 0.02 || _congestedReports > 0
                ? "fair"
                : "good";

    /// <summary>
    /// The output size for the current rung: the source scaled to fit the height cap and the PC's
    /// width limit, preserving aspect ratio, rounded to even dimensions for the encoder.
    /// </summary>
    public (int Width, int Height) OutputSize => Fit(_sourceWidth, _sourceHeight, _limits.MaxWidth, MaxHeight);

    /// <summary>
    /// Applies a client's quality request. Values are ceilings and are clamped to the PC's limits.
    /// </summary>
    /// <returns>The ceilings now in force.</returns>
    public (int MaxHeight, int MaxFps, int MaxBitrateKbps) SetCeiling(int? maxHeight, int? maxFps, int? maxBitrateKbps)
    {
        if (maxHeight is { } h)
        {
            _ceilingHeight = Math.Clamp(h, 240, _limits.MaxHeight);
        }

        if (maxFps is { } f)
        {
            _ceilingFps = Math.Clamp(f, 1, _limits.FpsLimit);
        }

        if (maxBitrateKbps is { } b)
        {
            _ceilingBitrate = Math.Clamp(b, _limits.MinBitrateKbps, _limits.MaxBitrateKbps);
        }

        RebuildLadder();
        Level = Math.Min(Level, _ladder.Length - 1);
        BitrateKbps = Math.Min(BitrateKbps, LevelCeilingKbps(Level));
        Version++;

        return (_ceilingHeight, _ceilingFps, _ceilingBitrate);
    }

    /// <summary>
    /// Feeds one RTCP receiver report.
    /// </summary>
    /// <param name="nowMs">Current time.</param>
    /// <param name="fractionLost">Loss since the previous report, 0 to 1.</param>
    /// <param name="rttMs">Round-trip time, when the report allows it to be computed.</param>
    public void OnReceiverReport(long nowMs, double fractionLost, double? rttMs)
    {
        LastLoss = Math.Clamp(fractionLost, 0, 1);
        LastRttMs = rttMs;

        if (rttMs is { } rtt && rtt >= 0)
        {
            _rttBaselineMs = _rttBaselineMs is null ? rtt : Math.Min(_rttBaselineMs.Value, rtt);
        }

        // Queueing shows up as RTT growth before it shows up as loss. The absolute margin keeps
        // Wi-Fi's normal few-millisecond jitter from reading as congestion on a 2 ms baseline.
        bool rttInflated = rttMs is { } current && _rttBaselineMs is { } baseline &&
                           current > (baseline * 2) + 40;

        int before = BitrateKbps;
        int levelBefore = Level;

        if (LastLoss > HeavyLoss)
        {
            BitrateKbps = Math.Max(_limits.MinBitrateKbps, (int)(BitrateKbps * 0.7));
            _lastDecreaseMs = nowMs;
            _congestedReports++;
            _cleanSinceMs = nowMs;
        }
        else if (LastLoss > LightLoss || rttInflated)
        {
            BitrateKbps = Math.Max(_limits.MinBitrateKbps, (int)(BitrateKbps * 0.88));
            _lastDecreaseMs = nowMs;
            _congestedReports++;
            _cleanSinceMs = nowMs;
        }
        else
        {
            _congestedReports = 0;

            if (nowMs - _lastDecreaseMs >= IncreaseHoldMs && nowMs - _lastIncreaseMs >= 1_000)
            {
                BitrateKbps = Math.Min(LevelCeilingKbps(Level), (int)(BitrateKbps * 1.12) + 100);
                _lastIncreaseMs = nowMs;
            }
        }

        // Bitrate has done what it can: congestion persists with the bitrate already near the
        // bottom of this rung's range. Give up quality structurally instead. Both conditions are
        // needed — without the bitrate test, a stream that simply started low would drop a rung
        // after a couple of lossy reports and never have tried the cheaper remedy.
        double exhausted = Math.Max(_limits.MinBitrateKbps * 1.25, LevelCeilingKbps(Level) * 0.2);
        if (_congestedReports >= 3 && BitrateKbps <= exhausted && Level < _ladder.Length - 1)
        {
            Level++;
            _congestedReports = 0;
            BitrateKbps = Math.Min(BitrateKbps, LevelCeilingKbps(Level));
        }

        TryStepUp(nowMs);

        if (BitrateKbps != before || Level != levelBefore)
        {
            Version++;
        }
    }

    /// <summary>
    /// Feeds the time one frame took from capture to send, for the encoder-overload check.
    /// </summary>
    public void OnFrameProcessed(long nowMs, double pipelineMs)
    {
        _pipelineEwmaMs = _pipelineEwmaMs == 0 ? pipelineMs : (_pipelineEwmaMs * 0.9) + (pipelineMs * 0.1);

        if (_pipelineWindowStartMs < 0)
        {
            _pipelineWindowStartMs = nowMs;
            return;
        }

        if (nowMs - _pipelineWindowStartMs < 1_000)
        {
            return;
        }

        _pipelineWindowStartMs = nowMs;

        double budget = 1000.0 / Fps;
        if (_pipelineEwmaMs > budget * 0.85)
        {
            _overloadedWindows++;
            _cleanSinceMs = nowMs;
        }
        else
        {
            _overloadedWindows = 0;
        }

        // Two consecutive overloaded seconds: the PC cannot produce this rung in real time. Lower
        // bitrate would not help — the cost is per pixel and per frame — so step the ladder.
        if (_overloadedWindows >= 2 && Level < _ladder.Length - 1)
        {
            Level++;
            _overloadedWindows = 0;
            BitrateKbps = Math.Min(BitrateKbps, LevelCeilingKbps(Level));
            Version++;
        }
    }

    private void TryStepUp(long nowMs)
    {
        if (Level == 0 || nowMs - _cleanSinceMs < StepUpAfterMs)
        {
            return;
        }

        // Only step up if the bitrate has actually recovered, and the pipeline will fit the next
        // rung's budget with room to spare — otherwise the overload check would push straight back.
        if (BitrateKbps < LevelCeilingKbps(Level) * 0.8)
        {
            return;
        }

        LadderStep current = _ladder[Level];
        LadderStep upper = _ladder[Level - 1];
        double costRatio = (double)upper.Fps / current.Fps * Square((double)upper.MaxHeight / current.MaxHeight);
        double projected = _pipelineEwmaMs * costRatio;

        if (projected > (1000.0 / upper.Fps) * 0.6)
        {
            return;
        }

        Level--;
        _cleanSinceMs = nowMs;
    }

    /// <summary>
    /// The bitrate this rung is worth: the ceiling scaled by pixel rate to the power 0.75, since
    /// halving the pixels does not halve the bits needed for the same quality.
    /// </summary>
    private int LevelCeilingKbps(int level)
    {
        LadderStep top = _ladder[0];
        LadderStep step = _ladder[level];
        double ratio = (double)step.Fps / top.Fps * Square((double)step.MaxHeight / top.MaxHeight);
        int scaled = (int)(_ceilingBitrate * Math.Pow(ratio, 0.75));
        return Math.Clamp(scaled, _limits.MinBitrateKbps, _ceilingBitrate);
    }

    private void RebuildLadder()
    {
        int height = Math.Min(Math.Min(_sourceHeight, _ceilingHeight), _limits.MaxHeight);
        int fps = Math.Min(_ceilingFps, _limits.FpsLimit);

        // The configured minimum frame rate, unless the ceiling itself is lower: a client that
        // asked for 10 fps gets 10 fps, not the PC's idea of a minimum.
        int fpsFloor = Math.Min(_limits.MinFps, fps);

        (int Height, int Fps)[] rungs =
        [
            (height, fps),
            (height, 30),
            (900, 30),
            (720, 30),
            (720, 20),
            (540, 15),
        ];

        var ladder = new List<LadderStep>();
        foreach ((int h, int f) in rungs)
        {
            var step = new LadderStep(Math.Min(h, height), Math.Clamp(f, fpsFloor, fps));
            if (ladder.Count == 0 || ladder[^1] != step)
            {
                ladder.Add(step);
            }
        }

        _ladder = [.. ladder];
    }

    /// <summary>
    /// Fits a source into a width and height cap, preserving aspect ratio, with even dimensions.
    /// </summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight));

        // Rounded, not truncated: 1920 × (720 / 1080) is 1279.9999… in floating point, and
        // truncating it would stream 1278×720 instead of 1280×720.
        int width = Math.Max(2, (int)Math.Round(sourceWidth * scale) & ~1);
        int height = Math.Max(2, (int)Math.Round(sourceHeight * scale) & ~1);
        return (Math.Min(width, sourceWidth & ~1), Math.Min(height, sourceHeight & ~1));
    }

    private static double Square(double value) => value * value;
}
