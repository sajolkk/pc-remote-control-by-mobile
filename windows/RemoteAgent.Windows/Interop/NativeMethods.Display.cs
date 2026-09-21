using System.Runtime.InteropServices;

namespace RemoteAgent.Windows.Interop;

/// <summary>
/// Monitor metadata that DXGI does not report: DPI and refresh rate.
/// </summary>
internal static partial class NativeMethods
{
    // ---------------------------------------------------------------------
    // shcore — per-monitor DPI
    // ---------------------------------------------------------------------

    /// <summary>MDT_EFFECTIVE_DPI: the DPI Windows scales UI to, which is what users mean by "scale".</summary>
    internal const int MdtEffectiveDpi = 0;

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---------------------------------------------------------------------
    // user32 — current display mode
    // ---------------------------------------------------------------------

    internal const int EnumCurrentSettings = -1;

    /// <summary>
    /// sizeof(DEVMODEW). The structure is passed as a raw buffer because only two fields are read,
    /// and declaring its nested unions in full would be sixty lines for two integers.
    /// </summary>
    internal const int DevModeSize = 220;

    /// <summary>Offset of <c>dmSize</c>, which must be set before the call.</summary>
    internal const int DevModeSizeOffset = 68;

    /// <summary>Offset of <c>dmDisplayFrequency</c>.</summary>
    internal const int DevModeDisplayFrequencyOffset = 184;

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumDisplaySettings(string deviceName, int modeNumber, byte* devMode);

    /// <summary>
    /// Reads the current refresh rate of a display, or 0 when Windows reports none.
    /// </summary>
    internal static unsafe int GetRefreshRate(string deviceName)
    {
        byte* devMode = stackalloc byte[DevModeSize];
        new Span<byte>(devMode, DevModeSize).Clear();
        *(ushort*)(devMode + DevModeSizeOffset) = DevModeSize;

        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, devMode))
        {
            return 0;
        }

        int hz = *(int*)(devMode + DevModeDisplayFrequencyOffset);

        // 0 and 1 both mean "the hardware default", which tells the client nothing useful.
        return hz > 1 ? hz : 0;
    }
}

/// <summary>Multimedia timer resolution.</summary>
internal static partial class NativeMethods
{
    /// <remarks>
    /// Without this, <c>Thread.Sleep(1)</c> sleeps for a whole scheduler tick — 15.6 ms by default —
    /// which on the encode path is a full frame of added latency at 60 fps. Since Windows 10 2004 the
    /// request only affects the calling process, so raising it while a stream runs costs the rest of
    /// the system nothing.
    /// </remarks>
    [LibraryImport("winmm.dll")]
    internal static partial uint timeBeginPeriod(uint periodMs);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeEndPeriod(uint periodMs);
}
