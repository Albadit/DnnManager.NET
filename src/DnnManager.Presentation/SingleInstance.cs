using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace DnnManager.Presentation;

/// <summary>
/// DNN Manager runs once per Windows session. A second start finds the <see cref="RunningMarker"/> mutex, asks the
/// running DNN Manager to show its window (through a named event) and exits - before the UAC prompt, so starting it
/// again from the Start menu just brings the open window to the front.
/// </summary>
internal static class SingleInstance
{
    private const string ActivateEventName = "DnnManager.NET.Activate";

    // Lets any process take the foreground - here: the running DNN Manager, when this start hands over to it.
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// True when DNN Manager is already running in this session; it has been asked to show its window, and this
    /// start should exit.
    /// </summary>
    public static bool HandOffToRunning()
    {
        if (!IsRunning()) return false;
        try
        {
            // The running app is elevated and this start may not be: the event's security lets everyone set it.
            if (EventWaitHandleAcl.TryOpenExisting(ActivateEventName, EventWaitHandleRights.Modify, out var activate))
            {
                using (activate)
                {
                    // This start has the foreground (it was just launched) - pass it on, or Windows would only
                    // flash the running app's taskbar button.
                    AllowSetForegroundWindow(AsfwAny);
                    activate.Set();
                }
            }
        }
        catch (Exception)
        {
            // Still starting up (no event yet), or not allowed - its window shows up by itself.
        }
        return true;
    }

    private static bool IsRunning()
    {
        try
        {
            if (!MutexAcl.TryOpenExisting(RunningMarker.Name, MutexRights.Synchronize, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // it exists, we just may not open it
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for later starts (<see cref="HandOffToRunning"/>) and brings the app's window - the main window, or
    /// the dialog shown before it - to the front each time. Keep the returned handle alive until the app exits.
    /// </summary>
    public static IDisposable? Listen(Dispatcher dispatcher)
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
            using (var me = WindowsIdentity.GetCurrent())
            {
                if (me.User is { } user)
                    security.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            }
            var activate = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ActivateEventName, out _, security);
            var registration = ThreadPool.RegisterWaitForSingleObject(activate,
                (_, _) => dispatcher.BeginInvoke(BringToFront), null, Timeout.Infinite, executeOnlyOnce: false);
            return new Listener(activate, registration);
        }
        catch (Exception)
        {
            return null; // a second start then just exits without showing this window
        }
    }

    private static void BringToFront()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var main = app.MainWindow;
        if (main is { WindowState: WindowState.Minimized }) main.WindowState = WindowState.Normal;
        // A dialog open over it (a question, or the settings problem shown before the main window) is what
        // should get the keyboard.
        var window = app.Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w != main) ?? main;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private sealed class Listener(EventWaitHandle activate, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            activate.Dispose();
        }
    }
}
