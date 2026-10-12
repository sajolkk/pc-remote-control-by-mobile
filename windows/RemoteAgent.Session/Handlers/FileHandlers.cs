using System.Text.Json.Nodes;
using RemoteAgent.Core.Commands;
using RemoteAgent.Core.Files;
using RemoteAgent.Protocol;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Session.Handlers;

// File management (Permission.ManageFiles).
//
// Session commands, because they must run as the signed-in user: with that user's access rights,
// that user's known folders, and that user's Recycle Bin. Run by the service they would act as
// SYSTEM, which may read and delete nearly everything on the machine — the opposite of what a
// permission model is for (§7.3).
//
// The handlers hold no path logic at all. Every path goes to FileManager, whose first act is to
// canonicalise it and place it inside an allowed root; nothing here touches the file system.
//
// File operations block on the disk, so each handler runs its work on the thread pool rather than
// on the IPC read loop's own continuation, as the screenshot handler does. The handler still awaits
// it, and commands reach this process one at a time, so ordering is unchanged — which also means a
// slow file operation holds up the commands behind it. That is why FileManager gives copies and
// cross-drive moves a time and size budget rather than letting them run for minutes.

/// <summary>Shared translation from a <see cref="FileResult{T}"/> to a wire result.</summary>
internal static class FileResults
{
    /// <summary>Maps a failed outcome onto the contract's error codes.</summary>
    internal static CommandResult ToFailure<T>(FileResult<T> result)
    {
        string detail = result.Detail ?? "The file operation failed.";

        return result.Status switch
        {
            FileStatus.InvalidArguments => CommandResult.InvalidArguments(detail),
            FileStatus.NotFound => CommandResult.Fail(ErrorCodes.NotFound, detail),
            FileStatus.PermissionRequired => CommandResult.Fail(ErrorCodes.PermissionDenied, detail),
            FileStatus.TooLarge => CommandResult.Fail(ErrorCodes.Timeout, detail),
            _ => CommandResult.Fail(ErrorCodes.AccessDenied, detail),
        };
    }

    /// <summary>The result of a command that changes one entry.</summary>
    internal static CommandResult ToEntryResult(FileResult<FileEntry> result) => result.Succeeded
        ? CommandResult.Success(new FileEntryResult { Entry = result.Value! })
        : ToFailure(result);

    /// <summary>The result of a command the contract answers with an empty object.</summary>
    internal static CommandResult ToEmptyResult(FileResult<bool> result) => result.Succeeded
        ? CommandResult.SuccessRaw(new JsonObject())
        : ToFailure(result);
}

/// <summary>Lists the allowed roots, or the contents of one directory inside them.</summary>
public sealed class FileListHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileListHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileList;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileListArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        FileResult<FileListResult> result = await Task
            .Run(() => _files.List(args.Path), cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded ? CommandResult.Success(result.Value!) : FileResults.ToFailure(result);
    }
}

/// <summary>
/// Opens a file with its default application, or a folder in Explorer, on the PC's screen.
/// </summary>
/// <remarks>
/// Opening a document shows it; opening a program, script, installer or shortcut <em>runs</em> it.
/// A device trusted to manage files has not thereby been trusted to run things, so a file of an
/// executable type is opened only for a caller that also holds <c>LaunchApps</c>, and is otherwise
/// refused with <c>E_PERMISSION_DENIED</c> and an explanation. The second check is made here
/// because the catalog can attach only one permission to a command.
/// </remarks>
public sealed class FileOpenHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileOpenHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileOpen;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileOpenArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        bool mayLaunch = PermissionSet.Allows(context.Caller.Permissions, Permission.LaunchApps);

        FileResult<bool> result = await Task
            .Run(() => _files.Open(args.Path, mayLaunch), cancellationToken)
            .ConfigureAwait(false);

        return FileResults.ToEmptyResult(result);
    }
}

/// <summary>Creates an empty file or a folder. Never overwrites.</summary>
public sealed class FileCreateHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileCreateHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileCreate;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileCreateArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        FileResult<FileEntry> result = await Task
            .Run(() => _files.Create(args.Parent, args.Name, args.IsDirectory), cancellationToken)
            .ConfigureAwait(false);

        return FileResults.ToEntryResult(result);
    }
}

/// <summary>Renames a file or folder within its directory. Never overwrites.</summary>
public sealed class FileRenameHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileRenameHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileRename;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileRenameArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        FileResult<FileEntry> result = await Task
            .Run(() => _files.Rename(args.Path, args.NewName), cancellationToken)
            .ConfigureAwait(false);

        return FileResults.ToEntryResult(result);
    }
}

/// <summary>Copies a file or folder into a directory, under a free name if its own is taken.</summary>
public sealed class FileCopyHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileCopyHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileCopy;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileTransferArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        FileResult<FileEntry> result = await Task
            .Run(() => _files.Copy(args.Path, args.Destination, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return FileResults.ToEntryResult(result);
    }
}

/// <summary>Moves a file or folder into a directory. Refuses if its name is taken there.</summary>
public sealed class FileMoveHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileMoveHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileMove;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileTransferArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        FileResult<FileEntry> result = await Task
            .Run(() => _files.Move(args.Path, args.Destination, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return FileResults.ToEntryResult(result);
    }
}

/// <summary>
/// Deletes a file or folder — to the Recycle Bin unless the caller explicitly asks otherwise.
/// </summary>
public sealed class FileDeleteHandler : ICommandHandler
{
    private readonly FileManager _files;

    /// <summary>Creates the handler.</summary>
    public FileDeleteHandler(FileManager files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Command => CommandNames.FileDelete;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.TryReadArgs(out FileDeleteArgs args, out CommandResult? failure))
        {
            return failure!;
        }

        // Permanent only on a literal true. Absent, null and false all mean the Recycle Bin.
        bool permanent = args.Permanent == true;

        FileResult<bool> result = await Task
            .Run(() => _files.Delete(args.Path, permanent), cancellationToken)
            .ConfigureAwait(false);

        return FileResults.ToEmptyResult(result);
    }
}
