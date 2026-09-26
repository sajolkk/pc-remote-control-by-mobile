using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// UseWindowsForms is enabled in this project for the tray icon; pin the ambiguous names to WPF.
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace RemoteAgent.Session.Ui;

/// <summary>One paired device as the service reports it.</summary>
internal sealed record DeviceRow(
    string DeviceId,
    string DeviceName,
    string Model,
    bool Connected,
    bool Blocked,
    string? Address,
    DateTimeOffset? LastSeenAt);

/// <summary>What the window needs from the host. Each call returns an error message, or null.</summary>
internal sealed record DevicesWindowActions(
    Func<Task<(IReadOnlyList<DeviceRow>? Devices, string? Error)>> Load,
    Func<string, bool, Task<string?>> SetBlocked,
    Func<string, Task<string?>> Remove);

/// <summary>
/// Lists paired phones, shows which are connected, and lets the owner disconnect, allow or remove
/// them.
/// </summary>
/// <remarks>
/// <para>Disconnect keeps the pairing and blocks the phone, so it stays disconnected instead of
/// reconnecting on its next retry. Allow lifts the block; the phone keeps retrying on its own, so
/// it reconnects within its retry interval without anyone touching it. Remove deletes the pairing,
/// after which the phone must pair again.</para>
///
/// <para>The list refreshes every few seconds while the window is open, so a phone connecting or
/// dropping shows up without a manual refresh.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class DevicesWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x60, 0x60, 0x60));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x1E, 0x8E, 0x3E));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xC5, 0x22, 0x1F));
    private static readonly SolidColorBrush Grey = new(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush CardBorder = new(Color.FromRgb(0xDD, 0xDD, 0xDD));

    private readonly DevicesWindowActions _actions;
    private readonly StackPanel _list;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _refresh;

    private bool _busy;

    private DevicesWindow(DevicesWindowActions actions)
    {
        _actions = actions;

        Title = "PC-Remote: devices";
        Width = 520;
        Height = 460;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));

        var root = new DockPanel { Margin = new Thickness(22) };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "Paired devices",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Disconnect keeps a phone paired but stops it connecting until you allow it again. " +
                   "Remove deletes the pairing.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 4, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = "Close", Width = 100, Height = 30 };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);

        _message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Red,
        };
        footer.Children.Add(_message);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _list = new StackPanel();
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        });

        Content = root;

        _refresh = new DispatcherTimer { Interval = RefreshInterval };
        _refresh.Tick += async (_, _) => await ReloadAsync();

        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            _refresh.Start();
        };

        Closed += (_, _) => _refresh.Stop();
    }

    /// <summary>Creates and shows the window. Must be called on the UI thread.</summary>
    internal static DevicesWindow ShowFor(DevicesWindowActions actions)
    {
        var window = new DevicesWindow(actions);
        window.Show();
        window.Activate();
        return window;
    }

    private async Task ReloadAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            (IReadOnlyList<DeviceRow>? devices, string? error) = await _actions.Load();

            if (devices is null)
            {
                _message.Text = error ?? "Could not reach the PC-Remote service.";
                return;
            }

            _message.Text = string.Empty;
            Render(devices);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Render(IReadOnlyList<DeviceRow> devices)
    {
        _list.Children.Clear();

        if (devices.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No phone is paired yet. Use \"Allow pairing and show QR code\" in the tray menu.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                Margin = new Thickness(0, 20, 0, 0),
            });
            return;
        }

        foreach (DeviceRow device in devices)
        {
            _list.Children.Add(Card(device));
        }
    }

    private Border Card(DeviceRow device)
    {
        (string statusText, SolidColorBrush statusBrush) = device switch
        {
            { Blocked: true } => ("Disconnected by you", Red),
            { Connected: true } => ($"Connected{(device.Address is null ? string.Empty : $" from {device.Address}")}", Green),
            _ => ($"Offline{DescribeLastSeen(device.LastSeenAt)}", Grey),
        };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = device.DeviceName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (!string.IsNullOrWhiteSpace(device.Model) &&
            !string.Equals(device.Model, device.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            info.Children.Add(new TextBlock { Text = device.Model, Foreground = Muted });
        }

        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        status.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = statusBrush,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        status.Children.Add(new TextBlock { Text = statusText, Foreground = statusBrush });
        info.Children.Add(status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        buttons.Children.Add(device.Blocked
            ? ActionButton("Allow", () => _actions.SetBlocked(device.DeviceId, false))
            : ActionButton("Disconnect", () => _actions.SetBlocked(device.DeviceId, true)));

        buttons.Children.Add(ActionButton("Remove", () => ConfirmRemove(device)));

        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(info);

        return new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row,
        };
    }

    private Task<string?> ConfirmRemove(DeviceRow device)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Remove {device.DeviceName}?\n\nIt will be disconnected and must be paired again with a QR code " +
            "before it can connect to this PC.",
            "PC-Remote",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes
            ? _actions.Remove(device.DeviceId)
            : Task.FromResult<string?>(null);
    }

    private Button ActionButton(string text, Func<Task<string?>> action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 90,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
        };

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            string? error = await action();
            _message.Text = error ?? string.Empty;
            await ReloadAsync();
        };

        return button;
    }

    private static string DescribeLastSeen(DateTimeOffset? lastSeen)
    {
        if (lastSeen is not { } seen)
        {
            return string.Empty;
        }

        TimeSpan ago = DateTimeOffset.UtcNow - seen;
        string when = ago switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)ago.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)ago.TotalHours} h ago",
            _ => seen.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture),
        };

        return $", last seen {when}";
    }
}
