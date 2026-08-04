using System.ComponentModel;
using System.IO;
using System.Windows;
using NitTray.Services;
using NitTray.Tray;
using NitTray.ViewModels;

using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;

namespace NitTray;

public partial class App : Application, IHotKeyCoordinator
{
    // One press of a global brightness shortcut moves every display by this much.
    private const int BrightnessStepPercent = 10;

    private TrayIconHost? _tray;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private BrightnessOsdWindow? _osdWindow;
    private MainViewModel? _viewModel;
    private SettingsViewModel? _settingsViewModel;
    private SystemRefreshTrigger? _refreshTrigger;
    private SingleInstance? _singleInstance;
    private HotKeyService? _hotKeys;
    private HotKeyApplyResult _hotKeyResult;
    private AppSettings _settings = new();

    // Shared with the settings window's key recorder and with ShortcutsPage, which
    // NavigationView constructs on its own and so cannot be handed the instance.
    internal SettingsViewModel Settings =>
        _settingsViewModel ??= new SettingsViewModel(_settings, this);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Log unhandled startup exceptions (e.g. a XAML load failure) instead of
        // silently dying at launch.
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal("Dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal("AppDomain", args.ExceptionObject as Exception);

        // One instance per session: if already running, surface it and exit.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.SignalExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        var service = new AppleDisplayService();
        var driverInstaller = new WinUsbDriverInstallService();
        _viewModel = new MainViewModel(service, driverInstaller);
        _viewModel.DriverSetupFailed += OnDriverSetupFailed;
        _viewModel.DriverUninstallRequested += OnDriverUninstallRequested;
        _viewModel.DriverUninstallSucceeded += OnDriverUninstallSucceeded;
        _viewModel.BrightnessStepped += OnBrightnessStepped;

        _mainWindow = new MainWindow { DataContext = _viewModel };
        _mainWindow.Closing += OnMainWindowClosing;

        // Follow the Windows light/dark setting (also applies the Mica backdrop).
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        ApplySubtextContrast();
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (_, _) => ApplySubtextContrast();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(_mainWindow);

        _tray = new TrayIconHost();
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.RefreshRequested += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _ = _viewModel.RefreshAsync();
            }
        };
        _tray.OpenLogRequested += (_, _) => OpenDiagnosticsLog();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.QuitRequested += (_, _) => RequestShutdown();
        _tray.AboutRequested += (_, _) => ShowAbout();

        // Auto-refresh when Windows signals the display set may have changed; the
        // device-change watch is hooked to the main window's message loop.
        _refreshTrigger = new SystemRefreshTrigger();
        _refreshTrigger.Refresh = OnAutoRefreshRequestedAsync;
        _refreshTrigger.AttachDeviceNotifications(_mainWindow);

        // Global brightness shortcuts, off unless the user enabled them. They ride on
        // the same (hidden) main-window message loop as the device-change watch.
        _settings = SettingsStore.Load();
        _hotKeys = new HotKeyService(_mainWindow);
        _hotKeys.BrightnessUpPressed += (_, _) => _viewModel?.StepBrightness(BrightnessStepPercent);
        _hotKeys.BrightnessDownPressed += (_, _) => _viewModel?.StepBrightness(-BrightnessStepPercent);
        _ = ApplyHotKeySettings(persist: false);

        // Surface the window when a later launch asks us to (callback runs off the
        // UI thread).
        _singleInstance.ListenForActivation(
            () => Dispatcher.InvokeAsync(ShowMainWindow));

        ShowMainWindow();
        _ = _viewModel.RefreshAsync();
    }

    private static void LogFatal(string source, Exception? ex)
    {
        var message = ex?.ToString() ?? "Unknown error (no exception object).";
        DiagnosticLog.WriteCritical($"FATAL [{source}]: {message}");
        try
        {
            System.Windows.MessageBox.Show(
                "NitTray hit an unexpected error and may not work correctly.\n\n" +
                $"Details have been written to:\n{DiagnosticLog.FilePath}\n\n" + message,
                "NitTray — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // A message box may be impossible this early; the log line is what matters.
        }
    }

    // Fluent's default subtext tokens are hard to read in the light theme; darken the
    // secondary/tertiary brushes there (dark theme is unaffected). Re-applied on every
    // theme change because WPF-UI swaps the theme dictionary underneath us.
    private void ApplySubtextContrast()
    {
        var isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                     == Wpf.Ui.Appearance.ApplicationTheme.Dark;

        var secondary = isDark
            ? System.Windows.Media.Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)
            : System.Windows.Media.Color.FromArgb(0xC8, 0x00, 0x00, 0x00); // ~78% vs Fluent's ~62%
        var tertiary = isDark
            ? System.Windows.Media.Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF)
            : System.Windows.Media.Color.FromArgb(0xA8, 0x00, 0x00, 0x00); // ~66% vs Fluent's ~45%

        Resources["TextFillColorSecondaryBrush"] = new System.Windows.Media.SolidColorBrush(secondary);
        Resources["TextFillColorTertiaryBrush"] = new System.Windows.Media.SolidColorBrush(tertiary);
    }

    private async Task<bool> OnAutoRefreshRequestedAsync(string reason)
    {
        if (_viewModel is null)
        {
            return false;
        }

        DiagnosticLog.Write($"Auto-refresh triggered: {reason}.");
        return await _viewModel.RefreshAsync().ConfigureAwait(true);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        // Force the window to the top of the z-order. A plain Activate() is often
        // ignored by Windows' foreground-lock (it just flashes the taskbar) when the
        // request comes from a tray click or a second instance that has already
        // exited; toggling Topmost reliably raises it, then we drop Topmost again.
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    // Opens the settings window on the About page (footer link and the tray
    // "About NitTray" item). Same window as ShowSettings, different landing page.
    public void ShowAbout() => ShowSettings(SettingsPage.About);

    // Opens or re-focuses the single settings window (tray menu and footer link).
    public void ShowSettings() => ShowSettings(SettingsPage.Shortcuts);

    private void ShowSettings(SettingsPage page)
    {
        // One view-model now outlives the window, so re-surface the live registration
        // state on every open instead of whatever the previous visit left behind.
        Settings.RefreshStatus();

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Settings, page);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;

            if (_mainWindow is not null && _mainWindow.IsVisible)
            {
                _settingsWindow.Owner = _mainWindow;
            }
            else
            {
                _settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            _settingsWindow.Show();
        }
        else
        {
            // Already open: re-target it so "About" doesn't just re-focus Shortcuts.
            _settingsWindow.NavigateTo(page);
        }

        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    // Registers whatever the current settings ask for. Persisting is opt-in so the
    // startup pass doesn't rewrite the file it just read.
    private HotKeyApplyResult ApplyHotKeySettings(bool persist)
    {
        if (persist)
        {
            SettingsStore.Save(_settings);
        }

        _hotKeyResult = _hotKeys?.Apply(
                            _settings.BrightnessHotKeysEnabled,
                            _settings.BrightnessUp,
                            _settings.BrightnessDown)
                        ?? new HotKeyApplyResult(
                            HotKeyRegistrationStatus.Failed, HotKeyRegistrationStatus.Failed);
        return _hotKeyResult;
    }

    HotKeyApplyResult IHotKeyCoordinator.Current => _hotKeyResult;

    HotKeyApplyResult IHotKeyCoordinator.Apply(AppSettings settings)
    {
        _settings = settings ?? _settings;
        return ApplyHotKeySettings(persist: true);
    }

    void IHotKeyCoordinator.SetShortcutsSuspended(bool suspended)
    {
        if (_hotKeys is null)
        {
            return;
        }

        _ = suspended
            ? _hotKeys.Apply(enabled: false, up: null, down: null)
            : ApplyHotKeySettings(persist: false);
    }

    // The window is usually hidden in the tray, so a shortcut needs its own feedback.
    private void OnBrightnessStepped(object? sender, BrightnessStepEventArgs e)
    {
        _osdWindow ??= new BrightnessOsdWindow();

        if (e.Percent is int percent)
        {
            _osdWindow.ShowLevel(percent, e.Caption);
        }
        else
        {
            _osdWindow.ShowMessage(e.Caption);
        }
    }

    private void OnDriverSetupFailed(object? sender, string message)
    {
        const string caption = "NitTray — driver setup";
        if (_mainWindow is not null)
        {
            System.Windows.MessageBox.Show(
                _mainWindow, message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            System.Windows.MessageBox.Show(
                message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDriverUninstallRequested(object? sender, DisplayViewModel display)
    {
        if (_viewModel is null)
        {
            return;
        }

        const string caption = "NitTray — uninstall driver";
        var prompt =
            $"Removing the WinUSB driver from {display.ProductName} will restore the " +
            "default Windows driver.\n\n" +
            "NitTray will not be able to control this display's brightness until setup " +
            "is run again.\n\n" +
            "Uninstall the driver now?";

        var choice = _mainWindow is not null
            ? System.Windows.MessageBox.Show(
                _mainWindow, prompt, caption, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No)
            : System.Windows.MessageBox.Show(
                prompt, caption, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);

        if (choice == MessageBoxResult.Yes)
        {
            _ = _viewModel.UninstallDriverAsync(display);
        }
    }

    private void OnDriverUninstallSucceeded(object? sender, string message)
    {
        const string caption = "NitTray — uninstall driver";
        if (_mainWindow is not null)
        {
            System.Windows.MessageBox.Show(
                _mainWindow, message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(
                message, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window only hides it; the tray icon keeps the app alive.
        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void RequestShutdown()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Closing -= OnMainWindowClosing;
            _mainWindow.Close();
        }

        Shutdown();
    }

    private static void OpenDiagnosticsLog()
    {
        try
        {
            var path = DiagnosticLog.FilePath;
            if (!File.Exists(path))
            {
                DiagnosticLog.Write("Log opened before any enumeration ran.");
            }

            // Open the parent folder with the log file pre-selected.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_refreshTrigger is not null)
        {
            _refreshTrigger.Refresh = null;
            _refreshTrigger.Dispose();
        }

        // Release the global shortcuts before the HWND goes away.
        _hotKeys?.Dispose();
        _osdWindow?.Close();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
