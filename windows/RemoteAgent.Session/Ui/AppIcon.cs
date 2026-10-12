using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteAgent.Session.Ui;

/// <summary>
/// The PC-Remote logo, for the tray and for window title bars.
/// </summary>
/// <remarks>
/// Read from the .ico embedded in this assembly — the same file the executable's own icon is
/// compiled from — so the tray, the windows, the taskbar and the Start menu shortcut cannot drift
/// apart. Regenerate it with <c>shared/branding/make_icons.py</c>.
/// <para>Every accessor falls back to "no custom icon" rather than throwing: a missing resource
/// should cost the logo, not the tray icon the whole product is driven from.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class AppIcon
{
    private const string ResourceName = "RemoteAgent.Session.pc-remote.ico";

    private static readonly Lazy<ImageSource?> WindowSource = new(LoadWindowSource);

    /// <summary>The icon at the size the notification area draws, or the stock icon.</summary>
    public static Icon ForTray()
    {
        try
        {
            using Stream? stream = Open();
            if (stream is not null)
            {
                return new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Fall through to the stock icon.
        }

        return SystemIcons.Application;
    }

    /// <summary>The icon for a WPF window's title bar and taskbar button, or null.</summary>
    public static ImageSource? ForWindow() => WindowSource.Value;

    private static ImageSource? LoadWindowSource()
    {
        try
        {
            using Stream? stream = Open();
            if (stream is null)
            {
                return null;
            }

            // OnLoad copies the pixels, so the stream can be closed; the frame WPF picks for each
            // use comes from the decoder's whole set, so the frames are returned together.
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame? largest = decoder.Frames.OrderByDescending(static frame => frame.PixelWidth).FirstOrDefault();
            largest?.Freeze();
            return largest;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static Stream? Open() => typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
}
