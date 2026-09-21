using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Media;
using RemoteAgent.Windows.Interop;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteAgent.Windows.Media;

/// <summary>
/// Finds and starts Media Foundation H.264 encoders (§9.2).
/// </summary>
/// <remarks>
/// <para><b>Registered is not the same as usable.</b> GPU drivers register their encoder MFT
/// whether or not the particular GPU has an encode block: an NVIDIA driver installs "NVIDIA H.264
/// Encoder MFT" on a GeForce GT 710, which has no NVENC. So <see cref="Probe"/> actually starts
/// each candidate at a small resolution, and only encoders that accept a configuration are
/// reported. The result is cached — probing takes a few hundred milliseconds.</para>
///
/// <para><b>Fallback is automatic.</b> <see cref="Create"/> walks the probed list in order,
/// hardware first when preferred, and the software encoder that ships with Windows is the floor.
/// Windows "N" editions without the Media Feature Pack have no H.264 encoder at all; there the
/// probe returns nothing and screen streaming is simply not advertised (§0).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationH264EncoderFactory : IVideoEncoderFactory
{
    private readonly ILogger<MediaFoundationH264Encoder> _logger;
    private readonly Lock _probeLock = new();
    private IReadOnlyList<VideoEncoderDescriptor>? _probed;

    /// <summary>Creates the factory.</summary>
    public MediaFoundationH264EncoderFactory(ILogger<MediaFoundationH264Encoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<VideoEncoderDescriptor> Probe()
    {
        lock (_probeLock)
        {
            if (_probed is not null)
            {
                return _probed;
            }

            var usable = new List<VideoEncoderDescriptor>();

            foreach (EncoderCandidate candidate in EncoderCandidate.Enumerate(_logger))
            {
                try
                {
                    using MediaFoundationH264Encoder encoder = MediaFoundationH264Encoder.Start(
                        candidate,
                        new VideoEncoderSettings(640, 360, 30, 1000, PreferHardware: true),
                        _logger);

                    // A configuration being accepted is not proof. Some drivers accept every
                    // media type and then fail on the first sample, so one frame is encoded.
                    byte[] black = new byte[640 * 360 * 3 / 2];
                    black.AsSpan(640 * 360).Fill(128);
                    encoder.Encode(black, 0, keyFrame: true);

                    usable.Add(candidate.Descriptor);
                    _logger.LogInformation(
                        "H.264 encoder available: {Name} ({Kind}).",
                        candidate.Descriptor.Name,
                        candidate.Descriptor.IsHardware ? "hardware" : "software");
                }
                catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException or NotSupportedException)
                {
                    _logger.LogInformation(
                        "H.264 encoder {Name} is registered but cannot be used on this PC: {Reason}",
                        candidate.Descriptor.Name,
                        ex.Message);
                }
            }

            _probed = usable;
            return usable;
        }
    }

    /// <inheritdoc />
    public IVideoEncoder Create(VideoEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<VideoEncoderDescriptor> usable = Probe();
        IEnumerable<VideoEncoderDescriptor> order = settings.PreferHardware
            ? usable.OrderByDescending(static d => d.IsHardware)
            : usable.OrderBy(static d => d.IsHardware);

        List<EncoderCandidate> candidates = EncoderCandidate.Enumerate(_logger);
        Exception? last = null;

        foreach (VideoEncoderDescriptor descriptor in order)
        {
            EncoderCandidate? candidate = candidates.FirstOrDefault(c => c.Descriptor == descriptor);
            if (candidate is null)
            {
                continue;
            }

            try
            {
                return MediaFoundationH264Encoder.Start(candidate, settings, _logger);
            }
            catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException)
            {
                // A hardware encoder can refuse a resolution it accepted at probe time (1080p on a
                // block limited to 4096 MB/frame, a session limit on consumer GPUs). Fall through
                // to the next one rather than failing the stream.
                _logger.LogWarning(
                    "Encoder {Name} refused {Width}x{Height}@{Fps}: {Reason}. Trying the next one.",
                    descriptor.Name,
                    settings.Width,
                    settings.Height,
                    settings.Fps,
                    ex.Message);
                last = ex;
            }
        }

        throw new NotSupportedException("No H.264 encoder could be started on this PC.", last);
    }
}

