using System.Drawing;
using Microsoft.Win32;

namespace NitTray.Services;

// Supplies the notification-area (tray) icon. The glyph is a monitor with a
// brightness sun, shipped as two monochrome variants; we pick the one that
// contrasts with the current taskbar theme (white on a dark taskbar, dark on a
// light one) so it stays legible either way.
internal static class IconFactory
{
    // True when Windows is using the light taskbar/system theme, so we should use
    // the dark glyph. Defaults to the dark-taskbar assumption (white glyph).
    public static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // SystemUsesLightTheme controls the taskbar/tray area specifically.
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    // Loads the tray icon that matches the current taskbar theme, at the size
    // Windows wants for the notification area (DPI-aware).
    public static Icon CreateTrayIcon()
    {
        var asset = IsLightTaskbar() ? "tray-dark.ico" : "tray-light.ico";
        var uri = new Uri($"pack://application:,,,/Assets/{asset}", UriKind.Absolute);

        var desired = System.Windows.Forms.SystemInformation.SmallIconSize;
        var info = System.Windows.Application.GetResourceStream(uri);
        if (info is not null)
        {
            using var stream = info.Stream;
            // Icon picks the best-matching embedded size for the requested dimensions.
            return new Icon(stream, desired);
        }

        // Fallback (should never happen — the asset is embedded in the assembly).
        return SystemIcons.Application;
    }
}
