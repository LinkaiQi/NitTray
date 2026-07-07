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

    // Every link and the donate button carries its destination URL in Tag; open it
    // in the user's default browser. Handles both FrameworkElement (Button) and
    // FrameworkContentElement (inline Hyperlink) senders.
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
