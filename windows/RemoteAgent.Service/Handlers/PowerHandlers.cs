using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Service.Handlers;

/// <summary>
/// Shared behaviour for the power commands.
/// </summary>
/// <remarks>
/// <para>Every power command passes two independent gates: the device must hold
/// <see cref="Permission.PowerControls"/> (checked by the authorization gate before this code
/// runs), <em>and</em> the machine's owner must not have disabled that action in configuration.
/// The second gate exists because "a phone I trust to view my screen" and "a phone I trust to
/// shut this machine down mid-render" are different levels of trust, and the PC's owner should
/// be able to draw that line locally rather than only per-device.</para>
///
/// <para>Power actions are logged at warning level. They are the most consequential thing a
/// remote device can do, and they should stand out in a log that is otherwise mostly routine.</para>
/// </remarks>
public abstract class PowerHandlerBase : ICommandHandler
{
    /// <summary>Power controller, implemented by the Windows layer.</summary>
    protected IPowerController Power { get; }

    /// <summary>Live configuration.</summary>
    protected IOptionsMonitor<AgentOptions> Options { get; }

    /// <summary>Diagnostic sink.</summary>
    protected ILogger Logger { get; }

    /// <summary>Creates the base handler.</summary>
    protected PowerHandlerBase(IPowerController power, IOptionsMonitor<AgentOptions> options, ILogger logger)
    {
        Power = power ?? throw new ArgumentNullException(nameof(power));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public abstract string Command { get; }

    /// <inheritdoc />
    public abstract Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken);

    /// <summary>Logs a power action with the device that requested it.</summary>
    protected void LogAction(CommandContext context, string action)
    {
        Logger.LogWarning(
            "{Action} requested by device {DeviceId} ({DeviceName}) from {Address}.",
            action,
            context.Caller.DeviceId,
            context.Caller.DeviceName,
            context.Caller.RemoteAddress);
    }

    /// <summary>Wraps a controller result, translating a refusal into a clear error.</summary>
    protected static CommandResult Translate(PowerActionResult result, string action) =>
        result.Accepted
            ? CommandResult.Success(result)
            : CommandResult.Fail(
                ErrorCodes.NotSupported,
                $"Windows did not accept the {action} request. It may be disabled on this PC, " +
                "or no user may be signed in.");
}

/// <summary>Locks the workstation.</summary>
/// <remarks>
/// Prefers the session agent, because <c>LockWorkStation</c> only works from the interactive
/// desktop. Falls back to disconnecting the session from the service, which produces the same
/// user-visible result — the logon screen, credentials required to return — when no agent is
/// available (§10).
/// </remarks>
public sealed class SystemLockHandler : PowerHandlerBase
{
    private readonly SessionAgentRegistry _agents;

    /// <summary>Creates the handler.</summary>
    public SystemLockHandler(
        IPowerController power,
        SessionAgentRegistry agents,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SystemLockHandler> logger)
        : base(power, options, logger)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <inheritdoc />
    public override string Command => CommandNames.SystemLock;

    /// <inheritdoc />
    public override async Task<CommandResult> HandleAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!Options.CurrentValue.Power.AllowLock)
        {
            return CommandResult.PolicyDenied("Remote lock is disabled on this PC.");
        }

        LogAction(context, "Lock");

        if (_agents.IsConnected)
        {
            CommandResult forwarded = await _agents
                .ForwardAsync(CommandNames.SystemLock, context.Args, context.Caller, cancellationToken)
                .ConfigureAwait(false);

            if (forwarded.Ok)
            {
                return forwarded;
            }

            Logger.LogInformation(
                "The session agent could not lock the workstation; falling back to session disconnect.");
        }

        PowerActionResult result = await Power.LockAsync(cancellationToken).ConfigureAwait(false);
        return Translate(result, "lock");
    }
}

/// <summary>Signs the interactive user out.</summary>
public sealed class SystemSignOutHandler : PowerHandlerBase
{
    /// <summary>Creates the handler.</summary>
    public SystemSignOutHandler(
        IPowerController power,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SystemSignOutHandler> logger)
        : base(power, options, logger)
    {
    }

    /// <inheritdoc />
    public override string Command => CommandNames.SystemSignOut;

