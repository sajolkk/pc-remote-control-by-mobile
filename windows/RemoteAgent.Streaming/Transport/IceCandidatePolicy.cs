using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteAgent.Streaming.Transport;

/// <summary>
/// Decides which of the phone's ICE candidates the PC will send media to.
/// </summary>
/// <remarks>
/// <para><b>The rule: UDP host candidates on the address that authenticated.</b> The phone's
/// control connection arrived from one address, and that connection is what proved who it is.
/// Media goes to the same address and nowhere else.</para>
///
/// <para>This is defence in depth rather than the primary protection — DTLS with the fingerprint
/// from the authenticated offer already means nobody else can receive the stream. What it prevents
/// is the PC being made to send ICE connectivity checks at arbitrary hosts: without it, a paired
/// phone could list any address on the LAN or the internet as a candidate, and the PC would
/// dutifully send UDP there. It also enforces §9.4's "host candidates only, no external service"
/// on the phone's side of the negotiation: server-reflexive and relay candidates are refused, as
/// are TCP candidates and mDNS <c>.local</c> hostnames, which would need a resolver.</para>
/// </remarks>
public static class IceCandidatePolicy
{
    /// <summary>
    /// Whether one candidate line may be used.
    /// </summary>
    /// <param name="candidate">
    /// The candidate, with or without the <c>a=</c> and <c>candidate:</c> prefixes.
    /// </param>
    /// <param name="allowedPeer">The control connection's peer address.</param>
    /// <param name="reason">Why it was refused, for the debug log.</param>
    public static bool IsAllowed(string? candidate, IPAddress allowedPeer, out string reason)
    {
        ArgumentNullException.ThrowIfNull(allowedPeer);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            // An empty candidate is the end-of-candidates marker. Nothing to use, nothing wrong.
            reason = "end of candidates";
            return false;
        }

        string line = candidate.Trim();
        if (line.StartsWith("a=", StringComparison.Ordinal))
        {
            line = line[2..];
        }

        if (line.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
        {
            line = line["candidate:".Length..];
        }

        // foundation component transport priority address port "typ" type [extensions...]
        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 8 || !string.Equals(fields[6], "typ", StringComparison.OrdinalIgnoreCase))
        {
            reason = "malformed";
            return false;
        }

        if (!string.Equals(fields[2], "udp", StringComparison.OrdinalIgnoreCase))
        {
            reason = "not UDP";
            return false;
        }

