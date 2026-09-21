using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Media;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Streaming.Adaptation;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Xunit;
using IVideoEncoder = RemoteAgent.Core.Abstractions.IVideoEncoder;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// A real WebRTC session between the PC's media stack and an in-process "phone" peer.
/// </summary>
/// <remarks>
/// <para>This is the Phase 3 spike (§9.4) kept as a regression suite: it proves the SDP exchange,
/// host-only ICE, DTLS-SRTP, H.264 packetization and RTCP feedback work end to end with the
/// library the PC uses. Capture and encoding are synthetic, so it runs on any machine.</para>
///
/// <para>The negative tests matter as much as the positive ones. A tampered DTLS fingerprint and a
/// candidate on an address other than the authenticated one must each result in no media at all —
/// those are the two properties that make the media plane safe to run on a shared LAN.</para>
/// </remarks>
public sealed class MediaLoopbackTests
{
    private static readonly VideoFormat H264 = new(
        VideoCodecsEnum.H264,
        102,
        90_000,
        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");

    [Fact]
    public async Task Stream_Connects_AndDeliversFramesStartingWithAKeyframe()
    {
        await using var harness = await Harness.StartAsync();

        await harness.WaitForFramesAsync(10, TimeSpan.FromSeconds(15));

        Assert.True(H264AnnexB.IsKeyFrame(harness.Frames.First()), "the first frame a decoder sees must be an IDR");
        Assert.True(harness.Frames.Skip(1).Any(f => !H264AnnexB.IsKeyFrame(f)), "later frames should be deltas");
        Assert.Contains(harness.Telemetry, t => t.State == "streaming");
    }

    [Fact]
    public async Task Pli_FromThePhone_ProducesANewKeyframe()
    {
        await using var harness = await Harness.StartAsync();
        await harness.WaitForFramesAsync(5, TimeSpan.FromSeconds(15));

        // Past the keyframe throttle, then ask for one the way a decoder that lost sync would.
        await Task.Delay(700);
        int keyframesBefore = harness.Encoder.KeyFrames;
        harness.Phone.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(0, 0, PSFBFeedbackTypesEnum.PLI));

        await WaitUntilAsync(() => harness.Encoder.KeyFrames > keyframesBefore, TimeSpan.FromSeconds(5));
        Assert.True(harness.Encoder.KeyFrames > keyframesBefore);
    }

    [Fact]
    public async Task TamperedDtlsFingerprint_NeverDeliversMedia()
    {
        // The offer's fingerprint is what binds the media plane to the authenticated control
        // channel. If it does not match the phone's real certificate, DTLS must fail.
        await using var harness = await Harness.StartAsync(tamperFingerprint: true);

        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Empty(harness.Frames);
        Assert.NotEqual(RTCPeerConnectionState.connected, harness.Phone.connectionState);
    }

    [Fact]
    public async Task CandidatesOnAnotherAddress_AreNeverUsed()
    {
        // The control connection "came from" an address the phone's candidates are not on, so the
        // PC has nowhere it is allowed to send media.
        await using var harness = await Harness.StartAsync(peerOverride: IPAddress.Parse("10.254.254.254"));

        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Empty(harness.Frames);
    }

    [Fact]
    public async Task SecureDesktop_PausesWithAReasonInsteadOfFailing()
    {
        await using var harness = await Harness.StartAsync(captureUnavailable: true);

        await WaitUntilAsync(
            () => harness.Telemetry.Any(t => t.State == "paused" && t.Reason == MediaPauseReasons.DesktopUnavailable),
            TimeSpan.FromSeconds(15));

        Assert.Contains(harness.Telemetry, t => t.Reason == MediaPauseReasons.DesktopUnavailable);
        Assert.False(harness.Manager.ActiveCount == 0, "a locked desktop pauses the stream; it does not end it");
    }

