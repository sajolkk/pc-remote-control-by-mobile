using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol;

/// <summary>
/// Frame kinds on the control channel. One byte on the wire.
/// </summary>
public enum FrameType : byte
{
    /// <summary>A command the peer should execute and answer.</summary>
    Request = 0x01,

    /// <summary>The answer to a <see cref="Request"/>, correlated by id.</summary>
    Response = 0x02,

    /// <summary>An unsolicited state change pushed to a subscriber.</summary>
    Event = 0x03,

    /// <summary>Keepalive probe.</summary>
    Ping = 0x04,

    /// <summary>Keepalive answer, echoing the probe's nonce.</summary>
    Pong = 0x05,
}

/// <summary>
/// A command sent from the mobile app to the PC.
/// </summary>
/// <remarks>
/// Field names are deliberately short: this envelope is also used for the
/// high-rate input path, where per-message overhead is worth minimizing.
/// </remarks>
public sealed class RequestEnvelope
{
    /// <summary>Protocol version this message is encoded in.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Correlation id, echoed in the response. Unique within a session.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Sender's clock, Unix milliseconds. Rejected outside the accepted skew window.</summary>
    [JsonPropertyName("ts")]
    public long TimestampMs { get; init; }

    /// <summary>
    /// Strictly increasing per-session counter. A gap or a repeat terminates the
    /// session — replay defence in depth on top of TLS (§6.2).
    /// </summary>
    [JsonPropertyName("seq")]
    public long Sequence { get; init; }

    /// <summary>Registry name of the command, e.g. <c>system.lock</c>.</summary>
    [JsonPropertyName("cmd")]
    public string Command { get; init; } = string.Empty;

    /// <summary>
    /// Command-specific arguments, left as raw JSON so the dispatcher can route
    /// before the handler's own schema is applied.
    /// </summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }

    /// <summary>
    /// Short-lived session token bound to this connection (§7.2). Absent only on
    /// the handshake and pairing commands.
    /// </summary>
    [JsonPropertyName("tok")]
    public string? Token { get; init; }
}

/// <summary>
/// The answer to a <see cref="RequestEnvelope"/>.
/// </summary>
public sealed class ResponseEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolVersion.Current;

    /// <summary>The id of the request this answers.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Whether the command succeeded.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Result payload on success. Absent when the command returns nothing.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Data { get; init; }

    /// <summary>Failure detail. Present if and only if <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("err")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProtocolError? Error { get; init; }

    /// <summary>Builds a success response carrying no payload.</summary>
    public static ResponseEnvelope Success(string id) => new() { Id = id, Ok = true };

    /// <summary>Builds a success response carrying <paramref name="payload"/>.</summary>
    public static ResponseEnvelope Success<T>(string id, T payload) => new()
    {
        Id = id,
        Ok = true,
        Data = JsonSerializer.SerializeToNode(payload, ProtocolJson.Options),
    };

    /// <summary>Builds a failure response.</summary>
    public static ResponseEnvelope Failure(string id, string code, string message, bool retryable = false) => new()
    {
        Id = id,
        Ok = false,
        Error = new ProtocolError { Code = code, Message = message, Retryable = retryable },
    };
}

/// <summary>
/// A state change pushed from the PC to a subscribed client.
/// </summary>
public sealed class EventEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolVersion.Current;

    /// <summary>Event topic, e.g. <c>system.state_changed</c>.</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    /// <summary>Emitter's clock, Unix milliseconds.</summary>
    [JsonPropertyName("ts")]
    public long TimestampMs { get; init; }

    /// <summary>Topic-specific payload.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Data { get; init; }

    /// <summary>Builds an event carrying <paramref name="payload"/>.</summary>
    public static EventEnvelope Create<T>(string topic, T payload, long timestampMs) => new()
    {
        Topic = topic,
        TimestampMs = timestampMs,
        Data = JsonSerializer.SerializeToNode(payload, ProtocolJson.Options),
    };
}

/// <summary>
/// Structured failure detail.
/// </summary>
public sealed class ProtocolError
{
    /// <summary>A stable code from <see cref="ErrorCodes"/>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = ErrorCodes.Internal;

    /// <summary>
    /// Human-readable explanation, safe to display. Never contains secrets,
    /// stack traces, or internal paths — those go to the PC's log only (§7.5).
    /// </summary>
    [JsonPropertyName("msg")]
    public string Message { get; init; } = string.Empty;

    /// <summary>Whether retrying the identical request could plausibly succeed.</summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}

/// <summary>
/// Keepalive probe and its echo. Doubles as the control-channel latency measurement.
/// </summary>
public sealed class PingPayload
{
    /// <summary>Opaque value the peer must echo unchanged.</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; init; } = string.Empty;

    /// <summary>Sender's clock at transmission, Unix milliseconds.</summary>
    [JsonPropertyName("ts")]
    public long TimestampMs { get; init; }
}
