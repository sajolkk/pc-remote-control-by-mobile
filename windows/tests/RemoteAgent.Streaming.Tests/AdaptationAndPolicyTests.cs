using System.Net;
using RemoteAgent.Streaming.Adaptation;
using RemoteAgent.Streaming.Transport;
using Xunit;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// The adaptive controller (§9.5) and the ICE candidate policy (§9.4).
/// </summary>
public sealed class AdaptationAndPolicyTests
{
    private static readonly StreamingLimits Defaults = new(
        MaxWidth: 1920,
        MaxHeight: 1080,
        FpsLimit: 60,
        MinFps: 15,
        MinBitrateKbps: 2000,
        MaxBitrateKbps: 40000);

    // --- Ladder ---

    [Fact]
    public void Ladder_For1080p60_MatchesTheArchitecture()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);

        Assert.Equal(
            [new(1080, 60), new(1080, 30), new(900, 30), new(720, 30), new(720, 20), new(540, 15)],
            controller.Ladder);
        Assert.Equal(5, controller.DegradedLevel);
        Assert.Equal((1920, 1080), controller.OutputSize);
    }

    [Fact]
    public void Ladder_OnASmallMonitor_CollapsesDuplicateRungs()
    {
        var controller = new AdaptiveController(Defaults with { FpsLimit = 30 }, 1280, 720, nowMs: 0);

        // 720p source, 30 fps cap: 1080/900 rungs collapse onto 720p30.
        Assert.Equal([new(720, 30), new(720, 20), new(540, 15)], controller.Ladder);
    }

    [Fact]
    public void Ladder_RespectsAClientFpsBelowTheConfiguredMinimum()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        controller.SetCeiling(maxHeight: null, maxFps: 10, maxBitrateKbps: null);

        Assert.All(controller.Ladder, step => Assert.Equal(10, step.Fps));
    }

    [Fact]
    public void SetCeiling_ClampsToThePcsLimits()
    {
        var controller = new AdaptiveController(Defaults, 3840, 2160, nowMs: 0);

        (int height, int fps, int bitrate) = controller.SetCeiling(4320, 240, 999_999);

        Assert.Equal(1080, height);
        Assert.Equal(60, fps);
        Assert.Equal(40000, bitrate);
        Assert.Equal((1920, 1080), controller.OutputSize);
    }

    [Theory]
    [InlineData(2560, 1440, 1920, 1080, 1920, 1080)]
    [InlineData(1920, 1080, 1920, 720, 1280, 720)]
    [InlineData(1920, 1200, 1920, 1080, 1728, 1080)]
    [InlineData(1366, 768, 1920, 1080, 1366, 768)]
    [InlineData(1365, 767, 1920, 1080, 1364, 766)]
    public void Fit_PreservesAspectWithEvenDimensions(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), AdaptiveController.Fit(sw, sh, mw, mh));
    }

    // --- Bitrate and ladder decisions ---

    [Fact]
    public void HeavyLoss_CutsBitrateHard()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        int before = controller.BitrateKbps;

        controller.OnReceiverReport(1000, fractionLost: 0.2, rttMs: 5);

        Assert.Equal((int)(before * 0.7), controller.BitrateKbps);
        Assert.Equal(0, controller.Level);
    }

    [Fact]
    public void CleanReports_RampBitrateUpToTheCeiling()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        int start = controller.BitrateKbps;

        for (long t = 5_000; t < 60_000; t += 1_000)
        {
            controller.OnReceiverReport(t, 0, 3);
        }

        Assert.True(controller.BitrateKbps > start);
        Assert.Equal(40000, controller.BitrateKbps);
        Assert.Equal("good", controller.Indicator);
    }

    [Fact]
    public void PersistentLoss_StepsDownTheLadderOnceBitrateIsExhausted()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);

        for (long t = 1_000; t <= 20_000; t += 1_000)
        {
            controller.OnReceiverReport(t, 0.2, 5);
        }

        Assert.True(controller.Level > 0, "sustained heavy loss should reduce resolution or frame rate");
        Assert.True(controller.BitrateKbps >= Defaults.MinBitrateKbps);
    }

    [Fact]
    public void BriefLoss_IsAnsweredWithBitrateNotResolution()
    {
        // Wi-Fi loss is bursty. Two bad reports should cost bitrate and nothing more: stepping down a
        // rung for a blip would make the picture visibly worse for ten seconds afterwards.
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        int before = controller.BitrateKbps;

        controller.OnReceiverReport(1_000, 0.2, 5);
        controller.OnReceiverReport(2_000, 0.2, 5);

        Assert.Equal(0, controller.Level);
        Assert.True(controller.BitrateKbps < before);
    }

    [Fact]
    public void AfterRecovery_StepsBackUpOnlyAfterTenCleanSeconds()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        long t = 0;
        while (controller.Level == 0 && t < 60_000)
        {
            t += 1_000;
            controller.OnReceiverReport(t, 0.25, 5);
        }

        int degraded = controller.Level;
        Assert.True(degraded > 0);

        long recoveredAt = t;
        for (t += 1_000; t < recoveredAt + AdaptiveController.StepUpAfterMs; t += 1_000)
        {
            controller.OnReceiverReport(t, 0, 3);
            Assert.Equal(degraded, controller.Level);
        }

        for (; t < recoveredAt + 120_000 && controller.Level == degraded; t += 1_000)
        {
            controller.OnReceiverReport(t, 0, 3);
        }

        Assert.True(controller.Level < degraded, "a clean link should earn quality back");
    }

    [Fact]
    public void RttInflation_IsTreatedAsCongestion()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        controller.OnReceiverReport(1_000, 0, 3);
        int before = controller.BitrateKbps;

        controller.OnReceiverReport(2_000, 0, 150);

        Assert.True(controller.BitrateKbps < before);
    }

    [Fact]
    public void EncoderOverload_StepsDownWithoutWaitingForTheNetwork()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);

        // 25 ms per frame against a 16.7 ms budget at 60 fps: the PC cannot keep up.
        for (long t = 0; t <= 3_000; t += 16)
        {
            controller.OnFrameProcessed(t, 25);
        }

        Assert.Equal(1, controller.Level);
        Assert.Equal(30, controller.Fps);
    }

    [Fact]
    public void Version_ChangesOnlyWhenThereIsSomethingToApply()
    {
        var controller = new AdaptiveController(Defaults, 1920, 1080, nowMs: 0);
        int version = controller.Version;

        // Inside the increase hold after construction? No decrease yet, so this increases.
        controller.OnReceiverReport(5_000, 0, 3);
        Assert.NotEqual(version, controller.Version);

        version = controller.Version;
        controller.OnReceiverReport(5_100, 0, 3); // within the 1 s increase spacing
        Assert.Equal(version, controller.Version);
    }

    // --- ICE candidate policy ---

    private static readonly IPAddress Peer = IPAddress.Parse("192.168.1.20");

    [Theory]
    [InlineData("candidate:1 1 udp 2122260223 192.168.1.20 50000 typ host generation 0")]
    [InlineData("a=candidate:1 1 UDP 2122260223 192.168.1.20 50000 typ host")]
    [InlineData("1 1 udp 2122260223 192.168.1.20 9 typ host")]
    public void Ice_AcceptsHostUdpOnTheAuthenticatedAddress(string candidate)
    {
        Assert.True(IceCandidatePolicy.IsAllowed(candidate, Peer, out string reason), reason);
    }

    [Theory]
    [InlineData("candidate:1 1 udp 2122260223 192.168.1.99 50000 typ host", "address")]
    [InlineData("candidate:1 1 udp 1686052607 203.0.113.7 50000 typ srflx raddr 192.168.1.20 rport 50000", "srflx")]
    [InlineData("candidate:1 1 udp 41885439 198.51.100.1 3478 typ relay raddr 192.168.1.20 rport 50000", "relay")]
    [InlineData("candidate:1 1 tcp 1518280447 192.168.1.20 9 typ host tcptype active", "UDP")]
    [InlineData("candidate:1 1 udp 2122260223 4f1b2c3d-1111-2222-3333-444455556666.local 50000 typ host", "mDNS")]
    [InlineData("candidate:1 1 udp 2122260223 192.168.1.20 0 typ host", "port")]
    [InlineData("candidate:garbage", "malformed")]
    [InlineData("", "end of candidates")]
    public void Ice_RefusesEverythingElse(string candidate, string expectedReason)
    {
        Assert.False(IceCandidatePolicy.IsAllowed(candidate, Peer, out string reason));
        Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ice_TreatsIpv4MappedAddressesAsTheSameHost()
    {
        IPAddress mapped = IPAddress.Parse("::ffff:192.168.1.20");

        Assert.True(IceCandidatePolicy.IsAllowed("candidate:1 1 udp 1 192.168.1.20 5000 typ host", mapped, out _));
    }

    [Fact]
    public void Ice_FilterSdp_RemovesOnlyRefusedCandidates()
    {
        string sdp = string.Join("\r\n",
            "v=0",
            "m=video 9 UDP/TLS/RTP/SAVPF 96",
            "a=candidate:1 1 udp 2122260223 192.168.1.20 50000 typ host",
            "a=candidate:2 1 udp 2122194687 10.0.0.5 50001 typ host",
            "a=candidate:3 1 udp 1686052607 203.0.113.7 50002 typ srflx raddr 192.168.1.20 rport 50000",
            "a=rtpmap:96 H264/90000",
            string.Empty);

        string filtered = IceCandidatePolicy.FilterSdp(sdp, Peer, out int removed);

        Assert.Equal(2, removed);
        Assert.Contains("192.168.1.20 50000 typ host", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.5", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("srflx", filtered, StringComparison.Ordinal);
        Assert.Contains("a=rtpmap:96 H264/90000", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Rtt_IsNullWithoutASenderReport()
    {
        Assert.Null(MediaSession.ComputeRttMs(0, 0));
    }
}
