namespace RemoteAgent.Protocol;

/// <summary>
/// Permission groups granted per paired device (architecture doc §7.4).
/// </summary>
/// <remarks>
/// Every command declares exactly one required permission. The authorization
/// gate rejects any command whose permission is not in the calling device's
/// granted set, before the command's arguments are even deserialized.
/// <para>
/// Values are a flags bitmap for compact storage, but they cross the wire as
/// string names (see <see cref="PermissionSet"/>) so that adding a group never
/// changes the meaning of a stored or transmitted integer.
/// </para>
/// </remarks>
[Flags]
public enum Permission
{
    /// <summary>No permission required. Used by commands available to any paired device.</summary>
    None = 0,

    /// <summary>View the desktop: screen capture, media offers, screenshots.</summary>
    ViewScreen = 1 << 0,

    /// <summary>Inject mouse and keyboard input.</summary>
    ControlInput = 1 << 1,

    /// <summary>Launch, focus and close applications and browsers.</summary>
    LaunchApps = 1 << 2,

    /// <summary>List, upload and download files within configured allowed roots.</summary>
    ManageFiles = 1 << 3,

    /// <summary>Lock, sign out, sleep, restart and shut down.</summary>
    PowerControls = 1 << 4,

    /// <summary>Read and write the clipboard.</summary>
    Clipboard = 1 << 5,
}

/// <summary>
/// Conversions between <see cref="Permission"/> and its wire representation.
/// </summary>
public static class PermissionSet
{
    /// <summary>Granted to a device on first pairing: view and control only, nothing else (§7.4).</summary>
    public const Permission Default = Permission.ViewScreen | Permission.ControlInput;

    /// <summary>Every permission group. Never a default — only an explicit user choice.</summary>
    public const Permission All =
        Permission.ViewScreen | Permission.ControlInput | Permission.LaunchApps |
        Permission.ManageFiles | Permission.PowerControls | Permission.Clipboard;

    private static readonly (Permission Value, string Name)[] Map =
    [
        (Permission.ViewScreen, "ViewScreen"),
        (Permission.ControlInput, "ControlInput"),
        (Permission.LaunchApps, "LaunchApps"),
        (Permission.ManageFiles, "ManageFiles"),
        (Permission.PowerControls, "PowerControls"),
        (Permission.Clipboard, "Clipboard"),
    ];

    /// <summary>All permission group names, for UI enumeration.</summary>
    public static IReadOnlyList<string> AllNames { get; } = Map.Select(static m => m.Name).ToArray();

    /// <summary>Expands a bitmap into its wire names, in declaration order.</summary>
    public static string[] ToNames(Permission permissions) =>
        Map.Where(m => permissions.HasFlag(m.Value)).Select(static m => m.Name).ToArray();

    /// <summary>
    /// Parses wire names back into a bitmap. Unknown names are ignored rather
    /// than throwing, so an older PC can accept a newer app's list without
    /// silently granting something it does not understand.
    /// </summary>
    public static Permission FromNames(IEnumerable<string>? names)
    {
        if (names is null)
        {
            return Permission.None;
        }

        Permission result = Permission.None;
        foreach (string name in names)
        {
            foreach ((Permission value, string candidate) in Map)
            {
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    result |= value;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Whether <paramref name="granted"/> satisfies <paramref name="required"/>.</summary>
    public static bool Allows(Permission granted, Permission required) =>
        required == Permission.None || (granted & required) == required;
}
