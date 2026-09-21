using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Validation;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Session.Handlers;

/// <summary>Lists browsers installed on this PC.</summary>
public sealed class BrowserListHandler : ICommandHandler
{
    private readonly IBrowserController _browsers;

    /// <summary>Creates the handler.</summary>
    public BrowserListHandler(IBrowserController browsers)
    {
        _browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
    }

    /// <inheritdoc />
    public string Command => CommandNames.BrowserList;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        BrowserListResult result = await _browsers.ListAsync(cancellationToken).ConfigureAwait(false);
        return CommandResult.Success(result);
    }
}

/// <summary>Opens a browser at its start page.</summary>
public sealed class BrowserOpenHandler : ICommandHandler
{
    private readonly IBrowserController _browsers;

    /// <summary>Creates the handler.</summary>
    public BrowserOpenHandler(IBrowserController browsers)
    {
        _browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
    }

    /// <inheritdoc />
    public string Command => CommandNames.BrowserOpen;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out BrowserTargetArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        AppActionResult result = await _browsers.OpenAsync(args.BrowserId, cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? CommandResult.Success(result)
            : CommandResult.Fail(ErrorCodes.NotFound, result.Detail ?? "The browser could not be opened.");
    }
}

/// <summary>
/// Opens a URL in a browser.
/// </summary>
/// <remarks>
/// <para>The highest-risk command in the system, because a URL is the one piece of free-form
/// text that reaches the Windows shell. It is validated here <em>and</em> again in the
/// controller: only absolute http/https, no embedded credentials, no control characters, length
/// capped, and the normalized form produced by the parser is what gets used rather than the
/// caller's original string.</para>
///
/// <para>Validating twice is intentional. A validator that only runs at the edge is one
/// refactor away from being bypassed, and the cost of checking again is a few microseconds on a
/// command a human triggered.</para>
/// </remarks>
public sealed class BrowserOpenUrlHandler : ICommandHandler
{
    private readonly IBrowserController _browsers;
    private readonly ILogger<BrowserOpenUrlHandler> _logger;

    /// <summary>Creates the handler.</summary>
    public BrowserOpenUrlHandler(IBrowserController browsers, ILogger<BrowserOpenUrlHandler> logger)
    {
        _browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Command => CommandNames.BrowserOpenUrl;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out BrowserOpenUrlArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        if (!UrlValidator.TryValidate(args.Url, out string normalized, out string? error))
        {
            _logger.LogWarning(
                "Rejected a URL from device {DeviceId}: {Reason}",
                context.Caller.DeviceId,
                error);

            return CommandResult.InvalidArguments(error ?? "The URL is not valid.");
        }

        AppActionResult result = await _browsers
            .OpenUrlAsync(normalized, args.BrowserId, cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? CommandResult.Success(result)
            : CommandResult.Fail(ErrorCodes.NotFound, result.Detail ?? "The URL could not be opened.");
    }
}

/// <summary>Closes a browser.</summary>
public sealed class BrowserCloseHandler : ICommandHandler
{
    private readonly IBrowserController _browsers;

    /// <summary>Creates the handler.</summary>
    public BrowserCloseHandler(IBrowserController browsers)
    {
        _browsers = browsers ?? throw new ArgumentNullException(nameof(browsers));
    }

    /// <inheritdoc />
    public string Command => CommandNames.BrowserClose;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out BrowserCloseArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        AppActionResult result = await _browsers
            .CloseAsync(args.BrowserId, args.Force, cancellationToken)
            .ConfigureAwait(false);

        return CommandResult.Success(result);
    }
}

/// <summary>
/// Locks the workstation from inside the interactive session.
/// </summary>
/// <remarks>
/// The service asks the agent to do this rather than doing it itself, because
/// <c>LockWorkStation</c> acts on the calling thread's desktop and a Session 0 service has no
/// visible desktop to lock (§10). Locking works; unlocking remotely does not, and is out of
/// scope by design (§12.1).
/// </remarks>
public sealed class SessionLockHandler : ICommandHandler
{
    private readonly IWorkstationLocker _locker;

    /// <summary>Creates the handler.</summary>
    public SessionLockHandler(IWorkstationLocker locker)
    {
        _locker = locker ?? throw new ArgumentNullException(nameof(locker));
    }

    /// <inheritdoc />
    public string Command => CommandNames.SystemLock;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        bool locked = await _locker.LockAsync(cancellationToken).ConfigureAwait(false);

        return locked
            ? CommandResult.Success(new PowerActionResult { Accepted = true })
            : CommandResult.Fail(ErrorCodes.AccessDenied, "Windows refused to lock the workstation.");
    }
}
