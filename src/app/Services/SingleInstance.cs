using System.Threading;

namespace NitTray.Services;

// Guarantees one NitTray per Windows session and lets a second instance hand focus
// back to the running one. The first instance owns a named mutex and listens on a
// named event; a later copy finds the mutex taken, signals the event (surface the
// window), and exits. Names use the per-session "Local\" namespace so fast user
// switching gives each user their own instance and tray icon.
internal sealed class SingleInstance : IDisposable
{
    // GUID suffix avoids collisions with other apps; Local\ scopes to the session.
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

    // First instance only: listen for activation requests from later instances.
    // onActivate fires on a thread-pool thread, so it must marshal to the UI thread.
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

    // Ask the already-running instance to surface its window; false if it couldn't
    // be signalled.
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
            // Startup race: the first instance holds the mutex but hasn't created the event yet.
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
