using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// UseWindowsForms is enabled in this project for the tray icon, which puts System.Drawing and
// System.Windows.Forms into scope alongside WPF. These aliases pin every ambiguous name to its WPF
// meaning so the layout code below reads normally.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace RemoteAgent.Session.Ui;

/// <summary>
/// The dialog a human must accept before a new device is paired (§8.1).
/// </summary>
/// <remarks>
/// <para>This dialog is the third and final gate of pairing, and the only one an attacker cannot
/// satisfy remotely. The other two — pairing mode being open, and a valid single-use token — can
/// both be satisfied by someone who obtained the QR code. This one requires a person at the
/// keyboard.</para>
///
/// <para><b>Design choices that are security decisions, not styling:</b></para>
/// <list type="bullet">
/// <item><b>Decline is the default.</b> It is the focused button, and closing the window by any
/// other means also declines. Someone dismissing an unexpected dialog should end up refusing.</item>
/// <item><b>The source address is shown</b> and labelled as the value the device cannot forge,
/// next to the name it can. A device chooses its own display name, so "Pixel 8" is a claim;
/// 192.168.1.44 is an observation.</item>
/// <item><b>The fingerprint is shown</b> so a cautious user can compare it with what their phone
/// displays, which detects a relayed or substituted request.</item>
/// <item><b>For code pairing the code is the largest thing on screen</b>, because there is no QR
/// token behind that request: comparing the code with the phone is what proves nothing on the
/// network is in between, so it must be impossible to miss.</item>
/// <item><b>Topmost, but not activated-by-stealing-focus.</b> The dialog must be seen, but a
/// window appearing under the user's cursor mid-click is how accidental approvals happen.</item>
/// <item><b>Full control is a box the owner has to tick.</b> It starts unticked every time, it
/// names what it adds, and it does nothing unless Allow is also clicked. A new device gets view and
/// input only unless the person at the PC deliberately says otherwise — and there is nothing a
/// device can send that ticks it.</item>
/// </list>
///
/// <para>Built in code rather than XAML deliberately: it keeps the security-relevant text and the
/// defaulting of the Decline button in one reviewable file, with no separate markup that could
/// drift from it.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PairingApprovalDialog : Window
{
    private readonly CheckBox _fullControl;
    private bool _approved;

    private PairingApprovalDialog(
        string deviceName,
        string platform,
        string model,
        string remoteAddress,
        string fingerprint,
        string code)
    {
        Title = "PC-Remote: pair a new device?";
        Icon = AppIcon.ForWindow();
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = "A device is asking to control this PC",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        var details = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        AddDetail(details, ref row, "Device", Describe(deviceName, platform, model));
        AddDetail(details, ref row, "Address", remoteAddress);
        AddDetail(details, ref row, "Fingerprint", fingerprint);

        root.Children.Add(details);

        if (code.Length > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Your phone should show this code:",
                Margin = new Thickness(0, 0, 0, 4),
            });

            root.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 14),
                Child = new TextBlock
                {
                    Text = code,
                    FontSize = 34,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                },
            });
        }

        root.Children.Add(new TextBlock
        {
            Text = code.Length > 0
                ? "Only allow this if you recognise the device, you started the pairing yourself, " +
                  "and the code above is exactly the code on your phone. If the codes differ, decline."
                : "Only allow this if you recognise the device and started the pairing yourself. " +
                  "The device name is chosen by the device; the address is not. If the fingerprint " +
                  "does not match the one shown on your phone, decline.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(new TextBlock
        {
            Text = "Allowing grants permission to view the screen and control the mouse and keyboard. " +
                   "Other permissions stay off unless you tick the box below, and can be changed " +
                   "later under Devices in the tray menu.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Unticked by default, and constructed that way explicitly rather than relying on the
        // control's default: this box is the difference between "may look and click" and "may
        // manage files, launch apps and shut the PC down".
        _fullControl = new CheckBox
        {
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 18),
            Content = new TextBlock
            {
                Text = "Give this device full control (apps, files, power, clipboard)",
                TextWrapping = TextWrapping.Wrap,
            },
        };

        root.Children.Add(_fullControl);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var allow = new Button
        {
            Content = "Allow",
            Width = 110,
            Height = 32,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var decline = new Button
        {
            Content = "Decline",
            Width = 110,
            Height = 32,

            // Decline is both the default action and the focused control: Enter and Escape must
            // both refuse, so no keystroke aimed at another window can approve access.
            IsDefault = true,
            IsCancel = true,
        };

        allow.Click += (_, _) =>
        {
            _approved = true;
            Close();
        };

        decline.Click += (_, _) =>
        {
            _approved = false;
            Close();
        };

        buttons.Children.Add(allow);
        buttons.Children.Add(decline);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => decline.Focus();
    }

    private static void AddDetail(Grid grid, ref int row, string label, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            Margin = new Thickness(0, 2, 8, 2),
        };

        var valueBlock = new TextBlock
        {
            Text = value,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
        };

        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        row++;
    }

    private static string Describe(string deviceName, string platform, string model)
    {
        string name = string.IsNullOrWhiteSpace(deviceName) ? "Unnamed device" : deviceName;
        string qualifier = string.Join(
            ", ",
            new[] { model, platform }.Where(static part => !string.IsNullOrWhiteSpace(part)));

        return qualifier.Length == 0 ? name : $"{name} ({qualifier})";
    }

    /// <summary>
    /// Shows the dialog modally and returns what the user decided.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI thread. Returns a refusal for every outcome other than an explicit
    /// click on Allow — including the window being closed, dismissed, or killed — because "no
    /// clear yes" has to mean no. The full-control box is read only when Allow was clicked, so a
    /// ticked box on a declined dialog grants nothing.
    /// </remarks>
    internal static PairingDialogResult Show(
        string deviceName,
        string platform,
        string model,
        string remoteAddress,
        string fingerprint,
        string code = "")
    {
        var dialog = new PairingApprovalDialog(deviceName, platform, model, remoteAddress, fingerprint, code);
        dialog.ShowDialog();

        return dialog._approved
            ? new PairingDialogResult(true, dialog._fullControl.IsChecked == true)
            : PairingDialogResult.Declined;
    }
}

/// <summary>What the person at the PC answered to the pairing dialog.</summary>
/// <param name="Approved">Whether they clicked Allow.</param>
/// <param name="FullControl">Whether "give this device full control" was ticked when they did.</param>
internal readonly record struct PairingDialogResult(bool Approved, bool FullControl)
{
    /// <summary>Declined, dismissed, or never shown.</summary>
    internal static PairingDialogResult Declined { get; } = new(false, false);
}
