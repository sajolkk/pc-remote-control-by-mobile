using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Core.Files;

/// <summary>Why a file operation did or did not happen.</summary>
public enum FileStatus
{
    /// <summary>Done.</summary>
    Ok,

    /// <summary>A path or name was malformed, or the request contradicts itself. <c>E_INVALID_ARGUMENTS</c>.</summary>
    InvalidArguments,

    /// <summary>The path does not exist. <c>E_NOT_FOUND</c>.</summary>
    NotFound,

    /// <summary>
    /// Outside the allowed roots, in a read-only root, or refused by Windows (an ACL, a file in
    /// use). <c>E_ACCESS_DENIED</c>.
    /// </summary>
    AccessDenied,

    /// <summary>The caller needs a permission it does not hold for this particular item. <c>E_PERMISSION_DENIED</c>.</summary>
    PermissionRequired,

    /// <summary>The operation was too large to finish inside its budget and was undone. <c>E_TIMEOUT</c>.</summary>
    TooLarge,
}

/// <summary>The outcome of a file operation.</summary>
/// <typeparam name="T">The payload on success.</typeparam>
/// <param name="Status">What happened.</param>
/// <param name="Value">The payload, present when <see cref="Status"/> is <see cref="FileStatus.Ok"/>.</param>
/// <param name="Detail">A human-readable explanation when it is not.</param>
public readonly record struct FileResult<T>(FileStatus Status, T? Value, string? Detail)
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Succeeded => Status == FileStatus.Ok;
}

/// <summary>
/// Lists, opens, creates, renames, copies, moves and deletes files inside the allowed roots (§7.3).
/// </summary>
/// <remarks>
/// <para><b>Every path takes the same route in.</b> <see cref="Resolve"/> canonicalises the text
/// with <see cref="FilePathPolicy"/>, finds the allowed root that governs the result, applies that
/// root's read-only flag to anything that would change the disk, and then walks the real directory
/// tree for junctions and symbolic links that lead out of the allowed roots. No operation below
/// touches the file system with a string that has not been through it, and none of them accepts a
/// second path-like argument that bypasses it: a new name is validated as a single component and
/// joined to an already-resolved directory.</para>
///
/// <para><b>Nothing is overwritten, ever.</b> Create and rename refuse an existing name, move
/// refuses an existing name at the destination, and copy picks a free <c>name (2)</c> style name.
/// The checks are backed by the file system's own create-new semantics, so losing a race with
/// another program produces a refusal rather than a destroyed file.</para>
///
/// <para><b>Deleting defaults to the Recycle Bin</b> through <see cref="IFileShell"/>, whose
/// contract forbids falling back to a permanent delete. A mistaken tap on a phone is then something
/// the owner can undo at the PC.</para>
///
/// <para><b>Roots protect themselves.</b> An allowed root, or any directory that contains one,
/// cannot be renamed, moved or deleted — "delete <c>C:\Users\me</c>" is refused because Documents
/// lives inside it, without needing a list of important folders.</para>
///
/// <para><b>Bounded work.</b> Commands reach the session agent one at a time, so an operation that
/// runs for minutes would freeze input and every other command behind it. Copies, and the moves
/// across drives that are really copies, carry a time and size budget; exceeding it removes the
/// partial copy and reports the item as too large to handle remotely.</para>
///
/// <para>Platform-neutral apart from the two shell operations behind <see cref="IFileShell"/>, so
/// the whole of this class runs in tests against a temporary directory.</para>
/// </remarks>
public sealed class FileManager
{
    /// <summary>Most entries one listing returns.</summary>
    public const int MaxListEntries = 2000;

    /// <summary>Most entries one listing examines before giving up on sorting all of them.</summary>
    public const int MaxScannedEntries = 50_000;

    private const int MaxCopyEntries = 20_000;
    private const int MaxNameAttempts = 1000;
    private const int CopyBufferBytes = 1024 * 1024;

