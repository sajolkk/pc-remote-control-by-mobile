using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Shell;

/// <summary>
/// One application known to this PC.
/// </summary>
/// <param name="Id">Opaque, stable handle used by remote callers.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">How it was discovered, which decides how it launches.</param>
/// <param name="Publisher">Publisher when known.</param>
/// <param name="ShortcutPath">The <c>.lnk</c> to launch, for desktop apps.</param>
/// <param name="ExecutablePath">Resolved target executable, used to attribute processes.</param>
/// <param name="AppUserModelId">Activation id, for packaged apps.</param>
internal sealed record AppEntry(
    string Id,
    string Name,
    AppKind Kind,
    string? Publisher,
    string? ShortcutPath,
    string? ExecutablePath,
    string? AppUserModelId)
{
    /// <summary>
    /// Process name used to attribute running processes to this app: the executable
    /// file name without its extension, which is what <see cref="Process.ProcessName"/>
    /// reports.
    /// </summary>
    internal string? ProcessName => ExecutablePath is null
        ? null
        : Path.GetFileNameWithoutExtension(ExecutablePath);
}

/// <summary>
/// The application registry and launcher (§4).
/// </summary>
/// <remarks>
/// <para><b>The security property.</b> Remote callers name an <see cref="AppEntry.Id"/>,
/// which is a hash this PC generated while enumerating its own Start Menu and package
/// list. A path, a command line or an arbitrary executable name simply has nowhere to
/// enter: the launch methods take an id, look it up in a locally-built dictionary, and
/// use the stored shortcut or activation id. "No arbitrary executable paths from the
/// network" is therefore a property of the data flow rather than a validation rule that
/// could be bypassed (§7.3).</para>
///
/// <para><b>Discovery sources.</b> Start Menu shortcuts (both all-users and per-user)
/// for desktop programs, and the package list for Store apps. The Start Menu is used in
/// preference to the registry's uninstall entries because it is what the user actually
/// sees as "my programs": uninstall entries include runtimes, redistributables and
/// update helpers that nobody wants in a launcher.</para>
///
/// <para><b>Graceful degradation.</b> Package enumeration needs a capability that some
/// configurations withhold. When it fails, desktop apps are still returned and the
/// failure is logged once — a PC with no Store apps listed is a smaller feature set, not
/// a broken agent (§0).</para>
///
/// <para><b>Caching.</b> The catalog is built once and rebuilt on request or when stale.
/// Running state and window state are recomputed on every list, because those change
/// constantly while the catalog itself does not.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsAppCatalog : IAppCatalog
{
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(10);
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;
    private const int DefaultCloseTimeoutMs = 5000;

    /// <summary>
    /// Shortcut names that are never applications the user wants to launch remotely.
    /// Matched case-insensitively as whole words within the display name.
    /// </summary>
    private static readonly string[] ExcludedNameFragments =
    [
        "uninstall", "readme", "read me", "release notes", "documentation", "help",
        "license", "licence", "changelog", "what's new", "repair", "modify setup",
        "website", "web site", "visit ", "support",
    ];

    private readonly WindowInspector _windows;
    private readonly ILogger<WindowsAppCatalog> _logger;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]> _iconCache = new(StringComparer.Ordinal);

    private Dictionary<string, AppEntry> _entries = new(StringComparer.Ordinal);
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;
    private bool _packageEnumerationFailed;

    /// <summary>Creates the catalog.</summary>
    public WindowsAppCatalog(WindowInspector windows, ILogger<WindowsAppCatalog> logger)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AppListResult> ListAsync(AppListArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        Dictionary<string, AppEntry> entries = await EnsureBuiltAsync(cancellationToken).ConfigureAwait(false);
        RuntimeState runtime = SnapshotRuntimeState();

        IEnumerable<AppEntry> candidates = entries.Values;

        if (!string.IsNullOrWhiteSpace(args.Query))
        {
            string query = args.Query.Trim();
            candidates = candidates.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var projected = new List<AppInfo>();
        foreach (AppEntry entry in candidates)
        {
            (bool running, int count, bool hasWindow) = runtime.Evaluate(entry);

            if (args.RunningOnly && !running)
            {
                continue;
            }

            projected.Add(new AppInfo
            {
                Id = entry.Id,
                Name = entry.Name,
                Kind = entry.Kind,
                Publisher = entry.Publisher,
                Running = running,
                ProcessCount = count,
                HasWindow = hasWindow,
                IconPng = args.IncludeIcons ? TryGetIconBase64(entry) : null,
            });
        }

        int total = projected.Count;
        int limit = Math.Clamp(args.Limit ?? DefaultLimit, 1, MaxLimit);

        // Running apps first, then alphabetical: the thing the user most likely wants to
        // focus or close is what they are already running.
        List<AppInfo> ordered = projected
            .OrderByDescending(static a => a.Running)
            .ThenBy(static a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();

        return new AppListResult
        {
            Apps = ordered.ToArray(),
            Total = total,
            CatalogBuiltMs = _builtAt == DateTimeOffset.MinValue ? 0 : _builtAt.ToUnixTimeMilliseconds(),
        };
    }

    /// <inheritdoc />
    public async Task<AppInfo?> FindAsync(string appId, CancellationToken cancellationToken)
    {
        Dictionary<string, AppEntry> entries = await EnsureBuiltAsync(cancellationToken).ConfigureAwait(false);
        if (!entries.TryGetValue(appId ?? string.Empty, out AppEntry? entry))
        {
            return null;
        }

        RuntimeState runtime = SnapshotRuntimeState();
        (bool running, int count, bool hasWindow) = runtime.Evaluate(entry);

        return new AppInfo
        {
            Id = entry.Id,
            Name = entry.Name,
            Kind = entry.Kind,
            Publisher = entry.Publisher,
            Running = running,
            ProcessCount = count,
            HasWindow = hasWindow,
        };
    }

    /// <inheritdoc />
    public async Task<AppActionResult> LaunchAsync(string appId, CancellationToken cancellationToken)
    {
        Dictionary<string, AppEntry> entries = await EnsureBuiltAsync(cancellationToken).ConfigureAwait(false);
        if (!entries.TryGetValue(appId ?? string.Empty, out AppEntry? entry))
        {
            return NotFound(appId);
        }

        try
        {
            if (entry.Kind == AppKind.Packaged && entry.AppUserModelId is { Length: > 0 } aumid)
            {
                uint processId = ActivatePackagedApp(aumid);
                _logger.LogInformation(
                    "Launched packaged app {Name} (pid {ProcessId}).",
                    entry.Name,
                    processId);

                return new AppActionResult { AppId = entry.Id, Succeeded = true, Running = true };
            }

            string? target = entry.ShortcutPath ?? entry.ExecutablePath;
            if (target is null)
            {
                return new AppActionResult
                {
                    AppId = entry.Id,
                    Succeeded = false,
                    Detail = "This entry has no launch target.",
                };
            }

            // UseShellExecute launches the shortcut exactly as double-clicking it would,
            // preserving the arguments, working directory and elevation the installer
            // configured. The path is one this PC enumerated, never client input.
            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty,
            };

            using Process? started = Process.Start(startInfo);
            _logger.LogInformation("Launched {Name}.", entry.Name);

            return new AppActionResult { AppId = entry.Id, Succeeded = true, Running = true };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or COMException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Could not launch {Name}.", entry.Name);
            return new AppActionResult
            {
                AppId = entry.Id,
                Succeeded = false,
                Detail = "Windows refused to start this application.",
            };
        }
    }

    /// <inheritdoc />
    public async Task<AppActionResult> FocusAsync(string appId, CancellationToken cancellationToken)
    {
        Dictionary<string, AppEntry> entries = await EnsureBuiltAsync(cancellationToken).ConfigureAwait(false);
        if (!entries.TryGetValue(appId ?? string.Empty, out AppEntry? entry))
        {
            return NotFound(appId);
        }

        RuntimeState runtime = SnapshotRuntimeState();
        List<TopLevelWindow> windows = runtime.WindowsFor(entry);

        if (windows.Count == 0)
        {
            return new AppActionResult
            {
                AppId = entry.Id,
                Succeeded = false,
                Running = runtime.Evaluate(entry).Running,
                Detail = "The application has no window to focus.",
            };
        }

        bool focused = _windows.TryFocus(windows[0]);

        return new AppActionResult
        {
            AppId = entry.Id,
            Succeeded = focused,
            Running = true,
            Detail = focused
                ? null
                : "Windows blocked the focus change. The window may have been restored but not brought to front.",
        };
    }

    /// <inheritdoc />
    public async Task<AppActionResult> CloseAsync(
        string appId,
        bool force,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        Dictionary<string, AppEntry> entries = await EnsureBuiltAsync(cancellationToken).ConfigureAwait(false);
        if (!entries.TryGetValue(appId ?? string.Empty, out AppEntry? entry))
        {
            return NotFound(appId);
        }

        RuntimeState runtime = SnapshotRuntimeState();
        List<Process> processes = runtime.ProcessesFor(entry);

        if (processes.Count == 0)
        {
            return new AppActionResult
            {
                AppId = entry.Id,
                Succeeded = true,
                Running = false,
                Detail = "The application was not running.",
            };
        }

        int timeout = timeoutMs <= 0 ? DefaultCloseTimeoutMs : Math.Clamp(timeoutMs, 250, 30000);

        // Step 1: ask politely. Every window gets WM_CLOSE, which is what clicking the X
        // does — including the chance to prompt the user about unsaved work.
        foreach (TopLevelWindow window in runtime.WindowsFor(entry))
        {
            _windows.RequestClose(window);
        }

        bool exited = await WaitForExitAsync(processes, timeout, cancellationToken).ConfigureAwait(false);

        if (exited)
        {
            _logger.LogInformation("{Name} closed gracefully.", entry.Name);
            return new AppActionResult { AppId = entry.Id, Succeeded = true, Running = false };
        }

        if (!force)
        {
            // Refusing to escalate without an explicit request is the point: the client
            // gets told the app is still running and can ask again with force, which
            // makes discarding unsaved work a decision rather than a side effect.
            _logger.LogInformation(
                "{Name} did not close within {Timeout}ms and force was not requested.",
                entry.Name,
                timeout);

            return new AppActionResult
            {
                AppId = entry.Id,
                Succeeded = false,
                Running = true,
                Detail = "The application did not close. It may be showing a prompt on the PC. " +
                         "Retry with force to terminate it, which will discard unsaved work.",
            };
        }

        int killed = 0;
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
            {
                _logger.LogWarning(ex, "Could not terminate a process for {Name}.", entry.Name);
            }
        }

        _logger.LogWarning("{Name} was force-terminated ({Count} process(es)).", entry.Name, killed);

        return new AppActionResult
        {
            AppId = entry.Id,
            Succeeded = killed > 0,
            Running = false,
            Forced = true,
            Detail = killed > 0 ? "The application was terminated." : "No process could be terminated.",
        };
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _builtAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _buildLock.Release();
        }

        await EnsureBuiltAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AppActionResult NotFound(string? appId) => new()
    {
        AppId = appId ?? string.Empty,
        Succeeded = false,
        Detail = "No such application.",
    };

    private async Task<Dictionary<string, AppEntry>> EnsureBuiltAsync(CancellationToken cancellationToken)
    {
        if (_builtAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - _builtAt < CatalogLifetime)
        {
            return _entries;
        }

        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_builtAt != DateTimeOffset.MinValue && DateTimeOffset.UtcNow - _builtAt < CatalogLifetime)
            {
                return _entries;
            }

            long started = Stopwatch.GetTimestamp();
            var entries = new Dictionary<string, AppEntry>(StringComparer.Ordinal);

            AddDesktopApps(entries);
            await AddPackagedAppsAsync(entries, cancellationToken).ConfigureAwait(false);

            _entries = entries;
            _builtAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Application catalog built: {Count} app(s) in {ElapsedMs:F0}ms.",
                entries.Count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return _entries;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// Enumerates Start Menu shortcuts from both the all-users and per-user trees.
    /// </summary>
    private void AddDesktopApps(Dictionary<string, AppEntry> entries)
    {
        foreach (Environment.SpecialFolder folder in
                 new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 6,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate the Start Menu folder {Root}.", root);
                continue;
            }

            foreach (string shortcut in shortcuts)
            {
                string name = Path.GetFileNameWithoutExtension(shortcut);
                if (IsExcluded(name))
                {
                    continue;
                }

                string? target = ShellLink.TryResolveTarget(shortcut);

                // A shortcut whose target is gone is a leftover from an incomplete
                // uninstall; offering it would produce a launch that fails.
                if (target is null || !File.Exists(target))
                {
                    continue;
                }

                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Shortcuts to documents, folders and control-panel items are not
                    // applications, and launching arbitrary file types remotely is not
                    // something this feature promises.
                    continue;
                }

                string id = MakeId("d", target.ToLowerInvariant());

                // Several shortcuts often point at the same executable. First one wins,
                // and the shorter display name is preferred as it is usually the real
                // product name rather than a variant.
                if (entries.TryGetValue(id, out AppEntry? existing))
                {
                    if (name.Length < existing.Name.Length)
                    {
                        entries[id] = existing with { Name = name, ShortcutPath = shortcut };
                    }

                    continue;
                }

                entries[id] = new AppEntry(
                    id,
                    name,
                    AppKind.Desktop,
                    TryGetPublisher(target),
                    shortcut,
                    target,
                    null);
            }
        }
    }

    /// <summary>
    /// Enumerates Store (packaged) applications.
    /// </summary>
    /// <remarks>
    /// Uses the package manager's per-user query and each package's app list entries,
    /// which is what yields a launchable Application User Model ID. Failure is tolerated:
    /// some configurations withhold the package query capability, and on those machines
    /// the agent simply reports desktop apps only (§0).
    /// </remarks>
    private async Task AddPackagedAppsAsync(
        Dictionary<string, AppEntry> entries,
        CancellationToken cancellationToken)
    {
        if (_packageEnumerationFailed)
        {
            return;
        }

        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();

            // An empty user security id means "the user this process is running as",
            // which is exactly right for the session agent and needs no privilege.
            foreach (global::Windows.ApplicationModel.Package package in manager.FindPackagesForUser(string.Empty))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
                {
                    // Frameworks and resource packages are not launchable applications.
                    continue;
                }

                IReadOnlyList<global::Windows.ApplicationModel.Core.AppListEntry> appEntries;
                try
                {
                    appEntries = await package.GetAppListEntriesAsync().AsTask(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException
                                              or FileNotFoundException)
                {
                    // A package mid-update or with a damaged manifest: skip it quietly.
                    continue;
                }

                foreach (global::Windows.ApplicationModel.Core.AppListEntry appEntry in appEntries)
                {
                    string aumid = appEntry.AppUserModelId;
                    string name = appEntry.DisplayInfo.DisplayName;

                    if (string.IsNullOrWhiteSpace(aumid) || string.IsNullOrWhiteSpace(name) || IsExcluded(name))
                    {
                        continue;
                    }

                    string id = MakeId("u", aumid.ToLowerInvariant());
                    entries.TryAdd(id, new AppEntry(
                        id,
                        name,
                        AppKind.Packaged,
                        SafePublisherName(package),
                        null,
                        null,
                        aumid));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or TypeLoadException
                                      or PlatformNotSupportedException or NotSupportedException)
        {
            // Logged once, then never retried for the life of the process: repeating a
            // failing multi-second enumeration on every app.list would be a self-inflicted
            // performance problem.
            _packageEnumerationFailed = true;
            _logger.LogWarning(
                ex,
                "Store apps could not be enumerated on this PC, so only desktop applications will be listed.");
        }
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static string? SafePublisherName(global::Windows.ApplicationModel.Package package)
    {
        try
        {
            return package.Id.Publisher;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private uint ActivatePackagedApp(string appUserModelId)
    {
        var manager = (NativeMethods.IApplicationActivationManager)new NativeMethods.ApplicationActivationManager();
        try
        {
            int hr = manager.ActivateApplication(
                appUserModelId,
                null,
                NativeMethods.ActivateOptions.NoErrorUI,
                out uint processId);

            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return processId;
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private static bool IsExcluded(string name)
    {
        foreach (string fragment in ExcludedNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? TryGetPublisher(string executablePath)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogTrace(ex, "Could not read version info for {Path}.", executablePath);
            return null;
        }
    }

    /// <summary>
    /// Derives a stable opaque id from a local identity string.
    /// </summary>
    /// <remarks>
    /// A hash rather than the path itself, for two reasons: the id is sent to the phone
    /// and stored there, so it should not disclose the PC's directory layout; and it must
    /// stay the same across catalog rebuilds so a mobile shortcut keeps working.
    /// </remarks>
    private static string MakeId(string prefix, string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return string.Create(
            2 + 16,
            (prefix, hash),
            static (span, state) =>
            {
                span[0] = state.prefix[0];
                span[1] = '-';
                for (int i = 0; i < 8; i++)
                {
                    state.hash[i].TryFormat(span[(2 + (i * 2))..], out _, "x2", CultureInfo.InvariantCulture);
                }
            });
    }

    private string? TryGetIconBase64(AppEntry entry)
    {
        if (entry.ExecutablePath is null)
        {
            // Packaged-app logos come from the package's display info, which is a
            // separate async path; not attempted here rather than returning something
            // misleading.
            return null;
        }

        if (_iconCache.TryGetValue(entry.Id, out byte[]? cached))
        {
            return cached.Length == 0 ? null : Convert.ToBase64String(cached);
        }

        try
        {
            byte[] png = IconExtractor.ExtractPng(entry.ExecutablePath);
            _iconCache[entry.Id] = png;
            return png.Length == 0 ? null : Convert.ToBase64String(png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException
                                      or ArgumentException)
        {
            // Cache the failure so a stubborn executable is not retried on every list.
            _iconCache[entry.Id] = [];
            _logger.LogTrace(ex, "Could not extract an icon for {Path}.", entry.ExecutablePath);
            return null;
        }
    }

    private RuntimeState SnapshotRuntimeState() => new(_windows.EnumerateByProcess(), _logger);

    private static async Task<bool> WaitForExitAsync(
        List<Process> processes,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(timeout.Token))).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return processes.All(static p =>
            {
                try
                {
                    return p.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            });
        }
    }

    /// <summary>
    /// A point-in-time view of running processes and their windows.
    /// </summary>
    /// <remarks>
    /// Taken once per command rather than per application: a machine can have hundreds
    /// of installed apps, and enumerating processes for each would make listing them
    /// quadratic.
    /// </remarks>
    private sealed class RuntimeState
    {
        private readonly Dictionary<string, List<Process>> _byProcessName;
        private readonly IReadOnlyDictionary<uint, List<TopLevelWindow>> _windowsByProcess;

        internal RuntimeState(
            IReadOnlyDictionary<uint, List<TopLevelWindow>> windowsByProcess,
            ILogger logger)
        {
            _windowsByProcess = windowsByProcess;
            _byProcessName = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    string name = process.ProcessName;
                    if (!_byProcessName.TryGetValue(name, out List<Process>? list))
                    {
                        list = [];
                        _byProcessName[name] = list;
                    }

                    list.Add(process);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Could not enumerate processes; running state will be incomplete.");
            }
        }

        internal (bool Running, int Count, bool HasWindow) Evaluate(AppEntry entry)
        {
            List<Process> processes = ProcessesFor(entry);
            if (processes.Count == 0)
            {
                return (false, 0, false);
            }

            bool hasWindow = processes.Any(p =>
            {
                try
                {
                    return _windowsByProcess.ContainsKey((uint)p.Id);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });

            return (true, processes.Count, hasWindow);
        }

        internal List<Process> ProcessesFor(AppEntry entry)
        {
            string? name = entry.ProcessName;
            if (name is null)
            {
                return [];
            }

            return _byProcessName.TryGetValue(name, out List<Process>? processes) ? processes : [];
        }

        internal List<TopLevelWindow> WindowsFor(AppEntry entry)
        {
            var result = new List<TopLevelWindow>();
            foreach (Process process in ProcessesFor(entry))
            {
                try
                {
                    if (_windowsByProcess.TryGetValue((uint)process.Id, out List<TopLevelWindow>? windows))
                    {
                        result.AddRange(windows);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process exited between enumeration and inspection.
                }
            }

            return result;
        }
    }
}
