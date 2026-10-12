using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteAgent.Core.Abstractions;
using RemoteAgent.Protocol.Messages;

namespace RemoteAgent.Core.Input;

/// <summary>Why an input command did or did not happen.</summary>
public enum InputStatus
{
    /// <summary>The input was injected (or there was nothing to inject).</summary>
    Ok,

    /// <summary>The arguments failed validation.</summary>
    InvalidArguments,

    /// <summary>The named monitor is not attached.</summary>
    MonitorNotFound,

    /// <summary>The workstation is locked.</summary>
    DesktopLocked,

    /// <summary>The secure desktop (a UAC prompt, Ctrl+Alt+Del) owns input.</summary>
    SecureDesktop,

    /// <summary>Windows refused the input for another reason.</summary>
    Blocked,
}

/// <summary>The outcome of one input command.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">A message for the client when it did not succeed.</param>
public readonly record struct InputResult(InputStatus Status, string? Detail = null)
{
    /// <summary>Success.</summary>
    public static InputResult Ok { get; } = new(InputStatus.Ok);

    /// <summary>Whether the command succeeded.</summary>
    public bool Succeeded => Status == InputStatus.Ok;

    /// <summary>A validation failure.</summary>
    public static InputResult Invalid(string detail) => new(InputStatus.InvalidArguments, detail);
}

/// <summary>
/// Turns the eight <c>input.*</c> commands into injected events (§4).
/// </summary>
/// <remarks>
/// <para>Everything that can be decided without Windows is decided here: argument validation,
/// which monitor a point belongs to, the coordinate conversion, what each connection is holding,
/// and when to let go of it. The platform's <see cref="IInputInjector"/> receives only finished
/// batches, so this class is exercised in full by unit tests with a fake injector and the real
/// desktop never sees a test.</para>
///
/// <para><b>Dead-man release.</b> Whatever a connection presses through <c>input.key</c> or
/// <c>input.mouse_button</c> is remembered, and released when that connection asks
/// (<see cref="ReleaseAll"/>), when it closes or loses <c>ControlInput</c>
/// (<see cref="ReleaseConnection"/>), and when the session locks
/// (<see cref="OnSessionLocked"/>). A phone that drops off Wi-Fi mid-drag must not leave the PC
/// with a held mouse button.</para>
///
/// <para><b>Hot path.</b> A dragging finger produces around sixty moves a second. An absolute move
/// costs one monitor lookup from a short-lived cache, a few integer operations and one
/// <c>SendInput</c>; nothing on this path allocates more than the event batch, and nothing logs.
/// The monitor list is cached because enumerating it walks DXGI, which is far too slow to do sixty
/// times a second.</para>
///
/// <para><b>Nothing typed is ever logged.</b> No method here passes text, a key code or a
/// coordinate to the logger at any level — only counts (§7.5).</para>
/// </remarks>
public sealed class InputController
{
    /// <summary>Longest text accepted by <c>input.text</c>, in UTF-16 units.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>Most keys accepted by <c>input.shortcut</c>.</summary>
    public const int MaxShortcutKeys = 6;

    /// <summary>Largest wheel turn accepted in one command: a hundred notches.</summary>
    public const int MaxWheelDelta = 120 * 100;

    /// <summary>Largest relative move accepted in one command, in pixels.</summary>
    public const int MaxRelativeMove = 16384;

    private const long MonitorCacheMs = 2_000;
    private const long DesktopStateCacheMs = 250;
    private const long UnknownMonitorRefreshMs = 250;
    private const long LockSelfHealMs = 5_000;
    private const int TextBatchEvents = 256;

    private readonly IInputInjector _injector;
    private readonly IMonitorProvider _monitors;
    private readonly Func<string, IReadOnlyList<MonitorInfo>, string?> _selectedMonitor;
    private readonly ILogger<InputController> _logger;
    private readonly Func<long> _nowMs;
    private readonly HeldInputTracker _held = new();
    private readonly Lock _gate = new();

    private IReadOnlyList<MonitorInfo>? _monitorCache;
    private long _monitorCacheAt;
    private InputDesktopState _desktopState;
    private long _desktopStateAt = long.MinValue / 2;
    private bool _locked;
    private long _lockedAt;
    private HeldInput _releaseAgainOnUnlock = HeldInput.None;

