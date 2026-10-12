using RemoteAgent.Core.Abstractions;

namespace RemoteAgent.Core.Input;

/// <summary>
/// The arithmetic that turns a touch on the phone into a pointer position on the PC (§4, §10).
/// </summary>
/// <remarks>
/// <para>Three coordinate spaces are involved, and confusing any two of them is the classic
/// remote-control bug that only shows up on somebody else's desk:</para>
/// <list type="number">
/// <item><b>Monitor-normalised</b>, what the phone sends: doubles in [0,1] across ONE monitor,
/// (0,0) its top-left pixel and (1,1) its bottom-right pixel.</item>
/// <item><b>Virtual-desktop pixels</b>, what Windows lays monitors out in. The primary monitor's
/// top-left is (0,0), so a monitor to its left or above it has a <em>negative</em> origin.</item>
/// <item><b>Absolute input units</b>, what <c>SendInput</c> takes with
/// <c>MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK</c>: 0..65535 stretched over the bounding
/// rectangle of <em>all</em> monitors, not over the one being touched.</item>
/// </list>
/// <para>Everything here is in <em>physical</em> pixels. The session agent is per-monitor-DPI-aware
/// (v2), so Windows gives it unscaled coordinates for every monitor and a mixed-DPI layout needs no
/// correction at all — which is the whole reason the manifest declares it (§12.2). Nothing in this
/// file multiplies by a scale factor, and nothing should.</para>
///
/// <para>Pure functions with no platform dependency, so the layouts that are awkward to assemble
/// physically (a portrait monitor above-left of a 4K primary) are a unit test instead.</para>
/// </remarks>
public static class InputCoordinates
{
    /// <summary>The largest value of the absolute input space.</summary>
    public const int AbsoluteMax = 65535;

    /// <summary>
    /// Validates one normalised coordinate: NaN and infinities are refused, anything else is
    /// clamped into [0,1].
    /// </summary>
    public static bool TryClampNormalized(double value, out double clamped)
    {
        if (!double.IsFinite(value))
        {
            clamped = 0;
            return false;
        }

        clamped = Math.Clamp(value, 0.0, 1.0);
        return true;
    }

    /// <summary>
    /// Maps a monitor-normalised point to the virtual-desktop pixel it names.
    /// </summary>
    /// <remarks>
    /// Scaled by <c>size - 1</c>, not <c>size</c>: 1.0 must land on the last pixel of this monitor,
    /// not on the first pixel of its neighbour.
    /// </remarks>
    public static (int X, int Y) MonitorPointToPixel(double x, double y, PixelRect monitor)
    {
        double nx = Math.Clamp(double.IsFinite(x) ? x : 0.0, 0.0, 1.0);
        double ny = Math.Clamp(double.IsFinite(y) ? y : 0.0, 0.0, 1.0);

        int px = monitor.X + (int)Math.Round(nx * Math.Max(0, monitor.Width - 1), MidpointRounding.AwayFromZero);
        int py = monitor.Y + (int)Math.Round(ny * Math.Max(0, monitor.Height - 1), MidpointRounding.AwayFromZero);
        return (px, py);
    }

    /// <summary>
    /// Maps a virtual-desktop pixel to absolute input units along one axis.
    /// </summary>
    /// <param name="pixel">The pixel coordinate, which may be negative.</param>
    /// <param name="origin">The virtual desktop's left or top edge (<c>SM_XVIRTUALSCREEN</c> / <c>SM_YVIRTUALSCREEN</c>).</param>
    /// <param name="extent">The virtual desktop's width or height.</param>
    /// <remarks>
    /// Windows converts back with <c>pixel = origin + (units * extent) / 65536</c>, truncating. The
    /// value returned here aims at the <em>centre</em> of the target pixel, so that truncation lands
    /// on exactly the pixel asked for at every position, and remains within the pixel even if the
    /// system rounds instead. A pixel outside the desktop is clamped onto its nearest edge.
    /// </remarks>
    public static int PixelToAbsolute(int pixel, int origin, int extent)
    {
        if (extent <= 0)
        {
            return 0;
        }

        long offset = Math.Clamp((long)pixel - origin, 0, extent - 1);
        long units = (((2 * offset) + 1) * 65536) / (2L * extent);
        return (int)Math.Clamp(units, 0, AbsoluteMax);
    }

