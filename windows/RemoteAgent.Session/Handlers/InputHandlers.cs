using System.Text.Json.Nodes;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Input;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Session.Handlers;

// The eight input.* commands (Permission.ControlInput), Phase 4.
//
// These handlers are deliberately empty of logic. Each reads its arguments, hands them to the
// InputController along with the caller's connection id, and translates the outcome into a wire
// result. Everything that decides anything — validation, coordinate mapping, held-key tracking —
// is in RemoteAgent.Core.Input, where it is unit tested with a fake injector.
//
// They run synchronously on the IPC pump, in arrival order, which is what guarantees a button-up
// is injected after the moves that preceded it. Nothing here logs: arguments are coordinates, key
// codes and typed text, none of which may reach a log (§7.5), and the dispatcher already demotes
// the per-command line for this family to Debug.

/// <summary>Shared translation from an <see cref="InputResult"/> to a wire result.</summary>
internal static class InputResults
{
    /// <summary>Maps an outcome onto the contract's result and error codes.</summary>
    internal static Task<CommandResult> ToCommandResult(InputResult result) => Task.FromResult(result.Status switch
    {
        // The contract's result for every input command is an empty object, not an absent one.
        InputStatus.Ok => CommandResult.SuccessRaw(new JsonObject()),

        InputStatus.InvalidArguments => CommandResult.InvalidArguments(result.Detail ?? "Invalid input arguments."),

        InputStatus.MonitorNotFound => CommandResult.NotFound("That monitor"),

        InputStatus.DesktopLocked => CommandResult.Fail(
            ErrorCodes.DesktopLocked,
            result.Detail ?? "The PC is locked.",
            retryable: true),

        InputStatus.SecureDesktop => CommandResult.Fail(
            ErrorCodes.SecureDesktopActive,
            result.Detail ?? "A Windows security prompt is showing on the PC.",
            retryable: true),

        _ => CommandResult.Fail(
            ErrorCodes.AccessDenied,
            result.Detail ?? "Windows did not accept the input.",
            retryable: true),
    });
}

/// <summary>Moves the pointer, to a point on a monitor or by a number of pixels.</summary>
public sealed class InputMouseMoveHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputMouseMoveHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputMouseMove;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out MouseMoveArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.MouseMove(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>Presses or releases one mouse button: the two halves of a drag.</summary>
public sealed class InputMouseButtonHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputMouseButtonHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputMouseButton;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out MouseButtonArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.MouseButtonChange(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>Clicks or double-clicks, as one atomic sequence.</summary>
public sealed class InputMouseClickHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputMouseClickHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputMouseClick;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out MouseClickArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.MouseClick(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>Turns the wheel, vertically or horizontally.</summary>
public sealed class InputScrollHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputScrollHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputScroll;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out ScrollArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.Scroll(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>Presses or releases one key by virtual-key code.</summary>
public sealed class InputKeyHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputKeyHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputKey;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out KeyArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.Key(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>Types a string as Unicode, independent of the PC's keyboard layout.</summary>
public sealed class InputTextHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputTextHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputText;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out TextArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.Text(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>Sends a key combination: pressed in order, released in reverse, atomically.</summary>
public sealed class InputShortcutHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputShortcutHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputShortcut;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        context.TryReadArgs(out ShortcutArgs args, out CommandResult? failure)
            ? InputResults.ToCommandResult(_input.Shortcut(context.Caller.ConnectionId, args))
            : Task.FromResult(failure!);
}

/// <summary>
/// Releases every key and button this connection is holding.
/// </summary>
/// <remarks>
/// The phone sends this when its control surface loses focus. The same release also happens
/// without being asked — when the connection closes and when the session locks — because the
/// case that matters most is the one where the phone is no longer able to ask.
/// </remarks>
public sealed class InputReleaseAllHandler : ICommandHandler
{
    private readonly InputController _input;

    /// <summary>Creates the handler.</summary>
    public InputReleaseAllHandler(InputController input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public string Command => CommandNames.InputReleaseAll;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        InputResults.ToCommandResult(_input.ReleaseAll(context.Caller.ConnectionId));
}
