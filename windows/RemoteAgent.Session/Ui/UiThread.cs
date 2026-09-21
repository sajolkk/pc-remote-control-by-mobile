using System.Runtime.Versioning;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace RemoteAgent.Session.Ui;

/// <summary>
/// A dedicated STA thread with a WPF dispatcher, for the rare moments this agent shows UI.
/// </summary>
/// <remarks>
/// <para>The session agent is a background process with no main window. It needs a dispatcher
/// anyway, because the one thing it must be able to do is put a pairing approval dialog on the
/// user's screen — and WPF windows require a single-threaded-apartment thread with a message
/// pump.</para>
///
/// <para>Running a dispatcher on a dedicated thread rather than using
/// <c>Application.Run</c> keeps the process's lifetime under the host's control: the agent exits
/// when the service says so or when its session ends, not when a window closes.</para>
///
/// <para>The thread is created eagerly at startup rather than on first use. A pairing approval is
/// exactly the moment when a delay is most costly — somebody is standing at the PC waiting — and
/// spinning up a thread and pump at that point risks the prompt arriving after the requester has
/// given up.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UiThread : IDisposable
{
    private readonly ILogger<UiThread> _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;

    private Dispatcher? _dispatcher;
    private bool _disposed;

    /// <summary>Starts the UI thread and waits for its dispatcher to come up.</summary>
    public UiThread(ILogger<UiThread> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _thread = new Thread(Run)
        {
            Name = "PCRemote-UI",
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // A short bounded wait: if the dispatcher cannot start, the agent should still run and
        // report that it cannot prompt, rather than hanging at startup.
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogError(
                "The UI thread did not start within 5 seconds. Pairing approval prompts will be " +
                "unavailable, so new devices cannot be paired from this session.");
        }
    }

    /// <summary>Whether a dispatcher is available to show UI on.</summary>
    public bool IsAvailable => _dispatcher is not null;

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread and returns its result.
    /// </summary>
    /// <remarks>
    /// Returns <paramref name="fallback"/> if no dispatcher is available, so callers never have to
    /// branch on whether UI is possible — they get the safe answer automatically. For pairing
    /// approval the safe answer is "declined".
    /// </remarks>
    public async Task<T> InvokeAsync<T>(Func<T> work, T fallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return fallback;
        }

        try
        {
            return await dispatcher.InvokeAsync(work, DispatcherPriority.Normal, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A UI operation failed on the agent's dispatcher thread.");
            return fallback;
        }
    }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The agent's UI dispatcher thread terminated.");
            _dispatcher = null;
            _ready.Set();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Dispatcher? dispatcher = _dispatcher;
        _dispatcher = null;

        if (dispatcher is not null)
        {
            dispatcher.InvokeShutdown();
        }

        _ready.Dispose();
    }
}
