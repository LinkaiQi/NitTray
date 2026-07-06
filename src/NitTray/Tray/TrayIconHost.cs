using System.Drawing;
using NitTray.Services;
using WinForms = System.Windows.Forms;

namespace NitTray.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public event EventHandler? ShowRequested;
    public event EventHandler? RefreshRequested;
#if DEBUG
    public event EventHandler? OpenLogRequested;
#endif
    public event EventHandler? QuitRequested;

    public TrayIconHost()
    {
        _icon = IconFactory.CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip
        {
            // Flat, native menu look (removes the old gray gradient) with no left
            // icon gutter — a light touch instead of the WinForms default.
            RenderMode = WinForms.ToolStripRenderMode.System,
            ShowImageMargin = false,
            BackColor = System.Drawing.Color.White,
        };
        var showItem = new WinForms.ToolStripMenuItem("Show NitTray");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        var refreshItem = new WinForms.ToolStripMenuItem("Refresh displays");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
#if DEBUG
        // The diagnostics log is a developer aid; only expose it in Debug builds.
        var logItem = new WinForms.ToolStripMenuItem("Open diagnostics log…");
        logItem.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(logItem);
#endif
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
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
