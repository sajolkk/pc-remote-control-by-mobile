using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.SystemInfo;

/// <summary>
/// Reads machine facts from the OS (§0).
/// </summary>
/// <remarks>
/// Every value is obtained at runtime. Nothing about the machine — its name, its
/// addresses, its memory, its Windows build — is compiled in, because the same binary
/// has to be correct on an arbitrary PC.
/// <para>
/// Addresses in particular are re-read on every call rather than cached: DHCP renewal,
/// docking, VPN connection and Wi-Fi roaming all change them while the process runs,
/// and a stale address in a QR code is a pairing that silently fails (§6.4).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    private readonly ILogger<WindowsSystemInfoProvider> _logger;
    private readonly Lazy<string> _osVersion;
    private readonly Lazy<long> _totalMemoryMb;

    /// <summary>Creates the provider.</summary>
    public WindowsSystemInfoProvider(ILogger<WindowsSystemInfoProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _osVersion = new Lazy<string>(ReadOsVersion);
        _totalMemoryMb = new Lazy<long>(ReadTotalMemoryMb);
    }

    /// <inheritdoc />
    public string HostName => Environment.MachineName;

    /// <inheritdoc />
    public string OsVersion => _osVersion.Value;

    /// <inheritdoc />
    public string Architecture => RuntimeInformation.OSArchitecture.ToString();

    /// <inheritdoc />
    public int CpuCount => Environment.ProcessorCount;

    /// <inheritdoc />
    public long TotalMemoryMb => _totalMemoryMb.Value;

    /// <inheritdoc />
    public long UptimeSeconds => Environment.TickCount64 / 1000;

    /// <inheritdoc />
    public int? BatteryPercent
    {
        get
        {
            if (!TryGetPowerStatus(out NativeMethods.SystemPowerStatus status))
            {
                return null;
            }

            // 128 in BatteryFlag means no system battery; 255 in the percentage means
            // Windows does not know. Both map to null rather than to a misleading 0.
            const byte noBattery = 128;
            const byte unknown = 255;

            if ((status.BatteryFlag & noBattery) != 0 || status.BatteryLifePercent == unknown)
            {
                return null;
            }

            return Math.Clamp(status.BatteryLifePercent, (byte)0, (byte)100);
        }
    }

    /// <inheritdoc />
    public bool? OnAcPower
    {
        get
        {
            if (!TryGetPowerStatus(out NativeMethods.SystemPowerStatus status))
            {
                return null;
            }

            return status.AcLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetLocalAddresses()
    {
        var addresses = new List<string>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    // 169.254.x.x is a failed-DHCP address that a phone can never reach,
                    // so advertising it only wastes a connection attempt per discovery.
                    byte[] octets = unicast.Address.GetAddressBytes();
                    if (octets[0] == 169 && octets[1] == 254)
                    {
                        continue;
                    }

                    addresses.Add(unicast.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate local addresses.");
        }

        return addresses;
    }

    private bool TryGetPowerStatus(out NativeMethods.SystemPowerStatus status)
    {
        status = default;
        try
        {
            return NativeMethods.GetSystemPowerStatus(out status);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "GetSystemPowerStatus is unavailable.");
            return false;
        }
    }

    /// <summary>
    /// Builds a display string for the Windows version.
    /// </summary>
    /// <remarks>
    /// <c>Environment.OSVersion</c> gives a build number but neither the edition nor
    /// the marketing name, so the registry supplies those. Both are optional: a missing
    /// value degrades the string rather than failing, because this is display-only and
    /// must never be the reason a PC cannot be reached.
    /// </remarks>
    private string ReadOsVersion()
    {
        string fallback = $"Windows {Environment.OSVersion.Version}";

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key is null)
            {
                return fallback;
            }

            string? productName = key.GetValue("ProductName") as string;
            string? displayVersion = key.GetValue("DisplayVersion") as string;
            string? buildNumber = key.GetValue("CurrentBuildNumber") as string;

            // Windows 11 still reports "Windows 10 ..." in ProductName, so the build
            // number is what actually distinguishes them. 22000 is the first Win11 build.
            if (productName is not null &&
                int.TryParse(buildNumber, out int build) &&
                build >= 22000 &&
                productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            var parts = new List<string>(3)
            {
                string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim(),
            };

            if (!string.IsNullOrWhiteSpace(displayVersion))
            {
                parts.Add(displayVersion.Trim());
            }

            string build2 = string.IsNullOrWhiteSpace(buildNumber)
                ? Environment.OSVersion.Version.Build.ToString()
                : buildNumber;

            parts.Add($"(build {build2})");
            return string.Join(' ', parts);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Could not read the Windows product name from the registry.");
            return fallback;
        }
    }

    private long ReadTotalMemoryMb()
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
        };

        if (NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            return (long)(status.TotalPhys / (1024 * 1024));
        }

        _logger.LogDebug("GlobalMemoryStatusEx failed: error {Error}.", Marshal.GetLastWin32Error());
        return 0;
    }
}
