using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Configuration;

namespace RemoteAgent.Service.Configuration;

/// <summary>
/// Creates and persists <c>config.json</c>, generating whatever is missing on first run.
/// </summary>
/// <remarks>
/// <para>This is where the zero-configuration promise in §0 is kept. On an arbitrary PC with
/// no prior setup, the first start writes a complete, valid configuration: a freshly
/// generated device id, the machine's own name as the display name, and defaults for
/// everything else. There is no installer step that bakes machine-specific values in, and no
/// file a user must edit before the system works.</para>
///
/// <para>Two files are read, in order: <c>config.json</c> (written by this class, safe to
/// regenerate) and <c>config.local.json</c> (never written by the agent, never overwritten by
/// an upgrade). The split means a user can pin a setting without fighting the agent over the
/// file, and an upgrade that adds a new default cannot silently discard their choice.</para>
///
/// <para>Writes are atomic — temp file then move — because a configuration truncated by a
/// power cut would leave the agent unable to start, and the device id it contains is what
/// every paired phone uses to recognise this PC.</para>
/// </remarks>
public sealed class AgentConfigurationStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly AgentPaths _paths;
    private readonly ILogger _logger;

    /// <summary>Creates the store.</summary>
    public AgentConfigurationStore(AgentPaths paths, ILogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures a usable configuration file exists, filling in generated values.
    /// </summary>
    /// <returns>The device identity that was found or created.</returns>
    public (string DeviceId, string DeviceName) EnsureInitialized()
    {
        _paths.EnsureCreated();

        AgentOptions options = LoadOrDefault();
        bool changed = false;

        if (string.IsNullOrWhiteSpace(options.Device.Id))
        {
            // A random GUID, deliberately unrelated to any hardware serial or machine SID.
            // It is broadcast during discovery, so it must reveal nothing about the machine
            // beyond "a PC-Remote agent exists here" (see DiscoveryBeacon).
            options.Device.Id = Guid.NewGuid().ToString("n");
            changed = true;
            _logger.LogInformation("Generated a new device id for this PC.");
        }

        if (string.IsNullOrWhiteSpace(options.Device.Name))
        {
            options.Device.Name = Environment.MachineName;
            changed = true;
            _logger.LogInformation("Using the computer name as this PC's display name.");
        }

        if (changed || !File.Exists(_paths.ConfigFile))
        {
            Save(options);
            _logger.LogInformation("Wrote configuration to {Path}.", _paths.ConfigFile);
        }

        return (options.Device.Id, options.Device.Name);
    }

    /// <summary>Reads the configuration, falling back to defaults when absent or invalid.</summary>
    public AgentOptions LoadOrDefault()
    {
        var options = new AgentOptions();

        if (!File.Exists(_paths.ConfigFile))
        {
            return options;
        }

        try
        {
            string json = File.ReadAllText(_paths.ConfigFile);
            JsonNode? root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            JsonNode? section = root?[AgentOptions.SectionName];
            if (section is null)
            {
                _logger.LogWarning(
                    "The configuration file has no '{Section}' section; using defaults.",
                    AgentOptions.SectionName);
                return options;
            }

            AgentOptions? parsed = section.Deserialize<AgentOptions>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            return parsed ?? options;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Refusing to start over a bad config would leave no way to reach the PC and fix
            // it. Starting on defaults is recoverable and the problem is logged loudly.
            _logger.LogError(
                ex,
                "The configuration file could not be read, so built-in defaults are in use. " +
                "The existing file has been left untouched for inspection.");

            return options;
        }
    }

    /// <summary>Persists the configuration atomically.</summary>
    public void Save(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var document = new JsonObject
        {
            [AgentOptions.SectionName] = JsonSerializer.SerializeToNode(options, WriteOptions),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ConfigFile)!);

        string temp = _paths.ConfigFile + ".tmp";
        File.WriteAllText(temp, document.ToJsonString(WriteOptions));
        File.Move(temp, _paths.ConfigFile, overwrite: true);
    }

    /// <summary>
    /// Applies a change and persists it, for settings the UI can toggle.
    /// </summary>
    public AgentOptions Update(Action<AgentOptions> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        AgentOptions options = LoadOrDefault();
        mutation(options);
        Save(options);
        return options;
    }
}
