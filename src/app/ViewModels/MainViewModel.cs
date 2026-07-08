using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using NitTray.Models;
using NitTray.Models.Displays;
using NitTray.Services;

namespace NitTray.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDisplayService _service;
    private readonly IDriverInstallService _driverInstaller;
    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _setUpDriverCommand;

    private bool _isLoading;
    private bool _isInstallingDriver;
    private string _statusMessage = "Ready.";
    private PendingDriverSetup? _pendingSetup;
    private string _driverSetupMessage = string.Empty;

    public ObservableCollection<DisplayViewModel> Displays { get; } = new();

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand SetUpDriverCommand => _setUpDriverCommand;

    // Raised when driver setup ends in a state worth interrupting the user for
    // (a hard failure or a missing helper). The App layer shows a message box.
    public event EventHandler<string>? DriverSetupFailed;

    // Raised when the user picks "Uninstall driver" from a display's ⋯ menu. Each
    // DisplayViewModel triggers this via a callback; the App layer shows a per-display
    // confirmation prompt and, on confirm, calls UninstallDriverAsync.
    public event EventHandler<DisplayViewModel>? DriverUninstallRequested;

    // Raised after a successful driver uninstall so the App layer can confirm it.
    public event EventHandler<string>? DriverUninstallSucceeded;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EmptyVisibility));
            _refreshCommand.RaiseCanExecuteChanged();
            _setUpDriverCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsInstallingDriver
    {
        get => _isInstallingDriver;
        private set
        {
            if (_isInstallingDriver == value)
            {
                return;
            }
            _isInstallingDriver = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SetUpButtonVisibility));
            OnPropertyChanged(nameof(InstallingVisibility));
            _refreshCommand.RaiseCanExecuteChanged();
            _setUpDriverCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string DriverSetupMessage
    {
        get => _driverSetupMessage;
        private set
        {
            if (_driverSetupMessage == value)
            {
                return;
            }
            _driverSetupMessage = value;
            OnPropertyChanged();
        }
    }

    // Title + serial for the pending display, so the driver-setup card mirrors a
    // connected display card (name as the heading, serial on the line beneath).
    public string DriverSetupProductName => _pendingSetup?.ProductName ?? string.Empty;

    public string DriverSetupSerial => DisplayIdentity.Format(_pendingSetup?.SerialNumber);

    public Visibility DriverSetupVisibility => _pendingSetup is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    // The action button shows only while a setup is pending and not already running;
    // the inline progress indicator takes its place during the install.
    public Visibility SetUpButtonVisibility => _pendingSetup is not null && !IsInstallingDriver
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility InstallingVisibility => IsInstallingDriver
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility EmptyVisibility => !IsLoading && Displays.Count == 0 && _pendingSetup is null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public MainViewModel(IDisplayService service, IDriverInstallService driverInstaller)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _driverInstaller = driverInstaller ?? throw new ArgumentNullException(nameof(driverInstaller));
        _refreshCommand = new RelayCommand(
            execute: async _ => await RefreshAsync().ConfigureAwait(true),
            canExecute: _ => !IsLoading && !IsInstallingDriver);
        _setUpDriverCommand = new RelayCommand(
            execute: async _ => await SetUpDriverAsync().ConfigureAwait(true),
            canExecute: _ => _pendingSetup is not null && !IsInstallingDriver && !IsLoading);

        Displays.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EmptyVisibility));
    }

    public async Task RefreshAsync()
    {
        if (IsLoading || IsInstallingDriver)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = "Scanning for displays…";

        try
        {
            var result = await _service.EnumerateAsync().ConfigureAwait(true);

            Displays.Clear();

            foreach (var info in result.Displays)
            {
                // The display was just enumerated, but right after a plug-in its
                // control interface can need a beat before it answers a read — so
                // retry a few times before falling back to a neutral default, and
                // log the outcome so a wrong-looking slider can be diagnosed.
                int initialPercent = 50;
                bool readOk = false;
                for (int attempt = 1; attempt <= 3 && !readOk; attempt++)
                {
                    try
                    {
                        initialPercent = await _service.ReadBrightnessPercentAsync(info).ConfigureAwait(true);
                        readOk = true;
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write(
                            $"Brightness read attempt {attempt} failed for {info.ProductName}: {ex.Message}");
                        if (attempt < 3)
                        {
                            await Task.Delay(250).ConfigureAwait(true);
                        }
                    }
                }
                if (!readOk)
                {
                    DiagnosticLog.Write(
                        $"Brightness read gave up for {info.ProductName}; showing default {initialPercent}%.");
                }

                Displays.Add(new DisplayViewModel(
                    info, initialPercent, _service, RequestDriverUninstall));
            }

            UpdatePendingSetup(result.PendingDriverSetups);

            StatusMessage = BuildStatusMessage(result.Displays.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rescan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SetUpDriverAsync()
    {
        var target = _pendingSetup;
        if (target is null || IsInstallingDriver)
        {
            return;
        }

        IsInstallingDriver = true;
        var previousMessage = DriverSetupMessage;
        DriverSetupMessage =
            "Approve the Windows permission prompt to continue. Keep the display connected.";
        StatusMessage = "Installing display driver… this can take up to a minute.";

        DriverInstallResult result;
        try
        {
            result = await _driverInstaller.InstallAsync(target).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsInstallingDriver = false;
            DriverSetupMessage = previousMessage;
            StatusMessage = "Driver setup failed.";
            DriverSetupFailed?.Invoke(this, $"Driver setup failed unexpectedly: {ex.Message}");
            return;
        }

        IsInstallingDriver = false;

        switch (result.Status)
        {
            case DriverInstallStatus.Success:
                StatusMessage = result.Message;
                // Re-enumerate; the display now binds via WinUSB and the banner self-clears.
                await RefreshAsync().ConfigureAwait(true);
                break;

            case DriverInstallStatus.Cancelled:
                DriverSetupMessage = previousMessage;
                StatusMessage = "Driver setup was cancelled.";
                break;

            default:
                DriverSetupMessage = previousMessage;
                StatusMessage = "Driver setup failed.";
                DriverSetupFailed?.Invoke(this, result.Message);
                break;
        }
    }

    // Callback handed to each DisplayViewModel so its ⋯ menu can request an uninstall.
    private void RequestDriverUninstall(DisplayViewModel display)
        => DriverUninstallRequested?.Invoke(this, display);

    public async Task UninstallDriverAsync(DisplayViewModel display)
    {
        ArgumentNullException.ThrowIfNull(display);

        if (IsInstallingDriver || IsLoading)
        {
            return;
        }

        IsInstallingDriver = true;
        StatusMessage =
            $"Uninstalling the driver for {display.ProductName}… approve the prompt, " +
            "then allow up to a minute.";

        DriverInstallResult result;
        try
        {
            result = await _driverInstaller.UninstallAsync(
                DisplayCatalog.AppleVendorId,
                display.ProductId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsInstallingDriver = false;
            StatusMessage = "Driver uninstall failed.";
            DriverSetupFailed?.Invoke(this, $"Driver uninstall failed unexpectedly: {ex.Message}");
            return;
        }

        IsInstallingDriver = false;

        switch (result.Status)
        {
            case DriverInstallStatus.Success:
                StatusMessage = result.Message;
                DriverUninstallSucceeded?.Invoke(this, result.Message);
                // Re-enumerate; the display is driverless again and returns to "needs setup".
                await RefreshAsync().ConfigureAwait(true);
                break;

            case DriverInstallStatus.Cancelled:
                StatusMessage = "Driver uninstall was cancelled.";
                break;

            default:
                StatusMessage = "Driver uninstall failed.";
                DriverSetupFailed?.Invoke(this, result.Message);
                break;
        }
    }

    private void UpdatePendingSetup(IReadOnlyList<PendingDriverSetup> pending)
    {
        var next = pending.Count > 0 ? pending[0] : null;
        _pendingSetup = next;
        DriverSetupMessage = next is null
            ? string.Empty
            : "Connected but needs a one-time driver setup before NitTray can " +
              "control its brightness.";
        OnPropertyChanged(nameof(DriverSetupProductName));
        OnPropertyChanged(nameof(DriverSetupSerial));
        OnPropertyChanged(nameof(DriverSetupVisibility));
        OnPropertyChanged(nameof(SetUpButtonVisibility));
        OnPropertyChanged(nameof(EmptyVisibility));
        _setUpDriverCommand.RaiseCanExecuteChanged();
    }

    private static string BuildStatusMessage(int displayCount) => displayCount switch
    {
        0 => "No Apple display detected.",
        1 => "Connected to 1 display.",
        _ => $"Connected to {displayCount} displays.",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
