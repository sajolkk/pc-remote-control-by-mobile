using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteAgent.Ipc;

/// <summary>
/// Pipe naming and access control for service-to-agent IPC (§7.3).
/// </summary>
/// <remarks>
/// <para><b>Naming.</b> A single pipe name with multiple server instances, rather than one
/// pipe per session. The session id is not in the name because the agent learns its own
/// identity from its environment, not from a name it would have to guess, and because a
/// predictable per-session name is one more thing an attacker could try to squat.</para>
///
/// <para><b>Why the ACL cannot be tight.</b> The session agent runs as an ordinary
/// interactive user, so the pipe must be openable by ordinary interactive users. That
/// means the ACL alone cannot answer "is this peer the real agent?" — any process running
/// as the logged-on user could open it. Access is therefore granted to
/// <c>INTERACTIVE</c> (logged-on users only, never network or service logons) and identity
/// is established separately by the one-time spawn token in
/// <see cref="IpcHelloArgs.Token"/>.</para>
///
/// <para><b>What this protects against.</b> Pipe squatting is prevented by
/// <see cref="PipeOptions.FirstPipeInstance"/> on the first server instance: if something
/// already owns the name, creation fails loudly instead of the service silently talking to
/// an impostor. Remote access is prevented by refusing <c>NETWORK</c> — named pipes are
/// reachable over SMB by default, and this service has no business being one of them.</para>
///
/// <para><b>What it does not protect against.</b> A process already running as the
/// logged-on user can open the pipe and attempt a handshake. It will fail the token check,
/// but it can consume a pipe instance. That is an accepted limit: code running as the user
/// can already drive the desktop directly, so it gains nothing here that it did not
/// already have.</para>
/// </remarks>
public static class IpcPipe
{
    /// <summary>The pipe name used for service-to-agent IPC.</summary>
    public const string PipeName = "pcremote-agent-v1";

    /// <summary>
    /// Environment variable through which the service passes the one-time spawn token.
    /// </summary>
    /// <remarks>
    /// An environment variable rather than a command-line argument: any local process can
    /// read another process's command line, while the environment block of a process
    /// running as a different user is not readable without debug privilege.
    /// </remarks>
    public const string SpawnTokenVariable = "PCREMOTE_AGENT_TOKEN";

    /// <summary>Environment variable carrying the session id the service expects.</summary>
    public const string SessionIdVariable = "PCREMOTE_SESSION_ID";

    /// <summary>Maximum concurrent server instances, one per plausible interactive session.</summary>
    public const int MaxServerInstances = 8;

    /// <summary>
    /// Builds the pipe ACL: full control for SYSTEM and Administrators, connect rights for
    /// interactive users, and nothing for anyone else.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // S-1-5-4. Members are users logged on interactively — which the session agent is,
        // and which a remote SMB caller or a service account is not.
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);

        security.AddAccessRule(new PipeAccessRule(
            system,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            administrators,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Enough to connect, exchange messages and read the peer's identity — not enough
        // to create another instance of the pipe or change its ACL.
        security.AddAccessRule(new PipeAccessRule(
            interactive,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        // The identity hosting the pipe needs FullControl, because creating each additional server
        // instance requires CreateNewInstance on the existing pipe's DACL. As a service that is
        // SYSTEM, already granted above; running as an ordinary user (console and portable modes)
        // it is not, and without this the first client connects and every subsequent accept fails
        // with access denied — leaving the agent unable to reconnect after any disconnect.
        using (WindowsIdentity current = WindowsIdentity.GetCurrent())
        {
            if (current.User is { } owner && owner != system)
            {
                security.AddAccessRule(new PipeAccessRule(
                    owner,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }
        }

        return security;
    }

    /// <summary>
    /// Creates a server instance with the correct ACL.
    /// </summary>
    /// <param name="firstInstance">
    /// True for the very first instance created at startup, which adds
    /// <see cref="PipeOptions.FirstPipeInstance"/> so that an existing squatter causes an
    /// immediate, visible failure rather than a silent hijack.
    /// </param>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServerStream(bool firstInstance)
    {
        PipeOptions options = PipeOptions.Asynchronous | PipeOptions.WriteThrough;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            MaxServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            CreateSecurity());
    }

    /// <summary>
    /// Creates the client side, used by the session agent.
    /// </summary>
    /// <remarks>
    /// <see cref="TokenImpersonationLevel.Identification"/> lets the service read the
    /// client's identity for logging without granting it the ability to act as the user.
    /// The service has no need to impersonate the agent, so it does not ask for the right
    /// to.
    /// </remarks>
    public static NamedPipeClientStream CreateClientStream() => new(
        serverName: ".",
        pipeName: PipeName,
        direction: PipeDirection.InOut,
        options: PipeOptions.Asynchronous | PipeOptions.WriteThrough,
        impersonationLevel: TokenImpersonationLevel.Identification,
        inheritability: HandleInheritability.None);
}
