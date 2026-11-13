using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Startup;

namespace DnnManager.Desktop.Services;

/// <summary>
/// Administrator rights on Windows, as the Windows app (src/DnnManager.Presentation/AdminElevation.cs): IIS needs them, so
/// DNN Manager asks for them as it starts - for its launcher when installed (which drops .NET's variables first), else
/// for itself. macOS and Linux never need them.
/// </summary>
internal static class AdminElevation
{
    /// <summary>
    /// Set to 1, DNN Manager doesn't ask for administrator rights - for building and trying it out, where Windows' prompt
    /// at every start is in the way. What needs them (IIS) then says it can't.
    /// </summary>
    public const string SkipVariable = "DNNMANAGER_NO_ELEVATION";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Whether this start should ask for the rights: Windows, without them, and not told otherwise.</summary>
    public static bool Wanted() =>
        OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(SkipVariable) != "1" && !IsAdministrator();

    /// <summary>
    /// Starts this exe again with administrator rights (Windows' prompt) - true when it started; the caller then exits.
    /// <paramref name="declined"/>: the prompt was answered No. An installed DNN Manager asks for its launcher's rights
    /// (<see cref="LaunchEnvironment.LauncherFileName"/>), which takes no arguments.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args, out bool declined)
    {
        declined = false;
        if (!OperatingSystem.IsWindows()) return false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var launcher = LaunchEnvironment.LauncherBeside(exe);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = launcher ?? exe, UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
            };
            if (launcher is null)
                foreach (var a in args) start.ArgumentList.Add(a);
            Process.Start(start);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            declined = true;
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A message before there is any window (Windows' own message box).</summary>
    public static void Tell(string text, bool error)
    {
        if (OperatingSystem.IsWindows()) _ = MessageBoxW(0, text, "DNN Manager", error ? 0x10u : 0x40u);
        else Console.Error.WriteLine(text);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}

/// <summary>
/// A named mutex that exists while DNN Manager runs (Windows) - Setup and the uninstaller look for it before replacing or
/// removing the files, and a second start finds it. Keep the name in step with <c>RunningMutex</c> in DnnManager.iss.
/// </summary>
internal static class RunningMarker
{
    public const string Name = "DnnManager.NET.Running";

    /// <summary>The mutex - keep it until the app exits; null off Windows or when it can't be made.</summary>
    public static Mutex? Create(out bool alreadyRunning)
    {
        alreadyRunning = false;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // The app may run elevated while Setup doesn't: everyone may wait on (see) it.
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.Synchronize, AccessControlType.Allow));
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is { } user) security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            var mutex = MutexAcl.Create(initiallyOwned: false, Name, out var createdNew, security);
            alreadyRunning = !createdNew;
            return mutex;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// DNN Manager runs once per Windows session (src/DnnManager.Presentation/SingleInstance.cs): a second start asks the
/// running one to show its window and exits - before the administrator prompt. Setup asks it to quit through another
/// event (keep <c>QuitEvent</c> in DnnManager.iss in step).
/// </summary>
internal static class SingleInstance
{
    private const string ActivateEventName = "DnnManager.NET.Activate";
    private const string QuitEventName = "DnnManager.NET.Quit";

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>True when DNN Manager runs already in this session - asked to show its window; this start should exit.</summary>
    public static bool HandOffToRunning()
    {
        if (!OperatingSystem.IsWindows() || !IsRunning()) return false;
        try
        {
            if (EventWaitHandleAcl.TryOpenExisting(ActivateEventName, EventWaitHandleRights.Modify, out var activate))
            {
                using (activate)
                {
                    AllowSetForegroundWindow(-1);
                    activate.Set();
                }
            }
        }
        catch (Exception)
        {
            // Still starting (no event yet), or not allowed: its window shows by itself.
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
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
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for later starts (the window to the front) and for Setup asking to quit - on Avalonia's UI thread. Keep the
    /// handle until the app exits; null off Windows.
    /// </summary>
    public static IDisposable? Listen(Action bringToFront, Action quitForSetup)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return new Listeners([On(ActivateEventName, bringToFront), On(QuitEventName, quitForSetup)]);
    }

    [SupportedOSPlatform("windows")]
    private static Listener? On(string name, Action action)
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
            using (var me = WindowsIdentity.GetCurrent())
                if (me.User is { } user)
                    security.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            var signal = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
            var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.UIThread.Post(action), null,
                Timeout.Infinite, executeOnlyOnce: false);
            return new Listener(signal, registration);
        }
        catch (Exception)
        {
            return null;
        }
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

/// <summary>
/// Restarting DNN Manager - and the update, which starts its helper instead (src/DnnManager.Presentation/AppRestart.cs).
/// The window closes as on any quit (asking first about a running operation); once this process has ended, Program starts
/// the new one, with <see cref="AfterArgument"/> and this process's id so it waits for this one to be gone.
/// </summary>
internal static class AppRestart
{
    public const string AfterArgument = "--after";

    public static bool Requested { get; private set; }

    private static ProcessStartInfo? _instead;

    /// <summary>Closes the window for a restart (or <paramref name="instead"/>) - false when the user chose to stay.</summary>
    public static async Task<bool> RestartAsync(ProcessStartInfo? instead = null)
    {
        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not Views.MainWindow window)
            return false;
        Requested = true;
        _instead = instead;
        if (await window.QuitAsync()) return true;
        Requested = false;
        _instead = null;
        return false;
    }

    /// <summary>Starts the new DNN Manager, from Program once the app has ended - with the rights this one has.</summary>
    public static void StartNew()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            var start = _instead ?? new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            if (_instead is null)
            {
                start.ArgumentList.Add(AfterArgument);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
            }
            ChildEnvironment.ForSelf(start.Environment);
            Process.Start(start);
        }
        catch (Exception ex)
        {
            AdminElevation.Tell($"DNN Manager could not start again: {ex.Message}\n\nStart it again yourself.", error: false);
        }
    }

    /// <summary>Started by <see cref="StartNew"/>: waits (30 s at most) for the previous one, and returns the arguments without ours.</summary>
    public static string[] WaitForPrevious(string[] args)
    {
        var at = Array.IndexOf(args, AfterArgument);
        if (at < 0) return args;
        if (at + 1 < args.Length && int.TryParse(args[at + 1], out var id))
        {
            try
            {
                using var previous = Process.GetProcessById(id);
                previous.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // Gone already.
            }
        }
        return args.Where((_, i) => i != at && i != at + 1).ToArray();
    }
}

/// <summary>
/// Opens an address in the browser, a folder or a file in its program. Elevated on Windows, through Explorer (which runs
/// as the signed-in user - <see cref="ElevatedStart.Explorer"/>), so the browser doesn't get DNN Manager's rights;
/// otherwise the system's own way (Avalonia's launcher).
/// </summary>
public static class Shell
{
    public static async void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows() && AdminElevation.IsAdministrator())
            {
                ElevatedStart.Explorer(target);
                return;
            }
            var top = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (top is null) return;
            var opened = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto"
                ? await top.Launcher.LaunchUriAsync(uri)
                : Directory.Exists(target) ? await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(target))
                : await top.Launcher.LaunchFileInfoAsync(new FileInfo(target));
            if (!opened) Toast.Show($"Could not open {target}.", ToastKind.Warning);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Toast.Show($"Could not open {target}: {ex.Message}", ToastKind.Error);
        }
    }
}
