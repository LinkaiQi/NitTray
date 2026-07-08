using System.Diagnostics;
using System.Windows;
using NitTray.ViewModels;
using Wpf.Ui.Controls;

namespace NitTray;

public partial class AboutWindow : FluentWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    // Open the URL carried in the sender's Tag (Button or inline Hyperlink) in the
    // default browser.
    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag
                  ?? (sender as FrameworkContentElement)?.Tag;

        if (tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(url);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: a missing browser or blocked shell-exec shouldn't crash the app.
        }
    }
}
