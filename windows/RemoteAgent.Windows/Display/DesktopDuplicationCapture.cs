using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteAgent.Windows.Display;

/// <summary>
/// Opens Desktop Duplication sessions (§9.1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopDuplicationCaptureFactory : IScreenCaptureFactory
{
    private readonly ILogger<DesktopDuplicationCapture> _logger;

    /// <summary>Creates the factory.</summary>
    public DesktopDuplicationCaptureFactory(ILogger<DesktopDuplicationCapture> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IScreenCapture Open(string monitorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);

        List<DxgiOutputEntry> outputs;
        try
        {
            outputs = DxgiOutputs.Enumerate();
        }
        catch (SharpGenException ex)
        {
            throw new ScreenCaptureUnavailableException("Monitors could not be enumerated.", transient: true, ex);
        }

        DxgiOutputEntry? match = outputs.FirstOrDefault(o => string.Equals(o.Info.Id, monitorId, StringComparison.Ordinal));

        try
        {
            if (match is null)
            {
                throw new ScreenCaptureUnavailableException(
                    "That monitor is not connected.",
                    transient: false);
            }

            return DesktopDuplicationCapture.Open(match, _logger);
        }
        finally
        {
            DxgiOutputs.Release(outputs);
        }
    }
}

/// <summary>
/// Captures one monitor with DXGI Desktop Duplication (§9.1).
/// </summary>
/// <remarks>
/// <para><b>The GPU-to-CPU copy is deliberate on this path.</b> §9.2's fast path hands the capture
/// texture straight to a hardware encoder on the same device. That needs the encoder bound to a
/// DXGI device manager and a GPU colour-conversion stage, and it is only a win when a hardware
/// encoder exists. The CPU path works with every encoder, including the software one, and on a
/// 1080p desktop the readback costs one to three milliseconds.</para>
///
/// <para><b>Idle desktops cost nothing.</b> <c>AcquireNextFrame</c> blocks until Windows composes a
/// new frame or the pointer moves, so an unchanging desktop returns
/// <see cref="CaptureStatus.Unchanged"/> and no frame is read, converted or encoded.</para>
///
/// <para><b>Capture is lost, not failed, on the secure desktop.</b> Locking the PC, a UAC prompt or
/// Ctrl+Alt+Del switches to a desktop no ordinary process may read (§12.1). Duplication reports
/// that as access lost, and reopening it fails with access denied until the user returns. Both are
/// reported as transient so the pipeline pauses and retries rather than giving up.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DesktopDuplicationCapture : IScreenCapture
{
    private readonly ILogger _logger;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ModeRotation _rotation;
    private readonly CapturedFrame _frame = new();

    private ID3D11Texture2D? _staging;
    private byte[] _pointerBuffer = new byte[64 * 64 * 4];
    private bool _disposed;

    private DesktopDuplicationCapture(
        MonitorInfo monitor,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication,
        ModeRotation rotation,
        ILogger logger)
    {
        Monitor = monitor;
        _device = device;
        _context = context;
        _duplication = duplication;
        _rotation = rotation;
        _logger = logger;

        _frame.Width = monitor.Width;
        _frame.Height = monitor.Height;
        _frame.Stride = monitor.Width * 4;
        _frame.Pixels = new byte[_frame.Stride * _frame.Height];
    }

    /// <inheritdoc />
    public MonitorInfo Monitor { get; }

    /// <inheritdoc />
    public CapturedFrame Frame => _frame;

    internal static DesktopDuplicationCapture Open(DxgiOutputEntry entry, ILogger logger)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

        // DriverType.Unknown is mandatory when an explicit adapter is passed. The device must live
        // on the adapter that drives the output: duplicating a monitor from another GPU's device
        // fails with DXGI_ERROR_UNSUPPORTED, which is the classic hybrid-laptop failure.
        Result created = D3D11.D3D11CreateDevice(
            entry.Adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (created.Failure || device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();
            throw new ScreenCaptureUnavailableException(
                $"Direct3D 11 is unavailable on this display adapter (0x{created.Code:X8}).",
                transient: false);
        }

        IDXGIOutputDuplication duplication;
        try
        {
            duplication = entry.Output.DuplicateOutput(device);
        }
        catch (SharpGenException ex)
        {
            context.Dispose();
            device.Dispose();
            throw Translate(ex);
        }

        logger.LogInformation(
            "Desktop Duplication opened on {Monitor} ({Width}x{Height}, rotation {Rotation}°).",
            entry.Info.Name,
            entry.Info.Width,
            entry.Info.Height,
            entry.Info.Rotation);

        return new DesktopDuplicationCapture(
            entry.Info,
            device,
            context,
            duplication,
            entry.Output.Description.Rotation,
            logger);
    }

    /// <inheritdoc />
    public CaptureStatus Acquire(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result result = _duplication.AcquireNextFrame(
            (uint)Math.Max(0, timeoutMs),
            out OutduplFrameInfo info,
            out IDXGIResource? resource);

        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return CaptureStatus.Unchanged;
        }

        if (result.Failure)
        {
            resource?.Dispose();

            // ACCESS_LOST covers mode changes, the secure desktop and full-screen exclusive apps;
            // anything else is treated the same way, because the only recovery for any duplication
            // failure is to discard the object and open a new one.
            _logger.LogDebug("Desktop Duplication lost (0x{Code:X8}).", result.Code);
            return CaptureStatus.Lost;
        }

        bool changed = false;

        try
        {
            if (info.LastPresentTime != 0 && resource is not null)
            {
                CopyDesktop(resource);
                _frame.ContentVersion++;
                changed = true;
            }

            if (info.LastMouseUpdateTime != 0)
            {
                bool visible = info.PointerPosition.Visible;
                int x = info.PointerPosition.Position.X;
                int y = info.PointerPosition.Position.Y;

                if (visible != _frame.PointerVisible || x != _frame.PointerX || y != _frame.PointerY)
                {
                    _frame.PointerVisible = visible;
                    _frame.PointerX = x;
                    _frame.PointerY = y;
                    changed = true;
                }
            }

            if (info.PointerShapeBufferSize > 0 && ReadPointerShape(info.PointerShapeBufferSize))
            {
                changed = true;
            }
        }
        catch (SharpGenException ex)
        {
            _logger.LogDebug(ex, "Reading a duplicated frame failed.");
            return CaptureStatus.Lost;
        }
        finally
        {
            resource?.Dispose();

            // Must be released before the next Acquire, and as early as possible: while a frame is
            // held, Windows cannot update the desktop image for any other duplication client.
            _duplication.ReleaseFrame();
        }

        return changed ? CaptureStatus.Updated : CaptureStatus.Unchanged;
    }

    private void CopyDesktop(IDXGIResource resource)
    {
        using ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>();

        if (_staging is null)
        {
            Texture2DDescription source = texture.Description;
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = source.Width,
                Height = source.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = source.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        _context.CopyResource(_staging, texture);

        MappedSubresource mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            Texture2DDescription description = _staging.Description;
            CopyMapped(mapped.DataPointer, (int)mapped.RowPitch, (int)description.Width, (int)description.Height);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    /// <summary>
    /// Copies the mapped surface into the frame buffer, undoing the monitor's rotation.
    /// </summary>
    /// <remarks>
    /// Duplication returns the image in the scan-out orientation, so a portrait monitor arrives
    /// sideways. Rotation is rare and costs a per-pixel loop; the common unrotated case is one
    /// row copy per line.
    /// </remarks>
    private unsafe void CopyMapped(nint source, int sourcePitch, int sourceWidth, int sourceHeight)
    {
        byte[] pixels = _frame.Pixels;
        int stride = _frame.Stride;
        int width = _frame.Width;
        int height = _frame.Height;

        byte* src = (byte*)source;

        fixed (byte* dstBase = pixels)
        {
            if (_rotation is ModeRotation.Identity or ModeRotation.Unspecified)
            {
                int rowBytes = Math.Min(sourceWidth, width) * 4;
                int rows = Math.Min(sourceHeight, height);
                for (int y = 0; y < rows; y++)
                {
                    Buffer.MemoryCopy(src + (y * sourcePitch), dstBase + (y * stride), rowBytes, rowBytes);
                }

                return;
            }

            uint* dst = (uint*)dstBase;
            int dstPitch = stride / 4;

            for (int sy = 0; sy < sourceHeight; sy++)
            {
                uint* row = (uint*)(src + (sy * sourcePitch));
                for (int sx = 0; sx < sourceWidth; sx++)
                {
                    (int dx, int dy) = _rotation switch
                    {
                        ModeRotation.Rotate90 => (width - 1 - sy, sx),
                        ModeRotation.Rotate180 => (width - 1 - sx, height - 1 - sy),
                        _ => (sy, height - 1 - sx), // Rotate270
                    };

                    if ((uint)dx < (uint)width && (uint)dy < (uint)height)
                    {
                        dst[(dy * dstPitch) + dx] = row[sx];
                    }
                }
            }
        }
    }

    private unsafe bool ReadPointerShape(uint requiredSize)
    {
        if (_pointerBuffer.Length < requiredSize)
        {
            _pointerBuffer = new byte[requiredSize];
        }

        OutduplPointerShapeInfo shape;
        Result result;

        fixed (byte* buffer = _pointerBuffer)
        {
            result = _duplication.GetFramePointerShape(
                (uint)_pointerBuffer.Length,
                (nint)buffer,
                out _,
                out shape);
        }

        if (result.Failure)
        {
            return false;
        }

        PointerShapeKind kind = shape.Type switch
        {
            1 => PointerShapeKind.Monochrome, // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME
            2 => PointerShapeKind.Color,      // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR
            _ => PointerShapeKind.MaskedColor,
        };

        int height = (int)shape.Height;
        if (kind == PointerShapeKind.Monochrome)
        {
            // The reported height covers both the AND and XOR masks stacked vertically.
            height /= 2;
        }

        int length = (int)(shape.Pitch * shape.Height);
        _frame.Pointer = new PointerShape
        {
            Kind = kind,
            Width = (int)shape.Width,
            Height = height,
            Pitch = (int)shape.Pitch,
            Data = _pointerBuffer.AsSpan(0, Math.Min(length, _pointerBuffer.Length)).ToArray(),
        };

        // The pointer position DXGI reports is already the image's top-left, not the hotspot, so
        // no hotspot adjustment is needed when drawing.
        return true;
    }

    internal static ScreenCaptureUnavailableException Translate(SharpGenException ex)
    {
        int code = ex.ResultCode.Code;

        return code switch
        {
            // E_ACCESSDENIED: the secure desktop is showing, or this process is not on the
            // interactive desktop at all.
            unchecked((int)0x80070005) => new ScreenCaptureUnavailableException(
                "The desktop cannot be captured while Windows is locked or showing a secure prompt.",
                transient: true,
                ex),

            // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE: too many duplication clients on this output.
            unchecked((int)0x887A0022) => new ScreenCaptureUnavailableException(
                "Another application is already capturing this monitor.",
                transient: true,
                ex),

            // DXGI_ERROR_UNSUPPORTED: typically a hybrid-GPU output owned by another adapter, or a
            // remote session whose display driver does not support duplication.
            unchecked((int)0x887A0004) => new ScreenCaptureUnavailableException(
                "This monitor cannot be captured with Desktop Duplication.",
                transient: false,
                ex),

            // DXGI_ERROR_SESSION_DISCONNECTED: the session is not attached to the console.
            unchecked((int)0x887A0028) => new ScreenCaptureUnavailableException(
                "The Windows session is disconnected from the display.",
                transient: true,
                ex),

            _ => new ScreenCaptureUnavailableException(
                FormattableString.Invariant($"Desktop Duplication could not start (0x{code:X8})."),
                transient: true,
                ex),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _staging?.Dispose();
        _duplication.Dispose();
        _context.ClearState();
        _context.Dispose();
        _device.Dispose();
    }
}
