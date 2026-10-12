using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Windows.Interop;

namespace RemoteAgent.Windows.Files;

/// <summary>
/// The Recycle Bin and "open with the default application", through the Windows shell (§7.3).
/// </summary>
/// <remarks>
/// <para><b>Recycling never becomes deleting.</b> The shell will, if asked not to confirm anything,
/// quietly destroy an item it cannot recycle — one larger than the bin, or on a volume without one.
/// <c>FOF_WANTNUKEWARNING</c> keeps that single question alive. It is then asked on the PC's own
/// screen, which is the right place for it, but nobody may be sitting there; so the operation runs
/// on its own thread and this class waits a bounded time for it. If Windows is still waiting for
/// an answer, the caller is told so and the item is left exactly as it was unless and until
/// someone at the PC decides.</para>
///
/// <para><b>Opening a path is the one place a caller-influenced string reaches the shell</b>, so it
/// is worth being exact about what arrives here: a canonical absolute path that
/// <c>FileManager</c> has already confirmed exists inside an allowed root, has screened for
/// executable types against the caller's permissions, and passes alone — as the thing to open,
/// with no arguments and no verb. The shell is not handed a command line to parse.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileShell : IFileShell
{
    /// <summary>The shell's own limit for <c>SHFileOperation</c> paths.</summary>
    private const int MaxShellPath = 259;

    private static readonly TimeSpan RecycleWait = TimeSpan.FromSeconds(15);

    private readonly ILogger<WindowsFileShell> _logger;

    /// <summary>Creates the shell wrapper.</summary>
    public WindowsFileShell(ILogger<WindowsFileShell> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public RecycleOutcome MoveToRecycleBin(string path, out string? detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (path.Length > MaxShellPath)
        {
            detail = "That path is too long for the Recycle Bin. It can only be deleted permanently.";
            return RecycleOutcome.Refused;
        }

        int result = -1;
        bool aborted = false;

        // A single-threaded apartment of its own: the shell's file operations are COM underneath
        // and may need to show a window, neither of which belongs on a thread-pool thread.
        var worker = new Thread(() => result = Recycle(path, out aborted))
        {
            IsBackground = true,
            Name = "PC-Remote recycle",
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(RecycleWait))
        {
            // Still running, which in practice means a question is on the PC's screen. The thread
            // is left to finish whenever it is answered; the item is untouched in the meantime.
            _logger.LogInformation("A recycle request is waiting for an answer on the desktop.");
            detail = "Windows is asking a question about this item on the PC's screen. It has to be answered there.";
            return RecycleOutcome.AwaitingConfirmation;
        }

        if (result == 0 && !aborted && !File.Exists(path) && !Directory.Exists(path))
        {
            detail = null;
            return RecycleOutcome.Recycled;
        }

        _logger.LogInformation(
            "The shell did not recycle an item. Result=0x{Result:X} Aborted={Aborted}",
            result,
            aborted);

        detail = aborted
            ? "The delete was cancelled on the PC."
            : DescribeShellError(result);

        return RecycleOutcome.Refused;
    }

    /// <inheritdoc />
    public bool Open(string path, out string? detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            // UseShellExecute with a document path and nothing else: the shell looks up the
            // default handler for the file's type, or opens a folder in Explorer.
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            });

            detail = null;
            return true;
        }
        catch (Win32Exception ex)
        {
            const int noAssociation = 1155;
            const int cancelled = 1223;

            _logger.LogInformation("An item could not be opened: Win32 error {Error}.", ex.NativeErrorCode);

            detail = ex.NativeErrorCode switch
            {
                noAssociation => "No app on the PC is set to open that type of file.",
                cancelled => "Opening was cancelled on the PC.",
                NativeMethods.ErrorAccessDenied => "Windows denied access to that item.",
                _ => "Windows could not open that item.",
            };

            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            _logger.LogInformation(ex, "An item could not be opened.");
            detail = "Windows could not open that item.";
            return false;
        }
    }

    private static int Recycle(string path, out bool aborted)
    {
        // pFrom is a list of paths, each null-terminated, with one more null ending the list.
        // StringToHGlobalUni supplies the final terminator; the explicit one ends the only entry.
        nint from = Marshal.StringToHGlobalUni(path + '\0');
        try
        {
            var operation = new NativeMethods.ShFileOpStruct
            {
                Function = NativeMethods.FoDelete,
                From = from,
                Flags = NativeMethods.FofAllowUndo |
                        NativeMethods.FofNoConfirmation |
                        NativeMethods.FofSilent |
                        NativeMethods.FofNoErrorUi |
                        NativeMethods.FofWantNukeWarning,
            };

            int result = NativeMethods.SHFileOperation(ref operation);
            aborted = operation.AnyOperationsAborted != 0;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    private static string DescribeShellError(int result) => result switch
    {
        // DE_ACCESSDENIEDSRC, plain access denied, and the write-protected-media code.
        0x78 or 0x05 or 0x13 => "Windows denied access. The item may be protected or need administrator rights.",

        // A sharing violation: something has it open.
        0x20 => "The item is in use by another program on the PC.",

        // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND / DE_INVALIDFILES.
        0x02 or 0x03 or 0x7C => "The item no longer exists.",

        _ => "Windows could not move that item to the Recycle Bin.",
    };
}
