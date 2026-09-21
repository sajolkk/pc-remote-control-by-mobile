using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace RemoteAgent.Session.Ui;

/// <summary>
/// The notification-area icon: the only way a user interacts with the agent directly.
/// </summary>
/// <remarks>
/// <para>This is where "Allow pairing" lives. It belongs in the session agent rather than in a
/// separate management application because the agent is the process that has a desktop: a Windows
/// service cannot show UI at all, and adding a third process just to host a menu would mean a third
/// IPC channel to secure for no benefit.</para>
///
/// <para>The menu is deliberately small. Anything that changes security posture — granting
/// permissions, revoking devices — is not here, because those decisions deserve a considered UI
/// rather than a right-click, and because the tray icon runs at the user's privilege level with no
/// additional confirmation. Opening a pairing window is the exception, and it is safe precisely
/// because it grants nothing on its own: a device still needs the single-use token and an explicit
/// approval.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly ILogger<TrayIcon> _logger;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pairItem;
    private readonly ToolStripMenuItem _statusItem;

    private bool _pairingOpen;
    private bool _disposed;

    /// <summary>Creates the tray icon. Must be constructed on the UI thread.</summary>
    public TrayIcon(ILogger<TrayIcon> logger)
    {
        _logger = logger;

        _statusItem = new ToolStripMenuItem("Connecting to the PC-Remote service…") { Enabled = false };
        _pairItem = new ToolStripMenuItem("Allow pairing and show QR code…");
        _pairItem.Click += (_, _) => OnPairClicked();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pairItem);

        _icon = new NotifyIcon
        {
            // The stock shield-free application icon: shipping a custom .ico adds a resource to
            // maintain for no functional gain at this stage.
            Icon = SystemIcons.Application,
            Text = "PC-Remote",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => OnPairClicked();
    }

    /// <summary>Raised when the user asks to open pairing.</summary>
    public event EventHandler? PairingRequested;

    /// <summary>Raised when the user asks to close pairing.</summary>
    public event EventHandler? PairingCancelled;

    /// <summary>Updates the tooltip and menu to reflect the current state.</summary>
    public void UpdateStatus(string status, bool pairingOpen)
    {
        if (_disposed)
        {
            return;
        }

        _pairingOpen = pairingOpen;
        _statusItem.Text = status;

        _pairItem.Text = pairingOpen
            ? "Stop allowing pairing"
            : "Allow pairing and show QR code…";

        // The tooltip is capped by Windows at 63 characters; a longer string is silently ignored,
        // which looks like the icon having no tooltip at all.
        string tooltip = $"PC-Remote — {status}";
        _icon.Text = tooltip.Length > 62 ? tooltip[..62] : tooltip;
    }

    /// <summary>Shows a balloon notification.</summary>
    public void Notify(string title, string message, bool isWarning = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _icon.ShowBalloonTip(
                5000,
                title,
                message,
                isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Balloon tips can be suppressed by policy or focus-assist. Not worth failing over.
            _logger.LogDebug(ex, "Could not show a tray notification.");
        }
    }

    private void OnPairClicked()
    {
        if (_pairingOpen)
        {
            PairingCancelled?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            PairingRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Hidden before disposal: a NotifyIcon disposed while visible can leave a dead icon in the
        // notification area until the user hovers over it.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
