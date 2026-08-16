using System.Windows.Input;
using NitTray.Pages;
using NitTray.Services;
using NitTray.ViewModels;
using Wpf.Ui.Controls;

namespace NitTray;

// Single home for everything that isn't the display list.
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        // NavigationView copies ItemTemplate onto its items when the property changes,
        // so this has to run after the XAML items exist.
        if (TryFindResource("TopTabItemTemplate") is System.Windows.Controls.ControlTemplate tabTemplate)
        {
            RootNavigation.ItemTemplate = tabTemplate;
        }

        // Navigation needs the template applied, so it waits for Loaded.
        Loaded += (_, _) => RootNavigation.Navigate(typeof(ShortcutsPage), null);

        // Leaving mid-capture would strand it and keep the shortcuts suspended.
        Deactivated += (_, _) => _viewModel.CancelCapture();
        Closed += (_, _) => _viewModel.CancelCapture();
    }

    private void OnNavigating(NavigationView sender, NavigatingCancelEventArgs args)
    {
        _viewModel.CancelCapture();
    }

    // Records the next complete combination while a shortcut button is armed. Lives on
    // the window rather than the page so it fires wherever focus sits.
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_viewModel.IsCapturing)
        {
            return;
        }

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Alt+F4 and Alt+Space reach the app as ordinary keys, so handling them here
        // would swallow the close and register Alt+F4 globally. Leave them to Windows.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.F4 or Key.Space)
        {
            return;
        }

        e.Handled = true;

        if (key == Key.Escape)
        {
            _viewModel.CancelCapture();
            return;
        }

        if (HotKeyBinding.IsModifierKey(key))
        {
            return;
        }

        _viewModel.CompleteCapture(new HotKeyBinding(CurrentModifiers(), key));
    }

    private static HotKeyModifiers CurrentModifiers()
    {
        var modifiers = HotKeyModifiers.None;
        var pressed = Keyboard.Modifiers;

        // WPF never sets ModifierKeys.Windows, so the Win key has to be probed directly.
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= HotKeyModifiers.Win;
        }
        if (pressed.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotKeyModifiers.Control;
        }
        if (pressed.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotKeyModifiers.Alt;
        }
        if (pressed.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        return modifiers;
    }
}
