using RemoteAgent.Protocol;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for the framing layer.
/// </summary>
/// <remarks>
/// The first code that touches bytes from an untrusted peer, so the emphasis is on malformed input
/// rather than on the happy path: a declared length that is negative, enormous, or larger than the
/// data that follows, and an unknown frame type. Each must be refused without allocating on the
/// peer's instruction and without leaving the stream in a state where the next read is misparsed.
/// </remarks>
public sealed class FrameCodecTests
{
    [Fact]
    public async Task RoundTripsAFrame()
    {
        byte[] payload = "hello"u8.ToArray();
        using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, FrameType.Request, payload, CancellationToken.None);
        stream.Position = 0;

        Frame? frame = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(FrameType.Request, frame!.Value.Type);
        Assert.Equal(payload, frame.Value.Payload.ToArray());
    }

    [Fact]
    public async Task RoundTripsSeveralFramesInSequence()
    {
        using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, FrameType.Request, "one"u8.ToArray(), CancellationToken.None);
        await FrameCodec.WriteAsync(stream, FrameType.Response, "two"u8.ToArray(), CancellationToken.None);
        await FrameCodec.WriteAsync(stream, FrameType.Event, "three"u8.ToArray(), CancellationToken.None);
        stream.Position = 0;

        Frame? first = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Frame? second = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Frame? third = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(first!.Value.Payload.Span));
        Assert.Equal("two", System.Text.Encoding.UTF8.GetString(second!.Value.Payload.Span));
        Assert.Equal("three", System.Text.Encoding.UTF8.GetString(third!.Value.Payload.Span));
    }

    [Fact]
    public async Task HandlesAnEmptyPayload()
    {
        using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, FrameType.Ping, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        stream.Position = 0;

        Frame? frame = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(FrameType.Ping, frame!.Value.Type);
        Assert.True(frame.Value.Payload.IsEmpty);
    }

    [Fact]
    public async Task ReturnsNullOnACleanCloseBetweenFrames()
    {
        using var stream = new MemoryStream();

        Frame? frame = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        // A peer that closes tidily is not an error: the read loop ends and the connection is
        // logged as a clean disconnect rather than a protocol violation.
        Assert.Null(frame);
    }

    [Fact]
    public async Task RejectsADeclaredLengthAboveTheLimit()
    {
        // The important property: the length is refused on the strength of the header alone, before
        // any attempt to allocate or read a body. A peer cannot make us reserve 2 GB by claiming to
        // be about to send it.
        byte[] header = [0x7F, 0xFF, 0xFF, 0xFF, (byte)FrameType.Request];
        using var stream = new MemoryStream(header);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));

        Assert.Contains("invalid payload length", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsANegativeDeclaredLength()
    {
        byte[] header = [0xFF, 0xFF, 0xFF, 0xFF, (byte)FrameType.Request];
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAnUnknownFrameType()
    {
        byte[] header = [0x00, 0x00, 0x00, 0x01, 0x7F];
        using var stream = new MemoryStream([.. header, 0x41]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));

        Assert.Contains("unknown type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAStreamThatEndsInsideTheHeader()
    {
        using var stream = new MemoryStream([0x00, 0x00]);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAStreamThatEndsInsideThePayload()
    {
        // Declares ten bytes and supplies three. Truncation must be detected rather than handed
        // upward as a short payload, which a parser would then misread.
        byte[] data = [0x00, 0x00, 0x00, 0x0A, (byte)FrameType.Request, 0x41, 0x42, 0x43];
        using var stream = new MemoryStream(data);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.ReadAsync(stream, CancellationToken.None));

        Assert.Contains("payload", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesToWriteAnOversizedPayload()
    {
        using var stream = new MemoryStream();
        byte[] tooBig = new byte[FrameCodec.MaxPayloadBytes + 1];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await FrameCodec.WriteAsync(stream, FrameType.Request, tooBig, CancellationToken.None));
    }

    [Fact]
    public async Task ToleratesAStreamThatReturnsDataInSmallChunks()
    {
        // A real TCP stream rarely returns a whole frame in one read. This asserts the reader
        // loops rather than assuming one read per frame — the kind of bug that only shows up
        // under load or with large payloads.
        byte[] payload = new byte[5000];
        Random.Shared.NextBytes(payload);

        using var buffer = new MemoryStream();
        await FrameCodec.WriteAsync(buffer, FrameType.Request, payload, CancellationToken.None);

        using var chunked = new ChunkedStream(buffer.ToArray(), chunkSize: 7);
        Frame? frame = await FrameCodec.ReadAsync(chunked, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(payload, frame!.Value.Payload.ToArray());
    }

    /// <summary>A stream that hands out a few bytes at a time, like a real socket.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(Math.Min(chunkSize, count), data.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int available = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
            if (available <= 0)
            {
                return ValueTask.FromResult(0);
            }

            data.AsSpan(_position, available).CopyTo(buffer.Span);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
