using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Input;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for the touch-to-pointer arithmetic.
/// </summary>
/// <remarks>
/// The layouts here are the ones that break naive implementations and are tedious to assemble on a
/// desk: a monitor to the left of the primary (negative X), one above it (negative Y), monitors of
/// different sizes, and a desktop wider than one screen so that "0..65535" does not mean "this
/// monitor". Every assertion goes through <see cref="InputCoordinates.AbsoluteToPixel"/>, which is
/// the conversion Windows applies to an absolute event, so what is checked is the pixel the pointer
/// would actually land on.
/// </remarks>
public sealed class InputCoordinatesTests
{
    /// <summary>Primary 1920×1080 at the origin, alone.</summary>
    private static readonly PixelRect Single = new(0, 0, 1920, 1080);

    /// <summary>
    /// A 2560×1440 monitor to the LEFT of a 1920×1080 primary, and a 1280×1024 monitor ABOVE the
    /// primary: the virtual desktop spans x −2560..1919 and y −1024..1439.
    /// </summary>
    private static readonly PixelRect Left = new(-2560, 0, 2560, 1440);

    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Above = new(320, -1024, 1280, 1024);
    private static readonly PixelRect VirtualDesktop = new(-2560, -1024, 4480, 2464);

    private static (int X, int Y) Land(double x, double y, PixelRect monitor, PixelRect desktop)
    {
        (int px, int py) = InputCoordinates.MonitorPointToPixel(x, y, monitor);
        (int ax, int ay) = InputCoordinates.PixelToAbsolute(px, py, desktop);

        return (InputCoordinates.AbsoluteToPixel(ax, desktop.X, desktop.Width),
                InputCoordinates.AbsoluteToPixel(ay, desktop.Y, desktop.Height));
    }

    [Fact]
    public void CornersOfASingleMonitorLandOnItsCornerPixels()
    {
        Assert.Equal((0, 0), Land(0, 0, Single, Single));
        Assert.Equal((1919, 1079), Land(1, 1, Single, Single));
        Assert.Equal((1919, 0), Land(1, 0, Single, Single));
        Assert.Equal((0, 1079), Land(0, 1, Single, Single));
    }

    [Fact]
    public void TheCentreOfAMonitorLandsOnItsCentre()
    {
        (int x, int y) = Land(0.5, 0.5, Single, Single);

        Assert.InRange(x, 959, 960);
        Assert.InRange(y, 539, 540);
    }

    [Fact]
    public void AMonitorWithANegativeOriginMapsOntoItsOwnPixels()
    {
        // Top-left of the left-hand monitor is the desktop's leftmost column, not x = 0.
        Assert.Equal((-2560, 0), Land(0, 0, Left, VirtualDesktop));
        Assert.Equal((-1, 1439), Land(1, 1, Left, VirtualDesktop));

        // And the monitor above the primary has a negative Y.
        Assert.Equal((320, -1024), Land(0, 0, Above, VirtualDesktop));
        Assert.Equal((1599, -1), Land(1, 1, Above, VirtualDesktop));
    }

    [Fact]
    public void ThePrimaryIsNotAtTheStartOfTheAbsoluteRangeWhenAMonitorSitsToItsLeft()
    {
        // The mistake this guards against: treating 0..65535 as spanning the monitor being
        // touched. With a 2560-wide monitor on the left, the primary's left edge is well into
        // the range.
        (int px, int py) = InputCoordinates.MonitorPointToPixel(0, 0, Primary);
        (int ax, int ay) = InputCoordinates.PixelToAbsolute(px, py, VirtualDesktop);

        Assert.InRange(ax, 37_000, 38_000); // 2560 / 4480 of the way across.
        Assert.InRange(ay, 27_000, 28_000); // 1024 / 2464 of the way down.

        Assert.Equal((0, 0), Land(0, 0, Primary, VirtualDesktop));
        Assert.Equal((1919, 1079), Land(1, 1, Primary, VirtualDesktop));
    }

    [Fact]
    public void OnePointOneOnAMonitorNeverSpillsOntoItsNeighbour()
    {
        // (1,1) of the left monitor must stay on the left monitor: x = -1, not x = 0.
        (int x, _) = Land(1, 0.5, Left, VirtualDesktop);
        Assert.True(x < 0, $"Landed on x={x}, which is the primary monitor.");
    }

    [Theory]
    [InlineData(0, 1920)]       // A single monitor.
    [InlineData(-2560, 4480)]   // Negative origin.
    [InlineData(-1024, 2464)]   // The Y axis of the layout above.
    [InlineData(0, 3840)]       // 4K.
    [InlineData(-3840, 9600)]   // 4K left of 4K left of 1080p.
    [InlineData(0, 1366)]       // A width that does not divide 65536 evenly.
    [InlineData(-1, 2)]         // Degenerate, but must not divide by zero or overflow.
    public void EveryPixelOfTheDesktopIsReachableExactly(int origin, int extent)
    {
        for (int pixel = origin; pixel < origin + extent; pixel++)
        {
            int units = InputCoordinates.PixelToAbsolute(pixel, origin, extent);

            Assert.InRange(units, 0, InputCoordinates.AbsoluteMax);
            Assert.Equal(pixel, InputCoordinates.AbsoluteToPixel(units, origin, extent));
        }
    }

