namespace RemoteAgent.Core.Abstractions;

/// <summary>
/// One directory tree a paired device may manage (§7.3).
/// </summary>
/// <param name="Label">Name shown in the roots view, e.g. <c>Documents</c> or <c>Local Disk (C:)</c>.</param>
/// <param name="Path">The directory, as a canonical absolute path.</param>
/// <param name="Writable">Whether entries beneath it may be created, changed or deleted.</param>
public sealed record FileRoot(string Label, string Path, bool Writable);

/// <summary>
/// The allowed roots currently in force, with configuration already applied.
/// </summary>
public interface IFileRootProvider
{
    /// <summary>
    /// Every allowed root. Re-read on each call: drives come and go, and the owner may narrow the
    /// configuration while the agent runs. An empty list means nothing may be touched.
    /// </summary>
    IReadOnlyList<FileRoot> GetRoots();
}

/// <summary>
/// The roots this PC offers when configuration does not say otherwise, discovered at runtime.
/// </summary>
/// <remarks>
/// Implemented by the platform layer, because "the signed-in user's Downloads folder" and "every
/// fixed drive" are questions for the shell. Nothing here is compiled in: the same binary reports
/// different folders for different users and different drives on different PCs (§0).
/// </remarks>
public interface IDefaultFileRoots
{
    /// <summary>The built-in roots that exist on this PC for this user.</summary>
    IReadOnlyList<FileRoot> GetDefaultRoots();
}

/// <summary>How a request to recycle an item ended.</summary>
public enum RecycleOutcome
{
    /// <summary>The item is in the Recycle Bin.</summary>
    Recycled,

    /// <summary>Windows refused: access denied, the item is in use, or it has no Recycle Bin to go to.</summary>
    Refused,

    /// <summary>
    /// Windows is asking a question on the PC's own screen (typically "this is too big to recycle,
    /// delete it permanently?") and nobody has answered it yet.
    /// </summary>
    AwaitingConfirmation,
}

/// <summary>
/// The two file operations that belong to the Windows shell rather than to the file system.
/// </summary>
/// <remarks>
/// Kept behind an interface so that everything else in file management — path validation, root
/// containment, copy, move, rename — is platform-neutral and runs in tests against a temporary
/// directory, while the test double for this records what would have been recycled or opened
/// without touching the real Recycle Bin or launching anything on a developer's desktop.
/// </remarks>
public interface IFileShell
{
    /// <summary>
    /// Sends a file or folder to the Recycle Bin. Must never delete permanently as a fallback.
    /// </summary>
    /// <param name="path">A canonical path that has already passed every policy check.</param>
    /// <param name="detail">An explanation when the item was not recycled.</param>
    RecycleOutcome MoveToRecycleBin(string path, out string? detail);

    /// <summary>
    /// Opens a file with its default application, or a folder in the file manager.
    /// </summary>
    /// <param name="path">A canonical path that has already passed every policy check.</param>
    /// <param name="detail">An explanation when it could not be opened.</param>
    bool Open(string path, out string? detail);
}
