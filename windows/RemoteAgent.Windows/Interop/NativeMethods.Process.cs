using System.Runtime.InteropServices;

namespace RemoteAgent.Windows.Interop;

/// <summary>
/// Token duplication and process creation across the Session 0 boundary.
/// </summary>
internal static partial class NativeMethods
{
    internal const uint TokenDuplicate = 0x0002;
    internal const uint TokenAssignPrimary = 0x0001;
    internal const uint TokenAdjustDefault = 0x0080;
    internal const uint TokenAdjustSessionId = 0x0100;
    internal const uint TokenAllAccess = 0xF01FF;

    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint CreateNewConsole = 0x00000010;
    internal const uint CreateBreakawayFromJob = 0x01000000;

    internal const uint StartfUseShowWindow = 0x00000001;
    internal const ushort SwHide = 0;

    internal enum SecurityImpersonationLevel
    {
        Anonymous = 0,
        Identification = 1,
        Impersonation = 2,
        Delegation = 3,
    }

    internal enum TokenType
    {
        Primary = 1,
        Impersonation = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public short Reserved2;
        public nint Reserved3;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    /// <summary>
    /// Obtains the primary token of the user logged into a session.
    /// </summary>
    /// <remarks>
    /// The single most important call in the service/agent split: it is what lets a Session 0
    /// service start a process that can see the user's desktop. Requires SYSTEM, which is
    /// precisely why the privileged half of the system exists (§3).
    /// </remarks>
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQueryUserToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint sessionId, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out nint newToken);

    /// <summary>
    /// Builds the user's environment block, including their profile variables.
    /// </summary>
    /// <remarks>
    /// Necessary because a process created from a duplicated token would otherwise inherit
    /// the service's Session 0 environment, where <c>%USERPROFILE%</c> and friends point at
    /// the system profile. The file-transfer roots are expressed with those variables, so
    /// getting this wrong would silently expose the wrong directories (§0).
    /// </remarks>
    [LibraryImport("userenv.dll", EntryPoint = "CreateEnvironmentBlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(
        out nint environment,
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", EntryPoint = "DestroyEnvironmentBlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(
        nint token,
        string? applicationName,
        string? commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);
}