    /// <summary>Maps a virtual-desktop pixel to absolute input units on both axes.</summary>
    public static (int X, int Y) PixelToAbsolute(int pixelX, int pixelY, PixelRect virtualScreen) =>
        (PixelToAbsolute(pixelX, virtualScreen.X, virtualScreen.Width),
         PixelToAbsolute(pixelY, virtualScreen.Y, virtualScreen.Height));

    /// <summary>
    /// The pixel Windows moves the pointer to for a given absolute value: the inverse of
    /// <see cref="PixelToAbsolute(int, int, int)"/>, as the system computes it.
    /// </summary>
    public static int AbsoluteToPixel(int units, int origin, int extent) =>
        origin + (int)(((long)units * extent) >> 16);

    /// <summary>Clamps a pixel onto the virtual desktop.</summary>
    public static (int X, int Y) ClampToScreen(long pixelX, long pixelY, PixelRect virtualScreen)
    {
        int x = (int)Math.Clamp(pixelX, virtualScreen.X, virtualScreen.X + (long)Math.Max(1, virtualScreen.Width) - 1);
        int y = (int)Math.Clamp(pixelY, virtualScreen.Y, virtualScreen.Y + (long)Math.Max(1, virtualScreen.Height) - 1);
        return (x, y);
    }
}

/// <summary>
/// Facts about Windows virtual-key codes that the input path needs and that do not depend on the
/// keyboard layout.
/// </summary>
public static class VirtualKeys
{
    /// <summary>Enter.</summary>
    public const int Return = 0x0D;

    /// <summary>Tab.</summary>
    public const int Tab = 0x09;

    /// <summary>Alt.</summary>
    public const int Menu = 0x12;

    /// <summary>
    /// Whether a code may be injected as a key.
    /// </summary>
    /// <remarks>
    /// 1..254 is the range Windows defines. The five mouse-button codes inside it are refused: they
    /// are not keys, sending one through the keyboard path does not click anything, and it would
    /// leave the system's key-state table claiming a button is down that the mouse path knows
    /// nothing about. Buttons go through <c>input.mouse_button</c>.
    /// </remarks>
    public static bool IsInjectable(int virtualKey) =>
        virtualKey is >= 1 and <= 254 &&
        virtualKey is not (0x01 or 0x02 or 0x04 or 0x05 or 0x06);

    /// <summary>
    /// Whether a key's scan code carries the 0xE0 "extended" prefix.
    /// </summary>
    /// <remarks>
    /// The extended flag is how Windows tells the dedicated arrow, navigation and editing keys from
    /// the numeric-keypad keys that share their scan codes. Without it an injected Left arrow is
    /// "numpad 4 with NumLock off", which mostly works — until Shift is held, where the keypad
    /// variant drops the selection, or a game or terminal reads the scan code directly.
    /// </remarks>
    public static bool IsExtended(int virtualKey) => virtualKey switch
    {
        // Page Up, Page Down, End, Home, Left, Up, Right, Down.
        >= 0x21 and <= 0x28 => true,

        // Print Screen, Insert, Delete.
        0x2C or 0x2D or 0x2E => true,

        // Left Windows, Right Windows, Menu (Apps), Sleep.
        0x5B or 0x5C or 0x5D or 0x5F => true,

        // Numpad Divide, Num Lock.
        0x6F or 0x90 => true,

        // Right Ctrl, Right Alt.
        0xA3 or 0xA5 => true,

        // Browser, volume and media keys.
        >= 0xA6 and <= 0xB7 => true,

        _ => false,
    };
}
