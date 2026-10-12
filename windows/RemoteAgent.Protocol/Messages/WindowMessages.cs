using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

/// <summary>
/// One visible top-level application window — an entry Alt+Tab would show.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is opaque to the phone, exactly like an application id: it is a handle this PC
/// produced while enumerating its own desktop, and the PC re-validates that it still names a live
/// top-level window before acting on it. There is no parameter through which a caller could name a
/// window the PC did not list.
/// </remarks>
public sealed class WindowInfo
{
    /// <summary>Opaque handle for use with <see cref="CommandNames.WindowAction"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The window's title bar text.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>The owning process's name without extension, or empty when it cannot be read.</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Whether the window is minimized to the taskbar.</summary>
    [JsonPropertyName("isMinimized")]
    public bool IsMinimized { get; init; }

    /// <summary>Whether the window is maximized.</summary>
    [JsonPropertyName("isMaximized")]
    public bool IsMaximized { get; init; }

    /// <summary>Whether the window currently has the foreground.</summary>
    [JsonPropertyName("isForeground")]
    public bool IsForeground { get; init; }
}

/// <summary>Result of <see cref="CommandNames.WindowList"/>.</summary>
public sealed class WindowListResult
{
    /// <summary>Visible top-level application windows, foreground first, then in Z order.</summary>
    [JsonPropertyName("windows")]
    public WindowInfo[] Windows { get; init; } = [];
}

/// <summary>Arguments for <see cref="CommandNames.WindowAction"/>.</summary>
public sealed class WindowActionArgs
{
    /// <summary>The <see cref="WindowInfo.Id"/> to act on.</summary>
    [JsonPropertyName("windowId")]
    public string? WindowId { get; init; }

    /// <summary>
    /// <c>focus</c>, <c>minimize</c>, <c>maximize</c>, <c>restore</c> or <c>close</c>. Close is a
    /// polite request (<c>WM_CLOSE</c>), never a kill, so the application keeps its chance to
    /// prompt about unsaved work.
    /// </summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }
}

/// <summary>Result of <see cref="CommandNames.WindowAction"/>.</summary>
public sealed class WindowActionResult
{
    /// <summary>Whether Windows accepted the request.</summary>
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    /// <summary>Explanation when the action did not fully succeed.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}