/// <summary>A registered encoder MFT that may or may not work.</summary>
[SupportedOSPlatform("windows")]
internal sealed class EncoderCandidate
{
    private static readonly Lock StartupLock = new();
    private static bool _started;

    public required VideoEncoderDescriptor Descriptor { get; init; }

    public required bool IsAsync { get; init; }

    /// <summary>Hardware URL or CLSID string, used to find the same MFT again.</summary>
    public required string Key { get; init; }

    public static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (!_started)
            {
                // MFSTARTUP_LITE: no socket-based sources, which this process never needs.
                MediaFactory.MFStartup(useLightVersion: true).CheckError();
                _started = true;
            }
        }
    }

    public static List<EncoderCandidate> Enumerate(ILogger logger)
    {
        EnsureStarted();

        var results = new List<EncoderCandidate>();
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };

        foreach ((EnumFlag flags, bool hardware) in new[]
                 {
                     (EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter, true),
                     (EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter, false),
                 })
        {
            try
            {
                using IMFActivateCollection collection = MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder,
                    (uint)flags,
                    null,
                    output);

                int index = 0;
                foreach (IMFActivate activate in collection)
                {
                    string name = ReadName(activate, hardware, index);
                    results.Add(new EncoderCandidate
                    {
                        Descriptor = new VideoEncoderDescriptor(name, hardware),
                        IsAsync = hardware,
                        Key = FormattableString.Invariant($"{(hardware ? "hw" : "sw")}:{index}:{name}"),
                    });
                    index++;
                }
            }
            catch (SharpGenException ex)
            {
                logger.LogDebug(ex, "Encoder enumeration failed for {Kind}.", hardware ? "hardware" : "software");
            }
        }

        return results;
    }

    /// <summary>Activates this candidate. The caller owns the returned transform.</summary>
    public IMFTransform Activate()
    {
        EnsureStarted();

        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        EnumFlag flags = Descriptor.IsHardware
            ? EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter
            : EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter;

        using IMFActivateCollection collection = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            (uint)flags,
            null,
            output);

        int index = 0;
        foreach (IMFActivate activate in collection)
        {
            string name = ReadName(activate, Descriptor.IsHardware, index);
            string key = FormattableString.Invariant($"{(Descriptor.IsHardware ? "hw" : "sw")}:{index}:{name}");
            index++;

            if (string.Equals(key, Key, StringComparison.Ordinal))
            {
                return activate.ActivateObject<IMFTransform>();
            }
        }

        throw new InvalidOperationException($"The encoder '{Descriptor.Name}' is no longer registered.");
    }

    private static string ReadName(IMFActivate activate, bool hardware, int index)
    {
        // Hardware MFTs name themselves through the hardware URL; the software encoder has a
        // friendly name. Either may be absent, and the fallback still distinguishes candidates.
        string? name = TryGetString(activate, TransformAttributeKeys.MftEnumHardwareUrlAttribute);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = TryGetString(activate, MftFriendlyName);
        }

        return string.IsNullOrWhiteSpace(name)
            ? (hardware ? FormattableString.Invariant($"Hardware H.264 encoder {index + 1}") : "Microsoft H.264 software encoder")
            : name.Trim();
    }

    private static string? TryGetString(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetString(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    /// <summary>MFT_FRIENDLY_NAME_Attribute.</summary>
    private static readonly Guid MftFriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
}

/// <summary>
/// One Media Foundation H.264 encoder, configured for interactive streaming (§9.2).
/// </summary>
/// <remarks>
/// <para><b>Low-latency configuration:</b> constrained baseline profile (no B-frames, no CABAC,
/// decodable by every phone's hardware decoder), CBR rate control, low-latency mode so each input
/// produces one output with no lookahead, and a very long GOP — keyframes are produced on demand
/// in answer to a receiver's PLI rather than on a timer, because a periodic IDR on a LAN is a
/// guaranteed bitrate spike for no benefit.</para>
///
/// <para><b>Sync and async MFTs.</b> The software encoder is synchronous: <c>ProcessInput</c>, then
/// <c>ProcessOutput</c> until it asks for more. Hardware encoders are asynchronous and must be
/// driven by their event queue — input only when they raise <c>METransformNeedInput</c>, output
/// only after <c>METransformHaveOutput</c>. Both are handled here, behind the same synchronous
/// <see cref="Encode"/> call, so the pipeline does not care which one it got.</para>
///
/// <para><b>Colour:</b> input is NV12 in BT.601 limited range, signalled on the input type. That is
/// what WebRTC's Android and iOS renderers assume when converting YUV to RGB, so it is the choice
/// that keeps colours accurate on the phone.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    /// <summary>How long to wait for an async encoder to ask for input or produce output.</summary>
    private const int AsyncEventTimeoutMs = 200;

    private const int MfEventFlagNoWait = 1;

    /// <summary>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.</summary>
    private const int OutputProvidesSamples = 0x100;

    /// <summary>MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.</summary>
    private const int OutputCanProvideSamples = 0x200;

    /// <summary>MF_E_NO_EVENTS_AVAILABLE.</summary>
    private const int NoEventsAvailable = unchecked((int)0xC00D3E80);

    /// <summary>eAVEncH264VProfile_Base. The MS encoder emits it as constrained baseline.</summary>
    private const uint ProfileBaseline = 66;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly CodecApi? _codecApi;
    private readonly ILogger _logger;
    private readonly int _frameBytes;
    private readonly bool _outputProvidesSamples;
    private readonly int _outputBufferSize;
    private readonly int _fps;

    private readonly (IMFSample Sample, IMFMediaBuffer Buffer)[] _inputPool;
    private readonly (IMFSample Sample, IMFMediaBuffer Buffer)? _outputSample;

    private byte[] _parameterSets = [];
    private int _nextInput;
    private bool _timerRaised;
    private int _pendingInputRequests;
    private int _pendingOutputs;
    private bool _disposed;

    private MediaFoundationH264Encoder(
        IMFTransform transform,
        IMFMediaEventGenerator? events,
        CodecApi? codecApi,
        VideoEncoderDescriptor descriptor,
        VideoEncoderSettings settings,
        ILogger logger)
    {
        _transform = transform;
        _events = events;
        _codecApi = codecApi;
        _logger = logger;
        Descriptor = descriptor;
        Width = settings.Width;
        Height = settings.Height;
        _fps = Math.Max(1, settings.Fps);
        _frameBytes = settings.Width * settings.Height * 3 / 2;

        // Raised for the encoder's lifetime, which is the stream's lifetime. The async event queue is
        // polled with 1 ms sleeps, and the pipeline paces frames with timed waits; at the default
        // 15.6 ms tick both would add most of a frame of latency. See NativeMethods.timeBeginPeriod.
        _timerRaised = NativeMethods.timeBeginPeriod(1) == 0;

        OutputStreamInfo info = transform.GetOutputStreamInfo(0);
        _outputProvidesSamples = (info.Flags & (OutputProvidesSamples | OutputCanProvideSamples)) != 0;
        _outputBufferSize = Math.Max(info.Size, _frameBytes);

        _inputPool = [CreateSample(_frameBytes), CreateSample(_frameBytes), CreateSample(_frameBytes)];
        _outputSample = _outputProvidesSamples ? null : CreateSample(_outputBufferSize);

        CaptureParameterSets();
    }

    /// <inheritdoc />
    public VideoEncoderDescriptor Descriptor { get; }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    internal static MediaFoundationH264Encoder Start(
        EncoderCandidate candidate,
        VideoEncoderSettings settings,
        ILogger logger)
    {
        if (settings.Width <= 0 || settings.Height <= 0 || settings.Width % 2 != 0 || settings.Height % 2 != 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Encoder dimensions must be positive and even, not {settings.Width}x{settings.Height}."));
        }

        IMFTransform transform = candidate.Activate();
        IMFMediaEventGenerator? events = null;
        CodecApi? codecApi = null;

        try
        {
            if (candidate.IsAsync)
            {
                // An async MFT refuses every call until it is explicitly unlocked, as a guard against
                // clients that do not know the async protocol.
                IMFAttributes attributes = transform.Attributes;
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
                events = transform.QueryInterface<IMFMediaEventGenerator>();
            }

            codecApi = CodecApi.From(transform.NativePointer);
            ConfigureCodec(codecApi, settings, logger);

            // Encoders require the output type first: the input types they offer depend on it.
            using (IMFMediaType output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
                output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(settings.BitrateKbps * 1000)).CheckError();
                output.Set(MediaTypeAttributeKeys.FrameSize, Pack(settings.Width, settings.Height)).CheckError();
                output.Set(MediaTypeAttributeKeys.FrameRate, Pack(settings.Fps, 1)).CheckError();
                output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
                output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError(); // MFVideoInterlace_Progressive
                output.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileBaseline).CheckError();
                transform.SetOutputType(0, output, 0);
            }

            using (IMFMediaType input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
                input.Set(MediaTypeAttributeKeys.FrameSize, Pack(settings.Width, settings.Height)).CheckError();
                input.Set(MediaTypeAttributeKeys.FrameRate, Pack(settings.Fps, 1)).CheckError();
                input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
                input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
                input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)settings.Width).CheckError();
                input.Set(MediaTypeAttributeKeys.YuvMatrix, 2u).CheckError();          // MFVideoTransferMatrix_BT601
                input.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u).CheckError();  // MFNominalRange_16_235
                transform.SetInputType(0, input, 0);
            }

            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            var encoder = new MediaFoundationH264Encoder(
                transform,
                events,
                codecApi,
                candidate.Descriptor,
                settings,
                logger);

            logger.LogInformation(
                "Started {Encoder} at {Width}x{Height}@{Fps}, {Bitrate} kbit/s.",
                candidate.Descriptor.Name,
                settings.Width,
                settings.Height,
                settings.Fps,
                settings.BitrateKbps);

            return encoder;
        }
        catch
        {
            codecApi?.Dispose();
            events?.Dispose();
            transform.Dispose();
            throw;
        }
    }

    private static void ConfigureCodec(CodecApi? codecApi, VideoEncoderSettings settings, ILogger logger)
    {
        if (codecApi is null)
        {
            logger.LogDebug("The encoder exposes no ICodecAPI; using its defaults.");
            return;
        }

        void Apply(string name, int hr)
        {
            if (hr < 0)
            {
                logger.LogDebug("Encoder ignored {Property} (0x{Hr:X8}).", name, hr);
            }
        }

        Apply("LowLatencyMode", codecApi.SetBool(CodecApiProperties.LowLatencyMode, true));
        Apply("RateControlMode", codecApi.SetUInt32(CodecApiProperties.RateControlMode, CodecApiProperties.RateControlCbr));
        Apply("MeanBitRate", codecApi.SetUInt32(CodecApiProperties.MeanBitRate, (uint)(settings.BitrateKbps * 1000)));
        Apply("BPictureCount", codecApi.SetUInt32(CodecApiProperties.BPictureCount, 0));

        // Speed over quality: at LAN bitrates the quality difference is small, and the encode time
        // is directly on the glass-to-glass latency path.
        Apply("QualityVsSpeed", codecApi.SetUInt32(CodecApiProperties.QualityVsSpeed, 33));

        // Ten minutes of frames between forced keyframes — effectively "only on request". Not zero:
        // some encoders read zero as "choose for me" and pick one second.
        Apply("GopSize", codecApi.SetUInt32(CodecApiProperties.GopSize, (uint)Math.Min(settings.Fps * 600, 65535)));
    }

    /// <inheritdoc />
    public byte[]? Encode(ReadOnlySpan<byte> nv12, long timestamp100ns, bool keyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (nv12.Length < _frameBytes)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Expected {_frameBytes} bytes of NV12, got {nv12.Length}."),
                nameof(nv12));
        }

        if (keyFrame && _codecApi is not null)
        {
            int hr = _codecApi.SetUInt32(CodecApiProperties.ForceKeyFrame, 1);
            if (hr < 0)
            {
                _logger.LogDebug("The encoder refused a keyframe request (0x{Hr:X8}).", hr);
            }
        }

        IMFSample sample = FillInputSample(nv12, timestamp100ns);

        return _events is null
            ? EncodeSync(sample)
            : EncodeAsync(sample);
    }

    /// <inheritdoc />
    public void SetBitrate(int kbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_codecApi is null)
        {
            return;
        }

        int hr = _codecApi.SetUInt32(CodecApiProperties.MeanBitRate, (uint)Math.Max(100, kbps) * 1000);
        if (hr < 0)
        {
            _logger.LogDebug("The encoder refused a bitrate change to {Kbps} kbit/s (0x{Hr:X8}).", kbps, hr);
        }
    }

    /// <summary>
    /// Copies a frame into the next pooled input sample.
    /// </summary>
    /// <remarks>
    /// Samples are pooled rather than allocated per frame: at 1080p60 that would be 180 MB/s of
    /// native allocations, which on a machine short of memory fails outright with E_OUTOFMEMORY.
    /// The pool has several entries because an encoder may keep a reference to an input sample
    /// until it has finished with it; with low-latency mode at most one is ever in flight.
    /// </remarks>
    private unsafe IMFSample FillInputSample(ReadOnlySpan<byte> nv12, long timestamp100ns)
    {
        (IMFSample sample, IMFMediaBuffer buffer) = _inputPool[_nextInput];
        _nextInput = (_nextInput + 1) % _inputPool.Length;

        buffer.Lock(out nint data, out _, out _);
        try
        {
            nv12[.._frameBytes].CopyTo(new Span<byte>((void*)data, _frameBytes));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = _frameBytes;
        sample.SampleTime = timestamp100ns;
        sample.SampleDuration = 10_000_000L / _fps;
        return sample;
    }

    private static (IMFSample Sample, IMFMediaBuffer Buffer) CreateSample(int bytes)
    {
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        return (sample, buffer);
    }

    private byte[]? EncodeSync(IMFSample sample)
    {
        _transform.ProcessInput(0, sample, 0);
        return DrainOutput();
    }

    private byte[]? EncodeAsync(IMFSample sample)
    {
        // Input may only be submitted in answer to a NeedInput event. Wait for one — in steady state
        // the encoder has already raised it after the previous frame's output.
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(AsyncEventTimeoutMs);
        while (_pendingInputRequests == 0)
        {
            if (!PumpEvent(blockUntil: deadline))
            {
                throw new InvalidOperationException("The hardware encoder stopped requesting input.");
            }
        }

        _pendingInputRequests--;
        _transform.ProcessInput(0, sample, 0);

        // Wait briefly for the output of this frame. Low-latency hardware encoders produce it within
        // a few milliseconds; if one is slower, the frame is collected on the next call instead of
        // stalling the pipeline here.
        deadline = DateTime.UtcNow.AddMilliseconds(AsyncEventTimeoutMs);
        while (_pendingOutputs == 0 && PumpEvent(blockUntil: deadline))
        {
        }

        if (_pendingOutputs == 0)
        {
            return null;
        }

        var chunks = new List<byte[]>();
        while (_pendingOutputs > 0)
        {
            _pendingOutputs--;
            byte[]? chunk = ProcessOneOutput();
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }
        }

        // Pick up any NeedInput raised while outputs were being collected.
        while (PumpEvent(blockUntil: null))
        {
        }

        return Combine(chunks);
    }

    /// <summary>Handles one queued encoder event. Returns false if none arrived in time.</summary>
    private bool PumpEvent(DateTime? blockUntil)
    {
        while (true)
        {
            IMFMediaEvent? mediaEvent = null;
            try
            {
                mediaEvent = _events!.GetEvent(MfEventFlagNoWait);
            }
            catch (SharpGenException ex) when (ex.ResultCode.Code == NoEventsAvailable)
            {
                if (blockUntil is null || DateTime.UtcNow >= blockUntil.Value)
                {
                    return false;
                }

                // Polling rather than blocking GetEvent: a blocking call cannot be abandoned, and a
                // wedged driver must not be able to hang the pump thread forever.
                Thread.Sleep(1);
                continue;
            }

            using (mediaEvent)
            {
                switch (mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _pendingInputRequests++;
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        _pendingOutputs++;
                        break;
                    case MediaEventTypes.Error:
                        throw new InvalidOperationException(
                            FormattableString.Invariant($"The hardware encoder reported an error (0x{mediaEvent.Status.Code:X8})."));
                }
            }

            return true;
        }
    }

    private byte[]? DrainOutput()
    {
        var chunks = new List<byte[]>();

        while (true)
        {
            byte[]? chunk = ProcessOneOutput();
            if (chunk is null)
            {
                break;
            }

            chunks.Add(chunk);
        }

        return Combine(chunks);
    }

    /// <summary>
    /// Calls ProcessOutput once. Returns the bytes produced, or null when the encoder needs more
    /// input.
    /// </summary>
    private unsafe byte[]? ProcessOneOutput()
    {
        IMFSample? provided = null;
        if (_outputSample is { } pooled)
        {
            // Reset so the encoder writes from the start of the buffer.
            pooled.Buffer.CurrentLength = 0;
            provided = pooled.Sample;
        }

        var output = new OutputDataBuffer { StreamID = 0, Sample = provided! };

        try
        {
            Result result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);

            if (result == ResultCode.TransformNeedMoreInput)
            {
                return null;
            }

            if (result == ResultCode.TransformStreamChange)
            {
                // The encoder renegotiated its output (typically to attach the sequence header).
                // Accept its first offer and pick the parameter sets up from it.
                using IMFMediaType type = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, type, 0);
                CaptureParameterSets();
                return [];
            }

            result.CheckError();

            IMFSample? produced = output.Sample;
            if (produced is null)
            {
                return [];
            }

            using IMFMediaBuffer contiguous = produced.ConvertToContiguousBuffer();
            contiguous.Lock(out nint data, out _, out int length);
            try
            {
                byte[] bytes = new ReadOnlySpan<byte>((void*)data, length).ToArray();
                return H264AnnexB.EnsureParameterSets(bytes, _parameterSets);
            }
            finally
            {
                contiguous.Unlock();
            }
        }
        finally
        {
            output.Events?.Dispose();

            // An encoder-allocated sample is ours to release. Our pooled sample may come back as a
            // different managed wrapper around the same COM object, so identity is compared by
            // native pointer: releasing the pooled sample here would free it out from under the
            // pool on the next frame.
            IMFSample? returned = output.Sample;
            if (returned is not null &&
                (provided is null || returned.NativePointer != provided.NativePointer))
            {
                returned.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads the SPS and PPS from the output type's sequence header, for encoders that do not
    /// repeat them in-band before every keyframe.
    /// </summary>
    private void CaptureParameterSets()
    {
        try
        {
            using IMFMediaType type = _transform.GetOutputCurrentType(0);
            if (type.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out uint size).Failure || size == 0)
            {
                return;
            }

            _parameterSets = type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch (SharpGenException)
        {
            // No sequence header on this encoder: it emits parameter sets in-band.
        }
    }

    private static byte[]? Combine(List<byte[]> chunks)
    {
        int total = chunks.Sum(static c => c.Length);
        if (total == 0)
        {
            return null;
        }

        if (chunks.Count == 1)
        {
            return chunks[0];
        }

        byte[] combined = new byte[total];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return combined;
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
            // Tearing down; a failed notification changes nothing.
        }

        if (_timerRaised)
        {
            NativeMethods.timeEndPeriod(1);
            _timerRaised = false;
        }

        _codecApi?.Dispose();
        _events?.Dispose();

        if (_events is not null)
        {
            // Async MFTs hold a reference cycle through their event queue until shut down.
            try
            {
                using IMFShutdown shutdown = _transform.QueryInterface<IMFShutdown>();
                shutdown.Shutdown();
            }
            catch (SharpGenException)
            {
            }
        }

        _transform.Dispose();

        foreach ((IMFSample sample, IMFMediaBuffer buffer) in _inputPool)
        {
            sample.Dispose();
            buffer.Dispose();
        }

        if (_outputSample is { } output)
        {
            output.Sample.Dispose();
            output.Buffer.Dispose();
        }
    }
}
