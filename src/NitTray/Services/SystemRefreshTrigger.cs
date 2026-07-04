using System.Windows.Threading;
using Microsoft.Win32;

namespace NitTray.Services;

// Watches Windows for moments when the set of connected displays may have changed
// while NitTray wasn't looking — the user unlocking their session, the machine
// waking from sleep, fast-user-switching back to the console, an RDP reconnect, or
// the monitor arrangement changing — and asks for a display refresh so the list and
// brightness values stay in sync without the user clicking "Refresh".
//
// Windows raises these SystemEvents on a dedicated background thread, and several
// often fire in a burst (unlocking usually triggers a display-settings change too),
// so we debounce and marshal the final RefreshRequested back onto the UI thread.
internal sealed class SystemRefreshTrigger : IDisposable
{
    // Long enough to coalesce the burst of events a single unlock/resume produces,
    // short enough to still feel immediate.
    private const int DebounceMilliseconds = 750;

    private readonly Dispatcher _dispatcher;
    private readonly System.Threading.Timer _debounce;
    private readonly object _gate = new();

    private string _pendingReason = string.Empty;
    private bool _disposed;

    // Raised on the UI thread when something happened that warrants re-enumerating
    // displays. The string is a short human-readable reason, suitable for the log.
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

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Only "the user is (re)engaging with this session" transitions matter;
        // locking / logging off / disconnecting are moments we can safely ignore.
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
        }

        // SystemEvents fired us on a background thread; hop to the UI thread so the
        // handler can safely touch the view model / display collection.
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
        _debounce.Dispose();
    }
}
