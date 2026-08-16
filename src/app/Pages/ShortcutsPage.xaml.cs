using System.Windows.Controls;
using NitTray.ViewModels;

namespace NitTray.Pages;

public partial class ShortcutsPage : Page
{
    // NavigationView constructs pages itself, so the view-model is pulled from the app
    // rather than injected. It has to be the very instance the window's key recorder
    // talks to, since that one owns the live hotkey registrations.
    public ShortcutsPage()
    {
        InitializeComponent();
        DataContext = (System.Windows.Application.Current as App)?.Settings;
    }
}
