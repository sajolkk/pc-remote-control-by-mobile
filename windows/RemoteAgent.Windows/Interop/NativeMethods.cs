using System.Runtime.InteropServices;

namespace RemoteAgent.Windows.Interop;

/// <summary>
/// Hand-written P/Invoke declarations, grouped by the DLL they come from.
/// </summary>
/// <remarks>
/// Deliberately minimal and fully documented: every entry point here is a place
/// where a mistake becomes a native crash or a silent security failure, so the list
/// is kept short enough to audit by reading.
/// </remarks>
internal static partial class NativeMethods
{
    // --- Error codes worth naming ---

    internal const int ErrorSuccess = 0;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNoToken = 1008;
    internal const int ErrorNotAllAssigned = 1300;
    internal const int ErrorNoShutdownInProgress = 1116;
    internal const int ErrorShutdownInProgress = 1115;

    // ---------------------------------------------------------------------
    // advapi32 — privileges and shutdown
    // ---------------------------------------------------------------------

    internal const string SeShutdownName = "SeShutdownPrivilege";

    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>Reason code: a planned operation initiated by an application.</summary>
    internal const uint ShtdnReasonMajorApplication = 0x00040000;

    /// <summary>Reason qualifier: planned rather than a fault.</summary>
    internal const uint ShtdnReasonFlagPlanned = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    /// <summary>
    /// Starts a shutdown or restart with an optional grace period.
    /// </summary>
    /// <remarks>
    /// Preferred over <c>ExitWindowsEx</c> because the grace period, the cancellation
    /// window and the "force apps closed" behaviour are all handled by Windows rather
    /// than reimplemented here — which also means the user sees the standard system
    /// warning and has a real chance to abort at the machine.
    /// </remarks>
    [LibraryImport("advapi32.dll", EntryPoint = "InitiateSystemShutdownExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitiateSystemShutdownEx(
        string? machineName,
        string? message,
        uint timeoutSeconds,
        [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool rebootAfterShutdown,
        uint reason);

    /// <summary>Cancels a shutdown that is still inside its grace period.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "AbortSystemShutdownW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AbortSystemShutdown(string? machineName);

    // ---------------------------------------------------------------------
    // kernel32
    // ---------------------------------------------------------------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// AC and battery state.
    /// </summary>
    /// <remarks>
    /// Used instead of <c>System.Windows.Forms.SystemInformation.PowerStatus</c>, which
    /// would drag a WinForms dependency into a project that has no UI.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        /// <summary>0 offline, 1 online, 255 unknown.</summary>
        public byte AcLineStatus;

        /// <summary>Bit flags; 128 means no system battery, 255 unknown.</summary>
        public byte BatteryFlag;

        /// <summary>0-100, or 255 when unknown.</summary>
        public byte BatteryLifePercent;

        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    // ---------------------------------------------------------------------
    // powrprof — sleep and power capabilities
    // ---------------------------------------------------------------------

    /// <summary>
    /// Suspends the machine. Requires <see cref="SeShutdownName"/> to be enabled in
    /// the calling process token.
    /// </summary>
    [LibraryImport("powrprof.dll", EntryPoint = "SetSuspendState", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    /// <summary>
    /// Reports which power states this machine supports, so sleep is advertised only
    /// when it will actually work (§0).
    /// </summary>
    /// <remarks>
    /// Declared with byte fields rather than <c>bool</c> fields carrying
    /// <c>[MarshalAs(UnmanagedType.U1)]</c>: the attribute makes the struct
    /// non-blittable, which source-generated P/Invoke cannot marshal. Byte fields keep
    /// the struct blittable (so it is passed with no marshalling at all) and the named
    /// properties below restore readability at the call site.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SystemPowerCapabilities
    {
        public byte PowerButtonPresent;
        public byte SleepButtonPresent;
        public byte LidPresent;
        public byte SystemS1;
        public byte SystemS2;
        public byte SystemS3;
        public byte SystemS4;
        public byte SystemS5;
        public byte HiberFilePresent;
        public byte FullWake;
        public byte VideoDimPresent;
        public byte ApmPresent;
        public byte UpsPresent;
        public byte ThermalControl;
        public byte ProcessorThrottle;
        public byte ProcessorMinThrottle;
        public byte ProcessorMaxThrottle;
        public byte FastSystemS4;
        public byte Hiberboot;
        public byte WakeAlarmPresent;
        public byte AoAc;
        public byte DiskSpinDown;
        public byte HiberFileType;
        public byte AoAcConnectivitySupported;
        public fixed byte Spare3[6];
        public byte SystemBatteriesPresent;
        public byte BatteriesAreShortTerm;
        /// <summary>
        /// Native <c>BATTERY_REPORTING_SCALE BatteryScale[3]</c>. Each element is two DWORDs
        /// (Granularity and Capacity), so this is 3 x 2 x 4 = 24 bytes, declared as six uints.
        /// </summary>
        /// <remarks>
        /// Getting this length wrong is not a silent bug: the struct is an out parameter on the
        /// stack, so a short declaration lets the OS write past the end of it and the process dies
        /// with STATUS_STACK_BUFFER_OVERRUN (0xC0000409) as soon as the stack cookie is checked —
        /// with no managed exception and no log line. An earlier version declared 12 bytes here and
        /// did exactly that.
        /// </remarks>
        public fixed uint BatteryScale[6];
        public int AcOnLineWake;
        public int SoftLidWake;
        public int RtcWake;
        public int MinDeviceWakeState;
        public int DefaultLowLatencyWake;

        /// <summary>Classic S3 "suspend to RAM". This is the state Wake-on-LAN works from.</summary>
        internal bool SupportsS3 => SystemS3 != 0;

        /// <summary>Hibernate to disk.</summary>
        internal bool SupportsS4 => SystemS4 != 0;

        /// <summary>Modern standby, used by tablets and many recent laptops instead of S3.</summary>
        internal bool SupportsModernStandby => AoAc != 0;

        /// <summary>
        /// Fast Startup. When on, "shut down" is really a hybrid hibernate, and waking
        /// with a magic packet usually does not work (§12.2).
        /// </summary>
        internal bool FastStartupEnabled => Hiberboot != 0;
    }

    [LibraryImport("powrprof.dll", EntryPoint = "GetPwrCapabilities", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    // ---------------------------------------------------------------------
    // user32 — workstation lock and windows
    // ---------------------------------------------------------------------

    /// <summary>
    /// Locks the workstation. Only works from a process running on the interactive
    /// desktop, which is why the service delegates this to the session agent (§10).
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ExitWindowsEx(uint flags, uint reason);

    internal const uint EwxLogoff = 0x00000000;
    internal const uint EwxForce = 0x00000004;
    internal const uint EwxForceIfHung = 0x00000010;

    // ---------------------------------------------------------------------
    // wtsapi32 — session enumeration and control
    // ---------------------------------------------------------------------

    internal static readonly nint WtsCurrentServerHandle = nint.Zero;

    internal enum WtsConnectStateClass
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init,
    }

    internal enum WtsInfoClass
    {
        InitialProgram = 0,
        ApplicationName = 1,
        WorkingDirectory = 2,
        OemId = 3,
        SessionId = 4,
        UserName = 5,
        WinStationName = 6,
        DomainName = 7,
        ConnectState = 8,
        ClientBuildNumber = 9,
        ClientName = 10,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WtsSessionInfo
    {
        public uint SessionId;
        public nint WinStationName;
        public WtsConnectStateClass State;
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateSessions(
        nint server,
        uint reserved,
        uint version,
        out nint sessionInfo,
        out uint count);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformation(
        nint server,
        uint sessionId,
        WtsInfoClass infoClass,
        out nint buffer,
        out uint bytesReturned);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    internal static partial void WTSFreeMemory(nint memory);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSLogoffSession", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSLogoffSession(
        nint server,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSDisconnectSession", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSDisconnectSession(
        nint server,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    internal static partial uint WTSGetActiveConsoleSessionId();
}
