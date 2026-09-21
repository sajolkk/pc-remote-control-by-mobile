using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace RemoteAgent.Windows.Shell;

/// <summary>
/// Extracts an application icon as a PNG.
/// </summary>
/// <remarks>
/// Icons are opt-in on <c>app.list</c> because extraction touches the file system once
/// per application: on a machine with hundreds of programs, doing it unconditionally
/// would turn a fast list into a slow one. The caller caches the result per application,
/// including failures, so a stubborn executable costs one attempt rather than one per
/// request.
/// <para>
/// PNG rather than the raw ICO: the mobile client can decode PNG directly, ICO support
/// on mobile is patchy, and a 32×32 PNG is small enough to inline in JSON without
/// needing a separate transfer channel.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class IconExtractor
{
    /// <summary>
    /// Returns the executable's associated icon as PNG bytes, or an empty array when
    /// there is no icon to extract.
    /// </summary>
    internal static byte[] ExtractPng(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
        if (icon is null)
        {
            return [];
        }

        using Bitmap bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
