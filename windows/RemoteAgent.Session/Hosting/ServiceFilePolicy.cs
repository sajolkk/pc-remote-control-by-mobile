using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Ipc;
using RemoteAgent.Protocol;
using FileOptions = RemoteAgent.Core.Configuration.FileOptions;

namespace RemoteAgent.Session.Hosting;

/// <summary>
/// Keeps the agent's copy of the file-management policy in step with the service's.
/// </summary>
/// <remarks>
/// <para><b>Why the agent does not just read the configuration file.</b> The agent serves files,
/// so it must know which directories are allowed; but <c>config.json</c> lives in a directory
/// restricted to SYSTEM and Administrators, and the agent runs as the ordinary signed-in user.
/// Every other setting the agent uses can fall back to a harmless default when the file is
/// unreadable. This one cannot: the default is the whole PC, so "could not read the file" would
/// quietly undo an owner's decision to share only one folder. The service can read the file, so
/// the service is asked (<see cref="IpcCommands.GetFilePolicy"/>).</para>
///
/// <para><b>Why it is polled rather than fetched on demand.</b> A file command is handled on the
/// IPC pump, and a request to the service from inside that handler could never be answered — the
/// same pump would have to read the reply. So the policy is refreshed in the background every few
/// seconds and handlers read the cached copy. That also means an edit to the configuration file
/// takes effect within seconds, with nothing restarted.</para>
///
/// <para><b>Unknown means nothing is allowed.</b> Until the service has answered once,
/// <see cref="Current"/> is null, which <c>ConfiguredFileRoots</c> treats as an empty policy. The
/// last answer is kept if a later refresh fails: it came from the same file, and dropping to
/// "nothing" on a slow reply would make file browsing flicker.</para>
/// </remarks>
public sealed class ServiceFilePolicy
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly ServiceChannelClient _channel;
    private readonly ILogger<ServiceFilePolicy> _logger;

    private volatile FileOptions? _current;
    private string? _lastDescription;

    /// <summary>Creates the policy holder.</summary>
    public ServiceFilePolicy(ServiceChannelClient channel, ILogger<ServiceFilePolicy> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The policy the service last reported, or null when it has not answered yet.</summary>
    public FileOptions? Current => _current;

    /// <summary>Refreshes the policy until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool refreshed = false;

            try
            {
                refreshed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
            {
                _logger.LogDebug(ex, "The file policy could not be refreshed.");
            }

            try
            {
                await Task.Delay(refreshed ? RefreshInterval : RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_channel.IsConnected)
        {
            return false;
        }

        IpcResponse response = await _channel
            .RequestAsync(IpcCommands.GetFilePolicy, null, RequestTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok || response.Data is null)
        {
            return false;
        }

        IpcFilePolicy? policy = response.Data.Deserialize<IpcFilePolicy>(ProtocolJson.Options);
        if (policy is null)
        {
            return false;
        }

        _current = new FileOptions
        {
            UseDefaultRoots = policy.UseDefaultRoots,
            MaxFileSizeMb = Math.Clamp(policy.MaxFileSizeMb, 1, 1024 * 64),
            AllowedRoots =
            [
                .. (policy.AllowedRoots ?? []).Select(static root => new AllowedRoot
                {
                    Alias = root.Alias,
                    Path = root.Path,
                    Write = root.Write,
                }),
            ],
        };

        // Logged when it changes, not every five seconds. Counts only: a configured path is the
        // owner's business and tells a log reader nothing the configuration file does not.
        string description = FormattableString.Invariant(
            $"built-in roots {(policy.UseDefaultRoots ? "on" : "off")}, {policy.AllowedRoots?.Length ?? 0} configured root(s)");

        if (!string.Equals(description, _lastDescription, StringComparison.Ordinal))
        {
            _lastDescription = description;
            _logger.LogInformation("File management policy from the service: {Policy}.", description);
        }

        return true;
    }
}
