using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace XtremePlays.App.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly ManualResetEvent _stopEvent = new(false);
    private Thread? _listener;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    public static SingleInstanceCoordinator? TryAcquireOrActivate()
    {
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var mutexName = $@"Local\XtremePlays-{userId}";
        var activationName = $"{mutexName}-Activate";
        var mutex = new Mutex(initiallyOwned: false, mutexName);

        bool ownsMutex;
        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            mutex.Dispose();
            NotifyPrimaryInstance(activationName);
            return null;
        }

        try
        {
            var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
            return new SingleInstanceCoordinator(mutex, activateEvent);
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void StartListening(Dispatcher dispatcher, Func<Window?> getMainWindow)
    {
        _listener = new Thread(() =>
        {
            var handles = new WaitHandle[] { _stopEvent, _activateEvent };
            while (WaitHandle.WaitAny(handles) == 1)
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(() => ActivateWindow(getMainWindow())), DispatcherPriority.Normal);
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "X-TREME PLAY'S single-instance activation"
        };
        _listener.Start();
    }

    private static void NotifyPrimaryInstance(string activationName)
    {
        // The primary process owns the mutex before it creates the activation event.
        // Retry briefly to cover that small startup window without launching a second UI.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(activationName);
                activateEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void ActivateWindow(Window? window)
    {
        if (window is null) return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();

        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopEvent.Set();
        _listener?.Join();
        _activateEvent.Dispose();
        _stopEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
