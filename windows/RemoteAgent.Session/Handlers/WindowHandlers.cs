using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Session.Handlers;

/// <summary>
/// Lists the visible top-level application windows — what Alt+Tab shows — foreground first.
/// </summary>
/// <remarks>
/// A session command for the same reason the application list is one: windows belong to the
/// signed-in user's desktop, and a service in Session 0 sees none of them (§4).
/// </remarks>
public sealed class WindowListHandler : ICommandHandler
{
    private readonly IWindowManager _windows;

    /// <summary>Creates the handler.</summary>
    public WindowListHandler(IWindowManager windows)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    /// <inheritdoc />
    public string Command => CommandNames.WindowList;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.Success(new WindowListResult { Windows = [.. _windows.List()] }));
}

/// <summary>
/// Focuses, minimizes, maximizes, restores or politely closes one window.
/// </summary>
/// <remarks>
/// <para>The window id is the security boundary, as the app id is for <c>app.launch</c>: it is a
/// handle this PC produced, and the window manager re-validates on every call that it still names a
/// live, visible, top-level window of the process it was issued for. A stale or invented id is
/// <c>E_NOT_FOUND</c>.</para>
///
/// <para>A refusal by Windows — focus-stealing prevention, or a window that belongs to an elevated
/// application — is returned as <c>succeeded: false</c> with the reason, not as an error. The
/// request was valid and was made; the client should show what happened (§12.2).</para>
/// </remarks>
public sealed class WindowActionHandler : ICommandHandler
{
    private readonly IWindowManager _windows;

    /// <summary>Creates the handler.</summary>
    public WindowActionHandler(IWindowManager windows)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    /// <inheritdoc />
    public string Command => CommandNames.WindowAction;

    /// <inheritdoc />
    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out WindowActionArgs args, out CommandResult? failure))
        {
            return Task.FromResult(failure!);
        }

        if (string.IsNullOrWhiteSpace(args.WindowId))
        {
            return Task.FromResult(CommandResult.InvalidArguments("A window id is required."));
        }

        WindowAction? action = args.Action switch
        {
            "focus" => WindowAction.Focus,
            "minimize" => WindowAction.Minimize,
            "maximize" => WindowAction.Maximize,
            "restore" => WindowAction.Restore,
            "close" => WindowAction.Close,
            _ => null,
        };

        if (action is null)
        {
            return Task.FromResult(CommandResult.InvalidArguments(
                "action must be \"focus\", \"minimize\", \"maximize\", \"restore\" or \"close\"."));
        }

        WindowActionResult? result = _windows.Act(args.WindowId, action.Value);

        return Task.FromResult(result is null
            ? CommandResult.NotFound("That window")
            : CommandResult.Success(result));
    }
}
