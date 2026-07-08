using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace NitTray.Tray;

// Gives the WinForms tray menu a Windows 11 Fluent look: flat themed background,
// rounded hover highlight, and thin inset separators. Rounded corners + shadow
// come from DWM (see MenuTheme.EnableRoundedCorners), a no-op on Windows 10.
internal sealed class ModernMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    private const int SelectionInsetX = 4;
    private const int SelectionInsetY = 2;
    private const int SelectionRadius = 5;
    private const int SeparatorInset = 12;
    // ToolStripDropDownMenu ignores an item's left Padding for text placement, so we
    // indent the text ourselves to get the Windows 11 shell-menu look.
    private const int TextLeftInset = 18;

    private readonly Palette _palette;

    public ModernMenuRenderer(bool dark)
        : base(new ModernColorTable(dark))
    {
        _palette = dark ? Palette.Dark : Palette.Light;
    }

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        => e.Graphics.Clear(_palette.Background);

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(
            SelectionInsetX,
            SelectionInsetY,
            e.Item.Width - (SelectionInsetX * 2),
            e.Item.Height - (SelectionInsetY * 2));

        using var path = RoundedRectangle(bounds, SelectionRadius);
        using var brush = new SolidBrush(_palette.Hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _palette.Text : _palette.TextDisabled;

        // Place text ourselves: fixed left inset + vertical centering across the full
        // item height, so it sits centered in the rounded hover highlight.
        e.TextRectangle = new Rectangle(
            TextLeftInset,
            0,
            Math.Max(0, e.Item.Width - TextLeftInset),
            e.Item.Height);
        e.TextFormat = WinForms.TextFormatFlags.Left
                     | WinForms.TextFormatFlags.VerticalCenter
                     | WinForms.TextFormatFlags.SingleLine
                     | WinForms.TextFormatFlags.NoPrefix;

        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        int y = e.Item.Height / 2;
        using var pen = new Pen(_palette.Separator, 1f);
        g.DrawLine(pen, SeparatorInset, y, e.Item.Width - SeparatorInset, y);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        // The window is clipped to rounded corners by DWM and DWM draws the shadow,
        // so a hard 1px rectangle border would poke out at the corners — skip it.
    }

    private static GraphicsPath RoundedRectangle(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private readonly struct Palette
    {
        public required Color Background { get; init; }
        public required Color Hover { get; init; }
        public required Color Text { get; init; }
        public required Color TextDisabled { get; init; }
        public required Color Separator { get; init; }

        public static readonly Palette Light = new()
        {
            Background = Color.FromArgb(0xF9, 0xF9, 0xF9),
            Hover = Color.FromArgb(0xEC, 0xEC, 0xEC),
            Text = Color.FromArgb(0x1A, 0x1A, 0x1A),
            TextDisabled = Color.FromArgb(0x9A, 0x9A, 0x9A),
            Separator = Color.FromArgb(0xE5, 0xE5, 0xE5),
        };

        public static readonly Palette Dark = new()
        {
            Background = Color.FromArgb(0x2C, 0x2C, 0x2C),
            Hover = Color.FromArgb(0x3A, 0x3A, 0x3A),
            Text = Color.FromArgb(0xF0, 0xF0, 0xF0),
            TextDisabled = Color.FromArgb(0x7A, 0x7A, 0x7A),
            Separator = Color.FromArgb(0x3A, 0x3A, 0x3A),
        };
    }
}

// Flattens the ProfessionalRenderer's gradients/margins so only our overrides
// above show through.
internal sealed class ModernColorTable : WinForms.ProfessionalColorTable
{
    private readonly Color _bg;
    private readonly Color _hover;
    private readonly Color _border;

    public ModernColorTable(bool dark)
    {
        UseSystemColors = false;
        _bg = dark ? Color.FromArgb(0x2C, 0x2C, 0x2C) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        _hover = dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEC, 0xEC, 0xEC);
        _border = dark ? Color.FromArgb(0x38, 0x38, 0x38) : Color.FromArgb(0xE5, 0xE5, 0xE5);
    }

    public override Color ToolStripDropDownBackground => _bg;
    public override Color ImageMarginGradientBegin => _bg;
    public override Color ImageMarginGradientMiddle => _bg;
    public override Color ImageMarginGradientEnd => _bg;
    public override Color MenuBorder => _border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => _hover;
    public override Color MenuItemSelectedGradientBegin => _hover;
    public override Color MenuItemSelectedGradientEnd => _hover;
    public override Color MenuItemPressedGradientBegin => _bg;
    public override Color MenuItemPressedGradientMiddle => _bg;
    public override Color MenuItemPressedGradientEnd => _bg;
    public override Color SeparatorDark => _border;
    public override Color SeparatorLight => _border;
}

internal static class MenuTheme
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // True when Windows apps use the dark theme (Personalize\AppsUseLightTheme == 0).
    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    // Round the popup window's corners (Windows 11+); no-op on Windows 10.
    public static void EnableRoundedCorners(IntPtr hwnd)
    {
        try
        {
            int preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // dwmapi is always present on supported targets; guard anyway.
        }
    }
}
