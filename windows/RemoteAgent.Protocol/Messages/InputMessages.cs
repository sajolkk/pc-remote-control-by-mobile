using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

// Input commands (Permission.ControlInput), Phase 4.
//
// Coordinates: x and y are doubles in [0,1], normalised to ONE monitor. (0,0) is that monitor's
// top-left pixel and (1,1) its bottom-right pixel. monitorId is an id from screen.monitor_list;
// when omitted the PC uses the monitor this connection is streaming, else the primary monitor.
//
// Every "required" value below is declared nullable. That is deliberate: a missing "down" must be
// refused as invalid rather than silently read as "false" (a release), and a missing "vk" must not
// become virtual key 0.

/// <summary>
/// Arguments for <see cref="CommandNames.InputMouseMove"/>.
/// </summary>
/// <remarks>
/// Absolute when <see cref="X"/> and <see cref="Y"/> are present, otherwise relative by
/// <see cref="Dx"/> and <see cref="Dy"/> pixels.
/// </remarks>
public sealed class MouseMoveArgs
{
    /// <summary>Horizontal position on the monitor, 0 at its left edge and 1 at its right edge.</summary>
    [JsonPropertyName("x")]
    public double? X { get; init; }

    /// <summary>Vertical position on the monitor, 0 at its top edge and 1 at its bottom edge.</summary>
    [JsonPropertyName("y")]
    public double? Y { get; init; }

    /// <summary>The monitor <see cref="X"/> and <see cref="Y"/> refer to. Null means the streamed one.</summary>
    [JsonPropertyName("monitorId")]
    public string? MonitorId { get; init; }

    /// <summary>Relative horizontal movement in pixels, used when no absolute position is given.</summary>
    [JsonPropertyName("dx")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int? Dx { get; init; }

    /// <summary>Relative vertical movement in pixels, used when no absolute position is given.</summary>
    [JsonPropertyName("dy")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int? Dy { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.InputMouseButton"/>: one half of a click, for drags.
/// </summary>
public sealed class MouseButtonArgs
{
    /// <summary><c>left</c>, <c>right</c> or <c>middle</c>.</summary>
    [JsonPropertyName("button")]
    public string? Button { get; init; }

    /// <summary>True to press, false to release.</summary>
    [JsonPropertyName("down")]
    public bool? Down { get; init; }

    /// <summary>Optional position to move to first. See <see cref="MouseMoveArgs.X"/>.</summary>
    [JsonPropertyName("x")]
    public double? X { get; init; }

    /// <summary>Optional position to move to first. See <see cref="MouseMoveArgs.Y"/>.</summary>
    [JsonPropertyName("y")]
    public double? Y { get; init; }

    /// <summary>The monitor the position refers to. Null means the streamed one.</summary>
    [JsonPropertyName("monitorId")]
    public string? MonitorId { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.InputMouseClick"/>: a complete press and release.
/// </summary>
public sealed class MouseClickArgs
{
    /// <summary><c>left</c>, <c>right</c> or <c>middle</c>.</summary>
    [JsonPropertyName("button")]
    public string? Button { get; init; }

    /// <summary>1 for a click, 2 for a double click. Defaults to 1.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    /// <summary>Optional position to click at. See <see cref="MouseMoveArgs.X"/>.</summary>
    [JsonPropertyName("x")]
    public double? X { get; init; }

    /// <summary>Optional position to click at. See <see cref="MouseMoveArgs.Y"/>.</summary>
    [JsonPropertyName("y")]
    public double? Y { get; init; }

    /// <summary>The monitor the position refers to. Null means the streamed one.</summary>
    [JsonPropertyName("monitorId")]
    public string? MonitorId { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.InputScroll"/>.
/// </summary>
/// <remarks>
/// Wheel delta units: 120 is one notch. A positive <see cref="Dy"/> scrolls up (away from the
/// user), a positive <see cref="Dx"/> scrolls right — the same signs Windows uses.
/// </remarks>
public sealed class ScrollArgs
{
    /// <summary>Horizontal wheel delta.</summary>
    [JsonPropertyName("dx")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int? Dx { get; init; }

    /// <summary>Vertical wheel delta.</summary>
    [JsonPropertyName("dy")]
    [JsonConverter(typeof(LenientInt32Converter))]
    public int? Dy { get; init; }

    /// <summary>Optional position to move to first. See <see cref="MouseMoveArgs.X"/>.</summary>
    [JsonPropertyName("x")]
    public double? X { get; init; }

    /// <summary>Optional position to move to first. See <see cref="MouseMoveArgs.Y"/>.</summary>
    [JsonPropertyName("y")]
    public double? Y { get; init; }

    /// <summary>The monitor the position refers to. Null means the streamed one.</summary>
    [JsonPropertyName("monitorId")]
    public string? MonitorId { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.InputKey"/>.
/// </summary>
public sealed class KeyArgs
{
    /// <summary>Windows virtual-key code, 1 to 254.</summary>
    [JsonPropertyName("vk")]
    public int? VirtualKey { get; init; }

    /// <summary>True to press, false to release.</summary>
    [JsonPropertyName("down")]
    public bool? Down { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.InputText"/>.
/// </summary>
public sealed class TextArgs
{
    /// <summary>
    /// 1 to 2000 UTF-16 units, typed as Unicode. A line feed is sent as Enter and a tab as Tab.
    /// Never logged, at any level (§7.5).
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.InputShortcut"/>.
/// </summary>
public sealed class ShortcutArgs
{
    /// <summary>1 to 6 virtual-key codes, pressed in order and then released in reverse.</summary>
    [JsonPropertyName("keys")]
    public int[]? Keys { get; init; }
}

/// <summary>
/// Reads an integer that may have been written with a fractional part of zero.
/// </summary>
/// <remarks>
/// Pointer and wheel deltas start life as floating-point gesture distances on the phone, and a
/// rounded Dart <c>double</c> still encodes as <c>3.0</c>. The wire options are otherwise strict,
/// so without this such a value would fail the whole request; with it <c>3</c> and <c>3.0</c> both
/// read as three and anything with a real fraction is rounded to the nearest pixel. Values outside
/// the 32-bit range are refused rather than wrapped.
/// </remarks>
public sealed class LenientInt32Converter : JsonConverter<int?>
{
    /// <inheritdoc />
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("Expected a number.");
        }

        if (reader.TryGetInt32(out int whole))
        {
            return whole;
        }

        if (reader.TryGetDouble(out double value) && double.IsFinite(value) &&
            value >= int.MinValue && value <= int.MaxValue)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        throw new JsonException("The number is out of range.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