    /// <summary>
    /// File types Windows runs rather than displays. Opening one is launching a program, which is
    /// what the <c>LaunchApps</c> permission is for.
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".mst", ".msix", ".appx",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".wsc", ".sct", ".hta",
        ".reg", ".inf", ".lnk", ".url", ".scf", ".cpl", ".msc", ".jar", ".application", ".appref-ms",
        ".gadget", ".shb", ".shs", ".library-ms", ".settingcontent-ms", ".diagcab", ".py", ".pyw",
    };

    private readonly IFileRootProvider _roots;
    private readonly IFileShell _shell;
    private readonly ILogger<FileManager> _logger;
    private readonly Func<long> _maxCopyBytes;
    private readonly TimeSpan _copyBudget;

    /// <summary>Creates the manager.</summary>
    /// <param name="roots">The allowed roots in force.</param>
    /// <param name="shell">Recycle Bin and default-application access.</param>
    /// <param name="logger">Audit and diagnostics.</param>
    /// <param name="maxCopyBytes">The most data one copy may write. Defaults to 2 GiB.</param>
    /// <param name="copyBudget">The longest one copy may run. Defaults to 20 seconds.</param>
    public FileManager(
        IFileRootProvider roots,
        IFileShell shell,
        ILogger<FileManager> logger,
        Func<long>? maxCopyBytes = null,
        TimeSpan? copyBudget = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxCopyBytes = maxCopyBytes ?? (static () => 2048L * 1024 * 1024);
        _copyBudget = copyBudget ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Whether at least one allowed root currently exists, i.e. whether there is anything to manage.</summary>
    public bool HasAnyRoot()
    {
        try
        {
            return _roots.GetRoots().Any(static root => Directory.Exists(root.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // file.list
    // ---------------------------------------------------------------------

    /// <summary>Lists the allowed roots (no path) or one directory.</summary>
    public FileResult<FileListResult> List(string? path)
    {
        IReadOnlyList<FileRoot> roots = _roots.GetRoots();

        if (string.IsNullOrEmpty(path))
        {
            return Ok(new FileListResult
            {
                Path = string.Empty,
                Entries =
                [
                    .. roots
                        .Where(static root => DirectoryExistsSafe(root.Path))
                        .Select(static root => new FileEntry
                        {
                            Name = root.Label,
                            Path = root.Path,
                            IsDirectory = true,
                            IsRoot = true,
                            ReadOnly = root.Writable ? null : true,
                        }),
                ],
            });
        }

        if (!Resolve(path, roots, forWrite: false, out Resolved target, out FileResult<FileListResult> failure))
        {
            return failure;
        }

        return Guarded(() =>
        {
            var directory = new DirectoryInfo(target.Path);
            if (!directory.Exists)
            {
                return File.Exists(target.Path)
                    ? Fail<FileListResult>(FileStatus.InvalidArguments, "That path is a file, not a folder.")
                    : Fail<FileListResult>(FileStatus.NotFound, "That folder does not exist.");
            }

            // The defaults skip hidden and system entries, which is what Explorer shows out of
            // the box, and IgnoreInaccessible keeps one unreadable child from failing the listing.
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };

            var entries = new List<FileEntry>();
            bool truncated = false;
            int scanned = 0;

            foreach (FileSystemInfo info in directory.EnumerateFileSystemInfos("*", options))
            {
                if (++scanned > MaxScannedEntries)
                {
                    truncated = true;
                    break;
                }

                // A name this PC would refuse to accept back (a trailing dot made by another
                // tool, say) is not offered, since nothing could then be done with it.
                if (!FilePathPolicy.IsValidName(info.Name))
                {
                    continue;
                }

                entries.Add(Describe(info, FilePathPolicy.Combine(target.Path, info.Name), roots));
            }

            entries.Sort(static (a, b) =>
            {
                int kind = b.IsDirectory.CompareTo(a.IsDirectory);
                return kind != 0 ? kind : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (entries.Count > MaxListEntries)
            {
                entries.RemoveRange(MaxListEntries, entries.Count - MaxListEntries);
                truncated = true;
            }

            return Ok(new FileListResult
            {
                Path = target.Path,
                Parent = IsRoot(target.Path, roots) ? null : FilePathPolicy.GetParent(target.Path),
                Entries = [.. entries],
                Truncated = truncated,
            });
        });
    }

    // ---------------------------------------------------------------------
    // file.open
    // ---------------------------------------------------------------------

    /// <summary>
    /// Opens a file with its default application, or a folder in Explorer, on the PC's screen.
    /// </summary>
    /// <param name="path">The file or folder.</param>
    /// <param name="mayLaunchPrograms">
    /// Whether the caller also holds <c>LaunchApps</c>. Opening a program, script, installer or
    /// shortcut runs it, and a device trusted only to manage files has not been trusted to run
    /// things; such a file is refused unless this is set.
    /// </param>
    public FileResult<bool> Open(string? path, bool mayLaunchPrograms)
    {
        IReadOnlyList<FileRoot> roots = _roots.GetRoots();
        if (!Resolve(path, roots, forWrite: false, out Resolved target, out FileResult<bool> failure))
        {
            return failure;
        }

        return Guarded(() =>
        {
            bool isDirectory = Directory.Exists(target.Path);
            if (!isDirectory && !File.Exists(target.Path))
            {
                return Fail<bool>(FileStatus.NotFound, "That file or folder does not exist.");
            }

            if (!isDirectory && !mayLaunchPrograms && IsExecutableType(target.Path))
            {
                return Fail<bool>(
                    FileStatus.PermissionRequired,
                    "Opening this file would run it. Enable \"Launch apps\" for this device on the PC to allow that.");
            }

            if (!_shell.Open(target.Path, out string? detail))
            {
                return Fail<bool>(FileStatus.AccessDenied, detail ?? "Windows could not open that item.");
            }

            Audit("open", target);
            return Ok(true);
        });
    }

    /// <summary>Whether Windows would run a file of this type rather than display it.</summary>
    public static bool IsExecutableType(string path) =>
        ExecutableExtensions.Contains(Path.GetExtension(path ?? string.Empty));

    // ---------------------------------------------------------------------
    // file.create
    // ---------------------------------------------------------------------

    /// <summary>Creates an empty file or a folder. Refuses an existing name.</summary>
    public FileResult<FileEntry> Create(string? parent, string? name, bool isDirectory)
    {
        if (!FilePathPolicy.IsValidName(name))
        {
            return InvalidName();
        }

        IReadOnlyList<FileRoot> roots = _roots.GetRoots();
        if (!Resolve(parent, roots, forWrite: true, out Resolved directory, out FileResult<FileEntry> failure))
        {
            return failure;
        }

        return Guarded(() =>
        {
            if (!Directory.Exists(directory.Path))
            {
                return Fail<FileEntry>(FileStatus.NotFound, "The parent folder does not exist.");
            }

            string path = FilePathPolicy.Combine(directory.Path, name!);
            if (Exists(path))
            {
                return AlreadyExists();
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                // CreateNew fails if the name appeared since the check above, so a race cannot
                // truncate somebody else's file.
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
            }

            Audit(isDirectory ? "create folder" : "create file", directory, name);
            return Ok(Describe(path, roots));
        });
    }

    // ---------------------------------------------------------------------
    // file.rename
    // ---------------------------------------------------------------------

    /// <summary>Renames a file or folder within its directory. Refuses an existing name.</summary>
    public FileResult<FileEntry> Rename(string? path, string? newName)
    {
        if (!FilePathPolicy.IsValidName(newName))
        {
            return InvalidName();
        }

        IReadOnlyList<FileRoot> roots = _roots.GetRoots();
        if (!Resolve(path, roots, forWrite: true, out Resolved source, out FileResult<FileEntry> failure))
        {
            return failure;
        }

        if (ProtectsARoot(source.Path, roots, out FileResult<FileEntry> rootFailure))
        {
            return rootFailure;
        }

        return Guarded(() =>
        {
            bool isDirectory = Directory.Exists(source.Path);
            if (!isDirectory && !File.Exists(source.Path))
            {
                return Fail<FileEntry>(FileStatus.NotFound, "That file or folder does not exist.");
            }

            string directory = FilePathPolicy.GetParent(source.Path)!;
            string target = FilePathPolicy.Combine(directory, newName!);

            if (string.Equals(source.Path, target, StringComparison.Ordinal))
            {
                return Ok(Describe(target, roots));
            }

            // A change of letter case only is the same entry under a new spelling, not a collision.
            bool caseOnly = string.Equals(source.Path, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(target))
            {
                return AlreadyExists();
            }

            if (isDirectory)
            {
                Directory.Move(source.Path, target);
            }
            else
            {
                File.Move(source.Path, target, overwrite: false);
            }

            Audit("rename", source);
            return Ok(Describe(target, roots));
        });
    }

    // ---------------------------------------------------------------------
    // file.copy
    // ---------------------------------------------------------------------

    /// <summary>
    /// Copies a file or folder into a directory, under a free name if its own is taken there.
    /// </summary>
    public FileResult<FileEntry> Copy(string? path, string? destination, CancellationToken cancellationToken)
    {
        IReadOnlyList<FileRoot> roots = _roots.GetRoots();

        if (!Resolve(path, roots, forWrite: false, out Resolved source, out FileResult<FileEntry> failure) ||
            !Resolve(destination, roots, forWrite: true, out Resolved target, out failure))
        {
            return failure;
        }

        return Guarded(() =>
        {
            bool isDirectory = Directory.Exists(source.Path);
            if (!isDirectory && !File.Exists(source.Path))
            {
                return Fail<FileEntry>(FileStatus.NotFound, "That file or folder does not exist.");
            }

            if (!Directory.Exists(target.Path))
            {
                return Fail<FileEntry>(FileStatus.NotFound, "The destination folder does not exist.");
            }

            if (isDirectory && FilePathPolicy.IsWithin(target.Path, source.Path))
            {
                return Fail<FileEntry>(FileStatus.InvalidArguments, "A folder cannot be copied into itself.");
            }

            var budget = new CopyBudget(_maxCopyBytes(), _copyBudget, cancellationToken);
            string name = FilePathPolicy.GetName(source.Path);

            for (int attempt = 1; attempt <= MaxNameAttempts; attempt++)
            {
                string candidate = FilePathPolicy.Combine(target.Path, NumberedName(name, attempt, isDirectory));
                if (Exists(candidate))
                {
                    continue;
                }

                CopyOutcome outcome = isDirectory
                    ? CopyDirectory(source.Path, candidate, budget)
                    : CopyFile(source.Path, candidate, budget);

                switch (outcome)
                {
                    case CopyOutcome.Done:
                        Audit("copy", source);
                        return Ok(Describe(candidate, roots));

                    case CopyOutcome.NameTaken:
                        // Something claimed the name between the check and the create. Try the next.
                        continue;

                    default:
                        return TooLarge();
                }
            }

            return Fail<FileEntry>(FileStatus.InvalidArguments, "Too many copies of that item already exist there.");
        });
    }

    // ---------------------------------------------------------------------
    // file.move
    // ---------------------------------------------------------------------

    /// <summary>Moves a file or folder into a directory. Refuses if its name is taken there.</summary>
    public FileResult<FileEntry> Move(string? path, string? destination, CancellationToken cancellationToken)
    {
        IReadOnlyList<FileRoot> roots = _roots.GetRoots();

        // Moving removes the item from where it was, so the source needs a writable root too.
        if (!Resolve(path, roots, forWrite: true, out Resolved source, out FileResult<FileEntry> failure) ||
            !Resolve(destination, roots, forWrite: true, out Resolved target, out failure))
        {
            return failure;
        }

        if (ProtectsARoot(source.Path, roots, out FileResult<FileEntry> rootFailure))
        {
            return rootFailure;
        }

        return Guarded(() =>
        {
            bool isDirectory = Directory.Exists(source.Path);
            if (!isDirectory && !File.Exists(source.Path))
            {
                return Fail<FileEntry>(FileStatus.NotFound, "That file or folder does not exist.");
            }

            if (!Directory.Exists(target.Path))
            {
                return Fail<FileEntry>(FileStatus.NotFound, "The destination folder does not exist.");
            }

            if (isDirectory && FilePathPolicy.IsWithin(target.Path, source.Path))
            {
                return Fail<FileEntry>(FileStatus.InvalidArguments, "A folder cannot be moved into itself.");
            }

            string moved = FilePathPolicy.Combine(target.Path, FilePathPolicy.GetName(source.Path));
            if (Exists(moved))
            {
                return AlreadyExists();
            }

            bool sameVolume = char.ToUpperInvariant(source.Path[0]) == char.ToUpperInvariant(moved[0]);
            if (sameVolume)
            {
                // A rename inside one volume: instant, atomic, and it fails rather than overwrites.
                if (isDirectory)
                {
                    Directory.Move(source.Path, moved);
                }
                else
                {
                    File.Move(source.Path, moved, overwrite: false);
                }
            }
            else
            {
                // Across volumes a move is a copy followed by deleting the original, and the
                // original is only deleted once the copy is complete.
                var budget = new CopyBudget(_maxCopyBytes(), _copyBudget, cancellationToken);

                CopyOutcome outcome = isDirectory
                    ? CopyDirectory(source.Path, moved, budget)
                    : CopyFile(source.Path, moved, budget);

                if (outcome == CopyOutcome.NameTaken)
                {
                    return AlreadyExists();
                }

                if (outcome != CopyOutcome.Done)
                {
                    return TooLarge();
                }

                try
                {
                    if (isDirectory)
                    {
                        DeleteDirectoryTree(source.Path);
                    }
                    else
                    {
                        File.Delete(source.Path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Fail<FileEntry>(
                        FileStatus.AccessDenied,
                        "The item was copied to the destination, but Windows would not remove the original: " +
                        Explain(ex));
                }
            }

            Audit("move", source);
            return Ok(Describe(moved, roots));
        });
    }

    // ---------------------------------------------------------------------
    // file.delete
    // ---------------------------------------------------------------------

    /// <summary>Deletes a file or folder, to the Recycle Bin unless <paramref name="permanent"/>.</summary>
    public FileResult<bool> Delete(string? path, bool permanent)
    {
        IReadOnlyList<FileRoot> roots = _roots.GetRoots();
        if (!Resolve(path, roots, forWrite: true, out Resolved target, out FileResult<bool> failure))
        {
            return failure;
        }

        if (ProtectsARoot(target.Path, roots, out FileResult<bool> rootFailure))
        {
            return rootFailure;
        }

        return Guarded(() =>
        {
            bool isDirectory = Directory.Exists(target.Path);
            if (!isDirectory && !File.Exists(target.Path))
            {
                return Fail<bool>(FileStatus.NotFound, "That file or folder does not exist.");
            }

            if (permanent)
            {
                if (isDirectory)
                {
                    DeleteDirectoryTree(target.Path);
                }
                else
                {
                    File.Delete(target.Path);
                }

                Audit("delete permanently", target);
                return Ok(true);
            }

            switch (_shell.MoveToRecycleBin(target.Path, out string? detail))
            {
                case RecycleOutcome.Recycled:
                    Audit("recycle", target);
                    return Ok(true);

                case RecycleOutcome.AwaitingConfirmation:
                    return Fail<bool>(
                        FileStatus.TooLarge,
                        detail ?? "Windows is asking for confirmation on the PC's screen before deleting this item.");

                default:
                    return Fail<bool>(
                        FileStatus.AccessDenied,
                        detail ?? "Windows could not move that item to the Recycle Bin.");
            }
        });
    }

    // ---------------------------------------------------------------------
    // Path resolution
    // ---------------------------------------------------------------------

    /// <summary>A path that passed every check, with the root that governs it.</summary>
    private readonly record struct Resolved(string Path, FileRoot Root);

    /// <summary>
    /// The single gate every caller-supplied path passes through.
    /// </summary>
    private static bool Resolve<T>(
        string? input,
        IReadOnlyList<FileRoot> roots,
        bool forWrite,
        out Resolved resolved,
        out FileResult<T> failure)
    {
        resolved = default;
        failure = default;

        if (!FilePathPolicy.TryCanonicalize(input, out string canonical, out PathRejection rejection))
        {
            failure = rejection == PathRejection.Denied
                ? Fail<T>(FileStatus.AccessDenied, "That path is not one this PC shares.")
                : Fail<T>(FileStatus.InvalidArguments, "The path is not a valid absolute Windows path.");
            return false;
        }

        FileRoot? root = ConfiguredFileRoots.FindGoverningRoot(canonical, roots);
        if (root is null)
        {
            failure = Fail<T>(FileStatus.AccessDenied, "That path is outside the folders this PC shares.");
            return false;
        }

        if (forWrite && !root.Writable)
        {
            failure = Fail<T>(FileStatus.AccessDenied, $"\"{root.Label}\" is shared read-only.");
            return false;
        }

        if (LeavesAllowedRoots(canonical, root, roots, forWrite))
        {
            failure = Fail<T>(
                FileStatus.AccessDenied,
                "That path goes through a link to a location this PC does not share.");
            return false;
        }

        resolved = new Resolved(canonical, root);
        return true;
    }

    /// <summary>
    /// Whether a junction or symbolic link below the governing root leads somewhere not allowed.
    /// </summary>
    /// <remarks>
    /// The lexical check proves the <em>text</em> of the path stays inside a root; a reparse point
    /// can make the text lie. Each component beneath the root is examined on disk, and where one is
    /// a link its final target has to fall inside an allowed root as well (and a writable one, for
    /// an operation that writes). Reparse points that are not links — cloud placeholders,
    /// deduplicated files — resolve to nothing and are passed over. A target that cannot be
    /// resolved, or that resolves to something other than a plain drive path, counts as leaving.
    /// </remarks>
    private static bool LeavesAllowedRoots(string canonical, FileRoot root, IReadOnlyList<FileRoot> roots, bool forWrite)
    {
        if (canonical.Length <= root.Path.Length)
        {
            return false;
        }

        string current = root.Path;
        string remainder = canonical[root.Path.Length..].TrimStart('\\');

        foreach (string segment in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = FilePathPolicy.Combine(current, segment);

            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    continue;
                }

                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);

                FileSystemInfo? final = info.ResolveLinkTarget(returnFinalTarget: true);
                if (final is null)
                {
                    continue;
                }

                if (!FilePathPolicy.TryCanonicalize(final.FullName, out string destination, out _))
                {
                    return true;
                }

                FileRoot? governing = ConfiguredFileRoots.FindGoverningRoot(destination, roots);
                if (governing is null || (forWrite && !governing.Writable))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Nothing exists from here down, so there is nothing left that could be a link.
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Could not be examined. Unknown is not the same as safe.
                return true;
            }
        }

        return false;
    }

    private static bool IsRoot(string canonical, IReadOnlyList<FileRoot> roots) =>
        roots.Any(root => string.Equals(root.Path, canonical, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Refuses to rename, move or delete an allowed root or a directory that contains one.
    /// </summary>
    private static bool ProtectsARoot<T>(string canonical, IReadOnlyList<FileRoot> roots, out FileResult<T> failure)
    {
        foreach (FileRoot root in roots)
        {
            if (FilePathPolicy.IsWithin(root.Path, canonical))
            {
                failure = Fail<T>(
                    FileStatus.AccessDenied,
                    string.Equals(root.Path, canonical, StringComparison.OrdinalIgnoreCase)
                        ? $"\"{root.Label}\" is a shared root folder and cannot be renamed, moved or deleted."
                        : $"That folder contains \"{root.Label}\" and cannot be renamed, moved or deleted.");
                return true;
            }
        }

        failure = default;
        return false;
    }

    // ---------------------------------------------------------------------
    // Copying
    // ---------------------------------------------------------------------

    private enum CopyOutcome
    {
        Done,
        NameTaken,
        OverBudget,
    }

    /// <summary>The limits one copy runs under.</summary>
    private sealed class CopyBudget(long maxBytes, TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _bytes;
        private int _entries;

        public bool Exhausted =>
            cancellationToken.IsCancellationRequested ||
            _bytes > maxBytes ||
            _entries > MaxCopyEntries ||
            Stopwatch.GetElapsedTime(_started) > maxDuration;

        public void AddBytes(long count) => _bytes += count;

        public void AddEntry() => _entries++;
    }

    /// <summary>
    /// Copies one file to a name that must not exist, removing the partial copy if it cannot finish.
    /// </summary>
    private static CopyOutcome CopyFile(string source, string target, CopyBudget budget)
    {
        budget.AddEntry();

        FileStream output;
        try
        {
            output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes);
        }
        catch (IOException) when (Exists(target))
        {
            return CopyOutcome.NameTaken;
        }

        bool complete = false;
        try
        {
            using (output)
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes))
            {
                // Streamed rather than File.Copy so the budget is checked between blocks: one very
                // large file must not hold up every other command for however long the disk takes.
                byte[] buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    budget.AddBytes(read);

                    if (budget.Exhausted)
                    {
                        return CopyOutcome.OverBudget;
                    }
                }
            }

            complete = true;

            try
            {
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy is complete; a timestamp that could not be carried over is cosmetic.
            }

            return CopyOutcome.Done;
        }
        finally
        {
            if (!complete)
            {
                TryDeleteFile(target);
            }
        }
    }

    /// <summary>
    /// Copies a directory tree to a name that must not exist, removing the partial copy if it cannot
    /// finish.
    /// </summary>
    private static CopyOutcome CopyDirectory(string source, string target, CopyBudget budget)
    {
        if (Exists(target))
        {
            return CopyOutcome.NameTaken;
        }

        Directory.CreateDirectory(target);

        bool complete = false;
        try
        {
            complete = CopyDirectoryContents(source, target, budget);
            return complete ? CopyOutcome.Done : CopyOutcome.OverBudget;
        }
        finally
        {
            if (!complete)
            {
                // Safe to remove wholesale: the directory did not exist before this call created
                // it, so everything inside it is this copy's own partial output.
                TryDeleteDirectory(target);
            }
        }
    }

    private static bool CopyDirectoryContents(string source, string target, CopyBudget budget)
    {
        budget.AddEntry();

        // Hidden and system entries are part of a folder's contents and come along; unreadable
        // ones fail the copy rather than being silently left out of it.
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.None,
        };

        foreach (FileSystemInfo entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", options))
        {
            if (budget.Exhausted)
            {
                return false;
            }

            // Links are not followed: a junction inside the tree could point anywhere, including
            // back at an ancestor, which would never end.
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null)
            {
                continue;
            }

            string destination = Path.Combine(target, entry.Name);

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destination);
                if (!CopyDirectoryContents(entry.FullName, destination, budget))
                {
                    return false;
                }
            }
            else if (CopyFile(entry.FullName, destination, budget) != CopyOutcome.Done)
            {
                return false;
            }
        }

        return !budget.Exhausted;
    }

    /// <summary><c>name</c>, then <c>name (2)</c>, <c>name (3)</c>… with a file's extension kept last.</summary>
    internal static string NumberedName(string name, int attempt, bool isDirectory)
    {
        if (attempt <= 1)
        {
            return name;
        }

        string suffix = string.Create(CultureInfo.InvariantCulture, $" ({attempt})");

        // A leading dot is part of the name (".gitignore"), not the start of an extension.
        int dot = isDirectory ? -1 : name.LastIndexOf('.');
        return dot <= 0 ? name + suffix : name[..dot] + suffix + name[dot..];
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool DirectoryExistsSafe(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the partial file stays, under the fresh name it was given.
        }
    }

    /// <summary>
    /// Deletes a directory and everything in it, without ever following a link out of it.
    /// </summary>
    /// <remarks>
    /// The runtime's recursive delete already removes a junction or symbolic link as a link — the
    /// directory it points at is never entered and never touched, which is the property that
    /// matters here. But for a junction it also tries to dismount it as if it were a volume mount
    /// point, which fails for an ordinary user, and it then reports that failure after having
    /// deleted everything else, leaving only the junction's parent directories behind. The link
    /// itself is gone by then, so one more pass finishes the job. A genuine refusal (an ACL, a file
    /// in use) fails the second pass exactly as it failed the first and is reported as usual.
    /// </remarks>
    private static void DeleteDirectoryTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectoryTree(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }

    private static FileEntry Describe(string canonicalPath, IReadOnlyList<FileRoot> roots)
    {
        FileSystemInfo info = Directory.Exists(canonicalPath)
            ? new DirectoryInfo(canonicalPath)
            : new FileInfo(canonicalPath);

        return Describe(info, canonicalPath, roots);
    }

    private static FileEntry Describe(FileSystemInfo info, string canonicalPath, IReadOnlyList<FileRoot> roots)
    {
        bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        FileRoot? root = ConfiguredFileRoots.FindGoverningRoot(canonicalPath, roots);

        bool readOnly = root is not { Writable: true } ||
                        (!isDirectory && (info.Attributes & FileAttributes.ReadOnly) != 0);

        DateTime modified = info.LastWriteTimeUtc;

        return new FileEntry
        {
            Name = FilePathPolicy.GetName(canonicalPath),
            Path = canonicalPath,
            IsDirectory = isDirectory,
            Size = isDirectory || info is not FileInfo file ? null : file.Length,

            // Windows reports 1601-01-01 when it has no timestamp; that is "unknown", not a date.
            Modified = modified.Year < 1980
                ? null
                : modified.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),

            ReadOnly = readOnly ? true : null,
        };
    }

    /// <summary>
    /// Records a change for the owner's audit trail: the root's label and the path beneath it, per
    /// the logging rules (§7.5), never file contents.
    /// </summary>
    private void Audit(string operation, Resolved target, string? childName = null)
    {
        string relative = target.Path.Length > target.Root.Path.Length
            ? target.Path[target.Root.Path.Length..].TrimStart('\\')
            : string.Empty;

        if (childName is not null)
        {
            relative = relative.Length == 0 ? childName : relative + '\\' + childName;
        }

        _logger.LogInformation(
            "File {Operation}: [{Root}] {RelativePath}",
            operation,
            target.Root.Label,
            relative.Length == 0 ? "(the root itself)" : relative);
    }

    /// <summary>
    /// Runs an operation and turns the file system's refusals into results.
    /// </summary>
    /// <remarks>
    /// These exceptions are the ordinary outcomes of asking Windows to do something to a file —
    /// an ACL says no, another program has it open, it vanished a moment ago — so they are
    /// answers for the client rather than defects for the log.
    /// </remarks>
    private static FileResult<T> Guarded<T>(Func<FileResult<T>> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail<T>(FileStatus.NotFound, "That file or folder does not exist.");
        }
        catch (PathTooLongException)
        {
            return Fail<T>(FileStatus.InvalidArguments, "The resulting path would be too long for Windows.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Fail<T>(FileStatus.AccessDenied, Explain(ex));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Fail<T>(FileStatus.InvalidArguments, "Windows does not accept that path.");
        }
    }

    /// <summary>
    /// A message for the client. Written here rather than taken from the exception, whose text
    /// would repeat full paths and is in whatever language Windows is set to.
    /// </summary>
    private static string Explain(Exception ex)
    {
        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);
        const int diskFull = unchecked((int)0x80070070);
        const int directoryNotEmpty = unchecked((int)0x80070091);
        const int alreadyExists = unchecked((int)0x800700B7);
        const int fileExists = unchecked((int)0x80070050);

        return ex.HResult switch
        {
            sharingViolation or lockViolation => "The item is in use by another program on the PC.",
            diskFull => "There is not enough free space on that drive.",
            directoryNotEmpty => "The folder could not be removed because something inside it is in use.",
            alreadyExists or fileExists => "Something with that name already exists there.",
            _ when ex is UnauthorizedAccessException =>
                "Windows denied access. The item may be read-only, protected, or need administrator rights.",
            _ => "Windows could not complete the operation on that item.",
        };
    }

    private static FileResult<T> Ok<T>(T value) => new(FileStatus.Ok, value, null);

    private static FileResult<T> Fail<T>(FileStatus status, string detail) => new(status, default, detail);

    private static FileResult<FileEntry> InvalidName() => Fail<FileEntry>(
        FileStatus.InvalidArguments,
        "That name is not allowed. A name cannot contain \\ / : * ? \" < > |, end with a dot or a space, " +
        "or be one of the names Windows reserves.");

    private static FileResult<FileEntry> AlreadyExists() =>
        Fail<FileEntry>(FileStatus.InvalidArguments, "Something with that name already exists there.");

    private static FileResult<FileEntry> TooLarge() => Fail<FileEntry>(
        FileStatus.TooLarge,
        "That item is too large to copy from the phone. Nothing was left behind at the destination.");
}
