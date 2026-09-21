using System.Reflection;

namespace RemoteAgent.Core.Configuration;

/// <summary>
/// Resolves where the agent keeps its configuration, keys, pairings and logs.
/// </summary>
/// <remarks>
/// Nothing here is a literal path (§0). The location is resolved at runtime in a
/// fixed order of preference:
/// <list type="number">
/// <item>the <c>PCREMOTE_DATA_DIR</c> environment variable, for testing and for
/// unusual deployments;</item>
/// <item>a <c>portable.txt</c> marker beside the executable, which switches the
/// agent to storing everything in a <c>data</c> folder next to itself — this is what
/// makes a no-install, copy-anywhere deployment possible;</item>
/// <item>otherwise <c>%ProgramData%\PCRemote</c>, the correct location for a
/// machine-wide service's state.</item>
/// </list>
/// </remarks>
public sealed class AgentPaths
{
    /// <summary>Environment variable that overrides the data directory.</summary>
    public const string DataDirectoryVariable = "PCREMOTE_DATA_DIR";

    /// <summary>Marker file that selects portable mode.</summary>
    public const string PortableMarkerFileName = "portable.txt";

    /// <summary>Folder name used under the product directory.</summary>
    public const string ProductFolderName = "PCRemote";

    private AgentPaths(string root, bool isPortable)
    {
        Root = root;
        IsPortable = isPortable;
    }

    /// <summary>The resolved data directory.</summary>
    public string Root { get; }

    /// <summary>Whether the agent is running in portable (no-install) mode.</summary>
    public bool IsPortable { get; }

    /// <summary>Main configuration file.</summary>
    public string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>
    /// Machine-local overrides, never committed and never overwritten by an upgrade.
    /// </summary>
    public string LocalConfigFile => Path.Combine(Root, "config.local.json");

    /// <summary>Directory holding the identity certificate and key.</summary>
    public string KeysDirectory => Path.Combine(Root, "keys");

    /// <summary>
    /// The PC's identity certificate and key. Not a bare PKCS#12 file: it is an
    /// encrypted envelope, hence the neutral extension.
    /// </summary>
    public string IdentityFile => Path.Combine(KeysDirectory, "identity.bin");

    /// <summary>Paired device records.</summary>
    public string PairingStoreFile => Path.Combine(Root, "pairings.json");

    /// <summary>Directory holding rolling structured logs.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Directory for transient state such as icon caches.</summary>
    public string CacheDirectory => Path.Combine(Root, "cache");

    /// <summary>Resolves the data directory for this process.</summary>
    public static AgentPaths Resolve()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new AgentPaths(Path.GetFullPath(fromEnvironment.Trim()), isPortable: false);
        }

        string executableDirectory = GetExecutableDirectory();
        if (File.Exists(Path.Combine(executableDirectory, PortableMarkerFileName)))
        {
            return new AgentPaths(Path.Combine(executableDirectory, "data"), isPortable: true);
        }

        string programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        return new AgentPaths(Path.Combine(programData, ProductFolderName), isPortable: false);
    }

    /// <summary>
    /// Creates the directory tree if absent. Callers that need restrictive ACLs
    /// apply them afterwards, since ACL handling is platform-specific and belongs
    /// in the Windows layer.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(KeysDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    private static string GetExecutableDirectory()
    {
        // AppContext.BaseDirectory is correct for both a normal build and a
        // single-file publish, where the assembly location is empty.
        string baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            return baseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }

        string? assemblyPath = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(assemblyPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(assemblyPath) ?? Directory.GetCurrentDirectory();
    }
}