        if (!string.Equals(fields[7], "host", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"type {fields[7]} (only host candidates are used)";
            return false;
        }

        if (!IPAddress.TryParse(fields[4], out IPAddress? address))
        {
            reason = "not an IP address (mDNS names are not resolved)";
            return false;
        }

        if (!int.TryParse(fields[5], out int port) || port is < 1 or > 65535)
        {
            reason = "invalid port";
            return false;
        }

        if (!Normalize(address).Equals(Normalize(allowedPeer)))
        {
            reason = "address differs from the authenticated connection";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Removes every candidate line from an SDP that <see cref="IsAllowed"/> refuses.
    /// </summary>
    /// <remarks>
    /// Needed because a phone that waits for ICE gathering before sending its offer puts its
    /// candidates in the SDP itself rather than trickling them, and those bypass
    /// <see cref="IsAllowed"/> unless the SDP is filtered too.
    /// </remarks>
    public static string FilterSdp(string sdp, IPAddress allowedPeer, out int removed)
    {
        ArgumentNullException.ThrowIfNull(sdp);

        var builder = new StringBuilder(sdp.Length);
        removed = 0;

        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase) &&
                !IsAllowed(line, allowedPeer, out _))
            {
                removed++;
                continue;
            }

            builder.Append(line).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Finds the local address this PC would use to reach <paramref name="peer"/>: the address of
    /// whichever interface the routing table picks for it.
    /// </summary>
    /// <remarks>
    /// "Connecting" a UDP socket sends nothing. It only asks the stack to choose a route and bind
    /// the matching local address, which is then read back. For a phone on the LAN that is the LAN
    /// address; for a phone reaching the PC through a VPN it is the PC's address on that VPN.
    /// Returns null when there is no route, or the answer is not a usable unicast address.
    /// </remarks>
    public static IPAddress? ResolveLocalAddressFor(IPAddress peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        IPAddress target = Normalize(peer);

        try
        {
            using var probe = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(target, 9);

            if (probe.LocalEndPoint is not IPEndPoint local)
            {
                return null;
            }

            IPAddress address = Normalize(local.Address);

            return IPAddress.IsLoopback(address) ||
                   address.Equals(IPAddress.Any) ||
                   address.Equals(IPAddress.IPv6Any) ||
                   address.IsIPv6LinkLocal
                ? null
                : address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure the PC's SDP offers a host candidate on <paramref name="localAddress"/>, adding
    /// one that reuses an existing candidate's port when it does not.
    /// </summary>
    /// <remarks>
    /// <para>The WebRTC stack gathers host candidates from one interface only — the one that leads
    /// to the internet — so as not to list every address the PC has. That is the LAN adapter, and
    /// it is the right answer for a phone on the LAN. For a phone that reached the PC over a VPN
    /// (Tailscale, WireGuard, ZeroTier) it is the wrong one: the phone is offered a LAN address it
    /// has no route to, and the stream then depends entirely on the PC's own outbound checks
    /// getting through first.</para>
    ///
    /// <para>The media socket is bound to every interface, so it is already reachable on the VPN
    /// address; only the advertisement is missing. This adds it. The address added is the one the
    /// control connection itself arrived on — the phone demonstrably knows it already — so nothing
    /// about the PC's network is disclosed that the phone was not using a moment ago.</para>
    ///
    /// <para>Still host candidates only, still no external server (§9.4), and the rule that media
    /// flows only to the authenticated peer address is untouched: that is enforced on the phone's
    /// candidates and on the pair ICE selects, not here.</para>
    /// </remarks>
    /// <param name="sdp">The PC's answer.</param>
    /// <param name="localAddress">The local address the authenticated peer is reached from.</param>
    /// <param name="added">Whether a candidate line was inserted.</param>
    public static string EnsureHostCandidate(string sdp, IPAddress localAddress, out bool added)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        ArgumentNullException.ThrowIfNull(localAddress);

        added = false;
        IPAddress wanted = Normalize(localAddress);

        string[] lines = sdp.Split('\n');
        int template = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8 ||
                !string.Equals(fields[2], "udp", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(fields[6], "typ", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(fields[7], "host", StringComparison.OrdinalIgnoreCase) ||
                !IPAddress.TryParse(fields[4], out IPAddress? address))
            {
                continue;
            }

            if (Normalize(address).Equals(wanted))
            {
                // Already offered. This is the ordinary LAN case.
                return sdp;
            }

            // Prefer a template of the same address family, so the port copied belongs to a socket
            // that can receive on the address being added.
            if (template < 0 ||
                (address.AddressFamily == wanted.AddressFamily &&
                 !SameFamilyAsTemplate(lines[template], wanted)))
            {
                template = i;
            }
        }

        if (template < 0)
        {
            // No host candidate to copy a port from. Nothing can be added that would be true.
            return sdp;
        }

        string source = lines[template].TrimEnd('\r');
        string[] parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // foundation component transport priority address port "typ" "host" [extensions...]
        // A new foundation, because a foundation identifies a candidate's base address; a slightly
        // lower priority, because priorities must differ within a component.
        parts[0] = "a=candidate:" + (uint)HashCode.Combine(parts[0], wanted.ToString());
        parts[3] = uint.TryParse(parts[3], out uint priority) && priority > 256
            ? (priority - 256).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : parts[3];
        parts[4] = wanted.ToString();

        string extra = string.Join(' ', parts);
        bool carriageReturn = lines[template].EndsWith('\r');

        var result = new List<string>(lines.Length + 1);
        for (int i = 0; i < lines.Length; i++)
        {
            result.Add(lines[i]);
            if (i == template)
            {
                result.Add(carriageReturn ? extra + '\r' : extra);
            }
        }

        added = true;
        return string.Join('\n', result);
    }

    private static bool SameFamilyAsTemplate(string templateLine, IPAddress wanted)
    {
        string[] fields = templateLine.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 4 &&
               IPAddress.TryParse(fields[4], out IPAddress? address) &&
               address.AddressFamily == wanted.AddressFamily;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