    [Fact]
    public async Task Manager_EnforcesTheStreamLimit_AndIsolatesConnections()
    {
        await using var harness = await Harness.StartAsync(maxStreams: 1);

        // A second connection cannot start a stream while the limit is reached.
        (MediaAnswerResult? answer, MediaFailure failure, _) = await harness.Manager.StartAsync(
            "conn-other",
            harness.PeerAddress.ToString(),
            new MediaOfferArgs { Sdp = harness.OfferSdp },
            CancellationToken.None);

        Assert.Null(answer);
        Assert.Equal(MediaFailure.Busy, failure);

        // Nor can it steer the first connection's session by guessing its id.
        Assert.Equal(
            MediaFailure.NotFound,
            harness.Manager.AddCandidate("conn-other", new MediaIceArgs { SessionId = harness.SessionId, Candidate = "x" }));
        Assert.Equal(
            MediaFailure.NotFound,
            harness.Manager.SetQuality("conn-other", new MediaQualityArgs { SessionId = harness.SessionId, MaxFps = 5 }).Failure);

        // Ending the owning connection ends its stream and frees the slot.
        await harness.Manager.EndConnectionAsync(Harness.ConnectionId, "test");
        Assert.Equal(0, harness.Manager.ActiveCount);
    }

    /// <summary>
    /// The whole PC-side pipeline on real hardware: Desktop Duplication, a Media Foundation encoder,
    /// and WebRTC, delivering this machine's actual desktop to the test peer.
    /// </summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RealDesktop_StreamsRealH264()
    {
        var monitors = new RemoteAgent.Windows.Display.DxgiMonitorProvider(
            NullLogger<RemoteAgent.Windows.Display.DxgiMonitorProvider>.Instance);
        var encoders = new RemoteAgent.Windows.Media.MediaFoundationH264EncoderFactory(
            NullLogger<RemoteAgent.Windows.Media.MediaFoundationH264Encoder>.Instance);

        if (monitors.GetMonitors().Count == 0 || encoders.Probe().Count == 0)
        {
            return; // No desktop or no encoder on this machine.
        }

        var capture = new RemoteAgent.Windows.Display.DesktopDuplicationCaptureFactory(
            NullLogger<RemoteAgent.Windows.Display.DesktopDuplicationCapture>.Instance);

        await using var harness = await Harness.StartAsync(real: (monitors, capture, encoders));

        // A static desktop sends a keyframe and then, correctly, very little. One keyframe plus the
        // refresh frame is enough to prove the chain.
        await harness.WaitForFramesAsync(1, TimeSpan.FromSeconds(20));

        byte[] first = harness.Frames.First();
        Assert.True(H264AnnexB.IsKeyFrame(first), "the first real frame must be an IDR");
        Assert.True(H264AnnexB.Contains(first, H264AnnexB.NalSps), "and carry its SPS in-band");

        await WaitUntilAsync(() => harness.Telemetry.Any(t => t.State == "streaming" && t.Width > 0), TimeSpan.FromSeconds(5));
        MediaQualityEvent streaming = harness.Telemetry.Last(t => t.State == "streaming");
        Assert.True(streaming.Width > 0 && streaming.Height > 0);
        Assert.False(string.IsNullOrEmpty(streaming.Encoder));
    }

    [Fact]
    public async Task Offer_WithoutH264_IsRefused()
    {
        var phone = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        phone.addTrack(new MediaStreamTrack(
            new List<VideoFormat> { new(VideoCodecsEnum.VP8, 96) },
            MediaStreamStatusEnum.RecvOnly));

        RTCSessionDescriptionInit offer = phone.createOffer(null);
        await phone.setLocalDescription(offer);

        await using MediaSessionManager manager = Harness.CreateManager(new FakeCaptureFactory(), new FakeEncoderFactory(), 2);

        (MediaAnswerResult? answer, MediaFailure failure, string? detail) = await manager.StartAsync(
            "conn",
            Harness.CandidateAddress(offer.sdp)?.ToString() ?? "127.0.0.1",
            new MediaOfferArgs { Sdp = offer.sdp },
            CancellationToken.None);

        Assert.Null(answer);
        Assert.Equal(MediaFailure.InvalidArguments, failure);
        Assert.False(string.IsNullOrEmpty(detail));
        phone.Close("done");
    }

