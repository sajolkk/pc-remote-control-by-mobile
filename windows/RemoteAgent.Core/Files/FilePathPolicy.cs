namespace RemoteAgent.Core.Files;

/// <summary>Why a path or name was refused.</summary>
public enum PathRejection
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>
    /// The text is not a well-formed absolute Windows path, or a component is not a legal name.
    /// Reported as <c>E_INVALID_ARGUMENTS</c>.
    /// </summary>
    Malformed,

    /// <summary>
    /// The path is of a kind that is never served — a device path, a UNC share, or an attempt to
    /// climb above a drive root — or it lies outside every allowed root. Reported as
    /// <c>E_ACCESS_DENIED</c>.
    /// </summary>
    Denied,
}

/// <summary>
/// Canonicalises and validates the paths and names that arrive from a phone (§7.3).
/// </summary>
/// <remarks>
/// <para><b>This is the security boundary of file management.</b> A path on the wire is text chosen
/// by the caller, and Windows has an unusually large number of ways for two different strings to
/// name the same file, or for one innocent-looking string to name something that is not a file at
/// all. Everything below exists to reduce a request to one canonical spelling <em>before</em> it is
/// compared with an allowed root, and to refuse outright the spellings that cannot be reduced
/// safely:</para>
/// <list type="bullet">
/// <item><b><c>..</c> and <c>.</c> segments</b> are resolved lexically here, so
/// <c>C:\Users\me\Documents\..\..\..\Windows</c> is compared as <c>C:\Windows</c>. Climbing above
/// the drive root is refused rather than clamped.</item>
/// <item><b>Forward slashes</b> are separators to Windows, so they are separators here.</item>
/// <item><b>Device and extended-length prefixes</b> (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>) switch
/// off Win32 path normalisation and reach raw devices, volumes by GUID, and named pipes. Never
/// served.</item>
/// <item><b>UNC paths</b> (<c>\\server\share</c>) would make the PC open connections to machines of
/// the caller's choosing, leaking the user's credentials to them. Never served.</item>
/// <item><b>Alternate data streams</b> (<c>file.txt:stream</c>, <c>dir::$DATA</c>): a colon is legal
/// only as the second character of the path. Anywhere else it is refused.</item>
/// <item><b>Reserved device names</b> (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, also <c>NUL.txt</c>) name
/// devices in <em>every</em> directory on Win32.</item>
/// <item><b>Trailing dots and spaces</b> are silently stripped by Win32, so <c>secret.txt.</c> and
/// <c>secret.txt</c> are the same file to the OS and two different strings to a naive check.</item>
/// <item><b>Root-prefix confusion</b>: containment is decided component-wise, so the root
/// <c>C:\Users\me</c> does not contain <c>C:\Users\meow</c>.</item>
/// </list>
/// <para>Two things are deliberately <em>not</em> done here, because they need the file system:
/// reparse points (junctions and symbolic links) are checked by <c>FileManager</c> against the real
/// directory tree, and 8.3 short names are simply not expanded — a short-name spelling of an
/// allowed folder fails the lexical containment test and is refused, which is the safe direction to
/// be wrong in.</para>
///
/// <para>Purely lexical and platform-neutral: no method here touches a disk, so every case above is
/// a unit test.</para>
/// </remarks>
public static class FilePathPolicy
{
    /// <summary>Longest path accepted. Windows' own long-path limit.</summary>
    public const int MaxPathLength = 32_000;

    /// <summary>Longest single name accepted. NTFS's component limit.</summary>
    public const int MaxNameLength = 255;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// Reduces a caller-supplied path to its canonical form: <c>C:\dir\file</c>, upper-case drive
    /// letter, backslashes, no <c>.</c> or <c>..</c>, no trailing separator except on a drive root.
    /// </summary>
    /// <param name="input">The path as received.</param>
    /// <param name="canonical">The canonical path when the method returns true.</param>
    /// <param name="rejection">Why the path was refused when it returns false.</param>
    public static bool TryCanonicalize(string? input, out string canonical, out PathRejection rejection)
    {
        canonical = string.Empty;

        if (string.IsNullOrEmpty(input) || input.Length > MaxPathLength)
        {
            rejection = PathRejection.Malformed;
            return false;
        }

        foreach (char c in input)
        {
            if (char.IsControl(c))
            {
                rejection = PathRejection.Malformed;
                return false;
            }
        }

        string path = input.Replace('/', '\\');

        // Anything starting with two separators is a UNC share, a device path (\\.\) or an
        // extended-length path (\\?\). \??\ is the NT object-manager spelling of the same thing.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            rejection = PathRejection.Denied;
            return false;
        }

