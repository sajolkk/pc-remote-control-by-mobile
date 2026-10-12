using System.Net;
using RemoteAgent.Streaming.Transport;
using Xunit;

namespace RemoteAgent.Streaming.Tests;

/// <summary>
/// Media negotiation when the phone reaches the PC through a VPN overlay rather than the LAN.
/// </summary>
/// <remarks>
/// <para>The supported way to use PC-Remote from outside the home network is a VPN — Tailscale,
/// WireGuard, ZeroTier — on which the PC has a second address, typically in 100.64.0.0/10. The
/// control channel needs nothing special for that. Media does: the WebRTC stack offers host
/// candidates for the internet-facing interface only, which is the LAN adapter, an address the
/// remote phone cannot route to.</para>
///
/// <para>These tests cover the two halves of the fix as pure functions: the phone's VPN candidate
/// is accepted when the VPN address is the one that authenticated, and the PC's answer gains a
/// candidate on its own VPN address. No socket is opened to anything but a local route lookup.</para>
/// </remarks>
public sealed class VpnCandidateTests
{
    private static readonly IPAddress PhoneOnVpn = IPAddress.Parse("100.101.102.103");
    private static readonly IPAddress PcOnVpn = IPAddress.Parse("100.64.0.7");

    private const string LanAnswer =
        "v=0\r\n" +
        "o=- 1 0 IN IP4 127.0.0.1\r\n" +
        "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
        "a=ice-ufrag:ABCD\r\n" +
        "a=candidate:1390596646 1 udp 2113937663 192.168.1.10 47810 typ host generation 0\r\n" +
        "a=rtpmap:96 H264/90000\r\n";

    // --- The phone's side: its VPN address is an ordinary authenticated address ---

    [Fact]
    public void ThePhonesVpnCandidateIsAcceptedWhenItConnectedOverTheVpn()
    {
        Assert.True(
            IceCandidatePolicy.IsAllowed(
                "candidate:7 1 udp 2122194687 100.101.102.103 51000 typ host generation 0",
                PhoneOnVpn,
                out string reason),
            reason);
    }

