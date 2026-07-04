using System.Threading;

namespace NitTray.Services;

// Guarantees only one NitTray runs per Windows session and lets a would-be second
// instance hand focus back to the one already running.
//
// The first instance to start owns a named mutex and listens on a named event.
// When another copy is launched it finds the mutex already taken, signals the
// event (asking the running instance to surface its window), and exits. The names
// live in the per-session ("Local\") namespace on purpose: with fast user
// switching, each logged-in user still gets their own single instance and tray
// icon, rather than being blocked by a copy running in someone else's session.
internal sealed class SingleInstance : IDisposable
{
    // A fixed unique suffix keeps these names from colliding with any other app's
    // sync objects. Local\ scopes them to the current session.
    private const string MutexName = @"Local\NitTray.SingleInstance.Mutex.b7a3c2e1-9f4d-4e6a-8c15-2a7e5d9b1f30";
    private const string ActivateEventName = @"Local\NitTray.SingleInstance.Activate.b7a3c2e1-9f4d-4e6a-8c15-2a7e5d9b1f30";

    private readonly Mutex _mutex;
    private readonly bool _isFirstInstance;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _disposed;

    public bool IsFirstInstance => _isFirstInstance;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _isFirstInstance);
    }

    // First instance only: start listening for activation requests from later
    // instances. onActivate fires on a thread-pool thread each time another copy is
    // launched, so the handler must marshal to the UI thread itself.
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);

        _activateEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ActivateEventName);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => onActivate(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    // Second instance only: ask the already-running instance to surface its window.
    // Returns false if the running instance couldn't be signalled.
    public bool SignalExistingInstance()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
            evt.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance owns the mutex but hasn't created the event yet
            // (a brief startup race). Nothing to signal.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _registeredWait?.Unregister(null);
        _activateEvent?.Dispose();

        // Only the owning instance holds the mutex; releasing one we don't own throws.
        if (_isFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned on this thread — safe to ignore during shutdown.
            }
        }
        _mutex.Dispose();
    }
}
