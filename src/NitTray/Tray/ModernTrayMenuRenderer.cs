using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace NitTray.Tray;

// A flat, theme-aware renderer that gives the tray ContextMenuStrip a modern
// Fluent-ish look instead of the dated WinForms default: solid background, a
// rounded hover highlight inset from the edges, thin separators, and (on Win11)
// rounded window corners. Colors follow the current Windows app theme.
internal sealed class ModernTrayMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    private readonly Color _background;
    private readonly Color _text;
    private readonly Color _textDisabled;
    private readonly Color _hover;
    private readonly Color _separator;
    private readonly Color _border;

    public ModernTrayMenuRenderer(bool dark)
        : base(new WinForms.ProfessionalColorTable())
    {
        RoundedEdges = false;
        if (dark)
        {
            _background = Color.FromArgb(43, 43, 43);
            _text = Color.FromArgb(240, 240, 240);
            _textDisabled = Color.FromArgb(130, 130, 130);
            _hover = Color.FromArgb(61, 61, 61);
            _separator = Color.FromArgb(69, 69, 69);
            _border = Color.FromArgb(31, 31, 31);
        }
        else
        {
            _background = Color.FromArgb(249, 249, 249);
            _text = Color.FromArgb(26, 26, 26);
            _textDisabled = Color.FromArgb(154, 154, 154);
            _hover = Color.FromArgb(233, 233, 233);
            _separator = Color.FromArgb(226, 226, 226);
            _border = Color.FromArgb(224, 224, 224);
        }
    }

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_background);
        e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        var r = e.ToolStrip.ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        using var pen = new Pen(_border);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        var highlight = Rectangle.Inflate(bounds, -4, -1);
        using var path = RoundedRect(highlight, 6);
        using var brush = new SolidBrush(_hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _text : _textDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(_separator);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // --- theme + Win11 rounded-corner helpers -------------------------------

    // True when Windows apps use the light theme (which is what context menus
    // follow). Defaults to dark if the value can't be read.
    public static bool IsLightAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    // Ask DWM to round the popup's corners (Windows 11). No-op / harmless on
    // earlier Windows where the attribute is unsupported.
    public static void TryRoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        try
        {
            var pref = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
        }
        catch
        {
            // Older Windows without this DWM attribute — the flat renderer alone
            // is still a clear improvement.
        }
    }
}
