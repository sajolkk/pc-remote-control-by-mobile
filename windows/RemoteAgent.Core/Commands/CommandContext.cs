using System.Text.Json;
using RemoteAgent.Protocol;

namespace RemoteAgent.Core.Commands;

/// <summary>
/// The authenticated caller behind a command.
/// </summary>
/// <param name="DeviceId">The paired device's stable id, or null before pairing.</param>
/// <param name="DeviceName">Display name, for logs and audit.</param>
/// <param name="Permissions">Permission groups granted to this device right now.</param>
/// <param name="Stage">How far this connection has progressed through the handshake.</param>
/// <param name="ConnectionId">
/// Per-connection id. Distinct from the device id because one device may hold
/// several connections, and a session token is bound to one of them (§7.2).
/// </param>
/// <param name="RemoteAddress">
/// Peer address, for audit and rate limiting. Never used for authorization — IP is
/// not an identity (§6.4).
/// </param>
public sealed record CallerIdentity(
    string? DeviceId,
    string DeviceName,
    Permission Permissions,
    CommandStage Stage,
    string ConnectionId,
    string RemoteAddress)
{
    /// <summary>An unpaired caller: a TLS peer whose certificate matches no pairing record.</summary>
    public static CallerIdentity Unpaired(string connectionId, string remoteAddress) =>
        new(null, "(unpaired)", Permission.None, CommandStage.Unpaired, connectionId, remoteAddress);
}

/// <summary>
/// Everything a handler needs to execute one command.
/// </summary>
public sealed class CommandContext
{
    /// <summary>Correlation id from the request, echoed in the response.</summary>
    public required string RequestId { get; init; }

    /// <summary>The command's wire name.</summary>
    public required string Command { get; init; }

    /// <summary>Raw arguments, deserialized on demand by the handler.</summary>
    public JsonElement? Args { get; init; }

    /// <summary>Who is calling.</summary>
    public required CallerIdentity Caller { get; init; }

    /// <summary>The catalog entry that authorized this dispatch.</summary>
    public required CommandDescriptor Descriptor { get; init; }

    /// <summary>
    /// Deserializes the arguments into <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Returns a failure result rather than throwing, because malformed arguments
    /// are ordinary hostile input on this path, not an exceptional condition. A
    /// handler whose arguments are all optional may treat a missing
    /// <see cref="Args"/> as an empty object.
    /// </remarks>
    public bool TryReadArgs<T>(out T value, out CommandResult? failure)
        where T : class, new()
    {
        if (Args is null || Args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            value = new T();
            failure = null;
            return true;
        }

        if (Args.Value.ValueKind != JsonValueKind.Object)
        {
            value = new T();
            failure = CommandResult.Fail(
                ErrorCodes.InvalidArguments,
                "Arguments must be a JSON object.");
            return false;
        }

        try
        {
            T? parsed = Args.Value.Deserialize<T>(ProtocolJson.Options);
            if (parsed is null)
            {
                value = new T();
                failure = CommandResult.Fail(ErrorCodes.InvalidArguments, "Arguments could not be read.");
                return false;
            }

            value = parsed;
            failure = null;
            return true;
        }
        catch (JsonException ex)
        {
            value = new T();
            // The exception message describes the caller's own malformed input, so
            // returning it aids debugging without disclosing anything about the PC.
            failure = CommandResult.Fail(ErrorCodes.InvalidArguments, $"Invalid arguments: {ex.Message}");
            return false;
        }
    }
}
