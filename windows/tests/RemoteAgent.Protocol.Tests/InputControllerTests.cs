using Microsoft.Extensions.Logging.Abstractions;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Input;
using RemoteAgent.Protocol.Messages;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Tests for the input controller: validation, monitor targeting, batching, and the dead-man
/// release.
/// </summary>
/// <remarks>
/// Nothing here injects anything. The controller is driven against <see cref="FakeInjector"/>,
/// which records the batches it would have sent — these tests run on a developer's live desktop,
/// and a test that moved the real pointer or pressed a real key would be a bug in the test.
/// </remarks>
public sealed class InputControllerTests
{
    private const string Phone = "conn-phone";
    private const string Tablet = "conn-tablet";

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkA = 0x41;
    private const int VkC = 0x43;

    /// <summary>A secondary monitor to the left of the primary, both listed primary-first.</summary>
    private static readonly MonitorInfo PrimaryMonitor = new()
    {
        Id = "mon-primary", X = 0, Y = 0, Width = 1920, Height = 1080, Primary = true,
    };

    private static readonly MonitorInfo LeftMonitor = new()
    {
        Id = "mon-left", X = -2560, Y = 0, Width = 2560, Height = 1440,
    };

    private static readonly PixelRect Desktop = new(-2560, 0, 4480, 1440);

    private sealed class Rig
    {
        public Rig()
        {
            Monitors = new FakeMonitors([PrimaryMonitor, LeftMonitor]);
            Controller = new InputController(
                Injector,
                Monitors,
                (connection, _) => Streaming.TryGetValue(connection, out string? id) ? id : null,
                NullLogger<InputController>.Instance,
                () => Now);
        }

        public FakeInjector Injector { get; } = new() { VirtualScreen = Desktop };

        public FakeMonitors Monitors { get; }

        public InputController Controller { get; }

        /// <summary>Which monitor each connection is streaming.</summary>
        public Dictionary<string, string> Streaming { get; } = [];

        public long Now { get; set; } = 1_000_000;

        /// <summary>Where an absolute move in the last batch would put the pointer.</summary>
        public (int X, int Y) LandingPixel()
        {
            InputEvent move = Injector.Sent.Last().First(e => e.Kind == InputEventKind.MouseMoveAbsolute);

            return (InputCoordinates.AbsoluteToPixel(move.X, Desktop.X, Desktop.Width),
                    InputCoordinates.AbsoluteToPixel(move.Y, Desktop.Y, Desktop.Height));
        }
    }

    // ---------------------------------------------------------------------
    // Pointer
    // ---------------------------------------------------------------------

