using System.Buffers.Binary;

namespace RemoteAgent.Protocol;

/// <summary>
/// One framed message read from or written to a stream.
/// </summary>
/// <param name="Type">The frame kind.</param>
/// <param name="Payload">UTF-8 JSON body. Empty for bodyless frames.</param>
public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Length-prefixed framing for the control channel and the named-pipe IPC (§6.2).
/// </summary>
/// <remarks>
/// Layout: <c>[4-byte big-endian payload length][1-byte frame type][payload]</c>.
/// <para>
/// The length prefix covers the payload only, not the type byte. A declared
/// length above <see cref="MaxPayloadBytes"/> is refused before a single byte of
/// body is read, so a malicious peer cannot make us allocate on demand — this is
/// the first line of the DoS defence in §7.3.
/// </para>
/// TLS already provides confidentiality, integrity and ordering underneath this
/// layer, so the framing itself carries no checksum or nonce.
/// </remarks>
public static class FrameCodec
{
    /// <summary>Size of the fixed header: 4 length bytes plus 1 type byte.</summary>
    public const int HeaderBytes = 5;

    /// <summary>Largest payload accepted or emitted, 1 MiB (§6.2).</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Writes one frame. Callers must serialize writes per connection.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The payload exceeds <see cref="MaxPayloadBytes"/>.</exception>
    public static async ValueTask WriteAsync(
        Stream stream,
        FrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Frame payload exceeds the {MaxPayloadBytes} byte limit.");
        }

        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), payload.Length);
        header[4] = (byte)type;

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes <paramref name="message"/> and writes it as one frame.</summary>
    public static ValueTask WriteMessageAsync<T>(
        Stream stream,
        FrameType type,
        T message,
        CancellationToken cancellationToken) =>
        WriteAsync(stream, type, ProtocolJson.SerializeToUtf8(message), cancellationToken);

    /// <summary>
    /// Reads one frame, or returns <c>null</c> when the peer closed the stream
    /// cleanly at a frame boundary.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The header declared an impossible length, an unknown frame type, or the
    /// stream ended mid-frame. Callers treat this as
    /// <see cref="ErrorCodes.MalformedFrame"/> and drop the connection.
    /// </exception>
    public static async ValueTask<Frame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int maxPayloadBytes = MaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderBytes];
        int headerRead = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null; // Clean close between frames.
        }

        if (headerRead < HeaderBytes)
        {
            throw new InvalidDataException("Stream ended inside a frame header.");
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
        if (length < 0 || length > maxPayloadBytes)
        {
            throw new InvalidDataException($"Frame declared an invalid payload length of {length} bytes.");
        }

        var type = (FrameType)header[4];
        if (!IsKnownType(type))
        {
            throw new InvalidDataException($"Frame declared unknown type 0x{header[4]:X2}.");
        }

        if (length == 0)
        {
            return new Frame(type, ReadOnlyMemory<byte>.Empty);
        }

        byte[] payload = new byte[length];
        int payloadRead = await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new InvalidDataException("Stream ended inside a frame payload.");
        }

        return new Frame(type, payload);
    }

    private static bool IsKnownType(FrameType type) => type switch
    {
        FrameType.Request or FrameType.Response or FrameType.Event or FrameType.Ping or FrameType.Pong => true,
        _ => false,
    };

    /// <summary>
    /// Fills <paramref name="buffer"/>, tolerating short reads. Returns the count
    /// read, which is 0 for a clean close and less than the buffer length for a
    /// truncated one.
    /// </summary>
    private static async ValueTask<int> ReadAtLeastAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
