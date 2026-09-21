using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Protocol;

namespace RemoteAgent.Ipc;

/// <summary>
/// Handles one duplex IPC conversation over a connected pipe.
/// </summary>
/// <remarks>
/// <para>Both ends use this same class. Either side may send a request and receive a
/// correlated response, and either may push an event — the service forwards commands to
/// the agent, the agent pushes desktop state changes back, and the agent asks the service
/// to show a pairing prompt. A single symmetric implementation avoids two near-identical
/// loops that drift apart.</para>
///
/// <para>Framing is <see cref="FrameCodec"/>, the same length-prefixed format as the
/// network control channel. Reusing it means the framing is tested once and a malformed
/// frame is handled identically on both paths.</para>
///
/// <para>Writes are serialized through a semaphore because a pipe is a single stream and
/// two concurrent writes would interleave two frames into garbage. Reads happen on one
/// pump loop, so no read lock is needed.</para>
/// </remarks>
public sealed class IpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly string _label;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pending =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _lifetime = new();
    private long _nextRequestId;
    private Task? _pump;
    private bool _disposed;

    /// <summary>Creates a connection over an already-connected pipe stream.</summary>
    /// <param name="stream">The connected pipe.</param>
    /// <param name="logger">Diagnostic sink.</param>
    /// <param name="label">A short name for this end, used in log messages.</param>
    public IpcConnection(Stream stream, ILogger logger, string label)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _label = label;
    }

    /// <summary>Invoked for each request the peer sends. Must not throw.</summary>
    public Func<IpcRequest, CancellationToken, Task<IpcResponse>>? RequestHandler { get; set; }

    /// <summary>Invoked for each event the peer pushes. Must not throw.</summary>
    public Func<IpcEvent, CancellationToken, Task>? EventHandler { get; set; }

    /// <summary>Raised once when the peer disconnects or the pipe fails.</summary>
    public event EventHandler? Closed;

    /// <summary>Whether the connection is still usable.</summary>
    public bool IsConnected { get; private set; } = true;

    /// <summary>Starts the read pump. Call once, after wiring the handlers.</summary>
    public void Start()
    {
        _pump ??= Task.Run(() => PumpAsync(_lifetime.Token));
    }

    /// <summary>
    /// Sends a request and waits for its response.
    /// </summary>
    /// <remarks>
    /// A dead pipe surfaces as a failure response rather than an exception, because the
    /// caller's job (answering a phone) always needs an answer to send back — the session
    /// agent being gone is an expected state, not an error condition (§6.4).
    /// </remarks>
    public async Task<IpcResponse> SendRequestAsync(
        IpcRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConnected)
        {
            return IpcResponse.Failure(
                request.Id,
                ErrorCodes.SessionUnavailable,
                "The session agent is not connected.");
        }

        string id = string.IsNullOrEmpty(request.Id)
            ? Interlocked.Increment(ref _nextRequestId).ToString()
            : request.Id;

        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var outgoing = new IpcRequest
            {
                Id = id,
                Command = request.Command,
                Args = request.Args,
                Caller = request.Caller,
            };

            await WriteAsync(FrameType.Request, outgoing, cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            timeoutSource.CancelAfter(timeout);

            await using CancellationTokenRegistration registration = timeoutSource.Token.Register(
                static state => ((TaskCompletionSource<IpcResponse>)state!).TrySetCanceled(),
                completion);

            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[{Label}] IPC request {Command} timed out after {TimeoutMs}ms.",
                _label,
                request.Command,
                timeout.TotalMilliseconds);

            return IpcResponse.Failure(
                id,
                ErrorCodes.Timeout,
                "The session agent did not respond in time.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            MarkClosed(ex);
            return IpcResponse.Failure(
                id,
                ErrorCodes.SessionUnavailable,
                "The connection to the session agent was lost.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Pushes an event to the peer. Failures are logged, never thrown.</summary>
    public async Task SendEventAsync(IpcEvent ipcEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ipcEvent);

        if (!IsConnected)
        {
            return;
        }

        try
        {
            await WriteAsync(FrameType.Event, ipcEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            MarkClosed(ex);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Frame? frame = await FrameCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    // Clean close at a frame boundary: the peer exited normally.
                    break;
                }

                await HandleFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (InvalidDataException ex)
        {
            // Malformed framing on a local pipe means a version mismatch or a bug, not an
            // attack — but it is unrecoverable for this connection either way.
            _logger.LogError(ex, "[{Label}] Malformed IPC frame; closing the connection.", _label);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "[{Label}] IPC pipe closed.", _label);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Label}] Unexpected IPC pump failure.", _label);
        }
        finally
        {
            MarkClosed(null);
        }
    }

    private async Task HandleFrameAsync(Frame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case FrameType.Request:
            {
                if (!ProtocolJson.TryDeserialize(frame.Payload.Span, out IpcRequest? request) || request is null)
                {
                    _logger.LogWarning("[{Label}] Discarded an unreadable IPC request.", _label);
                    return;
                }

                Func<IpcRequest, CancellationToken, Task<IpcResponse>>? handler = RequestHandler;
                IpcResponse response;

                if (handler is null)
                {
                    response = IpcResponse.Failure(
                        request.Id,
                        ErrorCodes.NotSupported,
                        "This endpoint does not accept requests.");
                }
                else
                {
                    try
                    {
                        response = await handler(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // A handler that throws must not kill the pipe: the peer is waiting
                        // for an answer, and losing the connection would escalate one bad
                        // command into a dead session agent.
                        _logger.LogError(ex, "[{Label}] IPC handler for {Command} threw.", _label, request.Command);
                        response = IpcResponse.Failure(
                            request.Id,
                            ErrorCodes.Internal,
                            "The command failed on the PC.");
                    }
                }

                try
                {
                    await WriteAsync(FrameType.Response, response, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    MarkClosed(ex);
                }

                return;
            }

            case FrameType.Response:
            {
                if (!ProtocolJson.TryDeserialize(frame.Payload.Span, out IpcResponse? response) || response is null)
                {
                    _logger.LogWarning("[{Label}] Discarded an unreadable IPC response.", _label);
                    return;
                }

                if (_pending.TryRemove(response.Id, out TaskCompletionSource<IpcResponse>? completion))
                {
                    completion.TrySetResult(response);
                }
                else
                {
                    // A response after its request timed out. Normal under load; not worth
                    // more than a trace.
                    _logger.LogTrace("[{Label}] Response {Id} had no waiter.", _label, response.Id);
                }

                return;
            }

            case FrameType.Event:
            {
                if (!ProtocolJson.TryDeserialize(frame.Payload.Span, out IpcEvent? ipcEvent) || ipcEvent is null)
                {
                    return;
                }

                Func<IpcEvent, CancellationToken, Task>? eventHandler = EventHandler;
                if (eventHandler is not null)
                {
                    try
                    {
                        await eventHandler(ipcEvent, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Label}] IPC event handler for {Topic} threw.", _label, ipcEvent.Topic);
                    }
                }

                return;
            }

            case FrameType.Ping:
                await WriteAsync(FrameType.Pong, new { }, cancellationToken).ConfigureAwait(false);
                return;

            case FrameType.Pong:
                return;

            default:
                _logger.LogWarning("[{Label}] Ignored an IPC frame of type {Type}.", _label, frame.Type);
                return;
        }
    }

    private async Task WriteAsync<T>(FrameType type, T message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteMessageAsync(_stream, type, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void MarkClosed(Exception? reason)
    {
        if (!IsConnected)
        {
            return;
        }

        IsConnected = false;

        // Every in-flight request must be completed, or its caller waits for its full
        // timeout on a connection that is already gone.
        foreach (KeyValuePair<string, TaskCompletionSource<IpcResponse>> entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out TaskCompletionSource<IpcResponse>? completion))
            {
                completion.TrySetResult(IpcResponse.Failure(
                    entry.Key,
                    ErrorCodes.SessionUnavailable,
                    "The session agent disconnected."));
            }
        }

        if (reason is not null)
        {
            _logger.LogDebug(reason, "[{Label}] IPC connection closed.", _label);
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifetime.CancelAsync().ConfigureAwait(false);
        MarkClosed(null);

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The pump is blocked on a read that will end when the stream closes.
            }
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
