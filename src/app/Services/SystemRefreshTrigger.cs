using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NitTray.Services;

// Watches Windows for moments when the connected-display set may have changed
// (session unlock/logon/connect, wake from sleep, monitor-arrangement change, and
// USB device-node changes) and asks for a rescan. A plug-in produces a burst of
// these events, so we debounce; and because a display's HID interface can become
// openable a beat after it appears, we fire a couple of spaced "settle" retries.
internal sealed class SystemRefreshTrigger : IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    // Coalesce the event burst from one unlock/resume/plug-in, yet still feel prompt.
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
    private int _generation;
    private bool _disposed;

    // Re-enumerates displays on the UI thread; returns true when the scan "settled"
    // so the trigger can skip remaining retries. Arg is a log-friendly reason.
    public Func<string, Task<bool>>? Refresh { get; set; }

    public SystemRefreshTrigger()
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _debounce = new System.Threading.Timer(
            OnDebounceElapsed, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // Hooks WM_DEVICECHANGE on the window so USB arrivals/removals trigger a rescan.
    // Realizes the HWND if needed and keeps working while the window is hidden in the
    // tray. Call once, after the window is constructed.
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
            _generation++;
            _debounce.Change(DebounceMilliseconds, System.Threading.Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        string reason;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            reason = _pendingReason;
            generation = _generation;

            // Re-arm a spaced retry until the settle attempts are exhausted.
            if (_retriesRemaining > 0)
            {
                _retriesRemaining--;
                _debounce.Change(SettleRetryMilliseconds, System.Threading.Timeout.Infinite);
            }
        }

        // Hop to the UI thread to run the refresh. If it settled, drop the remaining
        // settle retries — the display is already showing.
        _dispatcher.InvokeAsync(async () =>
        {
            var refresh = Refresh;
            if (refresh is null)
            {
                return;
            }

            if (await refresh(reason).ConfigureAwait(true))
            {
                CancelSettleRetries(generation);
            }
        });
    }

    // Stop the settle retries for this schedule generation, unless a newer device
    // change has since been scheduled (its generation won't match).
    private void CancelSettleRetries(int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }
            _retriesRemaining = 0;
            _debounce.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
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

        // SystemEvents holds these handlers statically; detach or we leak for the
        // life of the process.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hwndSource?.RemoveHook(DeviceChangeHook);
        _hwndSource = null;
        _debounce.Dispose();
    }
}
