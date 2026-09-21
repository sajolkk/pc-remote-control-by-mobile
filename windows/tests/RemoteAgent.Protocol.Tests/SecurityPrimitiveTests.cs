using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Validation;
using RemoteAgent.Protocol;
using RemoteAgent.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for the replay guard.
/// </summary>
/// <remarks>
/// The guard exists to make a duplicated or skipped request a loud failure. The cases that matter
/// are the ones TLS alone would not catch: an application-layer replay of the same message, and a
/// timestamp far enough off that a captured request could be resent later.
/// </remarks>
public sealed class ReplayGuardTests
{
    private const long Now = 1_758_300_000_000;

    [Fact]
    public void AcceptsAStrictlyIncreasingSequence()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);

        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(1, Now, Now));
        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(2, Now, Now));
        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(3, Now, Now));
    }

    [Fact]
    public void AllowsTheFirstRequestToStartAtAnyPositiveValue()
    {
        // A client may keep a counter across reconnects, so the opening value is not constrained —
        // only the progression within this connection is.
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);

        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(5000, Now, Now));
        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(5001, Now, Now));
    }

    [Fact]
    public void RejectsARepeatedSequenceNumber()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);
        guard.Evaluate(1, Now, Now);

        Assert.Equal(ReplayVerdict.SequenceRepeated, guard.Evaluate(1, Now, Now));
    }

    [Fact]
    public void RejectsASequenceThatGoesBackwards()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);
        guard.Evaluate(10, Now, Now);

        Assert.Equal(ReplayVerdict.SequenceRepeated, guard.Evaluate(9, Now, Now));
    }

    [Fact]
    public void RejectsAGapInTheSequence()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);
        guard.Evaluate(1, Now, Now);

        Assert.Equal(ReplayVerdict.SequenceGap, guard.Evaluate(3, Now, Now));
    }

    [Fact]
    public void DoesNotAdvanceStateOnARejectedRequest()
    {
        // A rejected request must not move the counter, or an attacker could desynchronise the
        // legitimate client by injecting one bad frame.
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);
        guard.Evaluate(1, Now, Now);
        guard.Evaluate(99, Now, Now);

        Assert.Equal(1, guard.LastSequence);
        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(2, Now, Now));
    }

    [Fact]
    public void RejectsATimestampTooFarInThePast()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);

        Assert.Equal(ReplayVerdict.TimestampOutOfWindow, guard.Evaluate(1, Now - 60_000, Now));
    }

    [Fact]
    public void RejectsATimestampTooFarInTheFuture()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);

        Assert.Equal(ReplayVerdict.TimestampOutOfWindow, guard.Evaluate(1, Now + 60_000, Now));
    }

    [Fact]
    public void AcceptsClockSkewInsideTheWindow()
    {
        var guard = new ReplayGuard(maxClockSkewSeconds: 30);

        // Phones do drift. The window has to tolerate normal skew or legitimate clients break.
        Assert.Equal(ReplayVerdict.Accepted, guard.Evaluate(1, Now - 20_000, Now));
    }
}

