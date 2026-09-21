using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Power;

namespace RemoteAgent.Windows.Network;

/// <summary>
/// Reports whether Wake-on-LAN will actually work, and what the phone needs to send.
/// </summary>
/// <remarks>
/// <para>The PC plays no part in being woken — it is asleep. The magic packet comes
/// from the phone. So this command's entire job is to hand the phone the MAC and
/// broadcast addresses, and to be <em>honest about the caveats</em>.</para>
///
/// <para>That honesty is the point. Wake-on-LAN fails far more often than users expect,
/// and each cause is invisible from the phone:</para>
/// <list type="bullet">
/// <item><b>Fast Startup.</b> With it enabled, "shut down" is really a hybrid
/// hibernate, and most NICs will not wake from it. Sleep works; shutdown usually does
/// not.</item>
/// <item><b>Wi-Fi.</b> Wake-on-Wireless-LAN is rare and usually disabled. A laptop on
/// Wi-Fi almost never wakes.</item>
/// <item><b>Adapter settings.</b> "Wake on Magic Packet" and "Allow this device to wake
/// the computer" are separate switches in Device Manager, both off on some drivers by
/// default, and neither is readable without per-vendor registry spelunking.</item>
/// </list>
/// <para>Rather than claim more certainty than is available, the result reports
/// capability conservatively and includes a plain-language note explaining what to
/// check. A button that silently does nothing is worse than a button that says why it
/// might not (§12.2).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WakeOnLanInfoProvider : IWakeOnLanInfoProvider
{
    private const int MagicPacketPort = 9;

    private readonly WindowsPowerController _power;
    private readonly ILogger<WakeOnLanInfoProvider> _logger;

    /// <summary>Creates the provider.</summary>
    public WakeOnLanInfoProvider(WindowsPowerController power, ILogger<WakeOnLanInfoProvider> logger)
    {
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<WolInfoResult> GetInfoAsync(CancellationToken cancellationToken)
    {
        var macs = new List<string>();
        var broadcasts = new HashSet<string>(StringComparer.Ordinal);
        bool anyWired = false;
        bool anyWireless = false;

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                // Only interfaces with a real IPv4 address are useful: a MAC on an
                // interface with no subnet gives the phone nowhere to broadcast to.
                IPInterfaceProperties properties = nic.GetIPProperties();
                bool hasIpv4 = false;

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    byte[] octets = unicast.Address.GetAddressBytes();
                    if (octets[0] == 169 && octets[1] == 254)
                    {
                        continue;
                    }

                    hasIpv4 = true;

                    string? broadcast = TryComputeBroadcast(unicast);
                    if (broadcast is not null)
                    {
                        broadcasts.Add(broadcast);
                    }
                }

                if (!hasIpv4)
                {
                    continue;
                }

                string mac = FormatMac(nic.GetPhysicalAddress());
                if (mac.Length == 0)
                {
                    continue;
                }

                macs.Add(mac);

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    anyWireless = true;
                }
                else
                {
                    anyWired = true;
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate adapters for Wake-on-LAN information.");
        }

        // The limited broadcast address always reaches the local segment, so it is
        // included as a fallback even when no subnet broadcast could be computed.
        broadcasts.Add(IPAddress.Broadcast.ToString());

        bool fastStartup = _power.IsFastStartupEnabled();
        string note = BuildNote(anyWired, anyWireless, fastStartup, macs.Count);

        return Task.FromResult(new WolInfoResult
        {
            // Reported as supported only when there is a wired adapter to target:
            // claiming support for a Wi-Fi-only machine would be wrong far more often
            // than right.
            Supported = macs.Count > 0 && anyWired,
            MacAddresses = macs.ToArray(),
            BroadcastAddresses = broadcasts.ToArray(),
            Port = MagicPacketPort,
            FastStartupEnabled = fastStartup,
            Note = note,
        });
    }

    private static string BuildNote(bool anyWired, bool anyWireless, bool fastStartup, int macCount)
    {
        var notes = new List<string>(3);

        if (macCount == 0)
        {
            return "No network adapter with a usable address was found, so Wake-on-LAN is unavailable.";
        }

        if (!anyWired && anyWireless)
        {
            notes.Add(
                "This PC is connected over Wi-Fi only. Wake-on-Wireless-LAN is rarely supported, " +
                "so waking will probably not work.");
        }

        if (fastStartup)
        {
            notes.Add(
                "Fast Startup is enabled, so a full shutdown usually cannot be woken. " +
                "Use Sleep instead, or turn Fast Startup off in Windows power settings.");
        }

        notes.Add(
            "Waking also requires 'Wake on Magic Packet' and 'Allow this device to wake the computer' " +
            "to be enabled for the network adapter in Device Manager.");

        return string.Join(" ", notes);
    }

    /// <summary>
    /// Derives the directed broadcast address for an interface from its address and
    /// prefix length, so the phone can target this subnet specifically rather than
    /// relying on limited broadcast, which some access points drop.
    /// </summary>
    private string? TryComputeBroadcast(UnicastIPAddressInformation unicast)
    {
        try
        {
            byte[] address = unicast.Address.GetAddressBytes();
            int prefixLength = unicast.PrefixLength;

            if (prefixLength is <= 0 or > 32)
            {
                return null;
            }

            uint mask = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
            uint host = ((uint)address[0] << 24) | ((uint)address[1] << 16) | ((uint)address[2] << 8) | address[3];
            uint broadcast = host | ~mask;

            return new IPAddress(
            [
                (byte)(broadcast >> 24),
                (byte)(broadcast >> 16),
                (byte)(broadcast >> 8),
                (byte)broadcast,
            ]).ToString();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
        {
            // PrefixLength is not available on every platform or adapter type.
            _logger.LogDebug(ex, "Could not compute a broadcast address for an adapter.");
            return null;
        }
    }

    private static string FormatMac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 6
            ? string.Join(':', bytes.Select(static b => b.ToString("X2")))
            : string.Empty;
    }
}
