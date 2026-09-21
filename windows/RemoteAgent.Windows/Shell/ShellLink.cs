using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace RemoteAgent.Windows.Shell;

/// <summary>
/// Resolves the target of a Start Menu shortcut.
/// </summary>
/// <remarks>
/// Needed only to work out <em>which executable</em> a shortcut refers to, so that a
/// running process can be attributed to an installed application. Launching does not
/// use this: the shortcut itself is launched through the shell, which preserves the
/// arguments, working directory and "run as" settings the installer configured.
/// <para>
/// A shortcut's target is read but never trusted as a launch parameter. Nothing a
/// remote caller sends ever reaches these APIs — the caller names a catalog id, and the
/// catalog was built from the local machine (§4).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ShellLink
{
    /// <summary>
    /// Reads a <c>.lnk</c> file's target path, or returns null when it cannot be
    /// resolved.
    /// </summary>
    /// <remarks>
    /// Resolution is requested with flags that suppress all UI and avoid any network
    /// search: a Start Menu full of shortcuts to disconnected network shares must not
    /// turn catalog building into a multi-second stall, and must never show a dialog on
    /// the user's desktop.
    /// </remarks>
    internal static string? TryResolveTarget(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return null;
        }

        IShellLinkW? link = null;
        try
        {
            // Created from the CLSID rather than by casting a [ComImport] class: the
            // compiler refuses that cast for a sealed coclass, and going through the
            // CLSID is the documented route anyway.
            Type? coClass = Type.GetTypeFromCLSID(ShellLinkClsid);
            if (coClass is null)
            {
                return null;
            }

            link = (IShellLinkW?)Activator.CreateInstance(coClass);
            if (link is null)
            {
                return null;
            }

            var persist = (IPersistFile)link;

            persist.Load(shortcutPath, 0 /* STGM_READ */);

            // SLR_NO_UI | SLR_NOSEARCH | SLR_NOTRACK | SLR_NOLINKINFO
            const uint noUi = 0x0001;
            const uint noSearch = 0x0010;
            const uint noTrack = 0x0020;
            const uint noLinkInfo = 0x0040;
            link.Resolve(nint.Zero, noUi | noSearch | noTrack | noLinkInfo);

            var builder = new StringBuilder(260);
            link.GetPath(builder, builder.Capacity, nint.Zero, 0);

            string target = builder.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (COMException)
        {
            // A broken or inaccessible shortcut is ordinary on a real machine: a
            // half-uninstalled program, a shortcut to a removed drive. Skipping it is
            // correct, and it must not interrupt catalog building.
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    /// <summary>CLSID_ShellLink.</summary>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxPath,
            nint findData,
            uint flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxArguments);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int iconPathLength,
            out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(nint window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
