namespace RemoteAgent.Service.Network;

/// <summary>
/// Where the control channel is actually listening.
/// </summary>
/// <remarks>
/// <para>A tiny shared holder rather than a reference to the listener itself, for two reasons.</para>
///
/// <para>The design reason: a command handler has no business knowing about sockets. It needs one
/// integer — the port to report — and depending on the whole listener to get it couples the
/// command layer to the transport layer, which §13 keeps apart.</para>
///
/// <para>The practical reason: it breaks a dependency cycle. The listener needs the dispatcher,
/// the dispatcher needs the command registry, and the registry constructs every handler — so a
/// handler that depends on the listener closes a loop. Microsoft's DI container detects cycles
/// and reports them clearly, <em>unless</em> the services involved are registered with factory
/// lambdas, which are opaque to its call-site chain. Several registrations here are factories, so
/// the cycle recursed until the stack overflowed, killing the process with no exception and no log
/// line. Breaking the cycle structurally is a better fix than relying on the container to
/// complain.</para>
/// </remarks>
public sealed class ListenerEndpointStatus
{
    /// <summary>The TCP port in use, or 0 before the listener has bound one.</summary>
    public int Port { get; private set; }

    /// <summary>Whether the listener is accepting connections.</summary>
    public bool IsListening { get; private set; }

    /// <summary>Called by the listener once it has bound a port.</summary>
    public void SetListening(int port)
    {
        Port = port;
        IsListening = true;
    }

    /// <summary>Called when the listener stops.</summary>
    public void SetStopped() => IsListening = false;
}
