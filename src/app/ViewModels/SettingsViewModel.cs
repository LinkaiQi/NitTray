using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NitTray.Services;

namespace NitTray.ViewModels;

public enum HotKeyTarget
{
    None,
    BrightnessUp,
    BrightnessDown,
}

// Backs the Settings window. Changes apply immediately: each edit is written to
// settings.json and re-registered with Windows, and the result comes straight back
// as an inline message so a combination another app already owns is never silently
// dropped.
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly IHotKeyCoordinator _coordinator;

    private HotKeyBinding _up;
    private HotKeyBinding _down;
    private bool _enabled;
    private HotKeyTarget _capturing = HotKeyTarget.None;
    private string _statusMessage = string.Empty;
    private Wpf.Ui.Controls.InfoBarSeverity _statusSeverity =
        Wpf.Ui.Controls.InfoBarSeverity.Warning;

    public SettingsViewModel(AppSettings settings, IHotKeyCoordinator coordinator)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        _enabled = settings.BrightnessHotKeysEnabled;
        _up = settings.BrightnessUp;
        _down = settings.BrightnessDown;

        CaptureBrightnessUpCommand = new RelayCommand(_ => BeginCapture(HotKeyTarget.BrightnessUp));
        CaptureBrightnessDownCommand = new RelayCommand(_ => BeginCapture(HotKeyTarget.BrightnessDown));
        RestoreDefaultsCommand = new RelayCommand(_ => RestoreDefaults());

        // Surface a collision that happened at startup, before any edit here.
        Report(coordinator.Current);
    }

    public ICommand CaptureBrightnessUpCommand { get; }

    public ICommand CaptureBrightnessDownCommand { get; }

    public ICommand RestoreDefaultsCommand { get; }

    public bool AreShortcutsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }
            _enabled = value;
            OnPropertyChanged();
            CancelCapture();
            ApplyAndReport();
        }
    }

    public bool IsCapturing => _capturing != HotKeyTarget.None;

    public string BrightnessUpText => _capturing == HotKeyTarget.BrightnessUp
        ? "Press keys…"
        : _up.DisplayText;

    public string BrightnessDownText => _capturing == HotKeyTarget.BrightnessDown
        ? "Press keys…"
        : _down.DisplayText;

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
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public Wpf.Ui.Controls.InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set
        {
            if (_statusSeverity == value)
            {
                return;
            }
            _statusSeverity = value;
            OnPropertyChanged();
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

    public void BeginCapture(HotKeyTarget target)
    {
        if (target == HotKeyTarget.None || _capturing == target)
        {
            return;
        }

        _capturing = target;
        _coordinator.SetShortcutsSuspended(true);
        StatusMessage = "Press the new shortcut. Esc cancels.";
        StatusSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
        RaiseShortcutTextChanged();
    }

    public void CancelCapture()
    {
        if (_capturing == HotKeyTarget.None)
        {
            return;
        }

        _capturing = HotKeyTarget.None;
        _coordinator.SetShortcutsSuspended(false);
        StatusMessage = string.Empty;
        RaiseShortcutTextChanged();
    }

    // Called by the window once a complete combination has been typed. Invalid or
    // duplicate combinations leave capture running so the user can simply try again.
    public void CompleteCapture(HotKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_capturing == HotKeyTarget.None)
        {
            return;
        }

        if (!binding.IsValid)
        {
            SetStatus(
                "Add Ctrl, Alt, Shift or the Windows key — a shortcut without a modifier " +
                "would capture that key everywhere.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        var other = _capturing == HotKeyTarget.BrightnessUp ? _down : _up;
        if (binding == other)
        {
            SetStatus(
                "Brightness up and down need different combinations.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        if (_capturing == HotKeyTarget.BrightnessUp)
        {
            _up = binding;
        }
        else
        {
            _down = binding;
        }

        _capturing = HotKeyTarget.None;
        RaiseShortcutTextChanged();
        ApplyAndReport();
    }

    private void RestoreDefaults()
    {
        CancelCapture();
        _up = HotKeyBinding.DefaultBrightnessUp;
        _down = HotKeyBinding.DefaultBrightnessDown;
        RaiseShortcutTextChanged();
        ApplyAndReport();
    }

    private void ApplyAndReport()
    {
        _settings.BrightnessHotKeysEnabled = _enabled;
        _settings.BrightnessUpHotKey = _up.ToStorageString();
        _settings.BrightnessDownHotKey = _down.ToStorageString();

        var result = _coordinator.Apply(_settings);
        Report(result);
    }

    private void Report(HotKeyApplyResult result)
    {
        var takenBy = Describe(result.BrightnessUp, _up) ?? Describe(result.BrightnessDown, _down);
        if (takenBy is not null)
        {
            SetStatus(takenBy.Value.Message, takenBy.Value.Severity);
            return;
        }

        StatusMessage = string.Empty;
    }

    private static (string Message, Wpf.Ui.Controls.InfoBarSeverity Severity)? Describe(
        HotKeyRegistrationStatus status, HotKeyBinding binding) => status switch
        {
            HotKeyRegistrationStatus.AlreadyInUse => (
                $"{binding.DisplayText} is already used by another app. Choose a different combination.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning),
            HotKeyRegistrationStatus.Failed => (
                $"Windows would not register {binding.DisplayText}. See the diagnostics log for details.",
                Wpf.Ui.Controls.InfoBarSeverity.Error),
            HotKeyRegistrationStatus.Invalid => (
                "That shortcut is not valid. Restore the defaults and try again.",
                Wpf.Ui.Controls.InfoBarSeverity.Error),
            _ => null,
        };

    private void SetStatus(string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        StatusSeverity = severity;
        StatusMessage = message;
    }

    private void RaiseShortcutTextChanged()
    {
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(BrightnessUpText));
        OnPropertyChanged(nameof(BrightnessDownText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
