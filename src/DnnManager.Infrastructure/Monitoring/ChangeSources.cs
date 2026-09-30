using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;

namespace DnnManager.Infrastructure.Monitoring;

/// <summary>What a <see cref="IChangeSource"/> reports changes in - which part of the state the monitor reads again.</summary>
public enum ChangeKind
{
    /// <summary>The project folders.</summary>
    Projects,
    /// <summary>IIS: its service, its sites and app pools.</summary>
    Iis,
}

/// <summary>
/// Something in Windows that tells us when the system changed, so the monitor doesn't have to keep asking: a folder
/// being watched, a service reporting its status, an event log being written to. A source only says "look again" -
/// the monitor then reads the real state, so a notification that carries no detail (or comes twice) does no harm.
/// </summary>
public interface IChangeSource : IDisposable
{
    ChangeKind Kind { get; }

    /// <summary>What it listens to, for messages: "the projects folder".</summary>
    string Name { get; }

    bool IsAttached { get; }

    /// <summary>
    /// Starts listening. False when what it listens to isn't there (IIS not installed, the folder not made yet) or
    /// can't be opened - the monitor tries again later and relies on its reconciliation meanwhile.
    /// </summary>
    bool TryAttach();

    /// <summary>Something changed. Raised on any thread; handlers must return at once.</summary>
    event Action? Changed;

    /// <summary>It was listening and no longer is (changes may have been missed) - it is detached now.</summary>
    event Action<string>? Lost;
}

/// <summary>
/// Watches a folder: for one file in it being written (<c>applicationHost.config</c>), or - without a file - for its
/// subfolders being made, removed or renamed (the project folders). Holds a handle to that folder only, never to
/// anything inside a project, so it is never in the way of removing one.
/// </summary>
public sealed class FolderChangeSource : IChangeSource
{
    private readonly Func<string> _directory;
    private readonly string? _file;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    // The folder the watcher is on - not the one to watch any more after the setting changed.
    private string? _watched;

    /// <param name="file">The file to watch in <paramref name="directory"/>; null to watch its subfolders.</param>
    public FolderChangeSource(ChangeKind kind, string name, string directory, string? file = null)
        : this(kind, name, () => directory, file) { }

    /// <param name="directory">The folder to watch, asked for whenever it attaches: a setting that can change.</param>
    public FolderChangeSource(ChangeKind kind, string name, Func<string> directory, string? file = null)
    {
        Kind = kind; Name = name; _directory = directory; _file = file;
    }

    public ChangeKind Kind { get; }
    public string Name { get; }
    public bool IsAttached => _watcher is not null && string.Equals(_watched, _directory(), StringComparison.OrdinalIgnoreCase);

    public event Action? Changed;
    public event Action<string>? Lost;

    public bool TryAttach()
    {
        lock (_lock)
        {
            var directory = _directory();
            if (_watcher is not null)
            {
                if (string.Equals(_watched, directory, StringComparison.OrdinalIgnoreCase)) return true;
                // Another folder is to be watched now.
                _watcher.Dispose();
                _watcher = null;
            }
            if (!Directory.Exists(directory)) return false;
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = _file is null
                    ? new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.DirectoryName }
                    : new FileSystemWatcher(directory, _file)
                    {
                        // Written in place, or replaced by a new file of the same name.
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
                    };
                watcher.Created += (_, _) => Changed?.Invoke();
                watcher.Deleted += (_, _) => Changed?.Invoke();
                watcher.Renamed += (_, _) => Changed?.Invoke();
                if (_file is not null) watcher.Changed += (_, _) => Changed?.Invoke();
                // Too many changes at once to keep up with, or the folder itself is gone.
                watcher.Error += (_, e) => Detach(watcher, e.GetException().Message);
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
                _watched = directory;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                return false;
            }
        }
    }

    private void Detach(FileSystemWatcher watcher, string reason)
    {
        lock (_lock)
        {
            // Not the one in use (any more): nothing is lost with it.
            if (!ReferenceEquals(_watcher, watcher)) return;
            _watcher.Dispose();
            _watcher = null;
        }
        Lost?.Invoke(reason);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}

/// <summary>
/// A Windows service telling us when its status changes (started, stopped, on its way) - the service control
/// manager calls back, nothing is polled. For IIS: the World Wide Web Publishing Service.
/// </summary>
public sealed class ServiceStatusSource : IChangeSource
{
    private readonly string _serviceName;
    private readonly object _lock = new();
    // Kept alive for as long as Windows may call it.
    private readonly ServiceNotification _callback;
    private IntPtr _manager, _service, _subscription;

    public ServiceStatusSource(ChangeKind kind, string name, string serviceName)
    {
        Kind = kind; Name = name; _serviceName = serviceName;
        _callback = OnNotification;
    }

