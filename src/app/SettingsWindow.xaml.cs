using System.Windows.Input;
using NitTray.Pages;
using NitTray.Services;
using NitTray.ViewModels;
using Wpf.Ui.Controls;

namespace NitTray;

// Single home for everything that isn't the display list. Two tabs keep the
// interactive settings and the static About text apart without a second window.
public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        // Assigned here rather than in XAML because NavigationView copies its
        // ItemTemplate onto each item, and only does so when the property changes — so
        // it has to be set once the XAML items exist, and it beats the style trigger
        // that would otherwise install WPF-UI's own top item template.
        if (TryFindResource("TopTabItemTemplate") is System.Windows.Controls.ControlTemplate tabTemplate)
        {
            RootNavigation.ItemTemplate = tabTemplate;
        }

        // NavigationView can only navigate once its template is applied. Shortcuts is
        // the reason to open the window, so it always leads; About is a tab away.
        Loaded += (_, _) => RootNavigation.Navigate(typeof(ShortcutsPage), null);

        // Leaving the window mid-capture would strand the "Press keys…" state and keep
        // the live shortcuts suspended.
        Deactivated += (_, _) => _viewModel.CancelCapture();
        Closed += (_, _) => _viewModel.CancelCapture();
    }

    // Switching pages mid-capture would strand it just like closing the window does.
    private void OnNavigating(NavigationView sender, NavigatingCancelEventArgs args)
    {
        _viewModel.CancelCapture();
    }

    // Records the next complete combination while a shortcut button is armed. Handled
    // unconditionally during capture so Tab, Space, and Alt don't drive the UI instead.
    // This lives on the window rather than the page so it fires wherever focus sits.
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_viewModel.IsCapturing)
        {
            return;
        }

        e.Handled = true;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _viewModel.CancelCapture();
            return;
        }

        // Wait for a real key: modifiers alone are not a shortcut.
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

        // Keyboard.Modifiers only reports Alt/Ctrl/Shift — WPF never sets
        // ModifierKeys.Windows — so the Win key has to be probed directly. Without
        // this, Win+Ctrl+Up would be silently recorded as plain Ctrl+Up.
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