        // Drive-absolute only. "C:foo" is relative to a per-drive current directory, and "\foo" to
        // the current drive: both mean something that depends on process state, not on the text.
        if (path.Length < 3 || !IsDriveLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            rejection = PathRejection.Malformed;
            return false;
        }

        var segments = new List<string>();

        foreach (string segment in path[3..].Split('\\'))
        {
            // Empty: a doubled or trailing separator. "." names the directory itself.
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    // Above the drive root. Win32 would clamp this to the root; a request that
                    // tries it is not one to be helpful to.
                    rejection = PathRejection.Denied;
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (!IsValidName(segment))
            {
                // Includes any colon (an alternate data stream) and every reserved device name.
                rejection = segment.Contains(':') || IsReservedName(segment)
                    ? PathRejection.Denied
                    : PathRejection.Malformed;
                return false;
            }

            segments.Add(segment);
        }

        string drive = char.ToUpperInvariant(path[0]) + @":\";
        canonical = segments.Count == 0 ? drive : drive + string.Join('\\', segments);

        if (canonical.Length > MaxPathLength)
        {
            canonical = string.Empty;
            rejection = PathRejection.Malformed;
            return false;
        }

        rejection = PathRejection.None;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is usable as a single path component.
    /// </summary>
    /// <remarks>
    /// Refuses separators, the colon, the wildcard and redirection characters Win32 forbids,
    /// control characters, <c>.</c> and <c>..</c>, reserved device names with or without an
    /// extension, and a trailing dot or space.
    /// </remarks>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        if (name is "." or "..")
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsControl(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        char last = name[^1];
        if (last is '.' or ' ')
        {
            return false;
        }

        return !IsReservedName(name);
    }

    /// <summary>
    /// Whether a name is one of the Win32 device names, which exist in every directory.
    /// </summary>
    /// <remarks>
    /// The device name is whatever precedes the first dot, with trailing spaces ignored:
    /// <c>NUL</c>, <c>nul.txt</c> and <c>NUL .tar.gz</c> all open the null device on Windows 10.
    /// </remarks>
    public static bool IsReservedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    /// <summary>
    /// Whether a canonical path is <paramref name="root"/> itself or lies beneath it.
    /// </summary>
    /// <remarks>
    /// Both arguments must already be canonical. The comparison is component-wise: the character
    /// after the root's text has to be a separator, which is what stops <c>C:\Users\me</c> from
    /// "containing" <c>C:\Users\meow</c>. Case-insensitive, as NTFS is by default.
    /// </remarks>
    public static bool IsWithin(string canonicalPath, string root)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        ArgumentNullException.ThrowIfNull(root);

        if (root.Length == 0 || canonicalPath.Length < root.Length ||
            !canonicalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (canonicalPath.Length == root.Length)
        {
            return true;
        }

        // A drive root ends with its separator ("C:\"); every other root does not.
        return root[^1] == '\\' || canonicalPath[root.Length] == '\\';
    }

    /// <summary>The containing directory of a canonical path, or null for a drive root.</summary>
    public static string? GetParent(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);

        int separator = canonicalPath.LastIndexOf('\\');
        if (separator < 2 || canonicalPath.Length <= 3)
        {
            return null;
        }

        // "C:\name" → "C:\"; "C:\a\b" → "C:\a".
        return separator == 2 ? canonicalPath[..3] : canonicalPath[..separator];
    }

    /// <summary>The last component of a canonical path; the drive root for a drive root.</summary>
    public static string GetName(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);

        if (canonicalPath.Length <= 3)
        {
            return canonicalPath;
        }

        return canonicalPath[(canonicalPath.LastIndexOf('\\') + 1)..];
    }

    /// <summary>Joins a canonical directory and a validated name.</summary>
    public static string Combine(string canonicalDirectory, string name)
    {
        ArgumentNullException.ThrowIfNull(canonicalDirectory);
        ArgumentNullException.ThrowIfNull(name);

        return canonicalDirectory.EndsWith('\\') ? canonicalDirectory + name : canonicalDirectory + '\\' + name;
    }

    private static bool IsDriveLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
