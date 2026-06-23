using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using DisplayDial.Models;
using DisplayDial.Services;

namespace DisplayDial.ViewModels;

public sealed class DisplayViewModel : INotifyPropertyChanged
{
    private readonly StudioDisplayInfo _info;
    private readonly IDisplayService _service;

    private int _brightnessPercent;
    private string _statusText = string.Empty;
    private bool _isAvailable = true;
    private bool _suppressWrite;

    private int? _pendingPercent;
    private Task _writeLoop = Task.CompletedTask;

    public DisplayViewModel(StudioDisplayInfo info, int initialPercent, IDisplayService service)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _brightnessPercent = Math.Clamp(initialPercent, 0, 100);
    }

    public string ProductName => _info.ProductName;

    public string SerialDescription => string.IsNullOrWhiteSpace(_info.SerialNumber)
        ? $"PID 0x{_info.ProductId:X4}"
        : $"PID 0x{_info.ProductId:X4} · Serial {_info.SerialNumber}";

    // Identity line shown under the product name: serial (when reported) plus the
    // control channel, so two displays of the same model stay easy to tell apart.
    public string Subtitle
    {
        get
        {
            var connection = _info.Transport == DisplayTransport.WinUsb
                ? "WinUSB control"
                : "HID control";
            return string.IsNullOrWhiteSpace(_info.SerialNumber)
                ? $"USB-C · {connection}"
                : $"Serial {_info.SerialNumber} · {connection}";
        }
    }

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
        // Coalesce rapid slider changes: only ever write the most recent pending value.
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
