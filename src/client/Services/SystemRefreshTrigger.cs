using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NitTray.Services;

// Watches Windows for moments when the set of connected displays may have changed
// while NitTray wasn't looking, and asks for a display refresh so the list and
// brightness values stay in sync without the user clicking "Rescan":
//   * the user unlocking / logging on / (re)connecting to the session,
//   * the machine waking from sleep,
//   * the monitor arrangement changing, and
//   * USB device nodes appearing/removing (WM_DEVICECHANGE) — this is what fires
//     when an Apple display's HID/brightness interface finishes enumerating, which
//     can happen a moment AFTER the monitor itself is added.
//
// Several events often fire in a burst when a display is plugged in (a display-
// settings change plus a stream of device-node changes as each USB interface
// enumerates), so we debounce and only rescan once things settle. Because the HID
// interface can still be a beat away from being openable even after it appears, we
// also fire a couple of spaced "settle" retries so a late interface is picked up
// without the user intervening.
internal sealed class SystemRefreshTrigger : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    // Long enough to coalesce the burst of events a single unlock/resume/plug-in
    // produces, short enough to still feel immediate.
    private const int DebounceMilliseconds = 750;

    // After the first rescan, retry a couple more times so a display whose HID
    // interface becomes openable a moment later still shows up on its own.
    private const int SettleRetryMilliseconds = 2500;
    private const int MaxSettleRetries = 2;

    private readonly Dispatcher _dispatcher;
    private readonly System.Threading.Timer _debounce;
    private readonly object _gate = new();

    private HwndSource? _hwndSource;
    private string _pendingReason = string.Empty;
    private int _retriesRemaining;
    private bool _disposed;

    // Raised on the UI thread when displays should be re-enumerated; the string is
    // a log-friendly reason.
    public event EventHandler<string>? RefreshRequested;

    public SystemRefreshTrigger()
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _debounce = new System.Threading.Timer(
            OnDebounceElapsed, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // Hooks WM_DEVICECHANGE on the given window so USB device arrivals/removals
    // (the signal that an Apple display's HID interface has enumerated) trigger a
    // rescan. Uses the window's HWND — realized here if the window hasn't been shown
    // yet — which keeps receiving broadcast device-change messages even while the
    // window is hidden in the tray. Call once, after the window is constructed.
    public void AttachDeviceNotifications(System.Windows.Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(DeviceChangeHook);
        }
        catch
        {
            // If message hooking fails for any reason, the SystemEvents-based
            // triggers above still keep the list reasonably fresh.
        }
    }

    private IntPtr DeviceChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            var evt = wParam.ToInt64();
            if (evt == DbtDevNodesChanged || evt == DbtDeviceArrival || evt == DbtDeviceRemoveComplete)
            {
                Schedule("USB device change");
            }
        }
        return IntPtr.Zero;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Only session (re)engagement transitions matter; ignore lock/logoff/disconnect.
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionUnlock:
                Schedule("session unlocked");
                break;
            case SessionSwitchReason.SessionLogon:
                Schedule("session logon");
                break;
            case SessionSwitchReason.ConsoleConnect:
                Schedule("console connected");
                break;
            case SessionSwitchReason.RemoteConnect:
                Schedule("remote session connected");
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Schedule("resumed from sleep");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Schedule("display configuration changed");

    private void Schedule(string reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _pendingReason = reason;
            _retriesRemaining = MaxSettleRetries;
            _debounce.Change(DebounceMilliseconds, System.Threading.Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        string reason;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            reason = _pendingReason;

            // Re-arm a spaced retry until the settle attempts are exhausted.
            if (_retriesRemaining > 0)
            {
                _retriesRemaining--;
                _debounce.Change(SettleRetryMilliseconds, System.Threading.Timeout.Infinite);
            }
        }

        // SystemEvents / the message hook fired us off the UI thread (or on it); hop
        // to the UI thread so the handler can safely touch the view model.
        _dispatcher.InvokeAsync(() => RefreshRequested?.Invoke(this, reason));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        // SystemEvents holds static references to these handlers, so failing to
        // detach would leak this instance for the life of the process.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hwndSource?.RemoveHook(DeviceChangeHook);
        _hwndSource = null;
        _debounce.Dispose();
    }
}
