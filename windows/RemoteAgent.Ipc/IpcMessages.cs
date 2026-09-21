using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RemoteAgent.Protocol;

namespace RemoteAgent.Ipc;

/// <summary>
/// Well-known IPC control commands, distinct from the wire command catalog.
/// </summary>
/// <remarks>
/// Prefixed <c>ipc.</c> so they can never collide with a remote command name, and
/// deliberately absent from <see cref="CommandCatalog"/>: nothing a phone sends can ever
/// be routed to one of these, because the dispatcher only accepts catalog names.
/// </remarks>
public static class IpcCommands
{
    /// <summary>Agent introduces itself and proves it is the process the service spawned.</summary>
    public const string Hello = "ipc.hello";

    /// <summary>Liveness probe in either direction.</summary>
    public const string Ping = "ipc.ping";

    /// <summary>Service asks the agent to shut down cleanly.</summary>
    public const string Shutdown = "ipc.shutdown";

    /// <summary>Service asks the agent to release all held input (on client disconnect).</summary>
    public const string ReleaseInput = "ipc.release_input";

    /// <summary>Service asks the agent to prompt the user to approve a pairing.</summary>
    public const string ApprovePairing = "ipc.approve_pairing";

    /// <summary>
    /// Agent asks the service to open a pairing window and return the QR payload.
    /// </summary>
    /// <remarks>
    /// This direction exists because the two halves each hold something the other needs: only the
    /// service knows the identity fingerprint, the listening port and the pairing token, and only
    /// the agent has a desktop to display a QR code on.
    /// </remarks>
    public const string OpenPairing = "ipc.open_pairing";

    /// <summary>Agent asks the service to close the pairing window early.</summary>
    public const string ClosePairing = "ipc.close_pairing";

    /// <summary>Agent asks the service for current status, to render in the tray menu.</summary>
    public const string Status = "ipc.status";

    /// <summary>
    /// Service tells the agent a control connection closed or its device's permissions changed.
    /// </summary>
    /// <remarks>
    /// This is what makes a stream end with the authorization behind it: the agent owns the media
    /// plane but not the control connection, so without this notice a phone that disconnected, or
    /// whose <c>ViewScreen</c> was revoked at the PC, would keep receiving video.
    /// </remarks>
    public const string ConnectionChanged = "ipc.connection_changed";
}

/// <summary>
/// The caller identity carried alongside a forwarded command.
/// </summary>
/// <remarks>
/// The session agent re-checks the permission before acting, even though the service has
/// already authorized the call. That duplication is deliberate: it means a bug in the
/// service's routing cannot turn into an unauthorized action on the desktop, and it keeps
/// the agent's own guarantees independent of another process being correct.
/// </remarks>
public sealed class IpcCaller
{
    /// <summary>Paired device id, for audit.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Device display name, for audit.</summary>
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Permission group names granted to that device.</summary>
    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];

    /// <summary>Originating control-channel connection id.</summary>
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Peer address, for audit only.</summary>
    [JsonPropertyName("remoteAddress")]
    public string RemoteAddress { get; init; } = string.Empty;

    /// <summary>Parses the permission names into a bitmap.</summary>
    public Permission ToPermissions() => PermissionSet.FromNames(Permissions);
}

/// <summary>A command sent across the pipe.</summary>
public sealed class IpcRequest
{
    /// <summary>Correlation id, echoed in the response.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Command name: either an <see cref="IpcCommands"/> value or a catalog command.</summary>
    [JsonPropertyName("cmd")]
    public string Command { get; init; } = string.Empty;

    /// <summary>Command arguments, passed through unchanged from the client.</summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }

    /// <summary>Who asked. Null for service-internal commands such as <c>ipc.hello</c>.</summary>
    [JsonPropertyName("caller")]
    public IpcCaller? Caller { get; init; }
}

/// <summary>A response to an <see cref="IpcRequest"/>.</summary>
public sealed class IpcResponse
{
    /// <summary>The request this answers.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Whether the command succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Success payload.</summary>
    [JsonPropertyName("data")]
    public JsonNode? Data { get; init; }

    /// <summary>Failure detail.</summary>
    [JsonPropertyName("err")]
    public ProtocolError? Error { get; init; }

    /// <summary>Builds a success response.</summary>
    public static IpcResponse Success(string id, JsonNode? data = null) => new()
    {
        Id = id,
        Ok = true,
        Data = data,
    };

    /// <summary>Builds a failure response.</summary>
    public static IpcResponse Failure(string id, string code, string message) => new()
    {
        Id = id,
        Ok = false,
        Error = new ProtocolError { Code = code, Message = message },
    };
}

/// <summary>An unsolicited state change pushed across the pipe.</summary>
public sealed class IpcEvent
{
    /// <summary>Topic from <see cref="EventNames"/>, or an IPC-internal topic.</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    /// <summary>When it happened, Unix milliseconds.</summary>
    [JsonPropertyName("ts")]
    public long TimestampMs { get; init; }

    /// <summary>Topic payload.</summary>
    [JsonPropertyName("data")]
    public JsonNode? Data { get; init; }

    /// <summary>
    /// The one control connection this event is for, or null to broadcast it.
    /// </summary>
    /// <remarks>
    /// Media telemetry is targeted: it describes what one device is watching, and no other paired
    /// device has any business receiving it. The service delivers a targeted event to that
    /// connection alone, and drops it if the connection has gone.
    /// </remarks>
    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }
}

/// <summary>
/// Arguments for <see cref="IpcCommands.ConnectionChanged"/>.
/// </summary>
public sealed class IpcConnectionChangedArgs
{
    /// <summary>The control connection concerned.</summary>
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Whether the connection has closed.</summary>
    [JsonPropertyName("closed")]
    public bool Closed { get; init; }

    /// <summary>The device's permissions now, by name. Empty when closed.</summary>
    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];
}

/// <summary>Arguments for <see cref="IpcCommands.Hello"/>.</summary>
public sealed class IpcHelloArgs
{
    /// <summary>
    /// The one-time token the service generated when it spawned this agent.
    /// </summary>
    /// <remarks>
    /// This is what distinguishes the real agent from any other process that can open the
    /// pipe. The pipe must be openable by interactive users — the agent runs as one — so
    /// the ACL alone cannot identify the peer. The token is passed to the agent through its
    /// environment block rather than its command line, because command lines are readable
    /// by any process on the machine while an environment block is not.
    /// </remarks>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>The Windows session the agent is running in.</summary>
    [JsonPropertyName("sessionId")]
    public int SessionId { get; init; }

    /// <summary>The user the agent is running as, for logging.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Agent build version, to detect a mismatched pair after an upgrade.</summary>
    [JsonPropertyName("agentVersion")]
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>Commands this agent implements, so the service knows what it can forward.</summary>
    [JsonPropertyName("commands")]
    public string[] Commands { get; init; } = [];

    /// <summary>Capabilities the agent probed in its session.</summary>
    [JsonPropertyName("capabilities")]
    public string[] Capabilities { get; init; } = [];
}
