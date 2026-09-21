using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Commands;

/// <summary>Why a command was allowed or refused.</summary>
public enum AuthorizationOutcome
{
    /// <summary>The caller may proceed.</summary>
    Allowed,

    /// <summary>No such command is declared in the catalog.</summary>
    UnknownCommand,

    /// <summary>The caller's certificate matches no active pairing record.</summary>
    NotPaired,

    /// <summary>The caller is paired but holds no valid session token for this connection.</summary>
    Unauthenticated,

    /// <summary>The caller is authenticated but lacks the required permission group.</summary>
    PermissionDenied,
}

/// <summary>
/// The verdict on one command attempt.
/// </summary>
public readonly record struct AuthorizationDecision(
    AuthorizationOutcome Outcome,
    CommandDescriptor? Descriptor,
    Permission Required)
{
    /// <summary>Whether dispatch may proceed.</summary>
    public bool IsAllowed => Outcome == AuthorizationOutcome.Allowed;

    /// <summary>Converts a refusal into the failure the caller will receive.</summary>
    public CommandResult ToFailure(string command) => Outcome switch
    {
        AuthorizationOutcome.UnknownCommand => CommandResult.Fail(
            ErrorCodes.UnknownCommand,
            $"Unknown command '{Sanitize(command)}'."),

        AuthorizationOutcome.NotPaired => CommandResult.Fail(
            ErrorCodes.NotPaired,
            "This device is not paired with the PC, or its pairing was revoked."),

        AuthorizationOutcome.Unauthenticated => CommandResult.Fail(
            ErrorCodes.Unauthenticated,
            "A valid session token is required. Complete the handshake first."),

        AuthorizationOutcome.PermissionDenied => CommandResult.Fail(
            ErrorCodes.PermissionDenied,
            $"The '{Required}' permission is not granted to this device. " +
            "Enable it for this device on the PC."),

        _ => CommandResult.Fail(ErrorCodes.Internal, "Authorization failed."),
    };

    /// <summary>
    /// Trims and strips control characters from a caller-supplied command name
    /// before it is echoed back or logged, so a hostile peer cannot inject
    /// newlines into the log or oversize a response.
    /// </summary>
    private static string Sanitize(string value)
    {
        const int limit = 64;
        string trimmed = value.Length > limit ? value[..limit] : value;
        return new string(Array.FindAll(trimmed.ToCharArray(), static c => !char.IsControl(c)));
    }
}

/// <summary>
/// Decides whether a caller may run a command (§7.4).
/// </summary>
public interface IAuthorizationGate
{
    /// <summary>Evaluates one command attempt.</summary>
    AuthorizationDecision Evaluate(string command, CallerIdentity caller);
}

/// <summary>
/// The single authorization decision point for every remote command.
/// </summary>
/// <remarks>
/// Ordering is deliberate. The command is resolved against the catalog first, so
/// an unknown name is refused before anything else happens; then the connection
/// stage, so an unauthenticated caller learns nothing about which permissions a
/// command needs; then the permission itself. Arguments are never deserialized
/// until all three pass, which keeps the parser out of reach of an unauthorized
/// peer (§7.3).
/// </remarks>
public sealed class AuthorizationGate : IAuthorizationGate
{
    /// <inheritdoc />
    public AuthorizationDecision Evaluate(string command, CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!CommandCatalog.TryGet(command, out CommandDescriptor descriptor))
        {
            return new AuthorizationDecision(AuthorizationOutcome.UnknownCommand, null, Permission.None);
        }

        // A caller may invoke a command whose required stage it has reached or
        // passed. The enum is ordered Unpaired < Paired < Authenticated.
        if (caller.Stage < descriptor.Stage)
        {
            AuthorizationOutcome outcome = caller.Stage == CommandStage.Unpaired
                ? AuthorizationOutcome.NotPaired
                : AuthorizationOutcome.Unauthenticated;
            return new AuthorizationDecision(outcome, descriptor, descriptor.RequiredPermission);
        }

        if (!PermissionSet.Allows(caller.Permissions, descriptor.RequiredPermission))
        {
            return new AuthorizationDecision(
                AuthorizationOutcome.PermissionDenied,
                descriptor,
                descriptor.RequiredPermission);
        }

        return new AuthorizationDecision(AuthorizationOutcome.Allowed, descriptor, descriptor.RequiredPermission);
    }
}
