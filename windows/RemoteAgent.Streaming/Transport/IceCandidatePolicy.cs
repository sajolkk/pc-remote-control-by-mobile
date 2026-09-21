using System.Net;
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

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
