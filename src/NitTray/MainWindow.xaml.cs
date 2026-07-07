using System.Windows;
using Wpf.Ui.Controls;

namespace NitTray;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Opens the About window from the footer "About NitTray" link (also reachable
    // from the tray menu).
    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        (System.Windows.Application.Current as App)?.ShowAbout();
    }

    // Opens the per-display ⋯ overflow menu on left-click (Fluent "more options"
    // pattern). The button carries the MoreHorizontal glyph and no chevron, so this
    // pure-UI handler just shows its attached ContextMenu below the button.
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
