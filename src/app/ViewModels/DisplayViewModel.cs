using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using NitTray.Models;
using NitTray.Services;

namespace NitTray.ViewModels;

public sealed class DisplayViewModel : INotifyPropertyChanged
{
    private readonly ConnectedDisplay _info;
    private readonly IDisplayService _service;
    private readonly Action<DisplayViewModel>? _onUninstallRequested;

    private int _brightnessPercent;
    private string _statusText = string.Empty;
    private bool _isAvailable = true;
    private bool _suppressWrite;

    private int? _pendingPercent;
    private Task _writeLoop = Task.CompletedTask;

    public DisplayViewModel(
        ConnectedDisplay info,
        int initialPercent,
        IDisplayService service,
        Action<DisplayViewModel>? onUninstallRequested = null)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _brightnessPercent = Math.Clamp(initialPercent, 0, 100);
        _onUninstallRequested = onUninstallRequested;
        UninstallDriverCommand = new RelayCommand(
            _ => _onUninstallRequested?.Invoke(this),
            _ => IsWinUsb);
    }

    public string ProductName => _info.ProductName;

    // USB product id of this display, used to target a per-display driver uninstall.
    public ushort ProductId => _info.ProductId;

    // Only WinUSB-bound displays (currently the Pro Display XDR after setup) have a
    // driver NitTray installed and can therefore be reverted to the in-box driver.
    public bool IsWinUsb => _info.Transport == DisplayTransport.WinUsb;

    // The ⋯ menu only carries "Uninstall driver", so it shows for WinUSB displays
    // only; Studio Displays use the in-box driver and get no menu.
    public Visibility OverflowMenuVisibility => IsWinUsb
        ? Visibility.Visible
        : Visibility.Collapsed;

    // Routes back to MainViewModel (via the construction callback), which confirms
    // and performs the elevated uninstall.
    public ICommand UninstallDriverCommand { get; }

    // Identity line under the product name. See DisplayIdentity for why some
    // displays show "Serial …" and others "USB ID …".
    public string Subtitle => DisplayIdentity.Format(_info.SerialNumber);

    public bool HasError => !string.IsNullOrEmpty(_statusText);

    public int BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (clamped == _brightnessPercent)
            {
                return;
            }

            _brightnessPercent = clamped;
            OnPropertyChanged();

            if (!_suppressWrite)
            {
                ScheduleWrite(clamped);
            }
        }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (_isAvailable == value)
            {
                return;
            }
            _isAvailable = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }
            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusVisibility));
            OnPropertyChanged(nameof(HasError));
        }
    }

    public Visibility StatusVisibility => string.IsNullOrEmpty(_statusText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public void UpdateFromDevice(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped == _brightnessPercent)
        {
            return;
        }

        _suppressWrite = true;
        try
        {
            _brightnessPercent = clamped;
            OnPropertyChanged(nameof(BrightnessPercent));
        }
        finally
        {
            _suppressWrite = false;
        }
    }

    private void ScheduleWrite(int percent)
    {
        _pendingPercent = percent;
        if (!_writeLoop.IsCompleted)
        {
            return;
        }
        _writeLoop = RunWriteLoopAsync();
    }

    private async Task RunWriteLoopAsync()
    {
        // Coalesce rapid slider changes to the most recent pending value.
        while (_pendingPercent is int next)
        {
            _pendingPercent = null;
            try
            {
                await _service.SetBrightnessPercentAsync(_info, next).ConfigureAwait(true);
                if (!IsAvailable)
                {
                    IsAvailable = true;
                }
                if (!string.IsNullOrEmpty(_statusText))
                {
                    StatusText = string.Empty;
                }
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                StatusText = $"Write failed: {ex.Message}";
                _pendingPercent = null;
                break;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
