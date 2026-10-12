using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// The catalog entries, capability names and logging rule behind full remote control.
/// </summary>
/// <remarks>
/// The command names and capability strings are a contract shared with the phone, which is being
/// written against the same document by other people. These tests pin the literal strings, so a
/// rename on this side fails here rather than as a feature that silently never appears on a phone.
/// </remarks>
public sealed class RemoteControlCatalogTests
{
    private static readonly AuthorizationGate Gate = new();

    private static CallerIdentity Caller(Permission permissions) =>
        new("device-1", "Test device", permissions, CommandStage.Authenticated, "conn-1", "192.168.1.50");

    /// <summary>Every command the contract defines, with the permission and host it must have.</summary>
    public static TheoryData<string, Permission> ContractCommands() => new()
    {
        { "input.mouse_move", Permission.ControlInput },
        { "input.mouse_button", Permission.ControlInput },
        { "input.mouse_click", Permission.ControlInput },
        { "input.scroll", Permission.ControlInput },
        { "input.key", Permission.ControlInput },
        { "input.text", Permission.ControlInput },
        { "input.shortcut", Permission.ControlInput },
        { "input.release_all", Permission.ControlInput },
        { "window.list", Permission.LaunchApps },
        { "window.action", Permission.LaunchApps },
        { "file.list", Permission.ManageFiles },
        { "file.open", Permission.ManageFiles },
        { "file.create", Permission.ManageFiles },
        { "file.rename", Permission.ManageFiles },
        { "file.copy", Permission.ManageFiles },
        { "file.move", Permission.ManageFiles },
        { "file.delete", Permission.ManageFiles },
    };

    [Theory]
    [MemberData(nameof(ContractCommands))]
    public void CommandIsDeclaredForTheSessionAgentWithItsPermission(string name, Permission permission)
    {
        Assert.True(CommandCatalog.TryGet(name, out CommandDescriptor descriptor), $"'{name}' is not in the catalog.");
        Assert.Equal(permission, descriptor.RequiredPermission);
        Assert.Equal(CommandTarget.Session, descriptor.Target);
        Assert.Equal(CommandStage.Authenticated, descriptor.Stage);
    }

    [Theory]
    [MemberData(nameof(ContractCommands))]
    public void CommandIsAllowedWithExactlyItsPermissionAndRefusedWithEveryOther(string name, Permission permission)
    {
        Assert.True(Gate.Evaluate(name, Caller(permission)).IsAllowed);

        // Holding every other permission is not enough. In particular a device with the default
        // grant (view and input) reaches no file or window command.
        AuthorizationDecision withoutIt = Gate.Evaluate(name, Caller(PermissionSet.All & ~permission));
        Assert.Equal(AuthorizationOutcome.PermissionDenied, withoutIt.Outcome);
    }

