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

    // Every link row and the donate button carry their destination URL in Tag; open
    // it in the user's default browser.
    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
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
