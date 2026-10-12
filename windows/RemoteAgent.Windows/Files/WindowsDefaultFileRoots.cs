using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Files;

/// <summary>
/// The built-in file roots: the signed-in user's personal folders and every fixed drive (§0, §7.3).
/// </summary>
/// <remarks>
/// <para>Everything is asked of Windows at runtime. The personal folders come from the shell's
/// known-folder table rather than from <c>%USERPROFILE%\Documents</c>, because users relocate them
/// (to another drive, into OneDrive) and the profile-relative guess is then simply wrong. Drives
/// are whatever is fixed and ready at this moment.</para>
///
/// <para>Only <em>fixed</em> drives are offered. Removable media, optical drives and mapped network
/// drives are left out by default: a network drive means network I/O under the user's credentials
/// on a remote caller's say-so, and removable media comes and goes. An owner who wants one adds it
/// to <c>files.allowedRoots</c> by drive letter.</para>
///
/// <para>The list is cached briefly. Reading a volume label can spin up a sleeping disk, and a file
/// browser asks for the roots on every command.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDefaultFileRoots : IDefaultFileRoots
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private readonly ILogger<WindowsDefaultFileRoots> _logger;
    private readonly Lock _sync = new();

    private IReadOnlyList<FileRoot>? _cache;
    private long _cachedAt;

    /// <summary>Creates the provider.</summary>
    public WindowsDefaultFileRoots(ILogger<WindowsDefaultFileRoots> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<FileRoot> GetDefaultRoots()
    {
        lock (_sync)
        {
            if (_cache is not null && Environment.TickCount64 - _cachedAt < CacheLifetime.TotalMilliseconds)
            {
                return _cache;
            }

            _cache = Discover();
            _cachedAt = Environment.TickCount64;
            return _cache;
        }
    }

    private List<FileRoot> Discover()
    {
        var roots = new List<FileRoot>();

        AddFolder(roots, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddFolder(roots, "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddFolder(roots, "Downloads", ResolveDownloads());
        AddFolder(roots, "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddFolder(roots, "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddFolder(roots, "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                string letter = drive.Name.TrimEnd('\\');
                string label = ReadVolumeLabel(drive);

                roots.Add(new FileRoot($"{label} ({letter})", drive.RootDirectory.FullName, Writable: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Drives could not be enumerated; only the personal folders are offered.");
        }

        return roots;
    }

    private static void AddFolder(List<FileRoot> roots, string label, string? path)
    {
        // An empty path means this user has no such folder; a path that does not exist is a folder
        // on a disconnected drive. Neither is worth offering.
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            roots.Add(new FileRoot(label, path, Writable: true));
        }
    }

    private static string ReadVolumeLabel(DriveInfo drive)
    {
        try
        {
            // An unlabelled volume is what Explorer calls "Local Disk".
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Local Disk";
        }
    }

    private string? ResolveDownloads()
    {
        nint buffer = nint.Zero;
        try
        {
            int result = NativeMethods.SHGetKnownFolderPath(NativeMethods.FolderIdDownloads, 0, nint.Zero, out buffer);
            if (result == 0 && buffer != nint.Zero)
            {
                return Marshal.PtrToStringUni(buffer);
            }

            _logger.LogDebug("The Downloads folder could not be resolved (HRESULT 0x{Result:X8}).", result);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "SHGetKnownFolderPath is unavailable.");
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        // The location every profile starts with, for the rare case the shell cannot say.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }
}