    [Fact]
    public void TheDefaultGrantCoversInputButNotWindowsOrFiles()
    {
        CallerIdentity fresh = Caller(PermissionSet.Default);

        Assert.True(Gate.Evaluate(CommandNames.InputMouseMove, fresh).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.WindowAction, fresh).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.FileDelete, fresh).IsAllowed);
        Assert.False(Gate.Evaluate(CommandNames.FileList, fresh).IsAllowed);
    }

    [Fact]
    public void ConstantsCarryTheContractsExactStrings()
    {
        Assert.Equal("window.list", CommandNames.WindowList);
        Assert.Equal("window.action", CommandNames.WindowAction);
        Assert.Equal("file.list", CommandNames.FileList);
        Assert.Equal("file.open", CommandNames.FileOpen);
        Assert.Equal("file.create", CommandNames.FileCreate);
        Assert.Equal("file.rename", CommandNames.FileRename);
        Assert.Equal("file.copy", CommandNames.FileCopy);
        Assert.Equal("file.move", CommandNames.FileMove);
        Assert.Equal("file.delete", CommandNames.FileDelete);

        Assert.Equal("input.mouse", CapabilityNames.InputMouse);
        Assert.Equal("input.keyboard", CapabilityNames.InputKeyboard);
        Assert.Equal("windows", CapabilityNames.Windows);
        Assert.Equal("files", CapabilityNames.Files);
    }

    [Fact]
    public void PermissionNamesOnTheWireAreUnchanged()
    {
        Assert.Equal(
            ["ViewScreen", "ControlInput", "LaunchApps", "ManageFiles", "PowerControls", "Clipboard"],
            PermissionSet.ToNames(PermissionSet.All));
    }

    [Fact]
    public void TransferCommandsStayDeclaredSoTheyAnswerNotSupportedRatherThanUnknown()
    {
        foreach (string name in new[] { CommandNames.FileUpload, CommandNames.FileDownload, CommandNames.FileCancel })
        {
            Assert.True(CommandCatalog.TryGet(name, out CommandDescriptor descriptor));
            Assert.Equal(Permission.ManageFiles, descriptor.RequiredPermission);
        }
    }

    // --- Logging of the hot path ---

    [Fact]
    public async Task InputCommandsAreNotLoggedAtInformation()
    {
        var logger = new RecordingLogger();
        CommandDispatcher dispatcher = CreateDispatcher(logger, new StubHandler(CommandNames.InputMouseMove));

        // Arguments shaped like the real thing, including values that must never be recorded.
        JsonElement args = JsonSerializer.SerializeToElement(new { x = 0.25, y = 0.75 });

        for (int i = 0; i < 60; i++)
        {
            CommandResult result = await dispatcher.DispatchAsync(
                CommandNames.InputMouseMove,
                args,
                "req",
                Caller(PermissionSet.Default),
                CancellationToken.None);

            Assert.True(result.Ok);
        }

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task TypedTextAndKeyCodesNeverReachTheLogAtAnyLevel()
    {
        var logger = new RecordingLogger();
        CommandDispatcher dispatcher = CreateDispatcher(
            logger,
            new StubHandler(CommandNames.InputText),
            new StubHandler(CommandNames.InputKey),
            new StubHandler(CommandNames.InputShortcut, fail: true));

        const string secret = "hunter2-correct-horse";

        await dispatcher.DispatchAsync(
            CommandNames.InputText,
            JsonSerializer.SerializeToElement(new { text = secret }),
            "r1",
            Caller(PermissionSet.Default),
            CancellationToken.None);

        await dispatcher.DispatchAsync(
            CommandNames.InputKey,
            JsonSerializer.SerializeToElement(new { vk = 7741, down = true }),
            "r2",
            Caller(PermissionSet.Default),
            CancellationToken.None);

        // A failing command and a refused one take different logging paths; neither may leak.
        await dispatcher.DispatchAsync(
            CommandNames.InputShortcut,
            JsonSerializer.SerializeToElement(new { keys = new[] { 7741, 7742 } }),
            "r3",
            Caller(PermissionSet.Default),
            CancellationToken.None);

        await dispatcher.DispatchAsync(
            CommandNames.InputText,
            JsonSerializer.SerializeToElement(new { text = secret }),
            "r4",
            Caller(Permission.ViewScreen),
            CancellationToken.None);

        // Debug and Trace are recorded by this logger too, so this covers every level.
        Assert.NotEmpty(logger.Entries);
        Assert.DoesNotContain(logger.Entries, entry => entry.Text.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Text.Contains("7741", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Text.Contains("7742", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherCommandsAreStillAuditedAtInformation()
    {
        var logger = new RecordingLogger();
        CommandDispatcher dispatcher = CreateDispatcher(logger, new StubHandler(CommandNames.FileDelete));

        await dispatcher.DispatchAsync(
            CommandNames.FileDelete,
            null,
            "req",
            Caller(PermissionSet.All),
            CancellationToken.None);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Text.Contains("file.delete", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheInputFamilyIsTreatedAsHighFrequency()
    {
        foreach (CommandDescriptor descriptor in CommandCatalog.All)
        {
            Assert.Equal(
                descriptor.Name.StartsWith("input.", StringComparison.Ordinal),
                CommandDispatcher.IsHighFrequency(descriptor.Name));
        }
    }

    private static CommandDispatcher CreateDispatcher(RecordingLogger logger, params ICommandHandler[] handlers) =>
        new(new CommandRegistry(AgentRole.Session, handlers), Gate, logger, sessionBridge: null);

    private sealed class StubHandler(string command, bool fail = false) : ICommandHandler
    {
        public string Command { get; } = command;

        public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
            Task.FromResult(fail
                ? CommandResult.Fail(ErrorCodes.SecureDesktopActive, "A security prompt is showing.")
                : CommandResult.Success());
    }

    /// <summary>A logger with every level enabled that keeps the rendered text of each entry.</summary>
    private sealed class RecordingLogger : ILogger<CommandDispatcher>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // The formatted message plus every structured property value: a secret passed as a
            // property is a leak even if the message template happens not to print it.
            string text = formatter(state, exception);

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                // The duration is left out: its unrounded digits are noise that could, by chance,
                // contain one of the marker numbers these tests search for.
                text += " | " + string.Join(
                    " ",
                    properties.Where(static p => p.Key != "DurationMs").Select(static p => $"{p.Key}={p.Value}"));
            }

            Entries.Add((logLevel, text));
        }
    }
}
