namespace RemoteAgent.Core.Abstractions;

/// <summary>A mouse button the protocol can name.</summary>
public enum MouseButton
{
    /// <summary>The primary button.</summary>
    Left,

    /// <summary>The secondary button.</summary>
    Right,

    /// <summary>The wheel button.</summary>
    Middle,
}

/// <summary>What kind of low-level event an <see cref="InputEvent"/> is.</summary>
public enum InputEventKind
{
    /// <summary>
    /// Move the pointer to an absolute position. <see cref="InputEvent.X"/> and
    /// <see cref="InputEvent.Y"/> are in the 0..65535 space that spans the whole virtual desktop.
    /// </summary>
    MouseMoveAbsolute,

    /// <summary>Press or release <see cref="InputEvent.Button"/>.</summary>
    MouseButton,

    /// <summary>Turn the vertical wheel by <see cref="InputEvent.X"/> delta units (120 a notch).</summary>
    WheelVertical,

    /// <summary>Turn the horizontal wheel by <see cref="InputEvent.X"/> delta units.</summary>
    WheelHorizontal,

    /// <summary>Press or release the virtual key in <see cref="InputEvent.X"/>.</summary>
    Key,

    /// <summary>Press or release the UTF-16 code unit in <see cref="InputEvent.X"/> as text.</summary>
    Unicode,
}

/// <summary>
/// One primitive input event, in the form the platform layer can inject without further decisions.
/// </summary>
/// <remarks>
/// Everything that requires judgement — which monitor a touch belongs to, how a normalised point
/// becomes a virtual-desktop coordinate, which keys a connection is holding — is settled in
/// platform-neutral code before one of these exists. The platform layer only translates it into
/// the native structure, which keeps the part that cannot be unit tested as small as possible.
/// </remarks>
/// <param name="Kind">What the event is.</param>
/// <param name="X">Absolute X, a wheel delta, a virtual-key code or a UTF-16 unit, by kind.</param>
/// <param name="Y">Absolute Y. Unused by every other kind.</param>
/// <param name="Button">The button, for <see cref="InputEventKind.MouseButton"/>.</param>
/// <param name="Down">Press (true) or release (false), for buttons, keys and Unicode units.</param>
public readonly record struct InputEvent(InputEventKind Kind, int X, int Y, MouseButton Button, bool Down)
{
    /// <summary>An absolute pointer move in virtual-desktop 0..65535 space.</summary>
    public static InputEvent MoveTo(int normalizedX, int normalizedY) =>
        new(InputEventKind.MouseMoveAbsolute, normalizedX, normalizedY, MouseButton.Left, false);

    /// <summary>A button press or release.</summary>
    public static InputEvent ButtonChange(MouseButton button, bool down) =>
        new(InputEventKind.MouseButton, 0, 0, button, down);

    /// <summary>A vertical wheel turn.</summary>
    public static InputEvent Wheel(int delta) =>
        new(InputEventKind.WheelVertical, delta, 0, MouseButton.Left, false);

    /// <summary>A horizontal wheel turn.</summary>
    public static InputEvent HorizontalWheel(int delta) =>
        new(InputEventKind.WheelHorizontal, delta, 0, MouseButton.Left, false);

    /// <summary>A virtual-key press or release.</summary>
    public static InputEvent KeyChange(int virtualKey, bool down) =>
        new(InputEventKind.Key, virtualKey, 0, MouseButton.Left, down);

    /// <summary>A UTF-16 code unit typed as text.</summary>
    public static InputEvent UnicodeChange(char unit, bool down) =>
        new(InputEventKind.Unicode, unit, 0, MouseButton.Left, down);
}

/// <summary>A rectangle in physical pixels on the virtual desktop. The origin may be negative.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Whether the desktop will currently accept injected input.</summary>
public enum InputDesktopState
{
    /// <summary>The user's own desktop has the input focus.</summary>
    Normal,

    /// <summary>
    /// The secure desktop owns input: the lock screen, Ctrl+Alt+Del, or a UAC consent prompt. No
    /// ordinary process can inject there (§12.1).
    /// </summary>
    Secure,
}

/// <summary>
/// Injects mouse and keyboard input into the interactive session (§4, §10).
/// </summary>
/// <remarks>
/// Implemented by the session agent over <c>SendInput</c>. The interface is deliberately a thin
/// primitive: a batch of already-resolved events goes in, and the three facts about the desktop
/// that only the platform knows come out. All policy lives above it in
/// <c>RemoteAgent.Core.Input.InputController</c>, where it is testable with a fake.
/// </remarks>
public interface IInputInjector
{
    /// <summary>The bounding rectangle of every monitor, in physical pixels.</summary>
    PixelRect GetVirtualScreen();

    /// <summary>The pointer's position in physical virtual-desktop pixels, or null when unreadable.</summary>
    (int X, int Y)? GetCursorPosition();

    /// <summary>Which desktop currently owns input.</summary>
    InputDesktopState GetDesktopState();

    /// <summary>
    /// Injects a batch as one uninterrupted sequence. Returns false when Windows refused it.
    /// </summary>
    /// <remarks>
    /// A batch is atomic with respect to the user's own input, which is what makes a click a click
    /// rather than a press, somebody's mouse movement, and a release somewhere else.
    /// </remarks>
    bool Send(ReadOnlySpan<InputEvent> events);
}
