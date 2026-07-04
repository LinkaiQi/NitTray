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

public partial class App : Application
{
    private TrayIconHost? _tray;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private SystemRefreshTrigger? _refreshTrigger;
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Enforce one instance per session. If a copy is already running, ask it to
        // surface its window and exit before creating any windows or a tray icon.
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            DiagnosticLog.Write(
                "Startup: another NitTray instance already owns this session; " +
                "requested it to focus and exiting this launch.");
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

        _mainWindow = new MainWindow { DataContext = _viewModel };
        _mainWindow.Closing += OnMainWindowClosing;

        // Match the Windows light/dark setting and keep following it while running
        // (also applies the Mica backdrop on the FluentWindow).
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
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
        _tray.QuitRequested += (_, _) => RequestShutdown();

        // Auto-refresh the display list when Windows signals a moment where the set
        // of connected displays may have changed (unlock, resume from sleep, monitor
        // arrangement change, etc.) so the user doesn't have to refresh manually.
        _refreshTrigger = new SystemRefreshTrigger();
        _refreshTrigger.RefreshRequested += OnAutoRefreshRequested;

        // Now that the window exists, listen for later launches and surface the
        // window when one asks us to (its callback runs off the UI thread).
        _singleInstance.ListenForActivation(OnSecondInstanceActivation);

        ShowMainWindow();
        _ = _viewModel.RefreshAsync();
    }

    private void OnSecondInstanceActivation()
    {
        // Runs on a thread-pool thread (fired by the named event); marshal to the UI
        // thread before touching the window.
        Dispatcher.InvokeAsync(() =>
        {
            DiagnosticLog.Write(
                "Received a focus request from a second launch; surfacing the window.");
            ShowMainWindow();
        });
    }

    private void OnAutoRefreshRequested(object? sender, string reason)
    {
        if (_viewModel is null)
        {
            return;
        }

        DiagnosticLog.Write($"Auto-refresh triggered: {reason}.");
        _ = _viewModel.RefreshAsync();
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
            $"This removes the WinUSB driver from {display.ProductName} and reverts it " +
            "to the default Windows driver.\n\n" +
            "NitTray will not be able to control its brightness until you run setup " +
            "again.\n\n" +
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
            _refreshTrigger.RefreshRequested -= OnAutoRefreshRequested;
            _refreshTrigger.Dispose();
        }

        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