    [Fact]
    public async Task Offer_OversizedOrEmpty_IsRefused()
    {
        await using MediaSessionManager manager = Harness.CreateManager(new FakeCaptureFactory(), new FakeEncoderFactory(), 2);

        foreach (string sdp in new[] { string.Empty, new string('a', MediaSession.MaxSdpLength + 1) })
        {
            (MediaAnswerResult? answer, MediaFailure failure, _) = await manager.StartAsync(
                "conn",
                "192.168.1.20",
                new MediaOfferArgs { Sdp = sdp },
                CancellationToken.None);

            Assert.Null(answer);
            Assert.Equal(MediaFailure.InvalidArguments, failure);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>A phone peer and a PC-side manager, connected through a real offer/answer.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const string ConnectionId = "conn-1";

        private Harness(RTCPeerConnection phone, MediaSessionManager manager, FakeEncoderFactory? encoders)
        {
            Phone = phone;
            Manager = manager;
            EncoderFactory = encoders;
        }

        public RTCPeerConnection Phone { get; }

        public MediaSessionManager Manager { get; }

        public FakeEncoderFactory? EncoderFactory { get; }

        public FakeEncoder Encoder => EncoderFactory?.Last ?? throw new InvalidOperationException("No fake encoder.");

        public ConcurrentQueue<byte[]> Frames { get; } = new();

        public ConcurrentQueue<MediaQualityEvent> Telemetry { get; } = new();

        public IPAddress PeerAddress { get; private set; } = IPAddress.Loopback;

        public string OfferSdp { get; private set; } = string.Empty;

        public string SessionId { get; private set; } = string.Empty;

        public static async Task<Harness> StartAsync(
            bool tamperFingerprint = false,
            IPAddress? peerOverride = null,
            bool captureUnavailable = false,
            int maxStreams = 2,
            (IMonitorProvider Monitors, IScreenCaptureFactory Capture, IVideoEncoderFactory Encoders)? real = null)
        {
            var phone = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
            phone.addTrack(new MediaStreamTrack(new List<VideoFormat> { H264 }, MediaStreamStatusEnum.RecvOnly));

            FakeEncoderFactory? fakeEncoders = real is null ? new FakeEncoderFactory() : null;
            var harness = new Harness(
                phone,
                real is { } r
                    ? CreateManager(r.Capture, r.Encoders, maxStreams, r.Monitors)
                    : CreateManager(new FakeCaptureFactory { Unavailable = captureUnavailable }, fakeEncoders!, maxStreams),
                fakeEncoders);

            phone.OnVideoFrameReceived += (_, _, frame, _) => harness.Frames.Enqueue(frame);
            harness.Manager.Telemetry += (_, report) => harness.Telemetry.Enqueue(report);

            RTCSessionDescriptionInit offer = phone.createOffer(null);
            await phone.setLocalDescription(offer);

            // The phone's own host candidate is where its "control connection" came from.
            harness.PeerAddress = peerOverride ?? CandidateAddress(offer.sdp)
                ?? throw new InvalidOperationException("The test peer produced no host candidate.");

            string sdp = offer.sdp;
            if (tamperFingerprint)
            {
                sdp = TamperFingerprint(sdp);
            }

            harness.OfferSdp = sdp;

            (MediaAnswerResult? answer, MediaFailure failure, string? detail) = await harness.Manager.StartAsync(
                ConnectionId,
                harness.PeerAddress.ToString(),
                new MediaOfferArgs { Sdp = sdp },
                CancellationToken.None);

            Assert.True(failure == MediaFailure.None, detail);
            harness.SessionId = answer!.SessionId;

            SetDescriptionResultEnum result = phone.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answer.Sdp,
            });

            Assert.Equal(SetDescriptionResultEnum.OK, result);
            return harness;
        }

        public static MediaSessionManager CreateManager(
            IScreenCaptureFactory capture,
            IVideoEncoderFactory encoders,
            int maxStreams,
            IMonitorProvider? monitors = null) =>
            new(
                () => new MediaSessionOptions
                {
                    Limits = new StreamingLimits(1920, 1080, 30, 15, 500, 8000),
                    PortRangeStart = 47910,
                    PortRangeEnd = 47950,
                    ConnectTimeoutSeconds = 20,
                },
                () => maxStreams,
                () => null,
                monitors ?? new FakeMonitorProvider(),
                capture,
                encoders,
                NullLogger<MediaSessionManager>.Instance,
                NullLogger<MediaSession>.Instance);