    [Fact]
    public void ThePhonesOtherInterfacesAreStillRefusedOverTheVpn()
    {
        // The phone lists every interface it has. Its mobile-data and home-Wi-Fi addresses are not
        // where the authenticated connection came from, and media is not sent to them.
        string offer = string.Join("\r\n",
            "v=0",
            "m=video 9 UDP/TLS/RTP/SAVPF 96",
            "a=candidate:1 1 udp 2122260223 10.33.7.2 50000 typ host",
            "a=candidate:2 1 udp 2122194687 100.101.102.103 50001 typ host",
            "a=candidate:3 1 udp 2122129151 192.168.43.1 50002 typ host",
            "a=candidate:4 1 udp 1686052607 203.0.113.7 50003 typ srflx raddr 10.33.7.2 rport 50000",
            string.Empty);

        string filtered = IceCandidatePolicy.FilterSdp(offer, PhoneOnVpn, out int removed);

        Assert.Equal(3, removed);
        Assert.Contains("100.101.102.103 50001 typ host", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("10.33.7.2", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.43.1", filtered, StringComparison.Ordinal);
    }

    // --- The PC's side: the answer offers the address the phone can actually reach ---

    [Fact]
    public void TheAnswerGainsAHostCandidateOnThePcsVpnAddress()
    {
        string answer = IceCandidatePolicy.EnsureHostCandidate(LanAnswer, PcOnVpn, out bool added);

        Assert.True(added);

        string[] candidates = [.. answer.Split("\r\n").Where(line => line.StartsWith("a=candidate:", StringComparison.Ordinal))];
        Assert.Equal(2, candidates.Length);

        // The LAN candidate is untouched and still first.
        Assert.Equal("a=candidate:1390596646 1 udp 2113937663 192.168.1.10 47810 typ host generation 0", candidates[0]);

        // The new one reuses the same socket — same port, same transport, still a host candidate —
        // under a different foundation and a lower priority.
        string[] fields = candidates[1].Split(' ');
        Assert.Equal("1", fields[1]);
        Assert.Equal("udp", fields[2]);
        Assert.True(uint.Parse(fields[3]) < 2113937663);
        Assert.Equal("100.64.0.7", fields[4]);
        Assert.Equal("47810", fields[5]);
        Assert.Equal(["typ", "host", "generation", "0"], fields[6..]);
        Assert.NotEqual("a=candidate:1390596646", fields[0]);
        Assert.Matches(@"^a=candidate:\d+$", fields[0]);

        // Nothing else moved, and the line endings are intact.
        Assert.Equal(LanAnswer.Split("\r\n").Length + 1, answer.Split("\r\n").Length);
        Assert.DoesNotContain("\n\n", answer.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("a=rtpmap:96 H264/90000\r\n", answer, StringComparison.Ordinal);
        Assert.Contains("a=ice-ufrag:ABCD\r\n", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAddedCandidateIsOneThePhonesStackWouldAccept()
    {
        // Parsed by the same rules this PC applies to the phone's candidates: well-formed, UDP,
        // host, numeric address, valid port.
        string answer = IceCandidatePolicy.EnsureHostCandidate(LanAnswer, PcOnVpn, out _);
        string added = answer.Split("\r\n").Single(line => line.Contains("100.64.0.7", StringComparison.Ordinal));

        Assert.True(IceCandidatePolicy.IsAllowed(added, PcOnVpn, out string reason), reason);
    }

    [Fact]
    public void OnTheLanNothingIsAdded()
    {
        // The route to a LAN phone is the LAN adapter, whose address is already offered.
        string answer = IceCandidatePolicy.EnsureHostCandidate(LanAnswer, IPAddress.Parse("192.168.1.10"), out bool added);

        Assert.False(added);
        Assert.Equal(LanAnswer, answer);

        // The same address in its IPv4-mapped form is still the same address.
        IceCandidatePolicy.EnsureHostCandidate(LanAnswer, IPAddress.Parse("::ffff:192.168.1.10"), out bool mapped);
        Assert.False(mapped);
    }

    [Fact]
    public void AddingTwiceDoesNotDuplicate()
    {
        string once = IceCandidatePolicy.EnsureHostCandidate(LanAnswer, PcOnVpn, out _);
        string twice = IceCandidatePolicy.EnsureHostCandidate(once, PcOnVpn, out bool addedAgain);

        Assert.False(addedAgain);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void AnAnswerWithNoHostCandidateIsLeftAlone()
    {
        // No port to borrow, so nothing true could be added.
        const string none = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\n";

        Assert.Equal(none, IceCandidatePolicy.EnsureHostCandidate(none, PcOnVpn, out bool added));
        Assert.False(added);
    }

    [Fact]
    public void ThePortIsBorrowedFromACandidateOfTheSameAddressFamily()
    {
        const string dual =
            "v=0\r\n" +
            "a=candidate:1 1 udp 2113937663 fe80::1 40000 typ host generation 0\r\n" +
            "a=candidate:2 1 udp 2113937151 192.168.1.10 47811 typ host generation 0\r\n";

        string answer = IceCandidatePolicy.EnsureHostCandidate(dual, PcOnVpn, out bool added);

        Assert.True(added);
        string line = answer.Split("\r\n").Single(l => l.Contains("100.64.0.7", StringComparison.Ordinal));
        Assert.Contains(" 100.64.0.7 47811 typ host", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouteLookupNeverReturnsAnUnusableAddress()
    {
        // Whatever this machine's routing table says for an address on the VPN range — a real
        // interface address, or nothing at all on a machine with no route — it is never loopback,
        // never the wildcard, and asking sends no packet.
        IPAddress? local = IceCandidatePolicy.ResolveLocalAddressFor(PhoneOnVpn);

        if (local is not null)
        {
            Assert.False(IPAddress.IsLoopback(local));
            Assert.NotEqual(IPAddress.Any, local);
            Assert.NotEqual(IPAddress.IPv6Any, local);
        }

        // A loopback peer is reached from loopback, which is not an address to advertise.
        Assert.Null(IceCandidatePolicy.ResolveLocalAddressFor(IPAddress.Loopback));
    }
}
