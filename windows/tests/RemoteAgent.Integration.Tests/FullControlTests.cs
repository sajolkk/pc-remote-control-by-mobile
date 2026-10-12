using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Security;
using RemoteAgent.Service.Configuration;
using RemoteAgent.Service.Hosting;
using RemoteAgent.Service.Network;
using RemoteAgent.Service.Services;
using Xunit;

// The harness points each test at its own temporary data directory by briefly setting a
// process-wide environment variable around AgentPaths.Resolve(). That was safe while this assembly
// held a single test class, because xUnit runs the tests of one class in sequence. With a second
// class the two would run side by side, and one harness could resolve its paths while the other
// had the variable set — or had just cleared it, which resolves to the REAL %ProgramData%\PCRemote
// of the machine running the tests. Running this assembly's tests strictly one at a time removes
// the race altogether.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RemoteAgent.Integration.Tests;

/// <summary>
/// End-to-end tests of granting a device full control: at pairing, and afterwards from the Devices
/// window.
/// </summary>
/// <remarks>
/// <para>"Full control" is every permission group, and the only way a device can come to hold it at
/// pairing time is a person at the PC ticking a box and then clicking Allow. These tests drive the
/// real pairing service, store, token service, gate and TLS listener, with only the human replaced,
/// to check that the choice arrives in the stored record when it was made — and that nothing else
/// produces it: not a tick without an approval, not a prompt that was never shown.</para>
///
/// <para>No command here can do anything to the machine running the tests. Authorization is probed
/// with commands whose handlers do not exist in the harness, so the difference being observed is
/// "refused for lack of permission" against "authorized, but there is no session agent".</para>
/// </remarks>
public sealed class FullControlTests
{
    private static async Task<(ResponseEnvelope Pairing, TestClient Client)> PairAsync(
        TestHarness harness,
        string deviceId,
        string? token = null)
    {
        TestClient client = TestClient.Create(token is null ? string.Empty : harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);
        ResponseEnvelope pairing = await client.PairAsync(token ?? string.Empty, deviceId);
        return (pairing, client);
    }

    /// <summary>
    /// Opening pairing mode writes the configuration file, the file is reloaded, and the reload
    /// replaces the options object. A service that kept the object it was built with went on
    /// seeing pairing mode as closed, and refused every QR code it had just issued.
    /// </summary>
    [Fact]
    public async Task PairingModeIsReadFromTheOptionsInForceNotTheOnesAtStartup()
    {
        await using TestHarness harness = await TestHarness.StartAsync();

        var current = new PairingOptions { AllowPairing = false };
        var service = new PairingService(
            harness.PairingStore,
            harness.Approval,
            new SystemClock(),
            NullLogger<PairingService>.Instance,
            () => current);

        // The reload: a different object, now saying pairing is allowed.
        current = new PairingOptions { AllowPairing = true };
        service.OpenWindow();

        PairingAttemptResult attempt = await service.TryPairAsync(
            new PairRequestArgs { DeviceId = "phone-reload", Token = "not-the-token" },
            "AA:BB",
            "127.0.0.1",
            CancellationToken.None);

        // Past the "is pairing open" gate and on to the token check, which this token fails.
        Assert.Equal(PairingOutcome.TokenInvalid, attempt.Outcome);
    }

    [Fact]
    public async Task LeavingTheBoxUntickedPairsWithTheDefaultPermissionsOnly()
    {
        await using TestHarness harness = await TestHarness.StartAsync();

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-default", harness.OpenPairing());
        await using TestClient owned = client;

        Assert.True(pairing.Ok, pairing.Error?.Message);
        Assert.Equal(["ViewScreen", "ControlInput"], TestClient.Payload<PairResult>(pairing).Permissions);

        PairedDevice? stored = await harness.PairingStore.FindByDeviceIdAsync("phone-default", CancellationToken.None);
        Assert.Equal(PermissionSet.Default, stored!.Permissions);
    }

    [Fact]
    public async Task TickingTheBoxAndAllowingPairsWithEveryPermission()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        harness.Approval.TickFullControl = true;

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-full", harness.OpenPairing());
        await using TestClient owned = client;

