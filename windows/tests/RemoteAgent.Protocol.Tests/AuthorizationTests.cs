using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for the authorization gate and the command catalog.
/// </summary>
/// <remarks>
/// This is the security test that matters most, because the gate is the single decision point for
/// every remote command. The cases below are the ones an attacker would actually try: an unknown
/// command name, a real command before authenticating, and a real command without the permission it
/// requires. Each must be refused, and refused for the right reason — a caller should not be able to
/// learn what permission a command needs before proving who it is.
/// </remarks>
public sealed class AuthorizationTests
{
    private static readonly AuthorizationGate Gate = new();

    private static CallerIdentity Caller(
        Permission permissions,
        CommandStage stage = CommandStage.Authenticated,
        string? deviceId = "device-1") =>
        new(deviceId, "Test device", permissions, stage, "conn-1", "192.168.1.50");

    [Fact]
    public void RefusesAnUnknownCommand()
    {
        AuthorizationDecision decision = Gate.Evaluate("system.format_disk", Caller(PermissionSet.All));

        Assert.Equal(AuthorizationOutcome.UnknownCommand, decision.Outcome);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void RefusesAnUnknownCommandEvenForAFullyPermittedCaller()
    {
        // The catalog, not the permission set, is what makes a command dispatchable. A device with
        // every permission still cannot invoke something that was never declared.
        foreach (string name in new[] { string.Empty, "  ", "../../etc/passwd", "cmd.exe", "SYSTEM.LOCK" })
        {
            AuthorizationDecision decision = Gate.Evaluate(name, Caller(PermissionSet.All));
            Assert.Equal(AuthorizationOutcome.UnknownCommand, decision.Outcome);
        }
    }

    [Fact]
    public void CommandNamesAreCaseSensitive()
    {
        // Deliberate: the wire contract is exact. Accepting "System.Lock" would mean the catalog has
        // more entries than it appears to, which is the opposite of an auditable allowlist.
        Assert.Equal(AuthorizationOutcome.Allowed, Gate.Evaluate("system.lock", Caller(PermissionSet.All)).Outcome);
        Assert.Equal(AuthorizationOutcome.UnknownCommand, Gate.Evaluate("System.Lock", Caller(PermissionSet.All)).Outcome);
    }

    [Fact]
    public void RefusesAnAuthenticatedCommandFromAPairedButUnauthenticatedCaller()
    {
        AuthorizationDecision decision = Gate.Evaluate(
            CommandNames.SystemShutdown,
            Caller(PermissionSet.All, CommandStage.Paired));

        Assert.Equal(AuthorizationOutcome.Unauthenticated, decision.Outcome);
    }

    [Fact]
    public void RefusesEverythingButPairingFromAnUnpairedCaller()
    {
        CallerIdentity unpaired = CallerIdentity.Unpaired("conn-1", "192.168.1.50");

        foreach (CommandDescriptor descriptor in CommandCatalog.All)
        {
            AuthorizationDecision decision = Gate.Evaluate(descriptor.Name, unpaired);

            if (descriptor.Stage == CommandStage.Unpaired)
            {
                Assert.True(
                    decision.IsAllowed,
                    $"'{descriptor.Name}' is declared Unpaired and should be reachable before pairing.");
            }
            else
            {
                Assert.False(
                    decision.IsAllowed,
                    $"'{descriptor.Name}' must not be reachable before pairing.");
                Assert.Equal(AuthorizationOutcome.NotPaired, decision.Outcome);
            }
        }
    }

    [Fact]
    public void OnlyPairingIsReachableBeforePairing()
    {
        // Pins the size of the pre-authentication attack surface. If a future change makes another
        // command reachable without a pairing record, this test fails and forces the decision to be
        // deliberate rather than incidental.
        string[] unpairedCommands = CommandCatalog.All
            .Where(static d => d.Stage == CommandStage.Unpaired)
            .Select(static d => d.Name)
            .ToArray();

        Assert.Equal([CommandNames.PairRequest], unpairedCommands);
    }

    [Fact]
    public void OnlyTheHandshakeIsReachableWithoutASessionToken()
    {
        string[] pairedStageCommands = CommandCatalog.All
            .Where(static d => d.Stage == CommandStage.Paired)
            .Select(static d => d.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([CommandNames.Hello, CommandNames.Ping], pairedStageCommands);
    }

    [Theory]
    [InlineData(CommandNames.SystemShutdown, Permission.PowerControls)]
    [InlineData(CommandNames.SystemRestart, Permission.PowerControls)]
    [InlineData(CommandNames.SystemSleep, Permission.PowerControls)]
    [InlineData(CommandNames.SystemLock, Permission.PowerControls)]
    [InlineData(CommandNames.AppLaunch, Permission.LaunchApps)]
    [InlineData(CommandNames.AppClose, Permission.LaunchApps)]
    [InlineData(CommandNames.BrowserOpenUrl, Permission.LaunchApps)]
    [InlineData(CommandNames.InputMouseClick, Permission.ControlInput)]
    [InlineData(CommandNames.InputKey, Permission.ControlInput)]
    [InlineData(CommandNames.ScreenScreenshot, Permission.ViewScreen)]
    [InlineData(CommandNames.ClipboardGet, Permission.Clipboard)]
    [InlineData(CommandNames.FileUpload, Permission.ManageFiles)]
    public void RequiresTheDeclaredPermission(string command, Permission required)
    {
        // Granted: allowed.
        Assert.True(Gate.Evaluate(command, Caller(required)).IsAllowed);

        // Every other single permission: refused. This catches a command wired to the wrong group,
        // which is the mistake that would silently widen what a device can do.
        foreach (Permission other in new[]
                 {
                     Permission.ViewScreen, Permission.ControlInput, Permission.LaunchApps,
                     Permission.ManageFiles, Permission.PowerControls, Permission.Clipboard,
                 })
        {
            if (other == required)
            {
                continue;
            }

            AuthorizationDecision decision = Gate.Evaluate(command, Caller(other));

            Assert.False(
                decision.IsAllowed,
                $"'{command}' should require {required} but was allowed with only {other}.");

            Assert.Equal(AuthorizationOutcome.PermissionDenied, decision.Outcome);
        }
    }

    [Fact]
    public void DefaultPermissionsDoNotIncludePowerOrFiles()
    {
        // The defaults granted at pairing are deliberately narrow. Power and file access are the two
        // groups whose misuse is hardest to undo, so they must never be implicit.
        CallerIdentity caller = Caller(PermissionSet.Default);

        Assert.False(Gate.Evaluate(CommandNames.SystemShutdown, caller).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.SystemRestart, caller).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.FileUpload, caller).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.ClipboardGet, caller).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.AppLaunch, caller).IsAllowed);

        // But viewing and controlling — what someone pairs a remote-control app to do — are allowed.
        Assert.True(Gate.Evaluate(CommandNames.ScreenScreenshot, caller).IsAllowed);
        Assert.True(Gate.Evaluate(CommandNames.InputMouseClick, caller).IsAllowed);
    }

    [Fact]
    public void StatusCommandsNeedNoPermissionBeyondPairing()
    {
        CallerIdentity caller = Caller(Permission.None);

        Assert.True(Gate.Evaluate(CommandNames.DeviceInfo, caller).IsAllowed);
        Assert.True(Gate.Evaluate(CommandNames.SystemState, caller).IsAllowed);
        Assert.True(Gate.Evaluate(CommandNames.PermissionsList, caller).IsAllowed);
        Assert.True(Gate.Evaluate(CommandNames.SessionRenew, caller).IsAllowed);
    }

    [Fact]
    public void EveryCatalogEntryIsUniqueAndResolvable()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (CommandDescriptor descriptor in CommandCatalog.All)
        {
            Assert.True(names.Add(descriptor.Name), $"'{descriptor.Name}' is declared more than once.");
            Assert.True(CommandCatalog.TryGet(descriptor.Name, out CommandDescriptor found));
            Assert.Equal(descriptor, found);
        }
    }

    [Fact]
    public void RefusalMessageNamesThePermissionWithoutLeakingInternals()
    {
        AuthorizationDecision decision = Gate.Evaluate(CommandNames.SystemShutdown, Caller(Permission.ViewScreen));
        CommandResult failure = decision.ToFailure(CommandNames.SystemShutdown);

        Assert.False(failure.Ok);
        Assert.Equal(ErrorCodes.PermissionDenied, failure.Error!.Code);
        Assert.Contains("PowerControls", failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizesAHostileCommandNameBeforeEchoingIt()
    {
        // The name comes straight from the wire, so it must not be able to inject newlines into a
        // log line or return an oversized message.
        string hostile = "a\r\nFAKE LOG LINE\u0000" + new string('x', 500);

        CommandResult failure = Gate.Evaluate(hostile, Caller(PermissionSet.All)).ToFailure(hostile);

        Assert.DoesNotContain("\n", failure.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", failure.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", failure.Error.Message, StringComparison.Ordinal);
        Assert.True(failure.Error.Message.Length < 200);
    }
}