        public static IPAddress? CandidateAddress(string sdp)
        {
            foreach (string line in sdp.Split('\n'))
            {
                if (!line.StartsWith("a=candidate:", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] fields = line.Trim().Split(' ');
                if (fields.Length > 7 && fields[7] == "host" && IPAddress.TryParse(fields[4], out IPAddress? address))
                {
                    return address;
                }
            }

            return null;
        }

        private static string TamperFingerprint(string sdp)
        {
            var lines = sdp.Split('\n').Select(line =>
            {
                if (!line.StartsWith("a=fingerprint:", StringComparison.Ordinal))
                {
                    return line;
                }

                // Flip the last hex digit: a well-formed fingerprint of a different certificate.
                string trimmed = line.TrimEnd('\r');
                char last = trimmed[^1];
                char flipped = last == '0' ? '1' : '0';
                return trimmed[..^1] + flipped + (line.EndsWith('\r') ? "\r" : string.Empty);
            });

            return string.Join('\n', lines);
        }

        public async Task WaitForFramesAsync(int count, TimeSpan timeout)
        {
            await WaitUntilAsync(() => Frames.Count >= count, timeout);
            Assert.True(Frames.Count >= count, $"expected {count} frames, received {Frames.Count}");
        }

        public async ValueTask DisposeAsync()
        {
            Phone.Close("test finished");
            await Manager.DisposeAsync();
        }
    }

    private sealed class FakeMonitorProvider : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() =>
        [
            new MonitorInfo { Id = "mon-test", Name = "Test", Width = 64, Height = 48, Primary = true },
        ];
    }

    private sealed class FakeCaptureFactory : IScreenCaptureFactory
    {
        public bool Unavailable { get; init; }

        public IScreenCapture Open(string monitorId) => Unavailable
            ? throw new ScreenCaptureUnavailableException("The secure desktop is showing.", transient: true)
            : new FakeCapture();
    }

    /// <summary>A desktop that changes on every frame, like a video playing.</summary>
    private sealed class FakeCapture : IScreenCapture
    {
        public MonitorInfo Monitor { get; } = new() { Id = "mon-test", Width = 64, Height = 48, Primary = true };

        public CapturedFrame Frame { get; } = new() { Pixels = new byte[64 * 48 * 4], Width = 64, Height = 48, Stride = 256 };

        public CaptureStatus Acquire(int timeoutMs)
        {
            Thread.Sleep(Math.Clamp(timeoutMs, 1, 15));
            Frame.Pixels[Frame.ContentVersion % Frame.Pixels.Length]++;
            Frame.ContentVersion++;
            return CaptureStatus.Updated;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeEncoderFactory : IVideoEncoderFactory
    {
        public FakeEncoder? Last { get; private set; }

        public IReadOnlyList<VideoEncoderDescriptor> Probe() => [new VideoEncoderDescriptor("Fake H.264", false)];

        public IVideoEncoder Create(VideoEncoderSettings settings) =>
            Last = new FakeEncoder(settings.Width, settings.Height);
    }

    /// <summary>
    /// Emits syntactically valid Annex-B: SPS + PPS + IDR on request, single slices otherwise. The
    /// payload bytes are meaningless — the transport only packetizes, it never decodes.
    /// </summary>
    private sealed class FakeEncoder : IVideoEncoder
    {
        private int _keyFrames;

        public FakeEncoder(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public VideoEncoderDescriptor Descriptor { get; } = new("Fake H.264", false);

        public int Width { get; }

        public int Height { get; }

        public int KeyFrames => Volatile.Read(ref _keyFrames);

        public byte[]? Encode(ReadOnlySpan<byte> nv12, long timestamp100ns, bool keyFrame)
        {
            if (keyFrame)
            {
                Interlocked.Increment(ref _keyFrames);

                // Big enough to need FU-A fragmentation across several packets.
                return [.. Nal(0x67, 10), .. Nal(0x68, 4), .. Nal(0x65, 4000)];
            }

            return Nal(0x41, 300);
        }

        public void SetBitrate(int kbps)
        {
        }

        public void Dispose()
        {
        }

        private static byte[] Nal(byte header, int length)
        {
            byte[] nal = new byte[4 + length];
            nal[3] = 1;
            nal[4] = header;
            for (int i = 5; i < nal.Length; i++)
            {
                nal[i] = (byte)((i % 250) + 1);
            }

            return nal;
        }
    }
}
