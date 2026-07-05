using System.Drawing;
using Microsoft.Win32;
using NitTray.Services;
using WinForms = System.Windows.Forms;

namespace NitTray.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private Icon _icon;

    public event EventHandler? ShowRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? OpenLogRequested;
    public event EventHandler? QuitRequested;

    public TrayIconHost()
    {
        _icon = IconFactory.CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem("Show NitTray");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        var refreshItem = new WinForms.ToolStripMenuItem("Refresh displays");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var logItem = new WinForms.ToolStripMenuItem("Open diagnostics log…");
        logItem.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);
        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(logItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "NitTray — Apple display brightness",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        // Keep the tray glyph legible when the user switches between the light and
        // dark taskbar themes while the app is running.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        var refreshed = IconFactory.CreateTrayIcon();
        var previous = _icon;
        _icon = refreshed;
        _notifyIcon.Icon = refreshed;
        previous.Dispose();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
