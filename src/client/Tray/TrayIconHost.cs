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
    public event EventHandler? AboutRequested;
    public event EventHandler? OpenLogRequested;
    public event EventHandler? QuitRequested;

    public TrayIconHost()
    {
        _icon = IconFactory.CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip
        {
            // Windows 11 Fluent look: rounded hover highlight + themed colours from
            // ModernMenuRenderer, no left icon gutter, and rounded window corners
            // (applied via DWM once the popup handle exists).
            Renderer = new ModernMenuRenderer(MenuTheme.IsDark()),
            ShowImageMargin = false,
            ShowCheckMargin = false,
        };
        menu.HandleCreated += (_, _) => MenuTheme.EnableRoundedCorners(menu.Handle);

        var showItem = new WinForms.ToolStripMenuItem("Open NitTray");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        var refreshItem = new WinForms.ToolStripMenuItem("Rescan Display");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var aboutItem = new WinForms.ToolStripMenuItem("About NitTray");
        aboutItem.Click += (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty);
        // The diagnostics log lets users capture an enumeration trace and attach it
        // to a bug report when a display isn't detected — useful in every build.
        var logItem = new WinForms.ToolStripMenuItem("Open Diagnostics Log");
        logItem.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);
        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        // Comfortable Windows 11 item spacing: taller rows and text inset from the
        // left like the shell's own tray menus.
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            if (item is WinForms.ToolStripMenuItem)
            {
                item.Padding = new WinForms.Padding(20, 6, 24, 6);
            }
        }

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