    /// <inheritdoc />
    public override async Task<CommandResult> HandleAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!Options.CurrentValue.Power.AllowSignOut)
        {
            return CommandResult.PolicyDenied("Remote sign-out is disabled on this PC.");
        }

        if (!context.TryReadArgs(out PowerActionArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        LogAction(context, args.Force ? "Forced sign-out" : "Sign-out");

        PowerActionResult result = await Power.SignOutAsync(args.Force, cancellationToken).ConfigureAwait(false);
        return Translate(result, "sign-out");
    }
}

/// <summary>Puts the PC to sleep.</summary>
public sealed class SystemSleepHandler : PowerHandlerBase
{
    /// <summary>Creates the handler.</summary>
    public SystemSleepHandler(
        IPowerController power,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SystemSleepHandler> logger)
        : base(power, options, logger)
    {
    }

    /// <inheritdoc />
    public override string Command => CommandNames.SystemSleep;

    /// <inheritdoc />
    public override async Task<CommandResult> HandleAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!Options.CurrentValue.Power.AllowSleep)
        {
            return CommandResult.PolicyDenied("Remote sleep is disabled on this PC.");
        }

        if (!Power.IsSleepSupported)
        {
            return CommandResult.NotSupported("sleep");
        }

        LogAction(context, "Sleep");

        PowerActionResult result = await Power.SleepAsync(cancellationToken).ConfigureAwait(false);
        return Translate(result, "sleep");
    }
}

/// <summary>Restarts Windows.</summary>
public sealed class SystemRestartHandler : PowerHandlerBase
{
    /// <summary>Creates the handler.</summary>
    public SystemRestartHandler(
        IPowerController power,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SystemRestartHandler> logger)
        : base(power, options, logger)
    {
    }

    /// <inheritdoc />
    public override string Command => CommandNames.SystemRestart;

    /// <inheritdoc />
    public override async Task<CommandResult> HandleAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!Options.CurrentValue.Power.AllowRestart)
        {
            return CommandResult.PolicyDenied("Remote restart is disabled on this PC.");
        }

        if (!context.TryReadArgs(out PowerActionArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        LogAction(context, args.Force ? "Forced restart" : "Restart");

        PowerActionResult result = await Power
            .RestartAsync(args.DelaySeconds, args.Force, cancellationToken)
            .ConfigureAwait(false);

        return Translate(result, "restart");
    }
}

/// <summary>Shuts Windows down.</summary>
public sealed class SystemShutdownHandler : PowerHandlerBase
{
    /// <summary>Creates the handler.</summary>
    public SystemShutdownHandler(
        IPowerController power,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SystemShutdownHandler> logger)
        : base(power, options, logger)
    {
    }

    /// <inheritdoc />
    public override string Command => CommandNames.SystemShutdown;

    /// <inheritdoc />
    public override async Task<CommandResult> HandleAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (!Options.CurrentValue.Power.AllowShutdown)
        {
            return CommandResult.PolicyDenied("Remote shutdown is disabled on this PC.");
        }

        if (!context.TryReadArgs(out PowerActionArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        LogAction(context, args.Force ? "Forced shutdown" : "Shutdown");

        PowerActionResult result = await Power
            .ShutdownAsync(args.DelaySeconds, args.Force, cancellationToken)
            .ConfigureAwait(false);

        return Translate(result, "shutdown");
    }
}

/// <summary>Cancels a pending restart or shutdown inside its grace period.</summary>
/// <remarks>
/// Exists because the grace period is only useful if it can be acted on. A user who sees the
/// shutdown warning on their PC can cancel it there; a user who realises from their phone that
/// they hit the wrong button should be able to cancel it from there too.
/// </remarks>
public sealed class SystemAbortShutdownHandler : PowerHandlerBase
{
    /// <summary>Creates the handler.</summary>
    public SystemAbortShutdownHandler(
        IPowerController power,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SystemAbortShutdownHandler> logger)
        : base(power, options, logger)
    {
    }

    /// <inheritdoc />
    public override string Command => CommandNames.SystemAbortShutdown;

    /// <inheritdoc />
    public override async Task<CommandResult> HandleAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        LogAction(context, "Abort shutdown");

        bool cancelled = await Power.AbortShutdownAsync(cancellationToken).ConfigureAwait(false);

        return cancelled
            ? CommandResult.Success(new { cancelled = true })
            : CommandResult.Fail(
                ErrorCodes.NotFound,
                "No shutdown or restart is pending, or it is already past the point of cancellation.");
    }
}
