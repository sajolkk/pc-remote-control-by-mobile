using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Validation;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Windows.Shell;

/// <summary>
/// Detects and controls installed browsers (§4).
/// </summary>
/// <remarks>
/// <para><b>Detection</b> reads the registry's registered-applications data rather than
/// probing for known install paths, so a browser installed anywhere — per-user, on
/// another drive, portable — is found, and a machine with only Firefox is as well served
/// as one with only Edge (§0). The user's actual default is read from the https URL
/// association, which is the same thing Windows itself consults.</para>
///
/// <para><b>URL safety.</b> A URL is the one free-form string this system hands to the
/// shell, so it passes <see cref="UrlValidator"/> first — http/https only, absolute, no
/// embedded credentials — and is then passed as a <em>separate argument value</em> via
/// <see cref="ProcessStartInfo.ArgumentList"/>. Nothing is ever concatenated into a
/// command line, so there is no quoting to get wrong and no argument injection to
/// defend against.</para>
///
/// <para><b>Closing a browser</b> is genuinely imprecise: browsers run many processes,
/// and killing them costs the user their tabs. So close is a graceful window-close
/// request across the browser's processes, and force is refused unless explicitly
/// requested.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsBrowserController : IBrowserController
{
    /// <summary>
    /// Browsers recognised by their registered ProgID prefix, with the display name to
    /// show. Anything registered as an https handler is also picked up generically, so
    /// this table improves naming rather than gating what is supported.
    /// </summary>
    private static readonly (string ProgIdPrefix, string DisplayName)[] KnownBrowsers =
    [
        ("ChromeHTML", "Google Chrome"),
        ("MSEdgeHTM", "Microsoft Edge"),
        ("FirefoxURL", "Mozilla Firefox"),
        ("BraveHTML", "Brave"),
        ("OperaStable", "Opera"),
        ("VivaldiHTM", "Vivaldi"),
        ("IE.HTTP", "Internet Explorer"),
    ];

    private readonly WindowInspector _windows;
    private readonly ILogger<WindowsBrowserController> _logger;

    private IReadOnlyList<BrowserEntry>? _cache;

    /// <summary>Creates the controller.</summary>
    public WindowsBrowserController(WindowInspector windows, ILogger<WindowsBrowserController> logger)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<BrowserListResult> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BrowserEntry> browsers = Detect();
        IReadOnlyDictionary<uint, List<TopLevelWindow>> windows = _windows.EnumerateByProcess();

        var infos = browsers
            .Select(b => new BrowserInfo
            {
                Id = b.Id,
                Name = b.DisplayName,
                IsDefault = b.IsDefault,
                Running = IsRunning(b, windows),
            })
            .OrderByDescending(static b => b.IsDefault)
            .ThenBy(static b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return Task.FromResult(new BrowserListResult { Browsers = infos });
    }

    /// <inheritdoc />
    public Task<AppActionResult> OpenAsync(string? browserId, CancellationToken cancellationToken)
    {
        BrowserEntry? browser = Resolve(browserId);
        if (browser is null)
        {
            return Task.FromResult(NotFound(browserId));
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = browser.ExecutablePath,
                UseShellExecute = true,
            };

            using Process? started = Process.Start(startInfo);
            _logger.LogInformation("Opened {Browser}.", browser.DisplayName);
            return Task.FromResult(new AppActionResult
            {
                AppId = browser.Id,
                Succeeded = true,
                Running = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Could not open {Browser}.", browser.DisplayName);
            return Task.FromResult(new AppActionResult
            {
                AppId = browser.Id,
                Succeeded = false,
                Detail = "Windows refused to start the browser.",
            });
        }
    }

    /// <inheritdoc />
    public Task<AppActionResult> OpenUrlAsync(string url, string? browserId, CancellationToken cancellationToken)
    {
        // Validated here as well as at the command handler: this is the last point before
        // the value reaches the OS, and a validator that only runs at the edge is one
        // refactor away from being skipped.
        if (!UrlValidator.TryValidate(url, out string normalized, out string? error))
        {
            _logger.LogWarning("Refused to open a URL: {Reason}", error);
            return Task.FromResult(new AppActionResult
            {
                AppId = browserId ?? string.Empty,
                Succeeded = false,
                Detail = error,
            });
        }

        BrowserEntry? browser = Resolve(browserId);

        try
        {
            ProcessStartInfo startInfo;

            if (browser is null && browserId is null)
            {
                // No specific browser requested and no default detected: let the shell
                // decide. ShellExecute on an http(s) URL opens the user's default handler
                // and cannot be steered at an executable, which is why the scheme
                // allowlist matters so much.
                startInfo = new ProcessStartInfo
                {
                    FileName = normalized,
                    UseShellExecute = true,
                };
            }
            else if (browser is null)
            {
                return Task.FromResult(NotFound(browserId));
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = browser.ExecutablePath,
                    UseShellExecute = true,
                };

                // ArgumentList, never a concatenated Arguments string: the runtime quotes
                // each value correctly, so a URL containing quotes or spaces cannot break
                // out into extra arguments.
                startInfo.ArgumentList.Add(normalized);
            }

            using Process? started = Process.Start(startInfo);

            // The URL is logged, deliberately. It is a command a paired device issued and
            // belongs in the audit trail; it is not user content like a clipboard or a
            // file, which §7.5 excludes.
            _logger.LogInformation(
                "Opened URL {Url} in {Browser}.",
                normalized,
                browser?.DisplayName ?? "the default browser");

            return Task.FromResult(new AppActionResult
            {
                AppId = browser?.Id ?? string.Empty,
                Succeeded = true,
                Running = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Could not open a URL in {Browser}.", browser?.DisplayName ?? "the default browser");
            return Task.FromResult(new AppActionResult
            {
                AppId = browser?.Id ?? string.Empty,
                Succeeded = false,
                Detail = "Windows refused to open the URL.",
            });
        }
    }

    /// <inheritdoc />
    public async Task<AppActionResult> CloseAsync(string? browserId, bool force, CancellationToken cancellationToken)
    {
        BrowserEntry? browser = Resolve(browserId);
        if (browser is null)
        {
            return NotFound(browserId);
        }

        string processName = Path.GetFileNameWithoutExtension(browser.ExecutablePath);
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not enumerate processes for {Browser}.", browser.DisplayName);
            return new AppActionResult { AppId = browser.Id, Succeeded = false, Detail = "Could not inspect the browser." };
        }

        if (processes.Length == 0)
        {
            return new AppActionResult
            {
                AppId = browser.Id,
                Succeeded = true,
                Running = false,
                Detail = "The browser was not running.",
            };
        }

        IReadOnlyDictionary<uint, List<TopLevelWindow>> windows = _windows.EnumerateByProcess();
        int requested = 0;

        foreach (Process process in processes)
        {
            try
            {
                if (windows.TryGetValue((uint)process.Id, out List<TopLevelWindow>? processWindows))
                {
                    foreach (TopLevelWindow window in processWindows)
                    {
                        if (_windows.RequestClose(window))
                        {
                            requested++;
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Process exited while we were looking at it.
            }
        }

        // Browsers take a moment to tear down their child processes even after the last
        // window closes, so give them one.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);

        bool stillRunning = Process.GetProcessesByName(processName).Length > 0;

        if (stillRunning && force)
        {
            int killed = 0;
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
                {
                    _logger.LogDebug(ex, "Could not terminate a {Browser} process.", browser.DisplayName);
                }
            }

            _logger.LogWarning("{Browser} was force-terminated ({Count} process(es)).", browser.DisplayName, killed);

            return new AppActionResult
            {
                AppId = browser.Id,
                Succeeded = killed > 0,
                Running = false,
                Forced = true,
                Detail = "The browser was terminated. Open tabs were not saved.",
            };
        }

        return new AppActionResult
        {
            AppId = browser.Id,
            Succeeded = !stillRunning,
            Running = stillRunning,
            Detail = stillRunning
                ? $"Asked {requested} window(s) to close, but the browser is still running. " +
                  "It may be prompting about open tabs on the PC."
                : null,
        };
    }

    private static AppActionResult NotFound(string? browserId) => new()
    {
        AppId = browserId ?? string.Empty,
        Succeeded = false,
        Detail = "No such browser is installed on this PC.",
    };

    private BrowserEntry? Resolve(string? browserId)
    {
        IReadOnlyList<BrowserEntry> browsers = Detect();

        if (string.IsNullOrWhiteSpace(browserId))
        {
            return browsers.FirstOrDefault(static b => b.IsDefault) ?? browsers.FirstOrDefault();
        }

        return browsers.FirstOrDefault(b => string.Equals(b.Id, browserId, StringComparison.Ordinal));
    }

    private static bool IsRunning(BrowserEntry browser, IReadOnlyDictionary<uint, List<TopLevelWindow>> windows)
    {
        try
        {
            string processName = Path.GetFileNameWithoutExtension(browser.ExecutablePath);
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates installed browsers from the registry.
    /// </summary>
    /// <remarks>
    /// <c>StartMenuInternet</c> is the key Windows itself uses to list browsers, and it is
    /// checked in both the machine and user hives so per-user installs (which Chrome and
    /// Firefox both do by default) are found.
    /// </remarks>
    private IReadOnlyList<BrowserEntry> Detect()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var found = new Dictionary<string, BrowserEntry>(StringComparer.OrdinalIgnoreCase);
        string? defaultProgId = ReadDefaultHttpsProgId();

        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey? clients = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
            if (clients is null)
            {
                continue;
            }

            foreach (string clientName in clients.GetSubKeyNames())
            {
                using RegistryKey? client = clients.OpenSubKey(clientName);
                if (client is null)
                {
                    continue;
                }

                using RegistryKey? command = client.OpenSubKey(@"shell\open\command");
                string? rawCommand = command?.GetValue(null) as string;
                string? executable = ExtractExecutablePath(rawCommand);

                if (executable is null || !File.Exists(executable))
                {
                    continue;
                }

                string displayName = client.GetValue(null) as string ?? clientName;
                displayName = PrettifyName(displayName, executable);

                string id = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();

                if (found.ContainsKey(id))
                {
                    continue;
                }

                found[id] = new BrowserEntry(
                    id,
                    displayName,
                    executable,
                    IsDefaultFor(defaultProgId, id, executable));
            }
        }

        if (found.Count == 0)
        {
            _logger.LogInformation(
                "No browsers were detected in the registry. URL requests will fall back to the shell's " +
                "default handler.");
        }

        _cache = found.Values.ToArray();
        return _cache;
    }

    /// <summary>Reads which ProgID handles https for this user, i.e. the real default browser.</summary>
    private string? ReadDefaultHttpsProgId()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");

            return key?.GetValue("ProgId") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Could not read the default browser association.");
            return null;
        }
    }

    private static bool IsDefaultFor(string? defaultProgId, string browserId, string executablePath)
    {
        if (string.IsNullOrEmpty(defaultProgId))
        {
            return false;
        }

        foreach ((string prefix, _) in KnownBrowsers)
        {
            if (defaultProgId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // Map the ProgID family back onto the executable name, which is how the
                // browsers are keyed here.
                string expected = prefix switch
                {
                    "ChromeHTML" => "chrome",
                    "MSEdgeHTM" => "msedge",
                    "FirefoxURL" => "firefox",
                    "BraveHTML" => "brave",
                    "OperaStable" => "opera",
                    "VivaldiHTM" => "vivaldi",
                    "IE.HTTP" => "iexplore",
                    _ => string.Empty,
                };

                return string.Equals(expected, browserId, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static string PrettifyName(string registryName, string executablePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(executablePath);

        foreach ((string prefix, string display) in KnownBrowsers)
        {
            string expected = prefix switch
            {
                "ChromeHTML" => "chrome",
                "MSEdgeHTM" => "msedge",
                "FirefoxURL" => "firefox",
                "BraveHTML" => "brave",
                "OperaStable" => "opera",
                "VivaldiHTM" => "vivaldi",
                "IE.HTTP" => "iexplore",
                _ => string.Empty,
            };

            if (string.Equals(expected, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }

        return registryName;
    }

    /// <summary>
    /// Pulls the executable out of a registry command string, which may or may not be
    /// quoted and may carry trailing arguments.
    /// </summary>
    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            int closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex > 0 ? trimmed[..(exeIndex + 4)] : trimmed;
    }

    /// <summary>One detected browser.</summary>
    private sealed record BrowserEntry(string Id, string DisplayName, string ExecutablePath, bool IsDefault);
}
