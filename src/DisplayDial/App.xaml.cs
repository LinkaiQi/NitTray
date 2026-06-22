using System.ComponentModel;
using System.IO;
using System.Windows;
using DisplayDial.Services;
using DisplayDial.Tray;
using DisplayDial.ViewModels;

using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;

namespace DisplayDial;

public partial class App : Application
{
    private TrayIconHost? _tray;
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var service = new StudioDisplayService();
        var driverInstaller = new WinUsbDriverInstallService();
        _viewModel = new MainViewModel(service, driverInstaller);
        _viewModel.DriverSetupFailed += OnDriverSetupFailed;
        _viewModel.DriverResetSucceeded += OnDriverResetSucceeded;

        _mainWindow = new MainWindow { DataContext = _viewModel };
        _mainWindow.Closing += OnMainWindowClosing;

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
        _tray.ResetDriverRequested += (_, _) => OnResetDriverRequested();
        _tray.QuitRequested += (_, _) => RequestShutdown();

        ShowMainWindow();
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
        const string caption = "DisplayDial — driver setup";
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

    private void OnResetDriverRequested()
    {
        if (_viewModel is null)
        {
            return;
        }

        const string caption = "DisplayDial — reset driver";
        const string prompt =
            "This removes the WinUSB driver from the Apple Pro Display XDR and reverts it " +
            "to the default Windows driver.\n\n" +
            "DisplayDial will not be able to control its brightness until you run setup " +
            "again. This is mainly intended for testing the install flow.\n\n" +
            "Reset the driver now?";

        var choice = _mainWindow is not null
            ? System.Windows.MessageBox.Show(
                _mainWindow, prompt, caption, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No)
            : System.Windows.MessageBox.Show(
                prompt, caption, MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);

        if (choice == MessageBoxResult.Yes)
        {
            _ = _viewModel.ResetDriverAsync();
        }
    }

    private void OnDriverResetSucceeded(object? sender, string message)
    {
        const string caption = "DisplayDial — reset driver";
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
        _tray?.Dispose();
        base.OnExit(e);
    }
}
