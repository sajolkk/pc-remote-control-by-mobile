using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using RemoteAgent.Core.Configuration;

namespace RemoteAgent.Diagnostics;

/// <summary>
/// Structured logging setup, with redaction enforced at the sink.
/// </summary>
/// <remarks>
/// <para>Two sinks: newline-delimited JSON files for machine reading and for the UI's log
/// view, and the console when running in console mode. JSON rather than formatted text
/// because the requirement is to be able to answer questions like "which device ran which
/// command when", and grepping prose does not do that reliably.</para>
///
/// <para><b>Redaction is structural.</b> §7.5 forbids logging credentials, keys, tokens,
/// clipboard contents and file contents. Relying on every call site to remember that is how
/// secrets end up in logs, so <see cref="SecretRedactionEnricher"/> inspects property values
/// as they are written and replaces anything that looks like a credential. That is a safety
/// net, not a licence to be careless — the call sites still avoid passing secrets — but it
/// means one forgetful log statement cannot leak a session token.</para>
/// </remarks>
public static class AgentLogging
{
    /// <summary>Builds the logger for a host.</summary>
    /// <param name="paths">Resolved data directory, which decides where logs live.</param>
    /// <param name="minimumLevel">Configured verbosity.</param>
    /// <param name="retainDays">How long to keep rolled files.</param>
    /// <param name="toConsole">Whether to also write to the console (console mode).</param>
    /// <param name="componentName">Short name distinguishing service, agent and UI logs.</param>
    public static Serilog.ILogger Create(
        AgentPaths paths,
        string minimumLevel,
        int retainDays,
        bool toConsole,
        string componentName)
    {
        ArgumentNullException.ThrowIfNull(paths);

        LogEventLevel level = ParseLevel(minimumLevel);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(level)

            // Framework noise at Information would bury the events that matter.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net", LogEventLevel.Warning)

            .Enrich.FromLogContext()
            .Enrich.WithProperty("component", componentName)
            .Enrich.With(new SecretRedactionEnricher())

            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(paths.LogsDirectory, $"{componentName}-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, retainDays),
                fileSizeLimitBytes: 20L * 1024 * 1024,
                rollOnFileSizeLimit: true,

                // The log must never be the reason the agent stops working, so a locked or
                // full log directory degrades to dropped log lines rather than an exception
                // on a request path.
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2));

        if (toConsole)
        {
            configuration = configuration.WriteTo.Console(
                outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        return configuration.CreateLogger();
    }

    private static LogEventLevel ParseLevel(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out LogEventLevel level) ? level : LogEventLevel.Information;
}

/// <summary>
/// Replaces values that look like secrets before they reach a sink (§7.5).
/// </summary>
/// <remarks>
/// <para>Matches on <em>property name</em>, not value content. Guessing whether a string is
/// a token from its shape produces both false positives (redacting a device name) and false
/// negatives (missing a short token), whereas the property name is chosen by the developer
/// and is a reliable signal.</para>
///
/// <para>The list intentionally includes <c>clipboard</c> and <c>content</c>: clipboard text
/// and file contents are user data that this system transports but has no business
/// recording, and a log file is a far more durable place than either.</para>
/// </remarks>
public sealed class SecretRedactionEnricher : ILogEventEnricher
{
    private const string Replacement = "[redacted]";

    private static readonly string[] SensitiveNameFragments =
    [
        "password", "passwd", "secret", "token", "credential", "privatekey", "private_key",
        "apikey", "api_key", "clipboard", "filecontent", "file_content", "content",
        "pin", "passphrase", "pfx", "certificatedata",
    ];

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        List<string>? toRedact = null;

        foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
        {
            if (IsSensitive(property.Key))
            {
                (toRedact ??= []).Add(property.Key);
            }
        }

        if (toRedact is null)
        {
            return;
        }

        foreach (string name in toRedact)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(Replacement)));
        }
    }

    private static bool IsSensitive(string propertyName)
    {
        foreach (string fragment in SensitiveNameFragments)
        {
            if (propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
