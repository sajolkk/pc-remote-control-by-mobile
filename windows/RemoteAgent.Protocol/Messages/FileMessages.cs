using System.Text.Json.Serialization;

namespace RemoteAgent.Protocol.Messages;

// File management (Permission.ManageFiles).
//
// Paths are absolute Windows paths as strings. The PC canonicalises every one and refuses anything
// outside its configured allowed roots with E_ACCESS_DENIED, so a path on the wire is a request to
// be validated, never something the PC trusts.

/// <summary>
/// One file, folder or allowed root.
/// </summary>
public sealed class FileEntry
{
    /// <summary>
    /// The entry's name. In the roots view this is a friendly label such as <c>Desktop</c> or
    /// <c>Local Disk (C:)</c> rather than the last path component.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Absolute path, in the canonical form the PC accepts back.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>Whether this is a folder.</summary>
    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; init; }

    /// <summary>Size in bytes. Present for files only.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; init; }

    /// <summary>Last write time, ISO-8601 UTC. Absent when Windows does not report one.</summary>
    [JsonPropertyName("modified")]
    public string? Modified { get; init; }

    /// <summary>True for an allowed root. Absent otherwise.</summary>
    [JsonPropertyName("isRoot")]
    public bool? IsRoot { get; init; }

    /// <summary>
    /// True when the entry cannot be changed: its root is configured read-only, or the file itself
    /// carries the read-only attribute. Absent otherwise.
    /// </summary>
    [JsonPropertyName("readOnly")]
    public bool? ReadOnly { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.FileList"/>.</summary>
public sealed class FileListArgs
{
    /// <summary>The directory to list. Omitted or empty asks for the list of allowed roots.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>Result of <see cref="CommandNames.FileList"/>.</summary>
public sealed class FileListResult
{
    /// <summary>The directory listed, canonicalised. Empty for the roots view.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>The containing directory. Absent at a root and in the roots view.</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; init; }

    /// <summary>Directories first, then files, each alphabetical. At most 2000 entries.</summary>
    [JsonPropertyName("entries")]
    public FileEntry[] Entries { get; init; } = [];

    /// <summary>Whether the directory holds more entries than were returned.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.FileOpen"/>.</summary>
public sealed class FileOpenArgs
{
    /// <summary>The file to open with its default application, or the folder to show in Explorer.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.FileCreate"/>.</summary>
public sealed class FileCreateArgs
{
    /// <summary>The existing directory to create inside.</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; init; }

    /// <summary>The new entry's name: a single path component.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>True for a folder, false for an empty file.</summary>
    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.FileRename"/>.</summary>
public sealed class FileRenameArgs
{
    /// <summary>The file or folder to rename.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Its new name: a single path component. The entry stays in the same directory.</summary>
    [JsonPropertyName("newName")]
    public string? NewName { get; init; }
}

/// <summary>
/// Arguments for <see cref="CommandNames.FileCopy"/> and <see cref="CommandNames.FileMove"/>.
/// </summary>
public sealed class FileTransferArgs
{
    /// <summary>The file or folder to copy or move.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// The DIRECTORY to copy or move into. The entry keeps its name; a copy that collides is given
    /// a <c>name (2)</c> style name, and a move that collides is refused.
    /// </summary>
    [JsonPropertyName("destination")]
    public string? Destination { get; init; }
}

/// <summary>Arguments for <see cref="CommandNames.FileDelete"/>.</summary>
public sealed class FileDeleteArgs
{
    /// <summary>The file or folder to delete.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Delete permanently instead of sending to the Recycle Bin. Defaults to false, so a mistaken
    /// tap on a phone is something the owner can undo at the PC.
    /// </summary>
    [JsonPropertyName("permanent")]
    public bool? Permanent { get; init; }
}

/// <summary>
/// Result of the commands that create or change one entry: create, rename, copy and move.
/// </summary>
public sealed class FileEntryResult
{
    /// <summary>The entry as it exists after the operation.</summary>
    [JsonPropertyName("entry")]
    public FileEntry Entry { get; init; } = new();
}
