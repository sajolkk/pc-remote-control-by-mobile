using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Interop;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace RemoteAgent.Windows.Display;

/// <summary>
/// Enumerates monitors through DXGI, which is also what captures them (§9.6).
/// </summary>
/// <remarks>
/// <para>Enumerating through the same API that captures means every monitor listed is one that
/// Desktop Duplication can actually open. GDI's <c>EnumDisplayMonitors</c> would list monitors
/// driven by adapters DXGI cannot duplicate — on some hybrid-GPU laptops, for instance — and the
/// client would offer a monitor that always fails.</para>
///
/// <para>Monitor ids are a hash of the adapter LUID and the output's device name. They are stable
/// while the topology is unchanged, reveal nothing about the hardware, and are deliberately
/// meaningless on any other PC.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DxgiMonitorProvider : IMonitorProvider
{
    private readonly ILogger<DxgiMonitorProvider> _logger;

    /// <summary>Creates the provider.</summary>
    public DxgiMonitorProvider(ILogger<DxgiMonitorProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            List<DxgiOutputEntry> outputs = DxgiOutputs.Enumerate();
            try
            {
                return outputs.Select(static o => o.Info).ToArray();
            }
            finally
            {
                DxgiOutputs.Release(outputs);
            }
        }
        catch (SharpGenException ex)
        {
            _logger.LogWarning(ex, "Monitors could not be enumerated through DXGI.");
            return [];
        }
    }
}

/// <summary>One DXGI output with the adapter that drives it.</summary>
internal sealed class DxgiOutputEntry
{
    public required IDXGIAdapter1 Adapter { get; init; }

    public required IDXGIOutput1 Output { get; init; }

    public required MonitorInfo Info { get; init; }
}

/// <summary>
/// Shared enumeration for the monitor provider and the capture factory, so both agree on ids.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DxgiOutputs
{
    /// <summary>
    /// Every output attached to the desktop, primary first. The caller owns the COM objects and
    /// must pass the list to <see cref="Release"/>.
    /// </summary>
    public static List<DxgiOutputEntry> Enumerate()
    {
        var entries = new List<DxgiOutputEntry>();

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            Result result = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter);
            if (result.Failure || adapter is null)
            {
                break;
            }

            AdapterDescription1 adapterDescription = adapter.Description1;
            bool keepAdapter = false;

            // The Basic Render Driver is a software adapter with no outputs worth listing; skipping
            // it explicitly keeps a headless VM from reporting a phantom monitor.
            if ((adapterDescription.Flags & AdapterFlags.Software) == 0)
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    Result outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput? output);
                    if (outputResult.Failure || output is null)
                    {
                        break;
                    }

                    OutputDescription description = output.Description;
                    if (!description.AttachedToDesktop)
                    {
                        output.Dispose();
                        continue;
                    }

                    IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();

                    entries.Add(new DxgiOutputEntry
                    {
                        Adapter = adapter,
                        Output = output1,
                        Info = Describe(adapterDescription, description),
                    });

                    keepAdapter = true;
                }
            }

            if (!keepAdapter)
            {
                adapter.Dispose();
            }
        }

        // Primary first, then left-to-right, which is the order a user would number them in.
        entries.Sort(static (a, b) =>
        {
            int primary = b.Info.Primary.CompareTo(a.Info.Primary);
            if (primary != 0)
            {
                return primary;
            }

            int x = a.Info.X.CompareTo(b.Info.X);
            return x != 0 ? x : a.Info.Y.CompareTo(b.Info.Y);
        });

        return entries;
    }

    /// <summary>Releases the COM objects returned by <see cref="Enumerate"/>.</summary>
    public static void Release(IEnumerable<DxgiOutputEntry> entries)
    {
        var adapters = new HashSet<IDXGIAdapter1>(ReferenceEqualityComparer.Instance);

        foreach (DxgiOutputEntry entry in entries)
        {
            entry.Output.Dispose();
            adapters.Add(entry.Adapter);
        }

        foreach (IDXGIAdapter1 adapter in adapters)
        {
            adapter.Dispose();
        }
    }

    private static MonitorInfo Describe(AdapterDescription1 adapter, OutputDescription output)
    {
        int left = output.DesktopCoordinates.Left;
        int top = output.DesktopCoordinates.Top;
        int width = output.DesktopCoordinates.Right - left;
        int height = output.DesktopCoordinates.Bottom - top;

        // The primary monitor is the one whose desktop origin is (0,0): that is the definition
        // Windows itself uses for the virtual-desktop coordinate system.
        bool primary = left == 0 && top == 0;

        double scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(output.Monitor, NativeMethods.MdtEffectiveDpi, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            scale = Math.Round(dpiX / 96.0, 2);
        }

        int rotation = output.Rotation switch
        {
            ModeRotation.Rotate90 => 90,
            ModeRotation.Rotate180 => 180,
            ModeRotation.Rotate270 => 270,
            _ => 0,
        };

        string deviceName = output.DeviceName ?? string.Empty;

        return new MonitorInfo
        {
            Id = MakeId(adapter.Luid, deviceName),
            Adapter = adapter.Description ?? string.Empty,
            DeviceName = deviceName,
            Name = FriendlyName(deviceName, width, height, primary),
            X = left,
            Y = top,
            Width = width,
            Height = height,
            Scale = scale,
            RefreshHz = deviceName.Length == 0 ? 0 : NativeMethods.GetRefreshRate(deviceName),
            Primary = primary,
            Rotation = rotation,
        };
    }

    private static string MakeId(Vortice.Luid luid, string deviceName)
    {
        string material = FormattableString.Invariant($"{luid.HighPart:X8}{luid.LowPart:X8}|{deviceName}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "mon-" + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    private static string FriendlyName(string deviceName, int width, int height, bool primary)
    {
        // \\.\DISPLAY2 → "Display 2". The number is what Windows' own Display Settings shows, so it
        // matches what the user sees on the PC.
        const string prefix = @"\\.\DISPLAY";
        string label = deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "Display " + deviceName[prefix.Length..]
            : "Display";

        return FormattableString.Invariant($"{label} ({width}×{height}{(primary ? ", primary" : string.Empty)})");
    }
}