    public ChangeKind Kind { get; }
    public string Name { get; }
    public bool IsAttached => _subscription != IntPtr.Zero;

    public event Action? Changed;
    public event Action<string>? Lost;

    public bool TryAttach()
    {
        lock (_lock)
        {
            if (_subscription != IntPtr.Zero) return true;
            _manager = OpenSCManager(null, null, ScManagerConnect);
            if (_manager == IntPtr.Zero) return false;
            // Fails when the service isn't installed (no IIS).
            _service = OpenService(_manager, _serviceName, ServiceQueryStatus);
            if (_service == IntPtr.Zero ||
                SubscribeServiceChangeNotifications(_service, ScEventStatusChange, _callback, IntPtr.Zero, out _subscription) != 0)
            {
                _subscription = IntPtr.Zero;
                CloseHandles();
                return false;
            }
            return true;
        }
    }

    // Called by Windows on a thread-pool thread; must not block - the handlers only queue work.
    private void OnNotification(uint notify, IntPtr context)
    {
        Changed?.Invoke();
        if ((notify & (ServiceNotifyDeleted | ServiceNotifyDeletePending)) == 0) return;
        // The service is being removed: this subscription is over. Unsubscribing waits for this call to return, so it
        // happens on another thread.
        Task.Run(() =>
        {
            Dispose();
            Lost?.Invoke($"the {_serviceName} service was removed");
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_subscription != IntPtr.Zero) UnsubscribeServiceChangeNotifications(_subscription);
            _subscription = IntPtr.Zero;
            CloseHandles();
        }
    }

    private void CloseHandles()
    {
        if (_service != IntPtr.Zero) CloseServiceHandle(_service);
        if (_manager != IntPtr.Zero) CloseServiceHandle(_manager);
        _service = _manager = IntPtr.Zero;
    }

    private const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004;
    private const int ScEventStatusChange = 2;
    private const uint ServiceNotifyDeleted = 0x100, ServiceNotifyDeletePending = 0x200;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceNotification(uint notify, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    // Windows 8 and later (sechost.dll).
    [DllImport("sechost.dll")]
    private static extern uint SubscribeServiceChangeNotifications(IntPtr service, int eventType, ServiceNotification callback,
        IntPtr context, out IntPtr subscription);

    [DllImport("sechost.dll")]
    private static extern void UnsubscribeServiceChangeNotifications(IntPtr subscription);
}

/// <summary>
/// New entries in a Windows event log that match a query - Windows calls back when one is written. For IIS: what
/// the process activation service (WAS) and the web service report in the System log, such as an app pool that
/// failed, was disabled after repeated failures, or recycled.
/// </summary>
public sealed class EventLogSource : IChangeSource
{
    private readonly string _log, _query;
    private readonly object _lock = new();
    private EventLogWatcher? _watcher;
    // While TryAttach runs: a subscription that can't be made doesn't throw - it says so through the event, before
    // TryAttach is back.
    private volatile bool _attaching;
    private volatile string? _attachError;

    /// <param name="query">An XPath event query, e.g. <c>*[System[Provider[@Name='Microsoft-Windows-WAS']]]</c>.</param>
    public EventLogSource(ChangeKind kind, string name, string log, string query)
    {
        Kind = kind; Name = name; _log = log; _query = query;
    }

    public ChangeKind Kind { get; }
    public string Name { get; }
    public bool IsAttached => _watcher is not null;

    public event Action? Changed;
    public event Action<string>? Lost;

    public bool TryAttach()
    {
        lock (_lock)
        {
            if (_watcher is not null) return true;
            EventLogWatcher? watcher = null;
            try
            {
                watcher = new EventLogWatcher(new EventLogQuery(_log, PathType.LogName, _query));
                watcher.EventRecordWritten += OnEvent;
                _attachError = null;
                _attaching = true;
                try { watcher.Enabled = true; }
                finally { _attaching = false; }
                if (_attachError is not null)
                {
                    watcher.Dispose();
                    return false;
                }
                _watcher = watcher;
                return true;
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException
                                           or Win32Exception)
            {
                watcher?.Dispose();
                return false;
            }
        }
    }

    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        e.EventRecord?.Dispose();
        if (e.EventException is null)
        {
            Changed?.Invoke();
            return;
        }

        var reason = e.EventException.Message;
        // It never worked: TryAttach, which this is called from, says so.
        if (_attaching)
        {
            _attachError = reason;
            return;
        }

        // The subscription broke (the log was cleared or is unavailable).
        Task.Run(() =>
        {
            Dispose();
            Lost?.Invoke(reason);
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
