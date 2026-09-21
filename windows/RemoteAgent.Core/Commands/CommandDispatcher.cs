using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Commands;

/// <summary>
/// Executes an authorized command, wherever it belongs.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Authorizes and executes one command. Never throws for an expected failure;
    /// every outcome, including a defect, comes back as a <see cref="CommandResult"/>.
    /// </summary>
    Task<CommandResult> DispatchAsync(
        string command,
        System.Text.Json.JsonElement? args,
        string requestId,
        CallerIdentity caller,
        CancellationToken cancellationToken);
}

/// <summary>
/// Forwards a session-scoped command to the user-session agent over IPC.
/// </summary>
/// <remarks>
/// Implemented in the service host by the named-pipe client. Absent (null) in the
/// session host, where session commands are handled locally.
/// </remarks>
public interface ISessionBridge
{
    /// <summary>Whether a session agent is currently connected and usable.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Forwards a command and returns the agent's result. Implementations must
    /// translate a dead or missing agent into
    /// <see cref="CommandResult.SessionUnavailable"/> rather than throwing.
    /// </summary>
    Task<CommandResult> ForwardAsync(
        string command,
        System.Text.Json.JsonElement? args,
        CallerIdentity caller,
        CancellationToken cancellationToken);
}

/// <summary>
/// The one path from a received request to an executed command.
/// </summary>
/// <remarks>
/// Every remote request funnels through here, which is what makes the security
/// properties auditable in one place: authorize, then route, then time, then log.
/// A command declared in the catalog but implemented in neither host returns
/// <see cref="ErrorCodes.NotSupported"/> — that is the mechanism that lets a
/// single build serve PCs with different capabilities (§0).
/// </remarks>
public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly IAuthorizationGate _gate;
    private readonly ISessionBridge? _sessionBridge;
    private readonly ILogger<CommandDispatcher> _logger;

    /// <summary>Creates the dispatcher for one host.</summary>
    /// <param name="registry">Handlers implemented locally.</param>
    /// <param name="gate">The authorization decision point.</param>
    /// <param name="logger">Audit and diagnostic sink.</param>
    /// <param name="sessionBridge">
    /// Forwarder for session-scoped commands. Supplied in the service host, null in
    /// the session host.
    /// </param>
    public CommandDispatcher(
        CommandRegistry registry,
        IAuthorizationGate gate,
        ILogger<CommandDispatcher> logger,
        ISessionBridge? sessionBridge = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionBridge = sessionBridge;
    }

    /// <inheritdoc />
    public async Task<CommandResult> DispatchAsync(
        string command,
        System.Text.Json.JsonElement? args,
        string requestId,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        command ??= string.Empty;

        AuthorizationDecision decision = _gate.Evaluate(command, caller);
        if (!decision.IsAllowed)
        {
            // Refusals are logged at warning: they are the security-relevant events
            // an operator needs to see. The command name is included; arguments are
            // not, because they were never parsed and may be hostile.
            _logger.LogWarning(
                "Command refused. Command={Command} Outcome={Outcome} Device={DeviceId} Connection={ConnectionId} Peer={Peer}",
                command,
                decision.Outcome,
                caller.DeviceId ?? "(unpaired)",
                caller.ConnectionId,
                caller.RemoteAddress);

            return decision.ToFailure(command);
        }

        CommandDescriptor descriptor = decision.Descriptor!;
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            CommandResult result = await ExecuteAsync(descriptor, args, requestId, caller, cancellationToken)
                .ConfigureAwait(false);

            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (result.Ok)
            {
                _logger.LogInformation(
                    "Command executed. Command={Command} Device={DeviceId} DurationMs={DurationMs:F1}",
                    descriptor.Name,
                    caller.DeviceId,
                    elapsed.TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation(
                    "Command failed. Command={Command} Device={DeviceId} Code={Code} DurationMs={DurationMs:F1}",
                    descriptor.Name,
                    caller.DeviceId,
                    result.Error?.Code,
                    elapsed.TotalMilliseconds);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Command cancelled. Command={Command} Device={DeviceId}",
                descriptor.Name,
                caller.DeviceId);

            return CommandResult.Fail(ErrorCodes.Timeout, "The command was cancelled.", retryable: true);
        }
        catch (Exception ex)
        {
            // A handler threw, which means a defect. The detail goes to the local
            // log only: the client gets a generic code, because exception text can
            // contain paths, user names and internal state (§7.5).
            _logger.LogError(
                ex,
                "Command threw. Command={Command} Device={DeviceId}",
                descriptor.Name,
                caller.DeviceId);

            return CommandResult.Fail(ErrorCodes.Internal, "The command failed on the PC.");
        }
    }

    private Task<CommandResult> ExecuteAsync(
        CommandDescriptor descriptor,
        System.Text.Json.JsonElement? args,
        string requestId,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        if (_registry.TryGetHandler(descriptor.Name, out ICommandHandler handler))
        {
            var context = new CommandContext
            {
                RequestId = requestId,
                Command = descriptor.Name,
                Args = args,
                Caller = caller,
                Descriptor = descriptor,
            };

            return handler.HandleAsync(context, cancellationToken);
        }

        // Not implemented here. If the session agent owns it, forward it. An Either command
        // reaches this point only when the service has no local handler for it, in which case
        // forwarding is still the right move.
        if (descriptor.Target is CommandTarget.Session or CommandTarget.Either &&
            _registry.Role == AgentRole.Service)
        {
            if (_sessionBridge is null || !_sessionBridge.IsConnected)
            {
                return Task.FromResult(CommandResult.SessionUnavailable());
            }

            return _sessionBridge.ForwardAsync(descriptor.Name, args, caller, cancellationToken);
        }

        // Declared in the catalog, but no handler exists in this build or on this
        // machine. Reported as unsupported rather than unknown, so the client can
        // distinguish "you sent nonsense" from "this PC can't do that".
        _logger.LogDebug("Command {Command} is declared but not implemented in the {Role} host.",
            descriptor.Name,
            _registry.Role);

        return Task.FromResult(CommandResult.NotSupported($"the '{descriptor.Name}' command"));
    }
}
