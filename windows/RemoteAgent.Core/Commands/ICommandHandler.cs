using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Commands;

/// <summary>
/// Implements one command.
/// </summary>
/// <remarks>
/// Handlers are registered in DI and discovered by the registry, so adding a
/// command never means editing a switch statement (§13 rule 5). A handler is
/// reached only after the authorization gate has confirmed the caller's stage and
/// permission, so it does not repeat those checks — but it is still responsible
/// for validating its own arguments and for enforcing any configuration policy
/// specific to it (for example "shutdown is disabled in config").
/// </remarks>
public interface ICommandHandler
{
    /// <summary>
    /// The wire name this handler serves. Must exist in <see cref="CommandCatalog"/>,
    /// and its declared target must match the host this handler is registered in;
    /// the registry verifies both at startup and fails fast otherwise.
    /// </summary>
    string Command { get; }

    /// <summary>Executes the command.</summary>
    Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Which host a component is running in.
/// </summary>
public enum AgentRole
{
    /// <summary>The privileged Windows service, in Session 0.</summary>
    Service,

    /// <summary>The user-session agent, on the interactive desktop.</summary>
    Session,
}