    /// <summary>Creates the controller.</summary>
    /// <param name="injector">The platform's event injector.</param>
    /// <param name="monitors">Monitor enumeration, for mapping a point onto a monitor.</param>
    /// <param name="selectedMonitor">
    /// The monitor a connection is streaming (or would stream), given the current monitor list:
    /// the default target when a command names no monitor.
    /// </param>
    /// <param name="logger">Diagnostics. Receives counts only, never keys or text.</param>
    /// <param name="nowMs">A monotonic millisecond clock. Defaults to the system tick count.</param>
    public InputController(
        IInputInjector injector,
        IMonitorProvider monitors,
        Func<string, IReadOnlyList<MonitorInfo>, string?> selectedMonitor,
        ILogger<InputController> logger,
        Func<long>? nowMs = null)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _selectedMonitor = selectedMonitor ?? throw new ArgumentNullException(nameof(selectedMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nowMs = nowMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>What a connection is currently holding. For diagnostics and tests.</summary>
    public HeldInput GetHeld(string connectionId) => _held.Peek(connectionId);

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    /// <summary><c>input.mouse_move</c>: absolute when x and y are given, otherwise relative.</summary>
    public InputResult MouseMove(string connectionId, MouseMoveArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            if (args.X is not null || args.Y is not null)
            {
                if (!TryResolvePoint(connectionId, args.X, args.Y, args.MonitorId, out InputEvent? move, out InputResult failure))
                {
                    return failure;
                }

                return Send([move!.Value]);
            }

            if (args.Dx is null && args.Dy is null)
            {
                return InputResult.Invalid("Either x and y, or dx and dy, are required.");
            }

            int dx = Math.Clamp(args.Dx ?? 0, -MaxRelativeMove, MaxRelativeMove);
            int dy = Math.Clamp(args.Dy ?? 0, -MaxRelativeMove, MaxRelativeMove);
            if (dx == 0 && dy == 0)
            {
                return InputResult.Ok;
            }

            // A relative move is computed here and sent as an absolute one. Windows' own relative
            // moves are in mickeys, bent by the pointer-speed slider and by "enhance pointer
            // precision", so the same swipe would travel a different distance on every PC. The
            // contract says pixels, and this makes it pixels.
            if (_injector.GetCursorPosition() is not { } cursor)
            {
                return Refused();
            }

            PixelRect screen = _injector.GetVirtualScreen();
            (int x, int y) = InputCoordinates.ClampToScreen((long)cursor.X + dx, (long)cursor.Y + dy, screen);
            (int ax, int ay) = InputCoordinates.PixelToAbsolute(x, y, screen);
            return Send([InputEvent.MoveTo(ax, ay)]);
        }
    }

    /// <summary><c>input.mouse_button</c>: press or release one button, moving first if asked.</summary>
    public InputResult MouseButtonChange(string connectionId, MouseButtonArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            if (!TryParseButton(args.Button, out MouseButton button))
            {
                return InputResult.Invalid("button must be \"left\", \"right\" or \"middle\".");
            }

            if (args.Down is not { } down)
            {
                return InputResult.Invalid("down is required.");
            }

            if (!TryResolvePoint(connectionId, args.X, args.Y, args.MonitorId, out InputEvent? move, out InputResult failure))
            {
                return failure;
            }

            var events = new List<InputEvent>(2);
            if (move is { } target)
            {
                events.Add(target);
            }

            events.Add(InputEvent.ButtonChange(button, down));

            InputResult result = Send(CollectionsMarshal.AsSpan(events));
            if (result.Succeeded)
            {
                _held.SetButton(connectionId, button, down);
            }

            return result;
        }
    }

