using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NitTray.Services.Native;

namespace NitTray.Services;

public enum HotKeyRegistrationStatus
{
    // Global shortcuts are switched off, so nothing was registered.
    Disabled,
    Registered,
    // Another application owns the combination (ERROR_HOTKEY_ALREADY_REGISTERED).
    AlreadyInUse,
    // Missing or modifier-less binding; never handed to Windows.
    Invalid,
    Failed,
}

public readonly record struct HotKeyApplyResult(
    HotKeyRegistrationStatus BrightnessUp,
    HotKeyRegistrationStatus BrightnessDown);

// How the settings UI talks to whoever owns the live registrations (the App layer).
public interface IHotKeyCoordinator
{
    // What Windows accepted the last time the shortcuts were applied, so the settings
    // window can report a startup collision the moment it opens.
    HotKeyApplyResult Current { get; }

    // Persists the settings and re-registers the shortcuts, reporting what Windows
    // accepted.
    HotKeyApplyResult Apply(AppSettings settings);

    // Releases the registrations while the user types a new combination, which would
    // otherwise arrive as WM_HOTKEY instead of a key press.
    void SetShortcutsSuspended(bool suspended);
}

// Registers the global brightness shortcuts against the main window's HWND and turns
// WM_HOTKEY into events. The window is normally hidden in the tray, but its handle
// keeps pumping messages. MOD_NOREPEAT keeps one press to one step.
internal sealed class HotKeyService : IDisposable
{
    // Application-defined ids must be 0x0000-0xBFFF.
    private const int IdBrightnessUp = 0xB001;
    private const int IdBrightnessDown = 0xB002;

    private readonly Window _window;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _upRegistered;
    private bool _downRegistered;
    private bool _disposed;

    public event EventHandler? BrightnessUpPressed;
    public event EventHandler? BrightnessDownPressed;

    public HotKeyService(Window window)
        => _window = window ?? throw new ArgumentNullException(nameof(window));

    // Re-registers both shortcuts from scratch; previous ones are always released first.
    public HotKeyApplyResult Apply(bool enabled, HotKeyBinding? up, HotKeyBinding? down)
    {
        if (_disposed)
        {
            return new HotKeyApplyResult(
                HotKeyRegistrationStatus.Disabled, HotKeyRegistrationStatus.Disabled);
        }

        UnregisterAll();

        if (!enabled)
        {
            DiagnosticLog.Write("Hotkeys: no global brightness shortcuts registered.");
            return new HotKeyApplyResult(
                HotKeyRegistrationStatus.Disabled, HotKeyRegistrationStatus.Disabled);
        }

        if (!EnsureHook())
        {
            return new HotKeyApplyResult(
                HotKeyRegistrationStatus.Failed, HotKeyRegistrationStatus.Failed);
        }

        var upStatus = Register(IdBrightnessUp, up, "brightness up");
        _upRegistered = upStatus == HotKeyRegistrationStatus.Registered;

        // The UI blocks a duplicate, but settings.json is hand-editable. Registering the
        // same chord twice would collide with our own first registration and be reported
        // as another application owning it.
        HotKeyRegistrationStatus downStatus;
        if (_upRegistered && down is not null && down == up)
        {
            DiagnosticLog.Write(
                $"Hotkeys: {down.DisplayText} is set for both directions; brightness down not registered.");
            downStatus = HotKeyRegistrationStatus.Invalid;
        }
        else
        {
            downStatus = Register(IdBrightnessDown, down, "brightness down");
        }

        _downRegistered = downStatus == HotKeyRegistrationStatus.Registered;

        return new HotKeyApplyResult(upStatus, downStatus);
    }

    private bool EnsureHook()
    {
        if (_source is not null)
        {
            return true;
        }

        try
        {
            _hwnd = new WindowInteropHelper(_window).EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            if (_source is null)
            {
                DiagnosticLog.Write("Hotkeys: no HwndSource for the main window; shortcuts unavailable.");
                return false;
            }

            _source.AddHook(HotKeyHook);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Hotkeys: could not hook the main window ({ex.Message}).");
            _source = null;
            return false;
        }
    }

    private HotKeyRegistrationStatus Register(int id, HotKeyBinding? binding, string label)
    {
        if (binding is null || !binding.IsValid)
        {
            DiagnosticLog.Write($"Hotkeys: no valid {label} shortcut configured.");
            return HotKeyRegistrationStatus.Invalid;
        }

        if (User32Native.RegisterHotKey(
                _hwnd,
                id,
                binding.NativeModifiers | User32Native.MOD_NOREPEAT,
                binding.VirtualKey))
        {
            DiagnosticLog.Write($"Hotkeys: registered {binding.DisplayText} for {label}.");
            return HotKeyRegistrationStatus.Registered;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == User32Native.ERROR_HOTKEY_ALREADY_REGISTERED)
        {
            DiagnosticLog.Write(
                $"Hotkeys: {binding.DisplayText} ({label}) is already taken by another application.");
            return HotKeyRegistrationStatus.AlreadyInUse;
        }

        DiagnosticLog.Write(
            $"Hotkeys: registering {binding.DisplayText} ({label}) failed with Win32 error {error}.");
        return HotKeyRegistrationStatus.Failed;
    }

    private IntPtr HotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != User32Native.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        // ToInt32 is checked, and any process can post WM_HOTKEY with an out-of-range
        // wParam, which would throw out of the window procedure.
        switch (wParam.ToInt64())
        {
            case IdBrightnessUp:
                handled = true;
                BrightnessUpPressed?.Invoke(this, EventArgs.Empty);
                break;
            case IdBrightnessDown:
                handled = true;
                BrightnessDownPressed?.Invoke(this, EventArgs.Empty);
                break;
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_upRegistered)
        {
            _ = User32Native.UnregisterHotKey(_hwnd, IdBrightnessUp);
            _upRegistered = false;
        }
        if (_downRegistered)
        {
            _ = User32Native.UnregisterHotKey(_hwnd, IdBrightnessDown);
            _downRegistered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        UnregisterAll();
        _source?.RemoveHook(HotKeyHook);
        _source = null;
    }
}
