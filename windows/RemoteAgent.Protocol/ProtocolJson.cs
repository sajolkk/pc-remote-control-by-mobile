using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol;

/// <summary>
/// The one JSON configuration used for everything on the wire.
/// </summary>
/// <remarks>
/// Both hosts and the tests share these options so that a message serialized by
/// one component is always parseable by another. The settings are chosen for a
/// hostile input path: no trailing commas, no comments, no case-insensitive
/// matching surprises beyond what we opt into, and a hard depth limit so a
/// deeply nested payload cannot exhaust the stack.
/// </remarks>
public static class ProtocolJson
{
    /// <summary>Maximum nesting depth accepted from a peer.</summary>
    public const int MaxDepth = 32;

    /// <summary>Serializer options for all control-channel and IPC traffic.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            // Property names are given explicitly by attribute; this only affects
            // any type that forgot one, and keeps those consistent.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = MaxDepth,
            WriteIndented = false,
            // The payload is length-prefixed binary, never embedded in HTML, so the
            // relaxed encoder is safe and avoids escaping every non-ASCII character
            // in file names and window titles.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.Converters.Add(new JsonStringEnumConverter());

        // populateMissingResolver: true is required. The parameterless MakeReadOnly() throws
        // "instance must specify a TypeInfoResolver setting before being marked as read-only",
        // because it refuses to silently pick a resolver for you. Passing true installs the
        // reflection-based resolver and then freezes the instance, which is what we want: a single
        // immutable options object shared by every component, so no code path can mutate the
        // serializer settings the wire format depends on.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Deserializes a payload, returning <c>false</c> rather than throwing on
    /// malformed input. Every parse of peer-supplied bytes goes through here.
    /// </summary>
    public static bool TryDeserialize<T>(ReadOnlySpan<byte> utf8Json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(utf8Json, Options);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
        catch (NotSupportedException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>Serializes a message to UTF-8 bytes ready for framing.</summary>
    public static byte[] SerializeToUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
}
