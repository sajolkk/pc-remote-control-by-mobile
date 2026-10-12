using System.Runtime.InteropServices;

namespace RemoteAgent.Windows.Interop;

/// <summary>
/// Shell operations used by file management: the Recycle Bin and known folders.
/// </summary>
internal static partial class NativeMethods
{
    // ---------------------------------------------------------------------
    // shell32 — SHFileOperation, used only to recycle
    // ---------------------------------------------------------------------

    /// <summary>FO_DELETE.</summary>
    internal const uint FoDelete = 0x0003;

    /// <summary>FOF_SILENT: no progress window.</summary>
    internal const ushort FofSilent = 0x0004;

    /// <summary>FOF_NOCONFIRMATION: answer "yes to all" to the routine "are you sure" questions.</summary>
    internal const ushort FofNoConfirmation = 0x0010;

    /// <summary>FOF_ALLOWUNDO: for a delete, this is what sends the item to the Recycle Bin.</summary>
    internal const ushort FofAllowUndo = 0x0040;

    /// <summary>FOF_NOERRORUI: report errors through the return value instead of a dialog.</summary>
    internal const ushort FofNoErrorUi = 0x0400;

    /// <summary>
    /// FOF_WANTNUKEWARNING: ask before destroying an item that cannot be recycled.
    /// </summary>
    /// <remarks>
    /// Without this flag, <see cref="FofNoConfirmation"/> turns "this is too big for the Recycle
    /// Bin" into a silent permanent delete. With it, that one question is still asked, on the PC's
    /// screen, of the only person who should be answering it.
    /// </remarks>
    internal const ushort FofWantNukeWarning = 0x4000;

    /// <summary>
    /// <c>SHFILEOPSTRUCTW</c>, 64-bit layout.
    /// </summary>
    /// <remarks>
    /// The 32-bit declaration of this structure is byte-packed and differs; only x64 and ARM64 are
    /// built, where natural alignment is correct. The path members are raw pointers because
    /// <c>pFrom</c> is a <em>double</em>-null-terminated list, which string marshalling cannot
    /// express.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShFileOpStruct
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    /// <summary>
    /// Performs a shell file operation. Returns zero on success; the nonzero values are the shell's
    /// own pre-Win32 codes and are not <c>GetLastError</c> values.
    /// </summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    internal static partial int SHFileOperation(ref ShFileOpStruct operation);

    // ---------------------------------------------------------------------
    // shell32 — known folders
    // ---------------------------------------------------------------------

    /// <summary>
    /// FOLDERID_Downloads. The one user folder <c>Environment.SpecialFolder</c> has no entry for.
    /// </summary>
    internal static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// Resolves a known folder to its current path, which the user may have relocated.
    /// </summary>
    /// <param name="folderId">The folder's KNOWNFOLDERID.</param>
    /// <param name="flags">KNOWN_FOLDER_FLAG values; zero for the plain path.</param>
    /// <param name="token">An access token, or zero for the calling user.</param>
    /// <param name="path">A CoTaskMem string the caller frees with <see cref="Marshal.FreeCoTaskMem"/>.</param>
    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);
}
