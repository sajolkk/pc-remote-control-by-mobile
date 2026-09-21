namespace RemoteAgent.Core.Validation;

/// <summary>
/// Validates URLs before any of them reaches a browser launch.
/// </summary>
/// <remarks>
/// A URL arriving from the network is the one piece of free-form text this system
/// hands to the Windows shell, so it is the highest-value validation in the
/// project. The rules are an allowlist, not a blocklist:
/// <list type="bullet">
/// <item>only <c>http</c> and <c>https</c> — every other scheme is a route to local
/// execution, whether that is <c>file:</c> reading local disk, <c>javascript:</c> in
/// some browsers, or an installed custom protocol handler;</item>
/// <item>absolute only, so nothing can be interpreted relative to a local path;</item>
/// <item>no embedded credentials, which are both a phishing vector and a way to
/// smuggle characters past a naive parser;</item>
/// <item>a length cap and no control characters, so nothing can inject an argument
/// break or a newline downstream;</item>
/// <item>the result is returned re-serialized by <see cref="Uri"/> rather than as
/// the caller's original string, so what gets launched is a normalized form we
/// actually parsed.</item>
/// </list>
/// The validated URL is still always passed to the browser as a single argument
/// value, never concatenated into a command line.
/// </remarks>
public static class UrlValidator
{
    /// <summary>Longest URL accepted.</summary>
    public const int MaxLength = 2048;

    /// <summary>
    /// Validates and normalizes a URL.
    /// </summary>
    /// <param name="candidate">The caller-supplied string.</param>
    /// <param name="normalized">The normalized absolute URL, when valid.</param>
    /// <param name="error">A displayable reason, when invalid.</param>
    public static bool TryValidate(string? candidate, out string normalized, out string? error)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "A URL is required.";
            return false;
        }

        string trimmed = candidate.Trim();

        if (trimmed.Length > MaxLength)
        {
            error = $"The URL exceeds the {MaxLength} character limit.";
            return false;
        }

        foreach (char c in trimmed)
        {
            if (char.IsControl(c))
            {
                error = "The URL contains control characters.";
                return false;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            error = "The URL is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Only http and https URLs are allowed; '{uri.Scheme}' is not.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "URLs containing credentials are not allowed.";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "The URL has no host.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        error = null;
        return true;
    }
}
