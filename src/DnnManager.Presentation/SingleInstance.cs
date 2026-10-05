    using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace DnnManager.Presentation;

/// <summary>
/// DNN Manager runs once per Windows session. A second start finds the <see cref="RunningMarker"/> mutex, asks the
/// running DNN Manager to show its window (through a named event) and exits - before the UAC prompt, so starting it
/// again from the Start menu just brings the open window to the front. Setup and the uninstaller ask it to quit through
/// another one before they replace or remove its files - they can't close it themselves, as it runs elevated.
/// </summary>
internal static class SingleInstance
{
    private const string ActivateEventName = "DnnManager.NET.Activate";
    /// <summary>Keep in step with <c>QuitEvent</c> in <c>src\DnnManager.Installer\DnnManager.iss</c>.</summary>
    private const string QuitEventName = "DnnManager.NET.Quit";

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
    /// the dialog shown before it - to the front each time; and for Setup asking DNN Manager to quit. Keep the
    /// returned handle alive until the app exits.
    /// </summary>
    public static IDisposable Listen(Dispatcher dispatcher) =>
        new Listeners([On(ActivateEventName, dispatcher, BringToFront), On(QuitEventName, dispatcher, QuitForSetup)]);

    /// <summary>Runs <paramref name="action"/> on the UI thread each time the named event is set - null when it can't be made.</summary>
    private static Listener? On(string name, Dispatcher dispatcher, Action action)
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
            var signal = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
            var registration = ThreadPool.RegisterWaitForSingleObject(signal,
                (_, _) => dispatcher.BeginInvoke(action), null, Timeout.Infinite, executeOnlyOnce: false);
            return new Listener(signal, registration);
        }
        catch (Exception)
        {
            // A second start then just exits without showing this window; Setup asks to quit DNN Manager by hand.
            return null;
        }
    }

    /// <summary>
    /// Setup is about to replace or remove DNN Manager's files: quit without asking anything. A dialog that is open (a
    /// question, say) is closed first - as if cancelled - and the window closes once its loop has ended. Before the main
    /// window is up (a settings problem at the start) nothing happens here; Setup then ends the process itself.
    /// </summary>
    private static void QuitForSetup()
    {
        var app = System.Windows.Application.Current;
        if (app?.MainWindow is not MainWindow { IsLoaded: true } main) return;
        foreach (var dialog in app.Windows.OfType<Window>().Where(w => w != main).ToList()) dialog.Close();
        main.Dispatcher.BeginInvoke(main.QuitForSetup, DispatcherPriority.Background);
    }

    private static void BringToFront()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var main = app.MainWindow;
        // Running in the background, with the window hidden.
        if (main is MainWindow { IsLoaded: true, IsVisible: false } hidden) hidden.ShowFromBackground();
        if (main is { WindowState: WindowState.Minimized }) main.WindowState = WindowState.Normal;
        // A dialog open over it (a question, or the settings problem shown before the main window) is what
        // should get the keyboard.
        var window = app.Windows.OfType<Window>().LastOrDefault(w => w.IsVisible && w != main) ?? main;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            signal.Dispose();
        }
    }

    private sealed class Listeners(Listener?[] listeners) : IDisposable
    {
        public void Dispose()
        {
            foreach (var listener in listeners) listener?.Dispose();
        }
    }
}
