using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// How an application was discovered, which determines how it is launched.
/// </summary>
public enum AppKind
{
    /// <summary>A classic desktop program found via Start Menu shortcut or registry.</summary>
    Desktop,

    /// <summary>A packaged (Store/UWP) app, launched by Application User Model ID.</summary>
    Packaged,

    /// <summary>A detected web browser. Also appears in the browser list.</summary>
    Browser,
}

/// <summary>
/// One entry in the application registry (§4).
/// </summary>
/// <remarks>
/// <see cref="Id"/> is an opaque, PC-generated handle — never a file path. The
/// mobile app can only ever ask to launch an id that this PC itself enumerated,
/// which is what makes "no arbitrary executable paths from the network" structural
/// rather than a validation step that could be forgotten.
/// </remarks>
public sealed class AppInfo
{
    /// <summary>Opaque launch handle, stable across restarts for the same application.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>How the app was discovered, and therefore how it launches.</summary>
    [JsonPropertyName("kind")]
    public AppKind Kind { get; init; }

    /// <summary>Publisher, when known.</summary>
    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    /// <summary>Whether at least one process for this app is currently running.</summary>
    [JsonPropertyName("running")]
    public bool Running { get; init; }

    /// <summary>Number of running processes attributed to this app.</summary>
    [JsonPropertyName("processCount")]
    public int ProcessCount { get; init; }

    /// <summary>Whether the app has a visible top-level window that can be focused.</summary>
    [JsonPropertyName("hasWindow")]
    public bool HasWindow { get; init; }

    /// <summary>
    /// Icon as a base64 PNG, or null when icons were not requested or could not be
    /// extracted. Sent separately from the list by default to keep <c>app.list</c>
    /// small on machines with hundreds of installed programs.
    /// </summary>
    [JsonPropertyName("iconPng")]
    public string? IconPng { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.AppList"/>.
/// </summary>
public sealed class AppListArgs
{
    /// <summary>Restrict the result to applications currently running.</summary>
    [JsonPropertyName("runningOnly")]
    public bool RunningOnly { get; init; }

    /// <summary>Case-insensitive substring filter on the display name.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    /// <summary>Include base64 PNG icons. Off by default because it is expensive.</summary>
    [JsonPropertyName("includeIcons")]
    public bool IncludeIcons { get; init; }

    /// <summary>Maximum entries to return. Clamped server-side.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

/// <summary>
/// Result of <see cref="CommandNames.AppList"/>.
/// </summary>
public sealed class AppListResult
{
    /// <summary>Matching applications, ordered running-first then by name.</summary>
    [JsonPropertyName("apps")]
    public AppInfo[] Apps { get; init; } = [];

    /// <summary>Total matches before the limit was applied.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>When the registry was last rebuilt, Unix milliseconds.</summary>
    [JsonPropertyName("catalogBuiltMs")]
    public long CatalogBuiltMs { get; init; }
}

/// <summary>
/// Arguments for commands that act on one application.
/// </summary>
public sealed class AppTargetArgs
{
    /// <summary>The <see cref="AppInfo.Id"/> to act on.</summary>
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;
}

/// <summary>
/// Arguments for <see cref="CommandNames.AppClose"/>.
/// </summary>
public sealed class AppCloseArgs
{
    /// <summary>The <see cref="AppInfo.Id"/> to close.</summary>
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    /// <summary>
    /// Terminate processes that ignore the close request. Defaults to false: a
    /// graceful close must be attempted first, because forcing loses unsaved work.
    /// </summary>
    [JsonPropertyName("force")]
    public bool Force { get; init; }

    /// <summary>How long to wait for a graceful exit before reporting back.</summary>
    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>
/// Result of launching, focusing or closing an application.
/// </summary>
public sealed class AppActionResult
{
    /// <summary>The app acted upon.</summary>
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    /// <summary>Whether the action achieved its goal.</summary>
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    /// <summary>Whether the app is running after the action.</summary>
    [JsonPropertyName("running")]
    public bool Running { get; init; }

    /// <summary>
    /// Whether force-termination was used. Reported honestly so the client can
    /// tell the user their app was killed rather than closed.
    /// </summary>
    [JsonPropertyName("forced")]
    public bool Forced { get; init; }

    /// <summary>Explanation when the action did not fully succeed.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// One detected browser.
/// </summary>
public sealed class BrowserInfo
{
    /// <summary>Opaque handle for use in browser commands.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name, e.g. <c>Google Chrome</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Whether this is the user's default browser for https.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    /// <summary>Whether the browser currently has a running process.</summary>
    [JsonPropertyName("running")]
    public bool Running { get; init; }
}

/// <summary>
/// Result of <see cref="CommandNames.BrowserList"/>.
/// </summary>
public sealed class BrowserListResult
{
    /// <summary>Detected browsers, default first.</summary>
    [JsonPropertyName("browsers")]
    public BrowserInfo[] Browsers { get; init; } = [];
}

/// <summary>
/// Arguments for browser commands that may target a specific browser.
/// </summary>
public sealed class BrowserTargetArgs
{
    /// <summary>The browser to use. Null means the user's default.</summary>
    [JsonPropertyName("browserId")]
    public string? BrowserId { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.BrowserClose"/>.
/// </summary>
public sealed class BrowserCloseArgs
{
    /// <summary>The browser to close. Null means the user's default.</summary>
    [JsonPropertyName("browserId")]
    public string? BrowserId { get; init; }

    /// <summary>
    /// Terminate the browser's processes if it does not close on request. Defaults to false,
    /// because forcing a browser closed loses the user's open tabs.
    /// </summary>
    [JsonPropertyName("force")]
    public bool Force { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.BrowserOpenUrl"/>.
/// </summary>
public sealed class BrowserOpenUrlArgs
{
    /// <summary>
    /// The URL to open. Validated to be absolute http or https before use; every
    /// other scheme is refused, because <c>file:</c>, <c>javascript:</c> and
    /// custom protocol handlers are all routes to arbitrary local execution.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>The browser to use. Null means the user's default.</summary>
    [JsonPropertyName("browserId")]
    public string? BrowserId { get; init; }
}
