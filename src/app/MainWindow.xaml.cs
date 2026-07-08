using System.Windows;
using Wpf.Ui.Controls;

namespace NitTray;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.ShowAbout();
    }

    // Show the per-display ⋯ overflow ContextMenu below the button on left-click.
    private void OnOverflowMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.ContextMenu is System.Windows.Controls.ContextMenu menu)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
