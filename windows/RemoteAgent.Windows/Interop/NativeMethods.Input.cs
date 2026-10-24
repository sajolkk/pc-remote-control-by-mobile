using System.Runtime.InteropServices;

namespace RemoteAgent.Windows.Interop;

/// <summary>
/// Input injection, and the desktop facts it depends on.
/// </summary>
internal static partial class NativeMethods
{
    // ---------------------------------------------------------------------
    // user32 — SendInput
    // ---------------------------------------------------------------------

    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint MouseEventMove = 0x0001;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint MouseEventRightDown = 0x0008;
    internal const uint MouseEventRightUp = 0x0010;
    internal const uint MouseEventMiddleDown = 0x0020;
    internal const uint MouseEventMiddleUp = 0x0040;
    internal const uint MouseEventWheel = 0x0800;
    internal const uint MouseEventHWheel = 0x1000;

    /// <summary>Map absolute coordinates onto the whole virtual desktop rather than the primary monitor.</summary>
    internal const uint MouseEventVirtualDesk = 0x4000;

    /// <summary><c>dx</c>/<c>dy</c> are 0..65535 positions, not relative mickeys.</summary>
    internal const uint MouseEventAbsolute = 0x8000;

    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventKeyUp = 0x0002;

    /// <summary><c>wScan</c> is a UTF-16 code unit to type; <c>wVk</c> must be zero.</summary>
    internal const uint KeyEventUnicode = 0x0004;

    /// <summary>MAPVK_VK_TO_VSC: virtual key to scan code, using the current keyboard layout.</summary>
    internal const uint MapVkToVsc = 0;

    /// <summary>
    /// <c>MOUSEINPUT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>
    /// <c>KEYBDINPUT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>
    /// The union inside <c>INPUT</c>. <c>MOUSEINPUT</c> is its largest member, so overlaying the two
    /// used here at offset zero gives the union its correct size without declaring
    /// <c>HARDWAREINPUT</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    /// <summary>
    /// <c>INPUT</c>. 40 bytes on 64-bit Windows: a 4-byte type, 4 bytes of padding the pointer-sized
    /// member forces, and the 32-byte union.
    /// </summary>
    /// <remarks>
    /// The size is passed to <see cref="SendInput"/> and Windows rejects the whole call when it is
    /// wrong, which is the failure mode of every hand-written declaration of this structure that
    /// forgets the padding. Sequential layout with a pointer-sized field produces it naturally.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    /// <summary>
    /// Inserts events into the input stream as one uninterrupted sequence.
    /// </summary>
    /// <returns>
    /// The number of events inserted. Zero means the call was blocked — by another thread's
    /// <c>BlockInput</c>, or because the calling thread's desktop is not the one receiving input.
    /// User Interface Privilege Isolation does not show up here: events aimed at an elevated window
    /// are accepted and then silently discarded (§12.1).
    /// </returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    internal static partial uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);

    // ---------------------------------------------------------------------
    // user32 — system metrics
    // ---------------------------------------------------------------------

    /// <summary>Nonzero when the user has swapped the primary and secondary mouse buttons.</summary>
    internal const int SmSwapButton = 23;

    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    /// <remarks>
    /// In a per-monitor-DPI-aware process the virtual-screen metrics are in physical pixels, which
    /// is what the absolute input space is defined over.
    /// </remarks>
    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    // ---------------------------------------------------------------------
    // user32 — which desktop owns input
    // ---------------------------------------------------------------------

    internal const uint DesktopReadObjects = 0x0001;

    /// <summary>UOI_NAME: the object's name.</summary>
    internal const int UoiName = 2;

    /// <summary>
    /// Opens the desktop that is currently receiving user input.
    /// </summary>
    /// <remarks>
    /// Fails with access denied while the secure desktop (lock screen, Ctrl+Alt+Del, UAC consent)
    /// is active, because an ordinary process has no rights on Winlogon's desktop. That failure is
    /// itself the signal the agent wants (§12.1).
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseDesktop(nint desktop);

    /// <summary>
    /// Desktop access for a thread that is to be moved onto the input desktop: enough to read it,
    /// draw on it, duplicate it and inject input into it.
    /// </summary>
    internal const uint GenericAll = 0x10000000;

    /// <summary>
    /// Moves the calling thread onto a desktop.
    /// </summary>
    /// <remarks>
    /// Fails if the thread owns a window or a hook. Only the desktop agent calls it, and only from
    /// threads that never create either (§12.1).
    /// </remarks>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetThreadDesktop(uint threadId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetUserObjectInformation(
        nint handle,
        int index,
        char* buffer,
        uint bufferBytes,
        out uint bytesNeeded);
}