    /// <summary><c>input.mouse_click</c>: a complete click or double click, as one atomic batch.</summary>
    public InputResult MouseClick(string connectionId, MouseClickArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            if (!TryParseButton(args.Button, out MouseButton button))
            {
                return InputResult.Invalid("button must be \"left\", \"right\" or \"middle\".");
            }

            int count = args.Count ?? 1;
            if (count is not (1 or 2))
            {
                return InputResult.Invalid("count must be 1 or 2.");
            }

            if (!TryResolvePoint(connectionId, args.X, args.Y, args.MonitorId, out InputEvent? move, out InputResult failure))
            {
                return failure;
            }

            var events = new List<InputEvent>(5);
            if (move is { } target)
            {
                events.Add(target);
            }

            for (int i = 0; i < count; i++)
            {
                events.Add(InputEvent.ButtonChange(button, true));
                events.Add(InputEvent.ButtonChange(button, false));
            }

            InputResult result = Send(CollectionsMarshal.AsSpan(events));
            if (result.Succeeded)
            {
                // A click ends with the button up, whatever a half-finished drag left behind.
                _held.SetButton(connectionId, button, false);
            }

            return result;
        }
    }

    /// <summary><c>input.scroll</c>: turn the wheel, moving first if asked.</summary>
    public InputResult Scroll(string connectionId, ScrollArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            if (!TryResolvePoint(connectionId, args.X, args.Y, args.MonitorId, out InputEvent? move, out InputResult failure))
            {
                return failure;
            }

            int dx = Math.Clamp(args.Dx ?? 0, -MaxWheelDelta, MaxWheelDelta);
            int dy = Math.Clamp(args.Dy ?? 0, -MaxWheelDelta, MaxWheelDelta);

            var events = new List<InputEvent>(3);
            if (move is { } target)
            {
                events.Add(target);
            }

            // The wire convention is Windows' own: positive dy is away from the user (up), positive
            // dx is to the right. No sign is flipped here.
            if (dy != 0)
            {
                events.Add(InputEvent.Wheel(dy));
            }

            if (dx != 0)
            {
                events.Add(InputEvent.HorizontalWheel(dx));
            }

            return events.Count == 0 ? InputResult.Ok : Send(CollectionsMarshal.AsSpan(events));
        }
    }

    /// <summary><c>input.key</c>: press or release one virtual key.</summary>
    public InputResult Key(string connectionId, KeyArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            if (args.VirtualKey is not { } virtualKey || !VirtualKeys.IsInjectable(virtualKey))
            {
                return InputResult.Invalid("vk must be a keyboard virtual-key code between 1 and 254.");
            }

            if (args.Down is not { } down)
            {
                return InputResult.Invalid("down is required.");
            }

            InputResult result = Send([InputEvent.KeyChange(virtualKey, down)]);
            if (result.Succeeded)
            {
                _held.SetKey(connectionId, virtualKey, down);
            }

            return result;
        }
    }

    /// <summary><c>input.text</c>: type a string as Unicode.</summary>
    public InputResult Text(string connectionId, TextArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            if (string.IsNullOrEmpty(args.Text) || args.Text.Length > MaxTextLength)
            {
                return InputResult.Invalid($"text must be 1 to {MaxTextLength} characters.");
            }

            List<InputEvent> events = BuildTextEvents(args.Text);

            // Sent in modest batches: one SendInput call holding thousands of events blocks the
            // user's own input for its whole duration, and some applications drop characters when
            // their queue is flooded in a single burst.
            for (int offset = 0; offset < events.Count; offset += TextBatchEvents)
            {
                // A key-down and its key-up are adjacent, and the batch size is even, so a batch
                // boundary never falls between the two halves of one character.
                int length = Math.Min(TextBatchEvents, events.Count - offset);
                InputResult result = Send(CollectionsMarshal.AsSpan(events).Slice(offset, length));
                if (!result.Succeeded)
                {
                    return result;
                }
            }

            return InputResult.Ok;
        }
    }

    /// <summary><c>input.shortcut</c>: press keys in order, release them in reverse, atomically.</summary>
    public InputResult Shortcut(string connectionId, ShortcutArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        lock (_gate)
        {
            if (Guard() is { } refused)
            {
                return refused;
            }

            int[]? keys = args.Keys;
            if (keys is null || keys.Length is < 1 or > MaxShortcutKeys)
            {
                return InputResult.Invalid($"keys must hold 1 to {MaxShortcutKeys} virtual-key codes.");
            }

            foreach (int key in keys)
            {
                if (!VirtualKeys.IsInjectable(key))
                {
                    return InputResult.Invalid("Every key must be a keyboard virtual-key code between 1 and 254.");
                }
            }

            var events = new List<InputEvent>(keys.Length * 2);
            foreach (int key in keys)
            {
                events.Add(InputEvent.KeyChange(key, true));
            }

            for (int i = keys.Length - 1; i >= 0; i--)
            {
                events.Add(InputEvent.KeyChange(keys[i], false));
            }

            InputResult result = Send(CollectionsMarshal.AsSpan(events));
            if (result.Succeeded)
            {
                // The shortcut released these, including any the connection was holding itself.
                foreach (int key in keys)
                {
                    _held.SetKey(connectionId, key, false);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// <c>input.release_all</c>: let go of everything this connection is holding.
    /// </summary>
    /// <remarks>
    /// Always succeeds. Its postcondition is "this connection holds nothing", which is true
    /// afterwards even when the desktop is locked and nothing could be injected: a lock already
    /// released everything, and whatever could not be delivered then is re-sent on unlock.
    /// </remarks>
    public InputResult ReleaseAll(string connectionId)
    {
        ReleaseConnection(connectionId, "the phone asked");
        return InputResult.Ok;
    }

    // ---------------------------------------------------------------------
    // Dead-man release and desktop state
    // ---------------------------------------------------------------------

    /// <summary>
    /// Releases what one connection is holding: on <c>input.release_all</c>, when the connection
    /// closes, and when its device loses <c>ControlInput</c>.
    /// </summary>
    public void ReleaseConnection(string connectionId, string reason)
    {
        lock (_gate)
        {
            HeldInput held = _held.Take(connectionId);
            if (held.IsEmpty)
            {
                return;
            }

            bool delivered = SendRelease(held);

            _logger.LogInformation(
                "Released {Keys} key(s) and {Buttons} mouse button(s) held by connection {ConnectionId}: {Reason}. Delivered={Delivered}",
                held.Keys.Count,
                held.Buttons.Count,
                connectionId,
                reason,
                delivered);
        }
    }

    /// <summary>Releases what every connection is holding, e.g. when the service goes away.</summary>
    public void ReleaseEverything(string reason)
    {
        lock (_gate)
        {
            HeldInput held = Merge(_held.TakeAll());
            if (held.IsEmpty)
            {
                return;
            }

            bool delivered = SendRelease(held);

            _logger.LogInformation(
                "Released {Keys} key(s) and {Buttons} mouse button(s): {Reason}. Delivered={Delivered}",
                held.Keys.Count,
                held.Buttons.Count,
                reason,
                delivered);
        }
    }

    /// <summary>
    /// The session locked: release everything, and refuse input until it unlocks.
    /// </summary>
    /// <remarks>
    /// By the time the lock notification arrives the secure desktop usually already owns input, so
    /// the release sent here may go nowhere. It is sent anyway, and the same keys are released
    /// again on unlock — a key-up for a key that is not down is harmless, and a key that stays
    /// down across an unlock is not.
    /// </remarks>
    public void OnSessionLocked()
    {
        lock (_gate)
        {
            _locked = true;
            _lockedAt = _nowMs();
            _desktopStateAt = long.MinValue / 2;

            HeldInput held = Merge(_held.TakeAll());
            if (held.IsEmpty)
            {
                return;
            }

            SendRelease(held);
            _releaseAgainOnUnlock = Merge([(string.Empty, _releaseAgainOnUnlock), (string.Empty, held)]);

            _logger.LogInformation(
                "The session locked; released {Keys} key(s) and {Buttons} mouse button(s) held by paired devices.",
                held.Keys.Count,
                held.Buttons.Count);
        }
    }

    /// <summary>The session unlocked: accept input again.</summary>
    public void OnSessionUnlocked()
    {
        lock (_gate)
        {
            _locked = false;
            _desktopStateAt = long.MinValue / 2;

            HeldInput again = _releaseAgainOnUnlock;
            _releaseAgainOnUnlock = HeldInput.None;

            if (!again.IsEmpty)
            {
                SendRelease(again);
            }
        }
    }

    /// <summary>Forgets the cached monitor list, after a hot-plug or a mode change.</summary>
    public void InvalidateMonitors()
    {
        lock (_gate)
        {
            _monitorCache = null;
        }
    }

    // ---------------------------------------------------------------------
    // Text
    // ---------------------------------------------------------------------

    /// <summary>
    /// Expands a string into the key events that type it.
    /// </summary>
    /// <remarks>
    /// Characters go as <c>KEYEVENTF_UNICODE</c> events, one UTF-16 unit at a time, so the result
    /// does not depend on the PC's keyboard layout: a phone typing "é" produces "é" whether the PC
    /// is set to US, French or Bengali. A character outside the Basic Multilingual Plane is two
    /// units (a surrogate pair), sent in order; Windows reassembles them. Line feeds become a real
    /// Enter key and tabs a real Tab key, because applications react to those keys rather than to
    /// the characters (a chat box sends on Enter, a form moves focus on Tab). A carriage return
    /// directly before a line feed is dropped so that "\r\n" is one Enter, not two.
    /// </remarks>
    public static List<InputEvent> BuildTextEvents(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var events = new List<InputEvent>(text.Length * 2);

        for (int i = 0; i < text.Length; i++)
        {
            char unit = text[i];

            switch (unit)
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    continue;

                case '\n' or '\r':
                    events.Add(InputEvent.KeyChange(VirtualKeys.Return, true));
                    events.Add(InputEvent.KeyChange(VirtualKeys.Return, false));
                    continue;

                case '\t':
                    events.Add(InputEvent.KeyChange(VirtualKeys.Tab, true));
                    events.Add(InputEvent.KeyChange(VirtualKeys.Tab, false));
                    continue;
            }

            // Other control characters have no key and nothing sensible to type.
            if (char.IsControl(unit))
            {
                continue;
            }

            events.Add(InputEvent.UnicodeChange(unit, true));
            events.Add(InputEvent.UnicodeChange(unit, false));
        }

        return events;
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    /// <summary>Returns a refusal when the desktop cannot take input right now, otherwise null.</summary>
    /// <remarks>
    /// Two sources agree on whether input is possible: the lock notification the host passes in,
    /// and the platform's own report of which desktop holds input. The notification is what tells
    /// "locked" from "a UAC prompt"; the platform report is what is actually true. So the lock flag
    /// is not trusted forever: if the user's own desktop has demonstrably had input back for a few
    /// seconds while the flag still says locked, an unlock notification was missed, and refusing
    /// every command until the agent restarts would be a far worse failure than believing Windows.
    /// </remarks>
    private InputResult? Guard()
    {
        long now = _nowMs();
        if (now - _desktopStateAt > DesktopStateCacheMs)
        {
            _desktopState = _injector.GetDesktopState();
            _desktopStateAt = now;
        }

        if (_locked)
        {
            if (_desktopState != InputDesktopState.Normal || now - _lockedAt < LockSelfHealMs)
            {
                return LockedResult();
            }

            _logger.LogInformation("The desktop is accepting input again without an unlock notice; resuming remote input.");
            _locked = false;
        }

        return _desktopState == InputDesktopState.Secure ? SecureResult() : null;
    }

    private InputResult Send(ReadOnlySpan<InputEvent> events) =>
        _injector.Send(events) ? InputResult.Ok : Refused();

    /// <summary>
    /// Explains a refused injection, by asking again which desktop owns input: the secure desktop
    /// can appear between the cached check and the call.
    /// </summary>
    private InputResult Refused()
    {
        _desktopState = _injector.GetDesktopState();
        _desktopStateAt = _nowMs();

        if (_locked)
        {
            return LockedResult();
        }

        return _desktopState == InputDesktopState.Secure
            ? SecureResult()
            : new InputResult(InputStatus.Blocked, "Windows did not accept the input.");
    }

    private static InputResult LockedResult() =>
        new(InputStatus.DesktopLocked, "The PC is locked. Unlock it at the PC to control it.");

    private static InputResult SecureResult() => new(
        InputStatus.SecureDesktop,
        "A Windows security prompt is showing on the PC. It has to be answered at the PC.");

    private bool SendRelease(HeldInput held)
    {
        var events = new List<InputEvent>(held.Keys.Count + held.Buttons.Count);

        foreach (MouseButton button in held.Buttons)
        {
            events.Add(InputEvent.ButtonChange(button, false));
        }

        foreach (int key in held.Keys)
        {
            events.Add(InputEvent.KeyChange(key, false));
        }

        return events.Count == 0 || _injector.Send(CollectionsMarshal.AsSpan(events));
    }

    private static HeldInput Merge(IReadOnlyList<(string ConnectionId, HeldInput Held)> all)
    {
        if (all.Count == 0)
        {
            return HeldInput.None;
        }

        var keys = new List<int>();
        var buttons = new List<MouseButton>();

        foreach ((_, HeldInput held) in all)
        {
            foreach (int key in held.Keys)
            {
                if (!keys.Contains(key))
                {
                    keys.Add(key);
                }
            }

            foreach (MouseButton button in held.Buttons)
            {
                if (!buttons.Contains(button))
                {
                    buttons.Add(button);
                }
            }
        }

        return new HeldInput(keys, buttons);
    }

    private static bool TryParseButton(string? name, out MouseButton button)
    {
        if (string.Equals(name, "left", StringComparison.OrdinalIgnoreCase))
        {
            button = MouseButton.Left;
            return true;
        }

        if (string.Equals(name, "right", StringComparison.OrdinalIgnoreCase))
        {
            button = MouseButton.Right;
            return true;
        }

        if (string.Equals(name, "middle", StringComparison.OrdinalIgnoreCase))
        {
            button = MouseButton.Middle;
            return true;
        }

        button = MouseButton.Left;
        return false;
    }

    /// <summary>
    /// Resolves an optional monitor-normalised point to an absolute move event.
    /// </summary>
    /// <returns>
    /// False with <paramref name="failure"/> set when the point is unusable. True with a null
    /// <paramref name="move"/> when no point was given, which is valid for every command but an
    /// absolute move.
    /// </returns>
    private bool TryResolvePoint(
        string connectionId,
        double? x,
        double? y,
        string? monitorId,
        out InputEvent? move,
        out InputResult failure)
    {
        move = null;
        failure = InputResult.Ok;

        if (x is null && y is null)
        {
            return true;
        }

        if (x is null || y is null)
        {
            failure = InputResult.Invalid("x and y must be given together.");
            return false;
        }

        if (!InputCoordinates.TryClampNormalized(x.Value, out double nx) ||
            !InputCoordinates.TryClampNormalized(y.Value, out double ny))
        {
            failure = InputResult.Invalid("x and y must be finite numbers.");
            return false;
        }

        PixelRect screen = _injector.GetVirtualScreen();

        if (!TryFindMonitor(connectionId, monitorId, out PixelRect monitor))
        {
            if (!string.IsNullOrEmpty(monitorId))
            {
                failure = new InputResult(InputStatus.MonitorNotFound, "That monitor is not connected.");
                return false;
            }

            // No monitor could be enumerated at all (a remote session, a basic display driver).
            // The whole desktop is the only sensible thing left for the point to refer to.
            monitor = screen;
        }

        (int px, int py) = InputCoordinates.MonitorPointToPixel(nx, ny, monitor);
        (int ax, int ay) = InputCoordinates.PixelToAbsolute(px, py, screen);
        move = InputEvent.MoveTo(ax, ay);
        return true;
    }

    private bool TryFindMonitor(string connectionId, string? monitorId, out PixelRect rect)
    {
        IReadOnlyList<MonitorInfo> monitors = GetMonitors(refresh: false);
        MonitorInfo? match = Pick(connectionId, monitorId, monitors);

        if (match is null && !string.IsNullOrEmpty(monitorId))
        {
            // The id may be newer than the cache: a monitor plugged in a second ago. Re-enumerating
            // is rate-limited, so a caller repeating an id that does not exist cannot make every
            // one of its sixty moves a second walk the display adapters.
            if (_nowMs() - _monitorCacheAt > UnknownMonitorRefreshMs)
            {
                monitors = GetMonitors(refresh: true);
                match = Pick(connectionId, monitorId, monitors);
            }
        }

        if (match is null)
        {
            rect = default;
            return false;
        }

        rect = new PixelRect(match.X, match.Y, match.Width, match.Height);
        return true;
    }

    private MonitorInfo? Pick(string connectionId, string? monitorId, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(monitorId))
        {
            return monitors.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.Ordinal));
        }

        // The monitor this connection is streaming is the one the user is touching a picture of.
        string? selected = _selectedMonitor(connectionId, monitors);

        return monitors.FirstOrDefault(m => string.Equals(m.Id, selected, StringComparison.Ordinal))
               ?? monitors.FirstOrDefault(static m => m.Primary)
               ?? monitors[0];
    }

    private IReadOnlyList<MonitorInfo> GetMonitors(bool refresh)
    {
        long now = _nowMs();

        if (refresh || _monitorCache is null || now - _monitorCacheAt > MonitorCacheMs)
        {
            _monitorCache = _monitors.GetMonitors();
            _monitorCacheAt = now;
        }

        return _monitorCache;
    }
}
