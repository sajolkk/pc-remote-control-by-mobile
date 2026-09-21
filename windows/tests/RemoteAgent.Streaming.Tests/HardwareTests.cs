using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Media;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Display;
using RemoteAgent.Windows.Media;
using Xunit;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// Real Desktop Duplication and Media Foundation on the machine running the tests.
/// </summary>
/// <remarks>
/// Each test returns early, passing, when the hardware it needs is absent — a headless CI runner
/// has no desktop to duplicate, and a Windows N edition has no H.264 encoder. On a developer's PC
/// they are the only tests that prove the platform layer works at all, which is why they exist
/// despite being environment-dependent.
/// </remarks>
[Trait("Category", "Hardware")]
public sealed class HardwareTests
{
    [Fact]
    public void Monitors_HaveUniqueIdsAndSaneGeometry()
    {
        IReadOnlyList<MonitorInfo> monitors = new DxgiMonitorProvider(NullLogger<DxgiMonitorProvider>.Instance).GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        Assert.Equal(monitors.Count, monitors.Select(m => m.Id).Distinct().Count());
        Assert.Single(monitors, m => m.Primary);
        Assert.True(monitors[0].Primary, "the primary monitor is listed first");
        Assert.All(monitors, m =>
        {
            Assert.StartsWith("mon-", m.Id, StringComparison.Ordinal);
            Assert.True(m.Width > 0 && m.Height > 0);
            Assert.InRange(m.Scale, 0.5, 5);
        });
    }

    [Fact]
    public void Capture_PrimaryMonitor_ProducesAFullFrame()
    {
        IReadOnlyList<MonitorInfo> monitors = new DxgiMonitorProvider(NullLogger<DxgiMonitorProvider>.Instance).GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var factory = new DesktopDuplicationCaptureFactory(NullLogger<DesktopDuplicationCapture>.Instance);

        IScreenCapture capture;
        try
        {
            capture = factory.Open(monitors[0].Id);
        }
        catch (ScreenCaptureUnavailableException)
        {
            // Locked, or not an interactive session (a service account running the tests).
            return;
        }

        using (capture)
        {
            for (int i = 0; i < 20 && capture.Frame.ContentVersion == 0; i++)
            {
                Assert.NotEqual(CaptureStatus.Lost, capture.Acquire(100));
            }

            Assert.True(capture.Frame.ContentVersion > 0, "a new duplication delivers the desktop on its first frame");
            Assert.Equal(monitors[0].Width, capture.Frame.Width);
            Assert.Equal(monitors[0].Height, capture.Frame.Height);
        }
    }

    [Fact]
    public void Capture_UnknownMonitor_IsAPermanentFailure()
    {
        var factory = new DesktopDuplicationCaptureFactory(NullLogger<DesktopDuplicationCapture>.Instance);

        var ex = Assert.Throws<ScreenCaptureUnavailableException>(() => factory.Open("mon-000000000000"));
        Assert.False(ex.Transient);
    }

    [Fact]
    public void Encoders_ProduceIdrOnDemandWithParameterSets()
    {
        var factory = new MediaFoundationH264EncoderFactory(NullLogger<MediaFoundationH264Encoder>.Instance);
        IReadOnlyList<VideoEncoderDescriptor> encoders = factory.Probe();
        if (encoders.Count == 0)
        {
            return;
        }

        foreach (bool hardware in encoders.Select(e => e.IsHardware).Distinct())
        {
            using IVideoEncoder encoder = factory.Create(new VideoEncoderSettings(320, 240, 30, 1000, hardware));
            Assert.Equal(hardware, encoder.Descriptor.IsHardware);

            byte[] frame = new byte[320 * 240 * 3 / 2];
            frame.AsSpan(320 * 240).Fill(128);

            var outputs = new List<byte[]>();
            for (int i = 0; i < 10; i++)
            {
                frame[i * 50]++; // make each frame differ slightly
                byte[]? au = encoder.Encode(frame, i * 333_333L, keyFrame: i == 0 || i == 6);
                if (au is not null)
                {
                    outputs.Add(au);
                }
            }

            encoder.SetBitrate(500);

            Assert.NotEmpty(outputs);
            byte[] first = outputs[0];
            Assert.True(H264AnnexB.IsKeyFrame(first), $"{encoder.Descriptor.Name}: first frame must be an IDR");
            Assert.True(H264AnnexB.Contains(first, H264AnnexB.NalSps), $"{encoder.Descriptor.Name}: IDR must carry SPS");
            Assert.True(H264AnnexB.Contains(first, H264AnnexB.NalPps), $"{encoder.Descriptor.Name}: IDR must carry PPS");
            Assert.True(outputs.Count(au => H264AnnexB.IsKeyFrame(au)) >= 2, $"{encoder.Descriptor.Name}: a forced keyframe must be honoured");
            Assert.Contains(outputs, au => !H264AnnexB.IsKeyFrame(au));
        }
    }

    [Fact]
    public void Jpeg_RoundTripsDimensions()
    {
        byte[] bgra = new byte[64 * 32 * 4];
        byte[] jpeg = new JpegImageEncoder().EncodeJpeg(bgra, 64, 32, 256, 70);

        // SOI marker, and small: a black image compresses to almost nothing.
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.InRange(jpeg.Length, 100, 10_000);
    }
}
