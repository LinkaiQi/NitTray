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
        // Register as the application main window so WPF-UI's theme manager applies
        // the Mica backdrop and the immersive dark/light window attribute to THIS
        // window (its theming step is a no-op when Application.MainWindow is null).
        MainWindow = _mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;

        // Match the Windows light/dark setting before the first paint so the title
        // bar, text, and Mica backdrop all render in the correct theme, then keep
        // following the OS setting while the app runs.
        ApplyMatchingTheme();
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
        _tray.ResetDriverRequested += (_, _) => OnResetDriverRequested();
        _tray.QuitRequested += (_, _) => RequestShutdown();

        ShowMainWindow();
        // Re-apply now that the window has a native handle, so the Mica backdrop and
        // the dark/light window attribute are applied to the live window (the first
        // apply runs before the handle exists, when those native calls are no-ops).
        ApplyMatchingTheme();
        _ = _viewModel.RefreshAsync();
    }

    // Detect the current Windows theme and apply the matching WPF-UI theme + Mica
    // backdrop in one shot, so the text brushes can never end up out of sync with
    // the window background (which is what makes text "invisible" in one mode).
    private static void ApplyMatchingTheme()
    {
        var systemTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme();
        var appTheme = systemTheme switch
        {
            Wpf.Ui.Appearance.SystemTheme.Dark
            or Wpf.Ui.Appearance.SystemTheme.HCBlack
            or Wpf.Ui.Appearance.SystemTheme.Glow
            or Wpf.Ui.Appearance.SystemTheme.CapturedMotion
                => Wpf.Ui.Appearance.ApplicationTheme.Dark,
            _ => Wpf.Ui.Appearance.ApplicationTheme.Light,
        };

        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            appTheme,
            Wpf.Ui.Controls.WindowBackdropType.Mica,
            true);
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
