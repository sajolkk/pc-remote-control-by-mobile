using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using Xunit;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// Whether the PC says it can stream its screen, and that it keeps finding out after a boot in
/// which nothing was ready at first.
/// </summary>
public sealed class ScreenCapabilityProbeTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void AWorkingPcReportsScreenCaptureAndItsEncoder()
    {
        var pc = new BootingPc();

        IReadOnlyList<string> capabilities = pc.CreateProbe().Probe();

        Assert.Contains(CapabilityNames.ScreenCapture, capabilities);
        Assert.Contains(CapabilityNames.SoftwareEncodeH264, capabilities);
        Assert.DoesNotContain(CapabilityNames.HardwareEncodeH264, capabilities);
        Assert.DoesNotContain(CapabilityNames.MultiMonitor, capabilities);
    }

    [Fact]
    public void HardwareEncodingAndASecondMonitorAreReported()
    {
        var pc = new BootingPc { Monitors = 2, HardwareEncoder = true };

        IReadOnlyList<string> capabilities = pc.CreateProbe().Probe();

        Assert.Contains(CapabilityNames.ScreenCapture, capabilities);
        Assert.Contains(CapabilityNames.HardwareEncodeH264, capabilities);
        Assert.Contains(CapabilityNames.MultiMonitor, capabilities);
    }

    [Fact]
    public void ALockedPcStillCountsAsAbleToStream()
    {
        var pc = new BootingPc { CaptureFailure = new ScreenCaptureUnavailableException("Secure desktop.", transient: true) };

        Assert.Contains(CapabilityNames.ScreenCapture, pc.CreateProbe().Probe());
    }

    [Fact]
    public void NoMonitorMeansNoScreenCapture()
    {
        var pc = new BootingPc { Monitors = 0 };

        Assert.Empty(pc.CreateProbe().Probe());
    }

    [Fact]
    public void NoEncoderMeansNoScreenCapture()
    {
        var pc = new BootingPc { Encoders = false };

        Assert.Empty(pc.CreateProbe().Probe());
    }

    [Fact]
    public void ACaptureThatCannotWorkMeansNoScreenCapture()
    {
        var pc = new BootingPc { CaptureFailure = new ScreenCaptureUnavailableException("No Direct3D 11.", transient: false) };

        Assert.Empty(pc.CreateProbe().Probe());
    }

    [Fact]
    public void AnUnexpectedErrorMeansNoScreenCaptureRatherThanACrash()
    {
        var pc = new BootingPc { MonitorFailure = new InvalidOperationException("DXGI went away.") };

        Assert.Empty(pc.CreateProbe().Probe());
    }

    /// <summary>The bug this exists for: the PC boots, and the driver and encoder come later.</summary>
    [Fact]
    public async Task KeepsProbingAfterABootUntilTheDisplayAndEncoderAreReady()
    {
        var pc = new BootingPc { Monitors = 0, Encoders = false };
        ScreenCapabilityProbe probe = pc.CreateProbe();

        Assert.Empty(probe.Probe());

        // The display driver loads first, the encoder a few probes later.
        pc.OnProbe = count =>
        {
            if (count >= 2)
            {
                pc.Monitors = 1;
            }

            if (count >= 4)
            {
                pc.Encoders = true;
            }
        };

        IReadOnlyList<string>? capabilities = await probe.WaitUntilAvailableAsync(
            Fast,
            Fast,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.NotNull(capabilities);
        Assert.Contains(CapabilityNames.ScreenCapture, capabilities);
        Assert.True(pc.MonitorQueries >= 4, $"Probed {pc.MonitorQueries} times.");
    }

    [Fact]
    public async Task StopsOnceItWorksInsteadOfProbingForever()
    {
        var pc = new BootingPc();
        ScreenCapabilityProbe probe = pc.CreateProbe();

        IReadOnlyList<string>? capabilities = await probe.WaitUntilAvailableAsync(Fast, Fast, TimeSpan.FromSeconds(30), CancellationToken.None);
        int probesWhenDone = pc.MonitorQueries;
        await Task.Delay(50);

        Assert.NotNull(capabilities);
        Assert.Equal(1, probesWhenDone);
        Assert.Equal(probesWhenDone, pc.MonitorQueries);
    }

    [Fact]
    public async Task GivesUpOnAPcThatNeverCanStream()
    {
        var pc = new BootingPc { Encoders = false };

        IReadOnlyList<string>? capabilities = await pc.CreateProbe().WaitUntilAvailableAsync(
            Fast,
            Fast,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.Null(capabilities);
        Assert.True(pc.MonitorQueries >= 2, $"Probed {pc.MonitorQueries} times.");
    }

    [Fact]
    public async Task WaitsLongerBetweenProbesUpToTheLimitThenGivesUp()
    {
        var pc = new BootingPc { Encoders = false };
        var waits = new List<TimeSpan>();
        ScreenCapabilityProbe probe = pc.CreateProbe(delay: (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });

        IReadOnlyList<string>? capabilities = await probe.WaitUntilAvailableAsync(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.Null(capabilities);
        Assert.Equal(
            new[] { 5, 10, 20, 30, 30, 30 },
            waits.Select(static w => (int)w.TotalSeconds).ToArray());
        Assert.Equal(waits.Count, pc.MonitorQueries);
    }

    [Fact]
    public async Task StopsWhenTheAgentStops()
    {
        var pc = new BootingPc { Encoders = false };
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pc.CreateProbe().WaitUntilAvailableAsync(Fast, Fast, TimeSpan.FromMinutes(5), stopping.Token));
    }

    /// <summary>A PC whose display and encoder can be switched on mid-test.</summary>
    private sealed class BootingPc : IMonitorProvider, IScreenCaptureFactory, IVideoEncoderFactory
    {
        private int _monitorQueries;

        public volatile int Monitors = 1;
        public volatile bool Encoders = true;
        public bool HardwareEncoder { get; init; }
        public Exception? MonitorFailure { get; init; }
        public ScreenCaptureUnavailableException? CaptureFailure { get; init; }
        public Action<int>? OnProbe { get; set; }

        public int MonitorQueries => Volatile.Read(ref _monitorQueries);

        public ScreenCapabilityProbe CreateProbe(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
            delay is null
                ? new(this, this, this, NullLogger<ScreenCapabilityProbe>.Instance)
                : new(this, this, this, NullLogger<ScreenCapabilityProbe>.Instance) { Delay = delay };

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            int count = Interlocked.Increment(ref _monitorQueries);
            OnProbe?.Invoke(count);

            if (MonitorFailure is not null)
            {
                throw MonitorFailure;
            }

            return Enumerable.Range(0, Monitors)
                .Select(static i => new MonitorInfo { Id = $"mon-{i}", Width = 64, Height = 48, Primary = i == 0 })
                .ToArray();
        }

        public IScreenCapture Open(string monitorId) => CaptureFailure is not null
            ? throw CaptureFailure
            : new IdleCapture(monitorId);

        public IReadOnlyList<VideoEncoderDescriptor> Probe() => !Encoders
            ? []
            : HardwareEncoder
                ? [new VideoEncoderDescriptor("Fake GPU H.264", true), new VideoEncoderDescriptor("Fake H.264", false)]
                : [new VideoEncoderDescriptor("Fake H.264", false)];

        public IVideoEncoder Create(VideoEncoderSettings settings) => throw new NotSupportedException();
    }

    private sealed class IdleCapture(string monitorId) : IScreenCapture
    {
        public MonitorInfo Monitor { get; } = new() { Id = monitorId, Width = 64, Height = 48, Primary = true };

        public CapturedFrame Frame { get; } = new();

        public CaptureStatus Acquire(int timeoutMs) => CaptureStatus.Unchanged;

        public void Dispose()
        {
        }
    }
}
