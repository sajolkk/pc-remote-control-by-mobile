using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Session.Handlers;

/// <summary>
/// Lists applications known to this PC.
/// </summary>
/// <remarks>
/// Runs in the session agent because the Start Menu, the package list and the process/window
/// state all belong to the signed-in user. A service in Session 0 sees a different Start Menu
/// and no windows at all (§4).
/// </remarks>
public sealed class AppListHandler : ICommandHandler
{
    private readonly IAppCatalog _catalog;

    /// <summary>Creates the handler.</summary>
    public AppListHandler(IAppCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Command => CommandNames.AppList;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out AppListArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        AppListResult result = await _catalog.ListAsync(args, cancellationToken).ConfigureAwait(false);
        return CommandResult.Success(result);
    }
}

/// <summary>
/// Launches an application by catalog id.
/// </summary>
/// <remarks>
/// The id is the security boundary. It is a hash this PC produced while enumerating its own
/// Start Menu, so a caller can only ever name something already present here — there is no
/// parameter through which a path or command line could arrive (§7.3).
/// </remarks>
public sealed class AppLaunchHandler : ICommandHandler
{
    private readonly IAppCatalog _catalog;

    /// <summary>Creates the handler.</summary>
    public AppLaunchHandler(IAppCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Command => CommandNames.AppLaunch;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out AppTargetArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(args.AppId))
        {
            return CommandResult.InvalidArguments("An application id is required.");
        }

        AppActionResult result = await _catalog.LaunchAsync(args.AppId, cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? CommandResult.Success(result)
            : CommandResult.Fail(ErrorCodes.NotFound, result.Detail ?? "The application could not be launched.");
    }
}

/// <summary>
/// Brings an application's window to the foreground.
/// </summary>
/// <remarks>
/// Reports failure honestly. Windows blocks focus changes from background processes, so this can
/// legitimately fail even when everything is correct, and a client that believes it succeeded
/// would leave the user wondering why nothing happened (§12.2).
/// </remarks>
public sealed class AppFocusHandler : ICommandHandler
{
    private readonly IAppCatalog _catalog;

    /// <summary>Creates the handler.</summary>
    public AppFocusHandler(IAppCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Command => CommandNames.AppFocus;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out AppTargetArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(args.AppId))
        {
            return CommandResult.InvalidArguments("An application id is required.");
        }

        AppActionResult result = await _catalog.FocusAsync(args.AppId, cancellationToken).ConfigureAwait(false);

        // Returned as a success with detail rather than a failure: the app may well have been
        // restored even when Windows refused to raise it, so the client should show the outcome
        // rather than an error.
        return CommandResult.Success(result);
    }
}

/// <summary>
/// Closes an application, gracefully by default.
/// </summary>
/// <remarks>
/// Force must be asked for explicitly. A graceful close gives the application its chance to
/// prompt about unsaved work — on the PC's own screen, which is the right place for that
/// decision — and only an explicit second request discards it (§4).
/// </remarks>
public sealed class AppCloseHandler : ICommandHandler
{
    private const int DefaultTimeoutMs = 5000;

    private readonly IAppCatalog _catalog;

    /// <summary>Creates the handler.</summary>
    public AppCloseHandler(IAppCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public string Command => CommandNames.AppClose;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out AppCloseArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(args.AppId))
        {
            return CommandResult.InvalidArguments("An application id is required.");
        }

        AppActionResult result = await _catalog
            .CloseAsync(args.AppId, args.Force, args.TimeoutMs ?? DefaultTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        return CommandResult.Success(result);
    }
}
