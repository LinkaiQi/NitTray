using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DisplayDial.Models;
using DisplayDial.Services;

namespace DisplayDial.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDisplayService _service;
    private readonly IDriverInstallService _driverInstaller;
    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _setUpDriverCommand;
    private readonly RelayCommand _resetDriverCommand;

    private bool _isLoading;
    private bool _isInstallingDriver;
    private string _statusMessage = "Ready.";
    private PendingDriverSetup? _pendingSetup;
    private string _driverSetupMessage = string.Empty;

    public ObservableCollection<DisplayViewModel> Displays { get; } = new();

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand SetUpDriverCommand => _setUpDriverCommand;

    // Removes the WinUSB driver from the Apple Pro Display XDR and reverts it to
    // the default Windows driver. Mainly a testing aid so the install flow can be
    // re-exercised without hand-running pnputil / Device Manager.
    public ICommand ResetDriverCommand => _resetDriverCommand;

    // Raised when driver setup ends in a state worth interrupting the user for
    // (a hard failure or a missing helper). The App layer shows a message box.
    public event EventHandler<string>? DriverSetupFailed;

    // Raised after a successful driver reset so the App layer can confirm it.
    public event EventHandler<string>? DriverResetSucceeded;

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
            _resetDriverCommand.RaiseCanExecuteChanged();
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
            _refreshCommand.RaiseCanExecuteChanged();
            _setUpDriverCommand.RaiseCanExecuteChanged();
            _resetDriverCommand.RaiseCanExecuteChanged();
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

    public Visibility DriverSetupVisibility => _pendingSetup is not null
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
        _resetDriverCommand = new RelayCommand(
            execute: async _ => await ResetDriverAsync().ConfigureAwait(true),
            canExecute: _ => !IsInstallingDriver && !IsLoading);

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
                int initialPercent = 50;
                try
                {
                    initialPercent = await _service.ReadBrightnessPercentAsync(info).ConfigureAwait(true);
                }
                catch
                {
                    // Tolerate read failure; user can still drive the slider.
                }

                Displays.Add(new DisplayViewModel(info, initialPercent, _service));
            }

            UpdatePendingSetup(result.PendingDriverSetups);

            StatusMessage = BuildStatusMessage(result.Displays.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
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
            $"Setting up {target.ProductName}… approve the Windows permission prompt, then " +
            "allow up to a minute while Windows restarts the display. Please don't unplug it.";
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
                // Re-enumerate: the display should now bind via WinUSB and the
                // banner clears itself when it is no longer pending.
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

    public async Task ResetDriverAsync()
    {
        if (IsInstallingDriver || IsLoading)
        {
            return;
        }

        IsInstallingDriver = true;
        StatusMessage = "Resetting display driver… approve the prompt, then allow up to a minute.";

        DriverInstallResult result;
        try
        {
            result = await _driverInstaller.UninstallAsync(
                AppleDisplays.VendorId,
                AppleDisplays.ProDisplayXdrProductId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsInstallingDriver = false;
            StatusMessage = "Driver reset failed.";
            DriverSetupFailed?.Invoke(this, $"Driver reset failed unexpectedly: {ex.Message}");
            return;
        }

        IsInstallingDriver = false;

        switch (result.Status)
        {
            case DriverInstallStatus.Success:
                StatusMessage = result.Message;
                DriverResetSucceeded?.Invoke(this, result.Message);
                // Re-enumerate: the display is now driverless again, so it returns
                // to the "needs setup" state and the install flow can be retested.
                await RefreshAsync().ConfigureAwait(true);
                break;

            case DriverInstallStatus.Cancelled:
                StatusMessage = "Driver reset was cancelled.";
                break;

            default:
                StatusMessage = "Driver reset failed.";
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
            : $"{next.ProductName} is connected but needs a one-time driver setup before " +
              "DisplayDial can control its brightness.";
        OnPropertyChanged(nameof(DriverSetupVisibility));
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
