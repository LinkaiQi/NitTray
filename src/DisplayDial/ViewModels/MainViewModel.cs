using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DisplayDial.Services;

namespace DisplayDial.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDisplayService _service;
    private readonly RelayCommand _refreshCommand;

    private bool _isLoading;
    private string _statusMessage = "Ready.";

    public ObservableCollection<DisplayViewModel> Displays { get; } = new();

    public ICommand RefreshCommand => _refreshCommand;

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

    public Visibility EmptyVisibility => !IsLoading && Displays.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public MainViewModel(IDisplayService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _refreshCommand = new RelayCommand(
            execute: async _ => await RefreshAsync().ConfigureAwait(true),
            canExecute: _ => !IsLoading);

        Displays.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EmptyVisibility));
    }

    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusMessage = "Scanning for displays…";

        try
        {
            var found = await _service.EnumerateAsync().ConfigureAwait(true);

            Displays.Clear();

            foreach (var info in found)
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

            StatusMessage = found.Count switch
            {
                0 => "No Apple display detected.",
                1 => "Connected to 1 display.",
                _ => $"Connected to {found.Count} displays.",
            };
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