    [Fact]
    public void AbsoluteMoveTargetsTheNamedMonitor()
    {
        var rig = new Rig();

        InputResult result = rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0, Y = 0, MonitorId = "mon-left" });

        Assert.True(result.Succeeded);
        Assert.Equal((-2560, 0), rig.LandingPixel());

        rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 1, Y = 1, MonitorId = "mon-left" });
        Assert.Equal((-1, 1439), rig.LandingPixel());
    }

    [Fact]
    public void AbsoluteMoveWithoutAMonitorUsesTheOneTheConnectionIsStreaming()
    {
        var rig = new Rig();
        rig.Streaming[Phone] = "mon-left";

        rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 1, Y = 1 });
        Assert.Equal((-1, 1439), rig.LandingPixel());

        // Another connection, streaming nothing, falls back to the primary monitor.
        rig.Controller.MouseMove(Tablet, new MouseMoveArgs { X = 1, Y = 1 });
        Assert.Equal((1919, 1079), rig.LandingPixel());
    }

    [Fact]
    public void AnUnknownMonitorIsNotFoundRatherThanGuessed()
    {
        var rig = new Rig();

        InputResult result = rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.5, Y = 0.5, MonitorId = "mon-gone" });

        Assert.Equal(InputStatus.MonitorNotFound, result.Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void OutOfRangeCoordinatesAreClampedOntoTheMonitor()
    {
        var rig = new Rig();

        rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 7.5, Y = -3, MonitorId = "mon-primary" });

        Assert.Equal((1919, 0), rig.LandingPixel());
    }

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, double.NaN)]
    public void NonFiniteCoordinatesAreRejected(double x, double y)
    {
        var rig = new Rig();

        InputResult result = rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = x, Y = y });

        Assert.Equal(InputStatus.InvalidArguments, result.Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void HalfAPointIsRejected()
    {
        var rig = new Rig();

        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.5 }).Status);
        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.MouseMove(Phone, new MouseMoveArgs()).Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void RelativeMoveIsInPixelsFromTheCurrentPosition()
    {
        var rig = new Rig();
        rig.Injector.Cursor = (100, 200);

        Assert.True(rig.Controller.MouseMove(Phone, new MouseMoveArgs { Dx = 15, Dy = -40 }).Succeeded);
        Assert.Equal((115, 160), rig.LandingPixel());

        // Crossing onto the left monitor: negative X is ordinary.
        rig.Controller.MouseMove(Phone, new MouseMoveArgs { Dx = -500 });
        Assert.Equal((-400, 200), rig.LandingPixel());
    }

    [Fact]
    public void RelativeMoveStopsAtTheEdgeOfTheDesktop()
    {
        var rig = new Rig();
        rig.Injector.Cursor = (1900, 10);

        rig.Controller.MouseMove(Phone, new MouseMoveArgs { Dx = 9000, Dy = -9000 });

        Assert.Equal((1919, 0), rig.LandingPixel());
    }

    [Fact]
    public void AZeroRelativeMoveSendsNothing()
    {
        var rig = new Rig();

        Assert.True(rig.Controller.MouseMove(Phone, new MouseMoveArgs { Dx = 0, Dy = 0 }).Succeeded);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void AClickIsOneAtomicBatchOfMoveDownUp()
    {
        var rig = new Rig();

        InputResult result = rig.Controller.MouseClick(
            Phone,
            new MouseClickArgs { Button = "right", X = 0.5, Y = 0.5, MonitorId = "mon-primary" });

        Assert.True(result.Succeeded);

        InputEvent[] batch = Assert.Single(rig.Injector.Sent);
        Assert.Equal(
            [InputEventKind.MouseMoveAbsolute, InputEventKind.MouseButton, InputEventKind.MouseButton],
            batch.Select(e => e.Kind));

        Assert.Equal(MouseButton.Right, batch[1].Button);
        Assert.True(batch[1].Down);
        Assert.False(batch[2].Down);
    }

    [Fact]
    public void ADoubleClickIsTwoPressesInOneBatch()
    {
        var rig = new Rig();

        rig.Controller.MouseClick(Phone, new MouseClickArgs { Button = "left", Count = 2 });

        InputEvent[] batch = Assert.Single(rig.Injector.Sent);
        Assert.Equal([true, false, true, false], batch.Select(e => e.Down));
        Assert.All(batch, e => Assert.Equal(MouseButton.Left, e.Button));
    }

    [Theory]
    [InlineData("left", 0)]
    [InlineData("left", 3)]
    [InlineData("LEFTISH", 1)]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    public void InvalidClicksAreRejected(string? button, int count)
    {
        var rig = new Rig();

        InputResult result = rig.Controller.MouseClick(Phone, new MouseClickArgs { Button = button, Count = count });

        Assert.Equal(InputStatus.InvalidArguments, result.Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void AButtonChangeNeedsAnExplicitDirection()
    {
        // "down" missing must not be read as false, i.e. as a release nobody asked for.
        var rig = new Rig();

        InputResult result = rig.Controller.MouseButtonChange(Phone, new MouseButtonArgs { Button = "left" });

        Assert.Equal(InputStatus.InvalidArguments, result.Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void ScrollUsesWindowsSignsAndMovesFirstWhenAsked()
    {
        var rig = new Rig();

        rig.Controller.Scroll(Phone, new ScrollArgs { Dx = 120, Dy = -240, X = 0, Y = 0, MonitorId = "mon-primary" });

        InputEvent[] batch = Assert.Single(rig.Injector.Sent);
        Assert.Equal(InputEventKind.MouseMoveAbsolute, batch[0].Kind);

        // dy > 0 is up and dx > 0 is right on the wire and in Windows alike: passed through as is.
        Assert.Equal((InputEventKind.WheelVertical, -240), (batch[1].Kind, batch[1].X));
        Assert.Equal((InputEventKind.WheelHorizontal, 120), (batch[2].Kind, batch[2].X));
    }

    [Fact]
    public void ScrollDeltasAreBoundedAndZeroSendsNothing()
    {
        var rig = new Rig();

        Assert.True(rig.Controller.Scroll(Phone, new ScrollArgs()).Succeeded);
        Assert.Empty(rig.Injector.Sent);

        rig.Controller.Scroll(Phone, new ScrollArgs { Dy = int.MaxValue });
        Assert.Equal(InputController.MaxWheelDelta, Assert.Single(rig.Injector.Sent)[0].X);
    }

    // ---------------------------------------------------------------------
    // Keyboard
    // ---------------------------------------------------------------------

    [Fact]
    public void AKeyPressAndReleaseAreSentAsGiven()
    {
        var rig = new Rig();

        Assert.True(rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true }).Succeeded);
        Assert.True(rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = false }).Succeeded);

        Assert.Equal(2, rig.Injector.Sent.Count);
        Assert.Equal(InputEvent.KeyChange(VkA, true), rig.Injector.Sent[0][0]);
        Assert.Equal(InputEvent.KeyChange(VkA, false), rig.Injector.Sent[1][0]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(255, true)]
    [InlineData(-5, true)]
    [InlineData(1, true)] // A mouse button code is not a key.
    public void InvalidKeysAreRejected(int virtualKey, bool down)
    {
        var rig = new Rig();

        Assert.Equal(
            InputStatus.InvalidArguments,
            rig.Controller.Key(Phone, new KeyArgs { VirtualKey = virtualKey, Down = down }).Status);

        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void AKeyNeedsBothACodeAndADirection()
    {
        var rig = new Rig();

        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.Key(Phone, new KeyArgs { Down = true }).Status);
        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA }).Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void AShortcutPressesInOrderAndReleasesInReverseInOneBatch()
    {
        var rig = new Rig();

        Assert.True(rig.Controller.Shortcut(Phone, new ShortcutArgs { Keys = [VkControl, VkShift, VkC] }).Succeeded);

        InputEvent[] batch = Assert.Single(rig.Injector.Sent);
        Assert.Equal(
            [
                (VkControl, true), (VkShift, true), (VkC, true),
                (VkC, false), (VkShift, false), (VkControl, false),
            ],
            batch.Select(e => (e.X, e.Down)));
    }

    [Fact]
    public void ShortcutsAreBoundedAndValidated()
    {
        var rig = new Rig();

        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.Shortcut(Phone, new ShortcutArgs()).Status);
        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.Shortcut(Phone, new ShortcutArgs { Keys = [] }).Status);
        Assert.Equal(
            InputStatus.InvalidArguments,
            rig.Controller.Shortcut(Phone, new ShortcutArgs { Keys = [VkA, VkA, VkA, VkA, VkA, VkA, VkA] }).Status);
        Assert.Equal(
            InputStatus.InvalidArguments,
            rig.Controller.Shortcut(Phone, new ShortcutArgs { Keys = [VkControl, 999] }).Status);

        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void TextIsTypedAsUnicodeWithEnterAndTabAsRealKeys()
    {
        List<InputEvent> events = InputController.BuildTextEvents("a\tb\nc");

        Assert.Equal(
            [
                InputEvent.UnicodeChange('a', true), InputEvent.UnicodeChange('a', false),
                InputEvent.KeyChange(VirtualKeys.Tab, true), InputEvent.KeyChange(VirtualKeys.Tab, false),
                InputEvent.UnicodeChange('b', true), InputEvent.UnicodeChange('b', false),
                InputEvent.KeyChange(VirtualKeys.Return, true), InputEvent.KeyChange(VirtualKeys.Return, false),
                InputEvent.UnicodeChange('c', true), InputEvent.UnicodeChange('c', false),
            ],
            events);
    }

    [Fact]
    public void ASurrogatePairIsSentAsItsTwoUnitsInOrder()
    {
        // U+1F600, outside the Basic Multilingual Plane: two UTF-16 units.
        const string emoji = "\U0001F600";
        Assert.Equal(2, emoji.Length);

        List<InputEvent> events = InputController.BuildTextEvents(emoji);

        Assert.Equal(
            [
                InputEvent.UnicodeChange(emoji[0], true), InputEvent.UnicodeChange(emoji[0], false),
                InputEvent.UnicodeChange(emoji[1], true), InputEvent.UnicodeChange(emoji[1], false),
            ],
            events);

        Assert.True(char.IsHighSurrogate((char)events[0].X));
        Assert.True(char.IsLowSurrogate((char)events[2].X));
    }

    [Fact]
    public void WindowsLineEndingsAreOneEnterAndOtherControlCharactersAreDropped()
    {
        List<InputEvent> events = InputController.BuildTextEvents("x\r\ny\u0007\u0000z\r");

        // x, Enter, y, z, Enter: five keystrokes, ten events.
        Assert.Equal(10, events.Count);
        Assert.Equal(2, events.Count(e => e is { Kind: InputEventKind.Key, Down: true } && e.X == VirtualKeys.Return));
        Assert.DoesNotContain(events, e => e.Kind == InputEventKind.Unicode && e.X < 0x20);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyTextIsRejected(string? text)
    {
        var rig = new Rig();

        Assert.Equal(InputStatus.InvalidArguments, rig.Controller.Text(Phone, new TextArgs { Text = text }).Status);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void TextLongerThanTheLimitIsRejectedAndTheLimitItselfIsAccepted()
    {
        var rig = new Rig();

        Assert.Equal(
            InputStatus.InvalidArguments,
            rig.Controller.Text(Phone, new TextArgs { Text = new string('a', InputController.MaxTextLength + 1) }).Status);
        Assert.Empty(rig.Injector.Sent);

        Assert.True(rig.Controller.Text(Phone, new TextArgs { Text = new string('a', InputController.MaxTextLength) }).Succeeded);

        // Every character arrived, split over several batches, none of which cuts a key in half.
        Assert.True(rig.Injector.Sent.Count > 1);
        Assert.Equal(InputController.MaxTextLength * 2, rig.Injector.Sent.Sum(batch => batch.Length));
        Assert.All(rig.Injector.Sent, batch => Assert.Equal(0, batch.Length % 2));
    }

    // ---------------------------------------------------------------------
    // Held keys and the dead-man release
    // ---------------------------------------------------------------------

    [Fact]
    public void HeldKeysAndButtonsAreTrackedPerConnection()
    {
        var rig = new Rig();

        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkShift, Down = true });
        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true });
        rig.Controller.MouseButtonChange(Phone, new MouseButtonArgs { Button = "left", Down = true });
        rig.Controller.Key(Tablet, new KeyArgs { VirtualKey = VkControl, Down = true });

        HeldInput phone = rig.Controller.GetHeld(Phone);
        Assert.Equal([VkA, VkShift], phone.Keys); // Most recent first: the release order.
        Assert.Equal([MouseButton.Left], phone.Buttons);

        Assert.Equal([VkControl], rig.Controller.GetHeld(Tablet).Keys);

        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = false });
        Assert.Equal([VkShift], rig.Controller.GetHeld(Phone).Keys);
    }

    [Fact]
    public void KeyRepeatDoesNotCountAsASecondHeldKey()
    {
        var rig = new Rig();

        for (int i = 0; i < 5; i++)
        {
            rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true });
        }

        Assert.Equal([VkA], rig.Controller.GetHeld(Phone).Keys);
    }

    [Fact]
    public void ReleaseAllReleasesOnlyTheCallersKeysAndButtons()
    {
        var rig = new Rig();
        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkShift, Down = true });
        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true });
        rig.Controller.MouseButtonChange(Phone, new MouseButtonArgs { Button = "middle", Down = true });
        rig.Controller.Key(Tablet, new KeyArgs { VirtualKey = VkControl, Down = true });
        rig.Injector.Sent.Clear();

        Assert.True(rig.Controller.ReleaseAll(Phone).Succeeded);

        InputEvent[] batch = Assert.Single(rig.Injector.Sent);
        Assert.All(batch, e => Assert.False(e.Down));
        Assert.Contains(InputEvent.ButtonChange(MouseButton.Middle, false), batch);
        Assert.Contains(InputEvent.KeyChange(VkA, false), batch);
        Assert.Contains(InputEvent.KeyChange(VkShift, false), batch);
        Assert.DoesNotContain(InputEvent.KeyChange(VkControl, false), batch);

        // The letter is released before the modifier.
        Assert.True(Array.IndexOf(batch, InputEvent.KeyChange(VkA, false)) <
                    Array.IndexOf(batch, InputEvent.KeyChange(VkShift, false)));

        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);
        Assert.Equal([VkControl], rig.Controller.GetHeld(Tablet).Keys);
    }

    [Fact]
    public void ReleaseAllWithNothingHeldSendsNothingAndSucceeds()
    {
        var rig = new Rig();

        Assert.True(rig.Controller.ReleaseAll(Phone).Succeeded);
        Assert.Empty(rig.Injector.Sent);
    }

    [Fact]
    public void AClosingConnectionHasItsHeldInputReleased()
    {
        // The case the mechanism exists for: a phone that vanished mid-drag.
        var rig = new Rig();
        rig.Controller.MouseButtonChange(Phone, new MouseButtonArgs { Button = "left", Down = true, X = 0.1, Y = 0.1 });
        rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.2, Y = 0.2 });
        rig.Injector.Sent.Clear();

        rig.Controller.ReleaseConnection(Phone, "the control connection closed");

        InputEvent[] batch = Assert.Single(rig.Injector.Sent);
        Assert.Equal([InputEvent.ButtonChange(MouseButton.Left, false)], batch);
        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);

        // A second notice for the same connection is a no-op, not a second release.
        rig.Controller.ReleaseConnection(Phone, "the control connection closed");
        Assert.Single(rig.Injector.Sent);
    }

    [Fact]
    public void AClickOrShortcutLeavesNothingHeld()
    {
        var rig = new Rig();

        rig.Controller.MouseButtonChange(Phone, new MouseButtonArgs { Button = "left", Down = true });
        rig.Controller.MouseClick(Phone, new MouseClickArgs { Button = "left" });
        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);

        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkControl, Down = true });
        rig.Controller.Shortcut(Phone, new ShortcutArgs { Keys = [VkControl, VkC] });
        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);
    }

    [Fact]
    public void LockingTheSessionReleasesEverythingAndRefusesInputUntilUnlock()
    {
        var rig = new Rig();
        rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkShift, Down = true });
        rig.Controller.MouseButtonChange(Tablet, new MouseButtonArgs { Button = "right", Down = true });
        rig.Injector.Sent.Clear();

        rig.Injector.DesktopState = InputDesktopState.Secure;
        rig.Controller.OnSessionLocked();

        InputEvent[] release = Assert.Single(rig.Injector.Sent);
        Assert.Contains(InputEvent.KeyChange(VkShift, false), release);
        Assert.Contains(InputEvent.ButtonChange(MouseButton.Right, false), release);
        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);
        Assert.True(rig.Controller.GetHeld(Tablet).IsEmpty);

        // Locked: every command reports it, and nothing is sent.
        rig.Injector.Sent.Clear();
        Assert.Equal(InputStatus.DesktopLocked, rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.5, Y = 0.5 }).Status);
        Assert.Equal(InputStatus.DesktopLocked, rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true }).Status);
        Assert.Equal(InputStatus.DesktopLocked, rig.Controller.Text(Phone, new TextArgs { Text = "password" }).Status);
        Assert.Empty(rig.Injector.Sent);
        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);

        // release_all still succeeds while locked: its postcondition already holds.
        Assert.True(rig.Controller.ReleaseAll(Phone).Succeeded);

        // Unlock: the keys are released once more, in case the first release went to a desktop
        // that was already gone, and input works again.
        rig.Injector.DesktopState = InputDesktopState.Normal;
        rig.Controller.OnSessionUnlocked();

        InputEvent[] again = Assert.Single(rig.Injector.Sent);
        Assert.Contains(InputEvent.KeyChange(VkShift, false), again);
        Assert.All(again, e => Assert.False(e.Down));

        Assert.True(rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.5, Y = 0.5 }).Succeeded);
    }

    [Fact]
    public void AMissedUnlockNoticeDoesNotLockInputOutForever()
    {
        var rig = new Rig();
        rig.Injector.DesktopState = InputDesktopState.Secure;
        rig.Controller.OnSessionLocked();

        // The user unlocked, but the notification never arrived. Windows says the user's own
        // desktop has input again.
        rig.Injector.DesktopState = InputDesktopState.Normal;

        // Too soon to conclude anything: a lock can be announced a moment before the switch.
        rig.Now += 1_000;
        Assert.Equal(InputStatus.DesktopLocked, rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0, Y = 0 }).Status);

        rig.Now += 10_000;
        Assert.True(rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0, Y = 0 }).Succeeded);
    }

    [Fact]
    public void TheSecureDesktopIsReportedAsSuchAndNothingIsSent()
    {
        var rig = new Rig { Injector = { DesktopState = InputDesktopState.Secure } };

        InputResult result = rig.Controller.MouseClick(Phone, new MouseClickArgs { Button = "left" });

        Assert.Equal(InputStatus.SecureDesktop, result.Status);
        Assert.Empty(rig.Injector.Sent);

        // The prompt is dismissed; after the short cache expires input works again.
        rig.Injector.DesktopState = InputDesktopState.Normal;
        rig.Now += 1_000;
        Assert.True(rig.Controller.MouseClick(Phone, new MouseClickArgs { Button = "left" }).Succeeded);
    }

    [Fact]
    public void ARefusedInjectionIsExplainedAndNotRecordedAsHeld()
    {
        var rig = new Rig { Injector = { Refuse = true } };

        InputResult plain = rig.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true });
        Assert.Equal(InputStatus.Blocked, plain.Status);
        Assert.True(rig.Controller.GetHeld(Phone).IsEmpty);

        // If the reason turns out to be a prompt that appeared between the check and the send —
        // the pre-check saw the normal desktop, the send failed, the second look sees the secure
        // one — say so, rather than the generic refusal.
        var late = new Rig { Injector = { Refuse = true, DesktopStateAfterFirstRead = InputDesktopState.Secure } };
        Assert.Equal(InputStatus.SecureDesktop, late.Controller.Key(Phone, new KeyArgs { VirtualKey = VkA, Down = true }).Status);
    }

    [Fact]
    public void TheMonitorListIsCachedOnTheHotPathAndRefreshedWhenAsked()
    {
        var rig = new Rig();

        for (int i = 0; i < 120; i++)
        {
            rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = i / 120.0, Y = 0.5 });
        }

        Assert.Equal(1, rig.Monitors.Enumerations);

        rig.Controller.InvalidateMonitors();
        rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.5, Y = 0.5 });
        Assert.Equal(2, rig.Monitors.Enumerations);

        // And it expires by itself, so a hot-plug with no notification is still noticed.
        rig.Now += 60_000;
        rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 0.5, Y = 0.5 });
        Assert.Equal(3, rig.Monitors.Enumerations);
    }

    [Fact]
    public void WithNoEnumerableMonitorAPointRefersToTheWholeDesktop()
    {
        var rig = new Rig();
        rig.Monitors.Monitors = [];

        Assert.True(rig.Controller.MouseMove(Phone, new MouseMoveArgs { X = 1, Y = 1 }).Succeeded);
        Assert.Equal((1919, 1439), rig.LandingPixel());
    }

    // ---------------------------------------------------------------------
    // The tracker on its own
    // ---------------------------------------------------------------------

    [Fact]
    public void TakeAllEmptiesTheTracker()
    {
        var tracker = new HeldInputTracker();
        tracker.SetKey(Phone, VkA, true);
        tracker.SetButton(Tablet, MouseButton.Left, true);
        Assert.False(tracker.IsEmpty);

        IReadOnlyList<(string ConnectionId, HeldInput Held)> all = tracker.TakeAll();

        Assert.Equal(2, all.Count);
        Assert.True(tracker.IsEmpty);
        Assert.True(tracker.Take(Phone).IsEmpty);
    }

    [Fact]
    public void ReleasingSomethingNeverHeldIsHarmless()
    {
        var tracker = new HeldInputTracker();

        tracker.SetKey(Phone, VkA, false);
        tracker.SetButton(Phone, MouseButton.Right, false);

        Assert.True(tracker.IsEmpty);
    }

    // ---------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------

    /// <summary>Records what would have been injected. Injects nothing.</summary>
    private sealed class FakeInjector : IInputInjector
    {
        private bool _stateRead;

        public List<InputEvent[]> Sent { get; } = [];

        public PixelRect VirtualScreen { get; set; }

        public (int X, int Y)? Cursor { get; set; } = (0, 0);

        public InputDesktopState DesktopState { get; set; } = InputDesktopState.Normal;

        /// <summary>When set, the state reported from the second read onwards.</summary>
        public InputDesktopState? DesktopStateAfterFirstRead { get; set; }

        public bool Refuse { get; set; }

        public PixelRect GetVirtualScreen() => VirtualScreen;

        public (int X, int Y)? GetCursorPosition() => Cursor;

        public InputDesktopState GetDesktopState()
        {
            if (_stateRead && DesktopStateAfterFirstRead is { } later)
            {
                return later;
            }

            _stateRead = true;
            return DesktopState;
        }

        public bool Send(ReadOnlySpan<InputEvent> events)
        {
            if (Refuse)
            {
                return false;
            }

            Sent.Add(events.ToArray());
            return true;
        }
    }

    private sealed class FakeMonitors(IReadOnlyList<MonitorInfo> monitors) : IMonitorProvider
    {
        public IReadOnlyList<MonitorInfo> Monitors { get; set; } = monitors;

        public int Enumerations { get; private set; }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            Enumerations++;
            return Monitors;
        }
    }
}
