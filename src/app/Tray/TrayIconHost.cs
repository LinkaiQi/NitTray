using System.Drawing;
using NitTray.Services;
using WinForms = System.Windows.Forms;

namespace NitTray.Tray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Icon _icon;

    public event EventHandler? ShowRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? OpenLogRequested;
    public event EventHandler? QuitRequested;

    public TrayIconHost()
    {
        _icon = IconFactory.CreateTrayIcon();

        var menu = new WinForms.ContextMenuStrip
        {
            // Windows 11 Fluent look via ModernMenuRenderer, no icon gutter, DWM
            // rounded corners once the popup handle exists.
            Renderer = new ModernMenuRenderer(MenuTheme.IsDark()),
            ShowImageMargin = false,
            ShowCheckMargin = false,
        };
        menu.HandleCreated += (_, _) => MenuTheme.EnableRoundedCorners(menu.Handle);

        var showItem = new WinForms.ToolStripMenuItem("Open NitTray");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        var refreshItem = new WinForms.ToolStripMenuItem("Rescan Display");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        // Settings covers About too; both are tabs of the one window.
        var settingsItem = new WinForms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        // Diagnostics log for bug reports when a display isn't detected.
        var logItem = new WinForms.ToolStripMenuItem("Open Diagnostics Log");
        logItem.Click += (_, _) => OpenLogRequested?.Invoke(this, EventArgs.Empty);
        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(quitItem);

        // Horizontal room so the text inset never clips; vertical padding for
        // Windows 11-style taller rows.
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            if (item is WinForms.ToolStripMenuItem)
            {
                item.Padding = new WinForms.Padding(18, 6, 18, 6);
            }
        }

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "NitTray — Apple display brightness",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _menu = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    // The renderer bakes in its palette, so following the system theme means swapping it.
    public void ApplyTheme(bool isDark)
        => _menu.Renderer = new ModernMenuRenderer(isDark);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
