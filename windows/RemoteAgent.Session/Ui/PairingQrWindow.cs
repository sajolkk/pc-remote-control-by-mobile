using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

// UseWindowsForms is enabled in this project for the tray icon, which puts System.Drawing and
// System.Windows.Forms into scope alongside WPF. These aliases pin every ambiguous name to its WPF
// meaning so the layout code below reads normally.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace RemoteAgent.Session.Ui;

/// <summary>
/// Shows the pairing QR code for the user to scan with their phone.
/// </summary>
/// <remarks>
/// <para>The window displays a live credential — the QR code embeds a single-use pairing token —
/// so it is treated accordingly: a visible countdown, an explicit warning not to share it, and the
/// window closes itself when the window expires rather than leaving a scannable code on screen.</para>
///
/// <para>The fingerprint is shown alongside, because it is the value the user should compare
/// against what their phone displays while pairing. Showing it here is what makes that comparison
/// possible; without it, the phone's fingerprint display would have nothing to be checked against.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PairingQrWindow : Window
{
    private readonly DispatcherTimer _countdown;
    private readonly DateTimeOffset _expiresAt;
    private readonly TextBlock _countdownText;

    private PairingQrWindow(
        string qrPayload,
        string deviceName,
        string fingerprintShort,
        IReadOnlyList<string> addresses,
        int port,
        DateTimeOffset expiresAt)
    {
        _expiresAt = expiresAt;

        Title = "PC-Remote: pair a device";
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(new TextBlock
        {
            Text = "Scan this with the PC Remote app",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        root.Children.Add(new TextBlock
        {
            Text = $"on {deviceName}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            Margin = new Thickness(0, 0, 0, 14),
        });

        root.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = new Image
            {
                Source = Render(qrPayload),
                Width = 260,
                Height = 260,

                // Nearest-neighbour keeps the module edges crisp when the bitmap is scaled, which
                // matters for a phone camera trying to decode it.
                Stretch = Stretch.Uniform,
            },
        });

        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.NearestNeighbor);

        _countdownText = new TextBlock
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            FontWeight = FontWeights.SemiBold,
        };

        root.Children.Add(_countdownText);

        root.Children.Add(new TextBlock
        {
            Text = "Do not share this code or a photo of it. Anyone who scans it can ask to pair, "
                   + "and you will be asked to approve the request here.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            Margin = new Thickness(0, 10, 0, 0),
        });

        var details = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        details.Children.Add(Detail("This PC's fingerprint", fingerprintShort, monospace: true));
        details.Children.Add(Detail(
            "Reachable at",
            addresses.Count == 0 ? "no network address found" : $"{string.Join(", ", addresses)} : {port}"));

        root.Children.Add(details);

        var close = new Button
        {
            Content = "Done",
            Width = 110,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            IsDefault = true,
            IsCancel = true,
        };

        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => UpdateCountdown();
        _countdown.Start();

        UpdateCountdown();

        Closed += (_, _) => _countdown.Stop();
    }

    /// <summary>Raised when the window closes, so the caller can close the pairing window too.</summary>
    internal event EventHandler? Dismissed;

    private static UIElement Detail(string label, string value, bool monospace = false)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };

        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)),
        });

        panel.Children.Add(new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = monospace ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
        });

        return panel;
    }

    private void UpdateCountdown()
    {
        TimeSpan remaining = _expiresAt - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            // The code is dead, so it must not stay on screen looking scannable.
            _countdown.Stop();
            _countdownText.Text = "This code has expired. Open pairing again to get a new one.";
            _countdownText.Foreground = Brushes.Firebrick;
            Close();
            return;
        }

        _countdownText.Text = $"Valid for another {remaining.Minutes:0}:{remaining.Seconds:00}";
        _countdownText.Foreground = remaining < TimeSpan.FromMinutes(1)
            ? Brushes.Firebrick
            : new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0x20));
    }

    private static BitmapImage Render(string payload)
    {
        using var generator = new QRCodeGenerator();

        // Error-correction level Q: robust enough to scan from an angle or a slightly dirty screen,
        // without inflating the module count so far that a phone struggles to resolve it.
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);

        var png = new PngByteQRCode(data);
        byte[] bytes = png.GetGraphic(pixelsPerModule: 8);

        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);

        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        return image;
    }

    /// <summary>
    /// Shows the window non-modally and returns it. Must be called on the UI thread.
    /// </summary>
    /// <remarks>
    /// Non-modal on purpose: the user has to be able to interact with the approval dialog that
    /// appears moments later, and a modal QR window would block it.
    /// </remarks>
    internal static PairingQrWindow ShowFor(
        string qrPayload,
        string deviceName,
        string fingerprintShort,
        IReadOnlyList<string> addresses,
        int port,
        DateTimeOffset expiresAt)
    {
        var window = new PairingQrWindow(qrPayload, deviceName, fingerprintShort, addresses, port, expiresAt);

        window.Closed += (_, _) => window.Dismissed?.Invoke(window, EventArgs.Empty);
        window.Show();
        window.Activate();

        return window;
    }
}