    [Fact]
    public void MixedDpiNeedsNoCorrectionBecauseEverythingIsPhysicalPixels()
    {
        // A 4K monitor at 200% beside a 1080p monitor at 100%. A per-monitor-aware process is
        // given both in physical pixels, so the mapping uses the rectangles as they are: the
        // centre of the 4K monitor is 1920 physical pixels in, whatever its scale factor says.
        var fourK = new PixelRect(0, 0, 3840, 2160);
        var hd = new PixelRect(3840, 0, 1920, 1080);
        var desktop = new PixelRect(0, 0, 5760, 2160);

        (int x, int y) = Land(0.5, 0.5, fourK, desktop);
        Assert.InRange(x, 1919, 1920);
        Assert.InRange(y, 1079, 1080);

        Assert.Equal((3840, 0), Land(0, 0, hd, desktop));
        Assert.Equal((5759, 1079), Land(1, 1, hd, desktop));
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(-1e300, 0.0)]
    [InlineData(1e300, 1.0)]
    [InlineData(0.25, 0.25)]
    public void OutOfRangeValuesAreClamped(double input, double expected)
    {
        Assert.True(InputCoordinates.TryClampNormalized(input, out double clamped));
        Assert.Equal(expected, clamped);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NaNAndInfinityAreRejected(double input)
    {
        Assert.False(InputCoordinates.TryClampNormalized(input, out _));
    }

    [Fact]
    public void APixelOutsideTheDesktopIsClampedOntoItsEdge()
    {
        Assert.Equal(
            -2560,
            InputCoordinates.AbsoluteToPixel(InputCoordinates.PixelToAbsolute(-99_999, -2560, 4480), -2560, 4480));
        Assert.Equal(
            1919,
            InputCoordinates.AbsoluteToPixel(InputCoordinates.PixelToAbsolute(99_999, -2560, 4480), -2560, 4480));

        Assert.Equal((-2560, 1439), InputCoordinates.ClampToScreen(-1_000_000, 1_000_000, VirtualDesktop));
    }

    [Fact]
    public void AnEmptyDesktopDoesNotThrow()
    {
        Assert.Equal(0, InputCoordinates.PixelToAbsolute(10, 0, 0));
        Assert.Equal((0, 0), InputCoordinates.MonitorPointToPixel(1, 1, new PixelRect(0, 0, 0, 0)));
    }

    // --- Virtual keys ---

    [Theory]
    [InlineData(0x25)] // Left
    [InlineData(0x26)] // Up
    [InlineData(0x27)] // Right
    [InlineData(0x28)] // Down
    [InlineData(0x21)] // Page Up
    [InlineData(0x22)] // Page Down
    [InlineData(0x23)] // End
    [InlineData(0x24)] // Home
    [InlineData(0x2C)] // Print Screen
    [InlineData(0x2D)] // Insert
    [InlineData(0x2E)] // Delete
    [InlineData(0x5B)] // Left Windows
    public void NavigationAndWindowsKeysCarryTheExtendedFlag(int virtualKey)
    {
        Assert.True(VirtualKeys.IsExtended(virtualKey));
    }

    [Theory]
    [InlineData(0x08)] // Backspace
    [InlineData(0x09)] // Tab
    [InlineData(0x0D)] // Enter
    [InlineData(0x10)] // Shift
    [InlineData(0x11)] // Ctrl
    [InlineData(0x12)] // Alt
    [InlineData(0x1B)] // Escape
    [InlineData(0x20)] // Space
    [InlineData(0x41)] // A
    [InlineData(0x30)] // 0
    [InlineData(0x70)] // F1
    public void OrdinaryKeysDoNot(int virtualKey)
    {
        Assert.False(VirtualKeys.IsExtended(virtualKey));
    }

    [Fact]
    public void EveryKeyThePhoneUsesIsInjectable()
    {
        // The list from the contract.
        int[] keys =
        [
            8, 9, 13, 16, 17, 18, 27, 32, 33, 34, 35, 36, 37, 38, 39, 40, 44, 45, 46, 91,
            .. Enumerable.Range(48, 10), .. Enumerable.Range(65, 26), .. Enumerable.Range(112, 12),
        ];

        Assert.All(keys, key => Assert.True(VirtualKeys.IsInjectable(key), $"VK {key} was refused."));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(255)]
    [InlineData(70_000)]
    [InlineData(1)] // VK_LBUTTON: a mouse button, not a key.
    [InlineData(2)] // VK_RBUTTON
    [InlineData(4)] // VK_MBUTTON
    public void CodesOutsideTheKeyboardRangeAreNotInjectable(int virtualKey)
    {
        Assert.False(VirtualKeys.IsInjectable(virtualKey));
    }
}
