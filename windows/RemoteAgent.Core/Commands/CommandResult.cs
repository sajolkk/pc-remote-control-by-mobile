using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Commands;

/// <summary>
/// The outcome of a command, independent of how it will be transported.
/// </summary>
/// <remarks>
/// Handlers return this rather than throwing for expected failures. Exceptions are
/// reserved for genuine defects, and the dispatcher converts any that escape into
/// <see cref="ErrorCodes.Internal"/> with the detail logged locally and withheld
/// from the client (§7.5).
/// </remarks>
public sealed class CommandResult
{
    private CommandResult(bool ok, JsonNode? data, ProtocolError? error)
    {
        Ok = ok;
        Data = data;
        Error = error;
    }

    /// <summary>Whether the command succeeded.</summary>
    public bool Ok { get; }

    /// <summary>Success payload, or null when the command returns nothing.</summary>
    public JsonNode? Data { get; }

    /// <summary>Failure detail, present if and only if <see cref="Ok"/> is false.</summary>
    public ProtocolError? Error { get; }

    /// <summary>A success with no payload.</summary>
    public static CommandResult Success() => new(true, null, null);

    /// <summary>A success carrying <paramref name="payload"/>.</summary>
    public static CommandResult Success<T>(T payload) =>
        new(true, JsonSerializer.SerializeToNode(payload, ProtocolJson.Options), null);

    /// <summary>A success carrying an already-serialized payload, used when forwarding.</summary>
    public static CommandResult SuccessRaw(JsonNode? payload) => new(true, payload, null);

    /// <summary>A failure with a stable code from <see cref="ErrorCodes"/>.</summary>
    public static CommandResult Fail(string code, string message, bool retryable = false) =>
        new(false, null, new ProtocolError { Code = code, Message = message, Retryable = retryable });

    /// <summary>A failure rebuilt from a forwarded error.</summary>
    public static CommandResult Fail(ProtocolError error) => new(false, null, error);

    /// <summary>Projects this result onto the wire response for <paramref name="requestId"/>.</summary>
    public ResponseEnvelope ToResponse(string requestId) => new()
    {
        Id = requestId,
        Ok = Ok,
        Data = Data,
        Error = Error,
    };

    // --- Shorthands for the failures that recur across handlers ---

    /// <summary>No interactive session agent is available to execute a session-scoped command.</summary>
    public static CommandResult SessionUnavailable() => Fail(
        ErrorCodes.SessionUnavailable,
        "No interactive Windows session is available. Nobody is signed in, or the session agent is starting.",
        retryable: true);

    /// <summary>The command is declared but this PC cannot perform it.</summary>
    public static CommandResult NotSupported(string what) => Fail(
        ErrorCodes.NotSupported,
        $"This PC does not support {what}.");

    /// <summary>A required argument was missing or failed validation.</summary>
    public static CommandResult InvalidArguments(string detail) => Fail(ErrorCodes.InvalidArguments, detail);

    /// <summary>The referenced app, monitor, file or device does not exist.</summary>
    public static CommandResult NotFound(string what) => Fail(ErrorCodes.NotFound, $"{what} was not found.");

    /// <summary>Local configuration or policy forbids this command.</summary>
    public static CommandResult PolicyDenied(string detail) => Fail(ErrorCodes.PolicyDenied, detail);
}