/// <summary>
/// Tests for URL validation.
/// </summary>
/// <remarks>
/// A URL is the one free-form string this system hands to the Windows shell, so this is the highest
/// value input test in the project. The rejected cases are deliberately the dangerous ones: schemes
/// that reach local files or protocol handlers, embedded credentials, and control characters.
/// </remarks>
public sealed class UrlValidatorTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path?query=1#frag")]
    [InlineData("https://192.168.1.10:8080/admin")]
    [InlineData("  https://example.com  ")]
    public void AcceptsHttpAndHttps(string url)
    {
        Assert.True(UrlValidator.TryValidate(url, out string normalized, out string? error));
        Assert.Null(error);
        Assert.StartsWith("http", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.com")]
    [InlineData("ms-settings:privacy")]
    [InlineData("shell:AppsFolder")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    public void RejectsEverySchemeButHttpAndHttps(string url)
    {
        Assert.False(UrlValidator.TryValidate(url, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsEmbeddedCredentials()
    {
        // Both a phishing vector and a way to smuggle characters past a careless parser.
        Assert.False(UrlValidator.TryValidate("https://user:pass@example.com", out _, out string? error));
        Assert.Contains("credentials", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsControlCharacters()
    {
        Assert.False(UrlValidator.TryValidate("https://example.com/\r\nHost: evil", out _, out string? error));
        Assert.Contains("control characters", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnOverlongUrl()
    {
        string url = "https://example.com/" + new string('a', UrlValidator.MaxLength);

        Assert.False(UrlValidator.TryValidate(url, out _, out string? error));
        Assert.Contains("limit", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("//example.com")]
    [InlineData("/relative/path")]
    public void RejectsEmptyAndRelativeInput(string? url)
    {
        Assert.False(UrlValidator.TryValidate(url, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ReturnsTheParsedFormRatherThanTheCallersString()
    {
        // What gets launched must be something we actually parsed, not the raw input.
        Assert.True(UrlValidator.TryValidate("https://EXAMPLE.com", out string normalized, out _));
        Assert.Equal("https://example.com/", normalized);
    }
}

/// <summary>Tests for session token issuance and binding.</summary>
/// <remarks>
/// The binding is the whole point: a token is useless on a connection other than the one it was
/// issued for, which is what makes a captured token not a credential (§7.2).
/// </remarks>
public sealed class SessionTokenServiceTests
{
    private static SessionTokenService Create(TestClock clock, int ttlSeconds = 600) =>
        new(clock, NullLogger<SessionTokenService>.Instance, ttlSeconds);

    [Fact]
    public void ValidatesATokenOnTheConnectionItWasIssuedFor()
    {
        var clock = new TestClock();
        SessionTokenService service = Create(clock);

        SessionToken token = service.Issue("device-1", "conn-1", PermissionSet.Default);

        Assert.True(service.TryValidate(token.Value, "conn-1", out SessionToken? found, out string? failure));
        Assert.Null(failure);
        Assert.Equal("device-1", found!.DeviceId);
    }

    [Fact]
    public void RejectsATokenPresentedOnADifferentConnection()
    {
        var clock = new TestClock();
        SessionTokenService service = Create(clock);

        SessionToken token = service.Issue("device-1", "conn-1", PermissionSet.Default);

        Assert.False(service.TryValidate(token.Value, "conn-2", out _, out string? failure));
        Assert.Contains("different connection", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnExpiredToken()
    {
        var clock = new TestClock();
        SessionTokenService service = Create(clock, ttlSeconds: 60);

        SessionToken token = service.Issue("device-1", "conn-1", PermissionSet.Default);
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.False(service.TryValidate(token.Value, "conn-1", out _, out string? failure));
        Assert.Contains("expired", failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsAnUnknownToken()
    {
        var clock = new TestClock();
        SessionTokenService service = Create(clock);

        Assert.False(service.TryValidate("not-a-real-token", "conn-1", out _, out _));
        Assert.False(service.TryValidate(null, "conn-1", out _, out _));
        Assert.False(service.TryValidate(string.Empty, "conn-1", out _, out _));
    }

    [Fact]
    public void RenewalInvalidatesThePreviousToken()
    {
        // Two simultaneously valid tokens would widen the window in which a captured one works.
        var clock = new TestClock();
        SessionTokenService service = Create(clock);

        SessionToken first = service.Issue("device-1", "conn-1", PermissionSet.Default);
        SessionToken second = service.Renew(first, PermissionSet.Default);

        Assert.NotEqual(first.Value, second.Value);
        Assert.False(service.TryValidate(first.Value, "conn-1", out _, out _));
        Assert.True(service.TryValidate(second.Value, "conn-1", out _, out _));
    }

    [Fact]
    public void RevokingADeviceInvalidatesEveryTokenItHolds()
    {
        var clock = new TestClock();
        SessionTokenService service = Create(clock);

        SessionToken a = service.Issue("device-1", "conn-1", PermissionSet.Default);
        SessionToken b = service.Issue("device-1", "conn-2", PermissionSet.Default);
        SessionToken other = service.Issue("device-2", "conn-3", PermissionSet.Default);

        int removed = service.RevokeDevice("device-1");

        Assert.Equal(2, removed);
        Assert.False(service.TryValidate(a.Value, "conn-1", out _, out _));
        Assert.False(service.TryValidate(b.Value, "conn-2", out _, out _));

        // Revocation is per device: another device's session must be untouched.
        Assert.True(service.TryValidate(other.Value, "conn-3", out _, out _));
    }

    [Fact]
    public void IssuesUnpredictableTokens()
    {
        var clock = new TestClock();
        SessionTokenService service = Create(clock);

        var values = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 500; i++)
        {
            values.Add(service.Issue("device-1", $"conn-{i}", PermissionSet.Default).Value);
        }

        Assert.Equal(500, values.Count);
        Assert.All(values, value => Assert.True(value.Length >= 40));
    }

    /// <summary>A clock the test moves by hand, so expiry is tested without waiting.</summary>
    private sealed class TestClock : IClock
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => _now;

        public long UnixTimeMilliseconds => _now.ToUnixTimeMilliseconds();

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

/// <summary>Tests for the permission bitmap and its wire representation.</summary>
public sealed class PermissionSetTests
{
    [Fact]
    public void RoundTripsThroughNames()
    {
        Permission original = Permission.ViewScreen | Permission.PowerControls;

        string[] names = PermissionSet.ToNames(original);
        Permission parsed = PermissionSet.FromNames(names);

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void IgnoresUnknownNamesRatherThanFailing()
    {
        // Forward compatibility: an older PC receiving a newer app's list must not break, and must
        // not accidentally grant something it does not understand.
        Permission parsed = PermissionSet.FromNames(["ViewScreen", "TeleportPC", "ControlInput"]);

        Assert.Equal(Permission.ViewScreen | Permission.ControlInput, parsed);
    }

    [Fact]
    public void TreatsNullAndEmptyAsNoPermissions()
    {
        Assert.Equal(Permission.None, PermissionSet.FromNames(null));
        Assert.Equal(Permission.None, PermissionSet.FromNames([]));
    }

    [Fact]
    public void AllowsOnlyWhatIsGranted()
    {
        Assert.True(PermissionSet.Allows(Permission.ViewScreen, Permission.ViewScreen));
        Assert.True(PermissionSet.Allows(PermissionSet.All, Permission.PowerControls));
        Assert.True(PermissionSet.Allows(Permission.None, Permission.None));

        Assert.False(PermissionSet.Allows(Permission.ViewScreen, Permission.PowerControls));
        Assert.False(PermissionSet.Allows(Permission.None, Permission.ViewScreen));
    }

    [Fact]
    public void DefaultGrantsViewAndControlOnly()
    {
        Assert.Equal(Permission.ViewScreen | Permission.ControlInput, PermissionSet.Default);
    }
}

/// <summary>Tests for the base64url helper used by fingerprints, tokens and QR payloads.</summary>
public sealed class Base64UrlTests
{
    [Fact]
    public void RoundTripsArbitraryBytes()
    {
        for (int length = 1; length <= 64; length++)
        {
            byte[] data = new byte[length];
            Random.Shared.NextBytes(data);

            string encoded = Base64Url.Encode(data);

            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            Assert.DoesNotContain('=', encoded);

            Assert.True(Base64Url.TryDecode(encoded, out byte[] decoded));
            Assert.Equal(data, decoded);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("!!!!")]
    public void RejectsMalformedInput(string? value)
    {
        Assert.False(Base64Url.TryDecode(value, out _));
    }

    [Fact]
    public void GeneratedTokensAreDistinct()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 1000; i++)
        {
            tokens.Add(Base64Url.GenerateToken(16));
        }

        Assert.Equal(1000, tokens.Count);
    }

    [Fact]
    public void RefusesAnUnsafelyShortTokenLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Base64Url.GenerateToken(8));
    }

    [Fact]
    public void FingerprintComparisonIsLengthSafe()
    {
        Assert.True(CertificateFingerprint.Equal("abcdef", "abcdef"));
        Assert.False(CertificateFingerprint.Equal("abcdef", "abcdeg"));
        Assert.False(CertificateFingerprint.Equal("abc", "abcdef"));
        Assert.False(CertificateFingerprint.Equal(null, "abc"));
        Assert.False(CertificateFingerprint.Equal("abc", null));
    }
}
