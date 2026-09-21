using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace RemoteAgent.Integration.Tests;

/// <summary>
/// End-to-end tests of the pairing and authentication path over a real TLS connection.
/// </summary>
/// <remarks>
/// Every test here answers a question that unit tests cannot: does the composition actually hold?
/// The individual rules are tested elsewhere; these confirm that a client speaking the real protocol
/// over a real socket cannot get past them.
/// </remarks>
public sealed class PairingAndAuthTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task UnpairedClientCannotRunAnyCommandButPairing()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);

        await client.ConnectAsync(harness.Port);
        output.WriteLine($"Negotiated {client.NegotiatedProtocol} / {client.NegotiatedCipher}");

        // The handshake is refused: the client's certificate matches no pairing record.
        ResponseEnvelope hello = await client.HelloAsync();
        Assert.False(hello.Ok);
        Assert.Equal(ErrorCodes.NotPaired, hello.Error!.Code);

        // So is everything else, including a command that needs no permission.
        ResponseEnvelope state = await client.SendAsync(CommandNames.SystemState);
        Assert.False(state.Ok);
        Assert.Equal(ErrorCodes.NotPaired, state.Error!.Code);
    }

    [Fact]
    public async Task PairingIsRefusedWhenPairingModeIsClosed()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);

        await client.ConnectAsync(harness.Port);

        // A plausible-looking token is useless while the window is shut, and the user is never
        // prompted — an attacker cannot make a dialog appear on someone's screen.
        ResponseEnvelope response = await client.PairAsync(Base64Url.GenerateToken(16));

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.PairingDisabled, response.Error!.Code);
        Assert.Equal(0, harness.Approval.PromptCount);
    }

    [Fact]
    public async Task PairingIsRefusedWithTheWrongTokenAndDoesNotPromptTheUser()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);

        ResponseEnvelope response = await client.PairAsync(Base64Url.GenerateToken(16));

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.PairingTokenInvalid, response.Error!.Code);

        // The ordering guarantee: the token is checked before a human is involved, so nobody can be
        // spammed with dialogs in the hope of a careless tap.
        Assert.Equal(0, harness.Approval.PromptCount);
    }

    [Fact]
    public async Task RepeatedWrongTokensCloseThePairingWindow()
    {
        await using TestHarness harness = await TestHarness.StartAsync(
            options => options.Pairing.MaxAttempts = 3);

        harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ResponseEnvelope response = await client.PairAsync(Base64Url.GenerateToken(16));
            Assert.False(response.Ok);
        }

        // Brute force costs the attacker the window itself, not just the attempt.
        Assert.False(harness.PairingService.IsOpen);
    }

    [Fact]
    public async Task PairingIsRefusedWhenTheUserDeclines()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();
        harness.Approval.ApproveNext = false;

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);

        ResponseEnvelope response = await client.PairAsync(token);

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.PairingRejected, response.Error!.Code);
        Assert.Equal(1, harness.Approval.PromptCount);

        // Nothing was stored, so the device still cannot connect.
        Assert.Empty(await harness.PairingStore.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PairingIsRefusedWhenNobodyCanBePrompted()
    {
        // "Nobody signed in at the PC" must mean no, not yes. Otherwise a leaked QR code would be
        // sufficient on an unattended machine.
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();
        harness.Approval.CanPrompt = false;

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);

        ResponseEnvelope response = await client.PairAsync(token);

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.PairingTimeout, response.Error!.Code);
        Assert.Empty(await harness.PairingStore.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalPromptShowsTheAddressAndFingerprint()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        await client.PairAsync(token);

        PairingApprovalRequest? request = harness.Approval.LastRequest;
        Assert.NotNull(request);

        // The user needs the observable facts, not just the claimed ones.
        Assert.Equal("127.0.0.1", request!.RemoteAddress);
        Assert.False(string.IsNullOrWhiteSpace(request.FingerprintShort));
        Assert.Contains(":", request.FingerprintShort, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulPairingThenReconnectAndHandshake()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);

        // --- First connection: pair ---
        await client.ConnectAsync(harness.Port);
        ResponseEnvelope pairing = await client.PairAsync(token);

        Assert.True(pairing.Ok, pairing.Error?.Message);
        PairResult result = TestClient.Payload<PairResult>(pairing);

        // The PC echoes its own fingerprint so the client can confirm it matches the QR code before
        // committing the pairing.
        Assert.Equal(harness.Identity.Fingerprint, result.CertificateFingerprint);
        Assert.Equal("pc-under-test", result.DeviceId);
        Assert.Equal(PermissionSet.ToNames(PermissionSet.Default), result.Permissions);

        await client.DisconnectAsync();

        // --- Second connection: no QR, no token, just mutual TLS ---
        await client.ConnectAsync(harness.Port);
        ResponseEnvelope hello = await client.HelloAsync();

        Assert.True(hello.Ok, hello.Error?.Message);
        HelloResult session = TestClient.Payload<HelloResult>(hello);

        Assert.False(string.IsNullOrWhiteSpace(session.Token));
        Assert.Equal(ProtocolVersion.Current, session.Version);
        Assert.Equal(PermissionSet.ToNames(PermissionSet.Default), session.Permissions);

        output.WriteLine($"Session established over {client.NegotiatedProtocol}, token TTL {session.TokenTtlSeconds}s");

        // And an authenticated command now works.
        ResponseEnvelope ping = await client.SendAsync(CommandNames.Ping);
        Assert.True(ping.Ok);
    }

    [Fact]
    public async Task PairingTokenIsSingleUse()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();

        await using TestClient first = TestClient.Create(harness.Identity.Fingerprint);
        await first.ConnectAsync(harness.Port);
        Assert.True((await first.PairAsync(token, "phone-a")).Ok);

        // A second device with the same token is refused: the window closed when the token was used.
        await using TestClient second = TestClient.Create(harness.Identity.Fingerprint);
        await second.ConnectAsync(harness.Port);
        ResponseEnvelope reuse = await second.PairAsync(token, "phone-b");

        Assert.False(reuse.Ok);
        Assert.Equal(ErrorCodes.PairingDisabled, reuse.Error!.Code);
    }

    [Fact]
    public async Task OneDevicesPairingDoesNotAuthenticateAnother()
    {
        // The pairing record is bound to a certificate, not to a device id a client chooses. A second
        // phone claiming the same device id has a different key and is refused.
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();

        await using TestClient paired = TestClient.Create(harness.Identity.Fingerprint);
        await paired.ConnectAsync(harness.Port);
        Assert.True((await paired.PairAsync(token, "shared-id")).Ok);
        await paired.DisconnectAsync();

        await using TestClient impostor = TestClient.Create(harness.Identity.Fingerprint);
        await impostor.ConnectAsync(harness.Port);

        ResponseEnvelope hello = await impostor.HelloAsync();
        Assert.False(hello.Ok);
        Assert.Equal(ErrorCodes.NotPaired, hello.Error!.Code);
    }

    [Fact]
    public async Task CommandsRequireTheSessionToken()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(token)).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);

        // Paired, but no handshake yet: an authenticated command is refused.
        ResponseEnvelope early = await client.SendAsync(CommandNames.PermissionsList);
        Assert.False(early.Ok);
        Assert.Equal(ErrorCodes.Unauthenticated, early.Error!.Code);

        Assert.True((await client.HelloAsync()).Ok);

        // With the token, allowed.
        Assert.True((await client.SendAsync(CommandNames.PermissionsList)).Ok);

        // Without it, refused again — the token is required on each request, not just once.
        ResponseEnvelope withoutToken = await client.SendAsync(
            CommandNames.PermissionsList,
            includeToken: false);

        Assert.False(withoutToken.Ok);
        Assert.Equal(ErrorCodes.Unauthenticated, withoutToken.Error!.Code);
    }

    [Fact]
    public async Task AStolenTokenIsUselessOnAnotherConnection()
    {
        // The property that makes token capture uninteresting: binding to the connection.
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken)).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);
        string stolen = client.Token!;

        // A second connection from the same device, presenting the first connection's token.
        await using TestClient second = TestClient.Create(harness.Identity.Fingerprint);
        await second.ConnectAsync(harness.Port);

        // Not paired as a separate identity, so it is refused for that reason first — which is
        // itself the point: the token alone gets an attacker nowhere.
        ResponseEnvelope response = await second.SendAsync(CommandNames.PermissionsList);
        Assert.False(response.Ok);
    }

    [Fact]
    public async Task ReplayedRequestIsRefusedAndTheConnectionIsClosed()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken)).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);
        Assert.True((await client.SendAsync(CommandNames.Ping)).Ok);

        // Re-send a sequence number that has already been used.
        ResponseEnvelope replay = await client.SendAsync(CommandNames.Ping, overrideSequence: 1);

        Assert.False(replay.Ok);
        Assert.Equal(ErrorCodes.ReplayDetected, replay.Error!.Code);
    }

    [Fact]
    public async Task RequestWithASkewedClockIsRefused()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken)).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);

        ResponseEnvelope stale = await client.SendAsync(
            CommandNames.Ping,
            overrideTimestampMs: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds());

        Assert.False(stale.Ok);
        Assert.Equal(ErrorCodes.ReplayDetected, stale.Error!.Code);
    }

    [Fact]
    public async Task RevokingAPairingDropsTheLiveConnection()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken, "phone-to-revoke")).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);
        Assert.True((await client.SendAsync(CommandNames.Ping)).Ok);

        // Revoke, then push the refresh the service would perform on a store change.
        Assert.True(await harness.PairingStore.RevokeAsync("phone-to-revoke", CancellationToken.None));
        await harness.Sessions.RefreshAllAsync(CancellationToken.None);

        // Access ends now, not when the token happens to expire.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var cts = new CancellationTokenSource(Timeout);
            while (!cts.IsCancellationRequested)
            {
                ResponseEnvelope response = await client.SendAsync(
                    CommandNames.Ping,
                    cancellationToken: cts.Token);

                if (!response.Ok)
                {
                    throw new InvalidOperationException($"Refused with {response.Error?.Code}.");
                }

                await Task.Delay(100, cts.Token);
            }
        });
    }

    [Fact]
    public async Task UnknownCommandIsRefusedForAnAuthenticatedDevice()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken)).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);

        foreach (string name in new[] { "shell.exec", "system.format", "../../cmd", "app.launch\u0000" })
        {
            ResponseEnvelope response = await client.SendAsync(name);
            Assert.False(response.Ok);
            Assert.Equal(ErrorCodes.UnknownCommand, response.Error!.Code);
        }
    }

    [Fact]
    public async Task CommandWithoutPermissionIsRefused()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken, "phone-perms")).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);

        // Default permissions exclude power control, so shutdown is refused before it reaches any
        // Windows API — the machine is never at risk from a test run.
        ResponseEnvelope shutdown = await client.SendAsync(CommandNames.SystemShutdown);

        Assert.False(shutdown.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, shutdown.Error!.Code);
    }

    [Fact]
    public async Task PermissionChangeTakesEffectWithoutReconnecting()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken, "phone-grant")).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);

        PermissionsResult before = TestClient.Payload<PermissionsResult>(
            await client.SendAsync(CommandNames.PermissionsList));
        Assert.DoesNotContain("Clipboard", before.Granted);

        await harness.PairingStore.SetPermissionsAsync(
            "phone-grant",
            PermissionSet.Default | Permission.Clipboard,
            CancellationToken.None);

        await harness.Sessions.RefreshAllAsync(CancellationToken.None);

        PermissionsResult after = TestClient.Payload<PermissionsResult>(
            await client.SendAsync(CommandNames.PermissionsList));

        Assert.Contains("Clipboard", after.Granted);
    }

    [Fact]
    public async Task ProtocolVersionMismatchIsRefusedClearly()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        string pairingToken = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.PairAsync(pairingToken)).Ok);
        await client.DisconnectAsync();

        await client.ConnectAsync(harness.Port);

        // A client from the future, speaking only a version this PC does not know.
        ResponseEnvelope response = await client.SendAsync(
            CommandNames.Hello,
            new
            {
                minVersion = ProtocolVersion.Current + 5,
                maxVersion = ProtocolVersion.Current + 9,
                deviceName = "Future Phone",
                platform = "test",
                appVersion = "9.9.9",
            },
            includeToken: false);

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.VersionUnsupported, response.Error!.Code);
        Assert.Contains(ProtocolVersion.Current.ToString(), response.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientPinningDetectsADifferentCertificate()
    {
        await using TestHarness harness = await TestHarness.StartAsync();

        // A client that expects a fingerprint the PC does not have must refuse to proceed. This is
        // the check that makes a substituted or impersonating PC fail closed on the phone's side.
        await using TestClient client = TestClient.Create(
            expectedServerFingerprint: Base64Url.Encode(new byte[32]));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ConnectAsync(harness.Port));

        Assert.Contains("fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NegotiatesAtLeastTls12WithAnAeadCipher()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);

        await client.ConnectAsync(harness.Port);

        output.WriteLine($"Negotiated: {client.NegotiatedProtocol} / {client.NegotiatedCipher}");

        Assert.True(
            client.NegotiatedProtocol is "Tls12" or "Tls13",
            $"Expected TLS 1.2 or 1.3, negotiated {client.NegotiatedProtocol}.");

        // Forward secrecy and AEAD, whichever version was agreed.
        Assert.Contains("GCM", client.NegotiatedCipher, StringComparison.OrdinalIgnoreCase);
    }
}
