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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        ShowMainWindow();
        _ = _viewModel.RefreshAsync();
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
        _mainWindow.Activate();
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
        base.OnExit(e);
    }
}
