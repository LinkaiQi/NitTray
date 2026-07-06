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

        var dark = !ModernTrayMenuRenderer.IsLightAppTheme();
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new ModernTrayMenuRenderer(dark),
            ShowImageMargin = false,      // drop the dated left icon gutter
            Font = ModernMenuFont(),
            Padding = new WinForms.Padding(3),
        };
        // Round the popup corners on Windows 11 each time it opens.
        menu.Opened += (_, _) => ModernTrayMenuRenderer.TryRoundCorners(menu.Handle);

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

        // A touch more breathing room per row than the WinForms default.
        foreach (WinForms.ToolStripItem item in menu.Items)
        {
            if (item is WinForms.ToolStripMenuItem)
            {
                item.Padding = new WinForms.Padding(10, 5, 24, 5);
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

    // Prefer the modern Windows 11 UI font; fall back to Segoe UI on older systems.
    private static Font ModernMenuFont()
    {
        foreach (var family in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                var font = new Font(family, 9.75f, FontStyle.Regular, GraphicsUnit.Point);
                if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }
                font.Dispose();
            }
            catch
            {
                // Try the next family.
            }
        }
        return WinForms.Control.DefaultFont;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