        Assert.True(pairing.Ok, pairing.Error?.Message);

        // The phone is told at once what it was granted.
        Assert.Equal(
            ["ViewScreen", "ControlInput", "LaunchApps", "ManageFiles", "PowerControls", "Clipboard"],
            TestClient.Payload<PairResult>(pairing).Permissions);

        // The stored record is what actually authorizes later connections.
        PairedDevice? stored = await harness.PairingStore.FindByDeviceIdAsync("phone-full", CancellationToken.None);
        Assert.Equal(PermissionSet.All, stored!.Permissions);

        // And it survives into a fresh, authenticated session.
        await client.DisconnectAsync();
        await client.ConnectAsync(harness.Port);

        ResponseEnvelope hello = await client.HelloAsync();
        Assert.True(hello.Ok, hello.Error?.Message);
        Assert.Equal(PermissionSet.ToNames(PermissionSet.All), TestClient.Payload<HelloResult>(hello).Permissions);

        foreach (string command in new[] { CommandNames.FileList, CommandNames.WindowList, CommandNames.InputReleaseAll })
        {
            ResponseEnvelope response = await client.SendAsync(command);

            // Authorized: it got past the gate and failed only for want of a session agent.
            Assert.False(response.Ok);
            Assert.Equal(ErrorCodes.SessionUnavailable, response.Error!.Code);
        }
    }

    [Fact]
    public async Task TheDefaultGrantReachesInputButNotFilesOrWindows()
    {
        await using TestHarness harness = await TestHarness.StartAsync();

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-plain", harness.OpenPairing());
        await using TestClient owned = client;
        Assert.True(pairing.Ok, pairing.Error?.Message);

        await client.DisconnectAsync();
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);

        Assert.Equal(ErrorCodes.SessionUnavailable, (await client.SendAsync(CommandNames.InputReleaseAll)).Error!.Code);
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.SendAsync(CommandNames.FileList)).Error!.Code);
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.SendAsync(CommandNames.FileDelete)).Error!.Code);
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.SendAsync(CommandNames.WindowAction)).Error!.Code);
    }

    [Fact]
    public async Task ATickedBoxWithADeclineGrantsNothingAtAll()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        harness.Approval.TickFullControl = true;
        harness.Approval.ApproveNext = false;

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-declined", harness.OpenPairing());
        await using TestClient owned = client;

        Assert.False(pairing.Ok);
        Assert.Equal(ErrorCodes.PairingRejected, pairing.Error!.Code);
        Assert.Empty(await harness.PairingStore.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FullControlWorksForCodePairingToo()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        harness.Approval.TickFullControl = true;

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-code");
        await using TestClient owned = client;

        Assert.True(pairing.Ok, pairing.Error?.Message);
        Assert.NotNull(harness.Approval.LastRequest!.ComparisonCode);

        PairedDevice? stored = await harness.PairingStore.FindByDeviceIdAsync("phone-code", CancellationToken.None);
        Assert.Equal(PermissionSet.All, stored!.Permissions);
    }

    [Fact]
    public async Task WithNoPromptThereIsNobodyToGrantFullControl()
    {
        // Approval switched off (a configuration the real service refuses to start with). Nobody
        // was asked, so nobody ticked anything, whatever the approval service would have said.
        await using TestHarness harness = await TestHarness.StartAsync(options => options.Pairing.RequireLocalApproval = false);
        harness.Approval.TickFullControl = true;

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-unasked", harness.OpenPairing());
        await using TestClient owned = client;

        Assert.True(pairing.Ok, pairing.Error?.Message);
        Assert.Equal(0, harness.Approval.PromptCount);

        PairedDevice? stored = await harness.PairingStore.FindByDeviceIdAsync("phone-unasked", CancellationToken.None);
        Assert.Equal(PermissionSet.Default, stored!.Permissions);
    }

    [Fact]
    public async Task ThePairingRequestHasNoWayToAskForMoreThanTheDefault()
    {
        // A hostile phone adds every field it can think of. The human left the box unticked.
        await using TestHarness harness = await TestHarness.StartAsync();
        string token = harness.OpenPairing();

        await using TestClient client = TestClient.Create(harness.Identity.Fingerprint);
        await client.ConnectAsync(harness.Port);

        ResponseEnvelope pairing = await client.SendAsync(
            CommandNames.PairRequest,
            new
            {
                deviceId = "phone-greedy",
                deviceName = "Greedy",
                platform = "test",
                model = "Harness",
                token,
                clientFingerprint = client.Fingerprint,
                fullControl = true,
                grantFullControl = true,
                permissions = PermissionSet.AllNames,
            },
            includeToken: false);

        Assert.True(pairing.Ok, pairing.Error?.Message);

        PairedDevice? stored = await harness.PairingStore.FindByDeviceIdAsync("phone-greedy", CancellationToken.None);
        Assert.Equal(PermissionSet.Default, stored!.Permissions);
    }

    // ---------------------------------------------------------------------
    // Input throughput on a real connection
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ABurstThatIsThrottledDoesNotCostThePhoneItsConnection()
    {
        // A deliberately tiny budget, so a burst is certain to exceed it: one request a second,
        // with the limiter's minimum burst allowance on top.
        await using TestHarness harness = await TestHarness.StartAsync(options => options.Security.MaxRequestsPerMinute = 60);

        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-burst", harness.OpenPairing());
        await using TestClient owned = client;
        Assert.True(pairing.Ok, pairing.Error?.Message);

        await client.DisconnectAsync();
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);

        var codes = new List<string?>();
        for (int i = 0; i < 90; i++)
        {
            ResponseEnvelope response = await client.SendAsync(CommandNames.InputMouseMove, new { x = 0.5, y = 0.5 });
            codes.Add(response.Error?.Code);
        }

        // Some were let through (and then failed only for want of a session agent), the rest were
        // throttled — and none of them tripped the replay guard, which would have been fatal.
        Assert.Contains(ErrorCodes.SessionUnavailable, codes);
        Assert.Contains(ErrorCodes.RateLimited, codes);
        Assert.DoesNotContain(ErrorCodes.ReplayDetected, codes);

        // The connection is still alive and still in sequence: once the budget refills, the very
        // next command is served.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        ResponseEnvelope after = await client.SendAsync(CommandNames.InputReleaseAll);
        Assert.Equal(ErrorCodes.SessionUnavailable, after.Error!.Code);
    }

    // ---------------------------------------------------------------------
    // The Devices window's path: agent -> service over IPC
    // ---------------------------------------------------------------------

    private static AgentRequestRouter CreateRouter(TestHarness harness) => new(
        harness.PairingService,
        new FakeSystemInfo(),
        new IdentityStore(
            harness.Paths,
            new PassthroughSecretProtector(),
            NullLogger<IdentityStore>.Instance,
            lifetimeYears: 1,
            subjectName: "PC-Remote Test"),
        new ListenerEndpointStatus(),
        harness.Sessions,
        new FakeSessionState(),
        new AgentConfigurationWriter(
            new AgentConfigurationStore(harness.Paths, NullLogger.Instance),
            NullLogger<AgentConfigurationWriter>.Instance),
        new StaticOptionsMonitor<AgentOptions>(harness.Options),
        harness.PairingStore,
        NullLogger<AgentRequestRouter>.Instance);

    private static IpcRequest Request(string command, object? args = null) => new()
    {
        Id = "req-1",
        Command = command,
        Args = args is null ? null : JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
    };

    [Fact]
    public async Task TheOwnerCanSetEveryPermissionForAnExistingDeviceIncludingFiles()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-edit", harness.OpenPairing());
        await using TestClient owned = client;
        Assert.True(pairing.Ok, pairing.Error?.Message);

        AgentRequestRouter router = CreateRouter(harness);

        // What the window is given to draw its checkboxes from.
        IpcResponse listed = await router.HandleAsync(Request(IpcCommands.ListDevices), CancellationToken.None);
        JsonNode device = Assert.Single(listed.Data!["devices"]!.AsArray())!;
        Assert.Equal(
            ["ViewScreen", "ControlInput"],
            device["permissions"]!.AsArray().Select(node => node!.GetValue<string>()));

        // Tick everything, one name per permission group the protocol defines.
        IpcResponse all = await router.HandleAsync(
            Request(IpcCommands.SetDevicePermissions, new { deviceId = "phone-edit", permissions = PermissionSet.AllNames }),
            CancellationToken.None);

        Assert.True(all.Ok, all.Error?.Message);
        Assert.Equal(
            PermissionSet.All,
            (await harness.PairingStore.FindByDeviceIdAsync("phone-edit", CancellationToken.None))!.Permissions);

        // Each group can be granted on its own, ManageFiles among them.
        foreach (string name in PermissionSet.AllNames)
        {
            IpcResponse single = await router.HandleAsync(
                Request(IpcCommands.SetDevicePermissions, new { deviceId = "phone-edit", permissions = new[] { name } }),
                CancellationToken.None);

            Assert.True(single.Ok, $"{name}: {single.Error?.Message}");

            PairedDevice? stored = await harness.PairingStore.FindByDeviceIdAsync("phone-edit", CancellationToken.None);
            Assert.Equal(PermissionSet.FromNames([name]), stored!.Permissions);
            Assert.NotEqual(Permission.None, stored.Permissions);
        }

        // And everything can be taken away again.
        IpcResponse none = await router.HandleAsync(
            Request(IpcCommands.SetDevicePermissions, new { deviceId = "phone-edit", permissions = Array.Empty<string>() }),
            CancellationToken.None);

        Assert.True(none.Ok);
        Assert.Equal(
            Permission.None,
            (await harness.PairingStore.FindByDeviceIdAsync("phone-edit", CancellationToken.None))!.Permissions);
    }

    [Fact]
    public async Task AGrantFromTheDevicesWindowAppliesToTheLiveConnection()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-live", harness.OpenPairing());
        await using TestClient owned = client;
        Assert.True(pairing.Ok, pairing.Error?.Message);

        await client.DisconnectAsync();
        await client.ConnectAsync(harness.Port);
        Assert.True((await client.HelloAsync()).Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.SendAsync(CommandNames.FileList)).Error!.Code);

        IpcResponse granted = await CreateRouter(harness).HandleAsync(
            Request(
                IpcCommands.SetDevicePermissions,
                new { deviceId = "phone-live", permissions = new[] { "ViewScreen", "ControlInput", "ManageFiles" } }),
            CancellationToken.None);

        Assert.True(granted.Ok, granted.Error?.Message);

        // The real host refreshes sessions when the store changes; the harness does it by hand.
        await harness.Sessions.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(ErrorCodes.SessionUnavailable, (await client.SendAsync(CommandNames.FileList)).Error!.Code);
        Assert.Equal(ErrorCodes.PermissionDenied, (await client.SendAsync(CommandNames.WindowList)).Error!.Code);
    }

    [Fact]
    public async Task SettingPermissionsIsStrictAboutItsArguments()
    {
        await using TestHarness harness = await TestHarness.StartAsync();
        (ResponseEnvelope pairing, TestClient client) = await PairAsync(harness, "phone-strict", harness.OpenPairing());
        await using TestClient owned = client;
        Assert.True(pairing.Ok, pairing.Error?.Message);

        AgentRequestRouter router = CreateRouter(harness);

        object[] bad =
        [
            new { permissions = new[] { "ManageFiles" } },
            new { deviceId = "phone-strict" },
            new { deviceId = "phone-strict", permissions = "ManageFiles" },
            new { deviceId = "phone-strict", permissions = new[] { "ManageFiles", "Administrator" } },
            new { deviceId = "phone-strict", permissions = new object[] { "ManageFiles", 8 } },
        ];

        foreach (object args in bad)
        {
            IpcResponse response = await router.HandleAsync(Request(IpcCommands.SetDevicePermissions, args), CancellationToken.None);

            Assert.False(response.Ok);
            Assert.Equal(ErrorCodes.InvalidArguments, response.Error!.Code);
        }

        IpcResponse unknownDevice = await router.HandleAsync(
            Request(IpcCommands.SetDevicePermissions, new { deviceId = "nobody", permissions = new[] { "ManageFiles" } }),
            CancellationToken.None);

        Assert.Equal(ErrorCodes.NotFound, unknownDevice.Error!.Code);

        // Nothing above changed the device.
        Assert.Equal(
            PermissionSet.Default,
            (await harness.PairingStore.FindByDeviceIdAsync("phone-strict", CancellationToken.None))!.Permissions);
    }

    // ---------------------------------------------------------------------
    // File policy served to the agent
    // ---------------------------------------------------------------------

    [Fact]
    public async Task TheServiceTellsTheAgentWhichRootsAreAllowed()
    {
        await using TestHarness harness = await TestHarness.StartAsync(options =>
        {
            options.Files.UseDefaultRoots = false;
            options.Files.MaxFileSizeMb = 64;
            options.Files.AllowedRoots =
            [
                new AllowedRoot { Alias = "Inbox", Path = "%USERPROFILE%\\Downloads", Write = true },
                new AllowedRoot { Alias = "Manuals", Path = "D:\\Manuals", Write = false },
            ];
        });

        IpcResponse response = await CreateRouter(harness).HandleAsync(Request(IpcCommands.GetFilePolicy), CancellationToken.None);

        Assert.True(response.Ok, response.Error?.Message);
        IpcFilePolicy policy = response.Data.Deserialize<IpcFilePolicy>(ProtocolJson.Options)!;

        Assert.False(policy.UseDefaultRoots);
        Assert.Equal(64, policy.MaxFileSizeMb);

        // Unexpanded: the variable has to be resolved by the agent, in the user's environment.
        Assert.Equal(["%USERPROFILE%\\Downloads", "D:\\Manuals"], policy.AllowedRoots.Select(r => r.Path));
        Assert.Equal([true, false], policy.AllowedRoots.Select(r => r.Write));
        Assert.Equal(["Inbox", "Manuals"], policy.AllowedRoots.Select(r => r.Alias));
    }

    [Fact]
    public async Task ByDefaultTheWholePcIsOfferedAndTheGeneratedLegacyListIsNotTreatedAsAChoice()
    {
        await using TestHarness fresh = await TestHarness.StartAsync();

        IpcFilePolicy defaults = (await CreateRouter(fresh).HandleAsync(Request(IpcCommands.GetFilePolicy), CancellationToken.None))
            .Data.Deserialize<IpcFilePolicy>(ProtocolJson.Options)!;

        Assert.True(defaults.UseDefaultRoots);
        Assert.Empty(defaults.AllowedRoots);

        // A config.json written by an earlier version carries that version's built-in list.
        await using TestHarness upgraded = await TestHarness.StartAsync(options => options.Files.AllowedRoots =
        [
            new AllowedRoot { Alias = "Downloads", Path = "%USERPROFILE%\\Downloads", Write = true },
            new AllowedRoot { Alias = "Documents", Path = "%USERPROFILE%\\Documents", Write = false },
            new AllowedRoot { Alias = "Desktop", Path = "%USERPROFILE%\\Desktop", Write = false },
        ]);

        IpcFilePolicy afterUpgrade = (await CreateRouter(upgraded).HandleAsync(Request(IpcCommands.GetFilePolicy), CancellationToken.None))
            .Data.Deserialize<IpcFilePolicy>(ProtocolJson.Options)!;

        Assert.True(afterUpgrade.UseDefaultRoots);
        Assert.Empty(afterUpgrade.AllowedRoots);
    }

    [Fact]
    public void TheConfigurationStoreDropsTheGeneratedLegacyRootsOnceAndKeepsAnEditedList()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcremote-tests", "config-" + Guid.NewGuid().ToString("n"));
        string? previous = Environment.GetEnvironmentVariable(AgentPaths.DataDirectoryVariable);

        try
        {
            Environment.SetEnvironmentVariable(AgentPaths.DataDirectoryVariable, directory);
            AgentPaths paths = AgentPaths.Resolve();
            Environment.SetEnvironmentVariable(AgentPaths.DataDirectoryVariable, previous);

            var store = new AgentConfigurationStore(paths, NullLogger.Instance);

            // An installation from before this change: the old list, exactly as it was generated.
            var legacy = new AgentOptions { Device = { Id = "pc", Name = "PC" } };
            legacy.Files.AllowedRoots =
            [
                new AllowedRoot { Alias = "Downloads", Path = "%USERPROFILE%\\Downloads", Write = true },
                new AllowedRoot { Alias = "Documents", Path = "%USERPROFILE%\\Documents", Write = false },
                new AllowedRoot { Alias = "Desktop", Path = "%USERPROFILE%\\Desktop", Write = false },
            ];
            paths.EnsureCreated();
            store.Save(legacy);

            store.EnsureInitialized();

            AgentOptions migrated = store.LoadOrDefault();
            Assert.Empty(migrated.Files.AllowedRoots);
            Assert.True(migrated.Files.UseDefaultRoots);
            Assert.Equal("pc", migrated.Device.Id);

            // An owner's own list, even one that narrows to a single read-only folder, is kept.
            migrated.Files.UseDefaultRoots = false;
            migrated.Files.AllowedRoots = [new AllowedRoot { Alias = "Documents", Path = "%USERPROFILE%\\Documents", Write = false }];
            store.Save(migrated);

            store.EnsureInitialized();

            AgentOptions kept = store.LoadOrDefault();
            Assert.False(kept.Files.UseDefaultRoots);
            Assert.Equal("%USERPROFILE%\\Documents", Assert.Single(kept.Files.AllowedRoots).Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentPaths.DataDirectoryVariable, previous);

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp litter, not a failure.
            }
        }
    }

    // ---------------------------------------------------------------------
    // Firewall ranges for VPN access
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("100.64.0.0/10", "100.64.0.0/10")]
    [InlineData("  10.8.0.0/24 ", "10.8.0.0/24")]
    [InlineData("192.168.191.0/24", "192.168.191.0/24")]
    [InlineData("fd7a:115c:a1e0::/48", "fd7a:115c:a1e0::/48")]
    [InlineData("100.100.100.100/32", "100.100.100.100/32")]
    public void AWellFormedRangeIsPassedToTheFirewallInCanonicalForm(string entry, string expected)
    {
        Assert.Equal(expected, ServiceInstaller.NormalizeCidr(entry));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("100.64.0.0")]
    [InlineData("100.64.0.0/")]
    [InlineData("100.64.0.0/33")]
    [InlineData("100.64.0.0/-1")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("any")]
    [InlineData("tailscale")]
    [InlineData("100.64.0.0/10 profile=public")]
    [InlineData("100.64.0.0/10,0.0.0.0/1")]
    [InlineData("100.64.0.0/10\" action=allow")]
    [InlineData("100.64.0.0/+10")]
    public void AnythingElseIsDroppedRatherThanReachingTheCommandLine(string? entry)
    {
        Assert.Null(ServiceInstaller.NormalizeCidr(entry));
    }

    [Fact]
    public void TailscalesAddressRangeIsAllowedByDefault()
    {
        Assert.Equal(["100.64.0.0/10"], new AgentOptions().Network.RemoteAccessSubnets);
        Assert.Empty(new AgentOptions().Network.SubnetAllowlist);
    }

    // ---------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------

    private sealed class FakeSystemInfo : ISystemInfoProvider
    {
        public string HostName => "TEST-PC";

        public string OsVersion => "Windows (test)";

        public string Architecture => "X64";

        public int CpuCount => 1;

        public long TotalMemoryMb => 1024;

        public long UptimeSeconds => 1;

        public int? BatteryPercent => null;

        public bool? OnAcPower => null;

        public IReadOnlyList<string> GetLocalAddresses() => ["192.168.1.10", "100.101.102.103"];
    }

    private sealed class FakeSessionState : ISessionStateProvider
    {
        public InteractiveSessionState State => InteractiveSessionState.Active;

        public string? UserName => "tester";

        public int? ActiveSessionId => 1;

        public event EventHandler<InteractiveSessionState>? StateChanged
        {
            add { }
            remove { }
        }
    }
}
