using System.Drawing;
using DisplayDial.Services;
using WinForms = System.Windows.Forms;

namespace DisplayDial.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public event EventHandler? ShowRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? OpenLogRequested;
    public event EventHandler? QuitRequested;

    public TrayIconHost()
    {
        _icon = IconFactory.CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem("Show DisplayDial");
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
            Text = "DisplayDial — Apple display brightness",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
