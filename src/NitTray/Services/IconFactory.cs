using System.Drawing;

namespace NitTray.Services;

// Supplies the notification-area (tray) icon by loading the app's brand icon
// (Assets/AppIcon.ico, embedded as a WPF resource) at the size Windows wants for
// the tray, which is DPI-aware.
internal static class IconFactory
{
    public static Icon CreateTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute);
        var desired = System.Windows.Forms.SystemInformation.SmallIconSize;
        var info = System.Windows.Application.GetResourceStream(uri);
        if (info is not null)
        {
            using var stream = info.Stream;
            // Icon picks the best-matching embedded size for the requested dimensions.
            return new Icon(stream, desired);
        }

        // Fallback (should never happen — the icon is embedded in the assembly).
        return SystemIcons.Application;
    }
}
