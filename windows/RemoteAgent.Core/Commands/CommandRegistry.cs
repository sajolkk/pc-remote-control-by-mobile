using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Commands;

/// <summary>
/// Maps command names to the handlers registered in this host.
/// </summary>
/// <remarks>
/// Built once at startup from the DI-registered handlers, and validated against
/// <see cref="CommandCatalog"/> at that moment so that a mistake is a startup
/// crash rather than a runtime surprise on a remote request. Specifically it
/// rejects a handler for an undeclared command, a handler registered in the wrong
/// host, and two handlers claiming the same command.
/// </remarks>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers;

    /// <summary>
    /// Builds the registry for <paramref name="role"/> from the available handlers.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A handler is undeclared, duplicated, or belongs in the other host.
    /// </exception>
    public CommandRegistry(AgentRole role, IEnumerable<ICommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        Role = role;
        _handlers = new Dictionary<string, ICommandHandler>(StringComparer.Ordinal);

        foreach (ICommandHandler handler in handlers)
        {
            if (!CommandCatalog.TryGet(handler.Command, out CommandDescriptor descriptor))
            {
                throw new InvalidOperationException(
                    $"Handler {handler.GetType().Name} serves '{handler.Command}', which is not declared in " +
                    "CommandCatalog. Every command must be declared there before it can be dispatched.");
            }

            CommandTarget expected = role == AgentRole.Service ? CommandTarget.Service : CommandTarget.Session;
            if (descriptor.Target != expected && descriptor.Target != CommandTarget.Either)
            {
                throw new InvalidOperationException(
                    $"Handler {handler.GetType().Name} serves '{handler.Command}', which the catalog assigns to " +
                    $"{descriptor.Target} but was registered in the {role} host.");
            }

            if (!_handlers.TryAdd(handler.Command, handler))
            {
                throw new InvalidOperationException(
                    $"Two handlers claim the command '{handler.Command}': " +
                    $"{_handlers[handler.Command].GetType().Name} and {handler.GetType().Name}.");
            }
        }
    }

    /// <summary>Which host this registry belongs to.</summary>
    public AgentRole Role { get; }

    /// <summary>Command names this host implements.</summary>
    public IReadOnlyCollection<string> ImplementedCommands => _handlers.Keys;

    /// <summary>Finds the handler for a command, if this host implements it.</summary>
    public bool TryGetHandler(string command, out ICommandHandler handler) =>
        _handlers.TryGetValue(command, out handler!);

    /// <summary>Whether this host implements the command.</summary>
    public bool Implements(string command) => _handlers.ContainsKey(command);
}
