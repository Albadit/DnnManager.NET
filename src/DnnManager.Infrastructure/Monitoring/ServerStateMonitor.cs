using System.Diagnostics;
using System.IO.Enumeration;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Sql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Infrastructure.Monitoring;

/// <summary>
/// The one place that knows the current state of the IIS sites (the Projects table's rows - IIS is the list, every
/// site in it is a project, whatever folder it serves), what their folders hold, IIS itself and this PC, and keeps
/// it current by itself - the Projects page has no Refresh. Modelled on how Docker Desktop follows its engine: take a
/// snapshot, follow the events, and reconcile now and then because an event stream is never guaranteed complete
/// (<c>docker events</c> only replays the last 256).
///
/// <para><b>How a change gets to the screen.</b> Everything runs in this one process, so the "connection" is a .NET
/// event: this monitor works on background threads and raises <see cref="Changed"/> with what differs; the
/// presentation's <c>ServerStore</c> applies that on the UI thread to the row objects the table is bound to, so only
/// the cells that changed are redrawn, and the check boxes, search, sorting, scroll position and expanded rows are not
/// touched. No IPC, WebSocket or SignalR - there is no second process to talk to.</para>
///
/// <para><b>Pushed by Windows</b> (each <see cref="IChangeSource"/> only says "look again"; the real state is then
/// read, so a missed or doubled notification does no harm):</para>
/// <list type="bullet">
/// <item>IIS started or stopped - the service control manager reports the web service's status (<see cref="ServiceStatusSource"/>).</item>
/// <item>A site or app pool added, removed or changed, from any tool - <c>applicationHost.config</c> being written (<see cref="FolderChangeSource"/>).</item>
/// <item>An app pool that failed, was disabled or recycled - what IIS writes to the System event log (<see cref="EventLogSource"/>).</item>
/// <item>A folder in the projects folder made, removed or renamed - the sites' folders are read again (<see cref="FolderChangeSource"/>).</item>
/// <item>The PC waking up - <see cref="SystemEvents.PowerModeChanged"/>.</item>
/// <item>DNN Manager's own operations - they ask for a sync when they finish (<see cref="SyncAsync"/>, <see cref="SyncSitesAsync"/>).</item>
/// </list>
///
/// <para><b>Read on a timer</b>, because Windows has no notification for them (the ones marked * only while the
/// Projects page is on screen - <see cref="Active"/>):</para>
/// <list type="bullet">
/// <item>This PC's memory and CPU: 2 s; its disk: 10 s.</item>
/// <item>* The worker processes' CPU, memory and I/O: 2 s - and whether a worker process came or went, which reads the sites again.</item>
/// <item>* HTTP traffic per site: 5 s, and only while that column is shown (<see cref="TrafficWanted"/>).</item>
/// <item>The sites' state, as a reconciliation: 5 s on the Projects page, 30 s otherwise. IIS has no notification for
/// a site's running state, so this is what catches a stop or start that touched nothing else.</item>
/// <item>* The SQL Server and its databases: 10 s.</item>
/// <item>* What each site's folder holds - its DNN version and its web.config's database: 30 s, and when the site
/// turns up or serves another folder. (Not watched per folder: a watcher inside a project folder would be in the way
/// of removing it.)</item>
/// <item>* The folder sizes: 10 minutes, and after an operation - only while the Size column is shown (<see cref="SizesShown"/>),
/// as they are a walk over every file; an open overview asks for its own folder (<see cref="MeasureSize"/>).</item>
/// </list>
/// <para>The intervals are counted from when a read was last asked for or finished, whichever is later, on a clock
/// that only goes forward (<see cref="Now"/>) - so a slow read isn't asked for twice, and setting the PC's date or
/// time doesn't stop or hurry anything.</para>
/// <para><b>While the window is minimized</b> (and the user wants resources saved meanwhile - <see cref="SavingResources"/>)
/// this PC's figures aren't read, folder-size walks wait, and the loop looks at what is due every 5 s instead of every
/// second. The notifications, the reconciliation every 30 s and the retries go on.</para>
///
/// <para><b>Start.</b> <see cref="Start"/> reads the sites and their folders once and publishes them as
/// <see cref="ProjectAdded"/> events, then <see cref="MonitorConnection.Live"/>; database state and sizes follow as
/// they arrive, each as a change to the rows already shown.</para>
///
/// <para><b>When something goes wrong.</b> What can't be read is kept as it was and reported as
/// <see cref="MonitorConnection.Reconnecting"/> with the reason; it is retried every 5 s. (IIS's configuration
/// failing to read once is first tried again a second later, without a word - it may just be being written. An IIS
/// that doesn't answer at all isn't waited for longer than 15 s.) When a source stops notifying, its part of the
/// state is read again at once (changes were missed) and the source is attached again as soon as that works - less
/// and less often when it keeps being lost right away; a source whose target isn't there yet (no IIS, no projects
/// folder) is simply tried again later. After the PC wakes up - or a timer tick arrives half a minute late, which
/// means the same - everything is read again. In all these cases the table stays as it is; the reads that follow
/// only change what differs.</para>
/// </summary>
public sealed class ServerStateMonitor : IServerStateFeed, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    // The loop's tick while resources are saved: nothing it does then is needed sooner, and every tick wakes the PC's
    // processor up.
    private static readonly TimeSpan SavingTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HostEvery = TimeSpan.FromSeconds(2);
    // After a pause, the CPU use is measured over at least this long before it is shown - a shorter span is a guess.
    private static readonly TimeSpan HostFirstAfter = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DiskEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TrafficEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SitesEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SitesEveryInBackground = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SqlEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProjectsEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SizesEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);
    // A source that keeps being lost right after it was attached is tried again less and less often, up to this.
    private static readonly TimeSpan SourceRetryAtMost = TimeSpan.FromMinutes(5);
    // A source that has listened for this long worked; one lost sooner never really did.
    private static readonly TimeSpan SourceWorkedAfter = TimeSpan.FromSeconds(10);
    // One failed read of IIS's configuration is tried again this soon, before anything is said about it.
    private static readonly TimeSpan QuickRetry = TimeSpan.FromSeconds(1);
    // A read of IIS that takes this long isn't waited for any longer, and is said to be a problem.
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(15);
    // A tick this late means the PC slept (or the process was suspended).
    private static readonly TimeSpan SleptAfter = TimeSpan.FromSeconds(30);
    // A burst of notifications (a config file written in several steps) is one change.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    // "Not yet", for the times below.
    private const long Never = -1;

    private static readonly IReadOnlyDictionary<string, IisSiteRuntime> NoSites =
        new Dictionary<string, IisSiteRuntime>(StringComparer.OrdinalIgnoreCase);

    private readonly AppOptions _options;
    private readonly IProjectRepository _repository;
    private readonly IIisManager _iis;
    private readonly IWebConfigService _webConfig;
    private readonly IServiceScopeFactory _scopes;
    private readonly ProcessSampler _processes;
    private readonly HostResourceMonitor _host;
    private readonly IReadOnlyList<IChangeSource> _sources;
    private readonly Dictionary<IChangeSource, SourceState> _sourceStates;
    private readonly ILogger<ServerStateMonitor> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly CoalescedJob _sitesJob, _projectsJob, _sqlJob, _sizesJob, _trafficJob;

    // ── What is known. Changed and published under _gate, so events leave in the order things happened. ──
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectState> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sizesWanted = new(StringComparer.OrdinalIgnoreCase);
    // Folders whose size is asked for by name (an open overview) - walked whether or not the Size column is shown.
    // Asked for from the UI thread: no lock, so it never waits for a read that holds _gate.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sizesAsked = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _sizesShown;
    // What couldn't be read or listened to: a key per thing, with the message shown while reconnecting.
    private readonly Dictionary<string, string> _problems = [];

    // Each site's database as its web.config connects to it, and what the last look at it found - per site, as
    // sites can be on different servers with different logins.
    private readonly Dictionary<string, SiteSqlConnection> _sqlConnections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SiteDatabaseCheck> _sqlChecks = new(StringComparer.OrdinalIgnoreCase);
    private IisServerState? _runtime;
    private MonitorConnection _connection = MonitorConnection.Connecting;
    private string? _connectionDetail;
    private volatile bool _snapshotTaken;
    private bool _resyncing;

    // ── When each timed read was last asked for or done, on the clock of Now (Never: at the next tick). ──
    private long _hostAt = Never, _diskAt = Never, _statsAt = Never, _trafficAt = Never, _sitesAt = Never, _sqlAt = Never,
        _projectsAt = Never, _sizesAt = Never, _retryAt = Never, _lastTick = Never;
    // Since when a read of the IIS sites has been running; Never while none is.
    private long _sitesReadSince = Never;
    // When the sites and the projects were last read successfully - a time of day, for showing.
    private long _lastSync;
    private DiskUse _disk = DiskUse.Unknown;
    // Reads of IIS's configuration that failed in a row.
    private int _sitesFailures;
    // The worker processes seen at the last look - a different set means a site's workers changed.
    private HashSet<int>? _workers;
    // The w3wp.exe processes, asked every two seconds - cheaply (WorkerProcessList).
    private readonly WorkerProcessList _workerList = new();
    private volatile bool _active, _trafficWanted, _saving;
    private int _started, _resuming;
    // The loop's timer, once it runs - its period changes with SavingResources (under _tickLock).
    private readonly object _tickLock = new();
    private PeriodicTimer? _timer;

    public ServerStateMonitor(IOptions<AppOptions> options, IProjectRepository repository, IIisManager iis,
        IWebConfigService webConfig, IServiceScopeFactory scopes, ProcessSampler processes, HostResourceMonitor host,
        IEnumerable<IChangeSource> sources, ILogger<ServerStateMonitor> log)
    {
        _options = options.Value; _repository = repository; _iis = iis; _webConfig = webConfig; _scopes = scopes;
        _processes = processes; _host = host; _sources = sources.ToList(); _log = log;
        _sourceStates = _sources.ToDictionary(source => source, _ => new SourceState());

        void Failed(Exception ex) => _log.LogWarning(ex, "A background read failed");
        _sitesJob = new CoalescedJob(ReadSitesAsync, Settle, ex => ReadFailed(SitesKey, "IIS can't be read", ex), _stop.Token);
        _projectsJob = new CoalescedJob(ReadProjectsAsync, Settle, ex => ReadFailed(ProjectsKey, "The projects can't be read", ex), _stop.Token);
        _sqlJob = new CoalescedJob(ReadSqlAsync, TimeSpan.Zero, Failed, _stop.Token);
        _sizesJob = new CoalescedJob(ReadSizesAsync, TimeSpan.Zero, Failed, _stop.Token);
        // In a job of its own: asking Windows for the counters can take seconds the first time, and the loop has
        // other things to do.
        _trafficJob = new CoalescedJob(ReadTrafficAsync, TimeSpan.Zero, Failed, _stop.Token);
    }

    /// <summary>
    /// What changed, in order. Raised on a background thread while the monitor holds its lock: a handler hands the
    /// batch on (to the UI thread) and returns - it must not block or call back into the monitor.
    /// </summary>
    public event Action<IReadOnlyList<MonitorEvent>>? Changed;

    /// <summary>
    /// The PC woke up (or the process was suspended for a while) and everything has been read again - what that read
    /// found has been published. Raised on a background thread, without the monitor's lock.
    /// </summary>
    public event Action? Resumed;

    /// <summary>
    /// The Projects page is on screen: the timed reads that only it shows run, and turning this on reads everything
    /// once, right away - fresh data when the screen is opened, no polling for a screen nobody looks at.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            // Before the first snapshot there is nothing to bring up to date - it is being read.
            if (!value || !_snapshotTaken) return;
            Done(ref _sitesAt);
            Done(ref _projectsAt);
            Done(ref _sqlAt);
            _sitesJob.Request();
            _projectsJob.Request();
            _sqlJob.Request();
            Volatile.Write(ref _statsAt, Never);
        }
    }

    /// <summary>
    /// The folder sizes are shown (the table's Size column): every folder is walked - tens of thousands of files each -
    /// only while they are. Otherwise the folders stay marked, and are walked once the column is shown.
    /// </summary>
    public bool SizesShown
    {
        get => _sizesShown;
        set
        {
            if (_sizesShown == value) return;
            _sizesShown = value;
            if (value && !_saving) _sizesJob.Request();
        }
    }

    /// <summary>Walks <paramref name="name"/>'s folder for its size now - for its overview - whether the column is shown or not.</summary>
    public void MeasureSize(string name)
    {
        _sizesAsked[name] = 0;
        if (!_saving) _sizesJob.Request();
    }

    /// <summary>The HTTP traffic per site is shown somewhere - it is only read while it is.</summary>
    public bool TrafficWanted
    {
        get => _trafficWanted;
        set
        {
            _trafficWanted = value;
            if (value) Volatile.Write(ref _trafficAt, Never);
        }
    }

    /// <summary>
    /// The window is minimized and resources are to be saved meanwhile: this PC's figures aren't read (only the
    /// status bar shows them), folder-size walks wait (only the table shows them), and the loop looks at what is due
    /// every 5 s - the sites' reconciliation every 30 s, the notifications and the retries go on. Turning it off reads
    /// the figures at the first tick half a second or more later, with the CPU use of that moment rather than an
    /// average over the pause, and does the walks that waited.
    /// </summary>
    public bool SavingResources
    {
        get => _saving;
        set
        {
            if (_saving == value) return;
            _saving = value;
            SetTick(value ? SavingTick : Tick);
            if (value) return;

            _host.Restart();
            Volatile.Write(ref _diskAt, Never);
            DueAfter(ref _hostAt, HostEvery, HostFirstAfter);
            // The walks that waited - the job finds out which (without the UI thread waiting here for the monitor's lock).
            _sizesJob.Request();
        }
    }

    /// <summary>When the sites and the projects were last read successfully.</summary>
    public DateTime? LastSync => Volatile.Read(ref _lastSync) is var ticks and > 0 ? new DateTime(ticks, DateTimeKind.Local) : null;

    /// <summary>Takes the first snapshot and starts following changes. Events only flow after this.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        foreach (var source in _sources)
        {
            source.Changed += () => OnSourceChanged(source);
            source.Lost += reason => OnSourceLost(source, reason);
        }
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _options.Changed += OnOptionsChanged;
        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Reads the sites again now and completes when the result has been published - after a start, stop or restart,
    /// so the row shows the outcome without waiting for a notification.
    /// </summary>
    public Task SyncSitesAsync()
    {
        Done(ref _sitesAt);
        return Within(_sitesJob.RunNowAsync());
    }

    /// <summary>
    /// Reads the sites and the project folders again now and completes when both have been published; the databases
    /// follow, and the sizes of <paramref name="measure"/>'s folders. After an operation that may have added, removed
    /// or changed a project. Only the folders named are walked again - a walk of every site is tens of thousands of
    /// files each (seconds of disk); a new site is measured when it turns up, and the rest by the timer.
    /// </summary>
    public async Task SyncAsync(IReadOnlyCollection<string>? measure = null)
    {
        // The sites first, so a project that turns up is published with its site.
        await SyncSitesAsync();
        Done(ref _projectsAt);
        await _projectsJob.RunNowAsync();
        Done(ref _sqlAt);
        _sqlJob.Request();
        if (measure is { Count: > 0 }) QueueSizes(measure);
    }

    /// <summary>
    /// Waits for a read of IIS - but not for ever: should IIS not answer, what comes after the read (a row waiting
    /// to show its state, the project folders) goes on without it; the loop reports the read as a problem.
    /// </summary>
    private async Task Within(Task read)
    {
        if (read.IsCompleted) return;
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        await Task.WhenAny(read, Task.Delay(StuckAfter, giveUp.Token));
        giveUp.Cancel();
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _options.Changed -= OnOptionsChanged;
        foreach (var source in _sources) source.Dispose();
    }

    // ─── The loop ─────────────────────────────────────────────────────────

    /// <summary>
    /// The clock the timed reads go by: milliseconds since Windows started. It only goes forward - setting the PC's
    /// date or time doesn't move it - and it counts the time the PC was asleep.
    /// </summary>
    private static long Now => Environment.TickCount64;

    private async Task RunAsync()
    {
        try
        {
            foreach (var source in _sources) Attach(source);
            await SyncAsync();
            // An IIS that didn't answer wasn't waited for: said with the snapshot, not a second after it.
            CheckSitesRead(Now);
            lock (_gate)
            {
                _snapshotTaken = true;
                var events = new List<MonitorEvent>();
                RefreshConnection(events);
                Publish(events);
            }

            using var timer = StartTimer();
            Done(ref _lastTick);
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try { await OnTickAsync(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "A monitor tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The loop's timer, ticking as often as <see cref="SavingResources"/> asks for.</summary>
    private PeriodicTimer StartTimer()
    {
        lock (_tickLock) return _timer = new PeriodicTimer(_saving ? SavingTick : Tick);
    }

    /// <summary>Changes how often the loop ticks - from now: the next tick is a whole <paramref name="period"/> away.</summary>
    private void SetTick(TimeSpan period)
    {
        lock (_tickLock)
        {
            if (_timer is null || _stop.IsCancellationRequested) return;
            try { _timer.Period = period; }
            catch (ObjectDisposedException) { } // the loop has ended
        }
    }

    private async Task OnTickAsync()
    {
        var now = Now;
        var last = Interlocked.Exchange(ref _lastTick, now);
        if (last != Never && now - last > SleptAfter.TotalMilliseconds)
        {
            await ResumeAsync();
            return;
        }

        if (!_saving && Due(ref _hostAt, HostEvery, now))
        {
            if (Due(ref _diskAt, DiskEvery, now)) _disk = HostResourceMonitor.SampleDisk(_options.BaseDirectory);
            var host = _host.Sample(_disk);
            lock (_gate) Publish([new HostStatsChanged(host)]);
        }

        if (Due(ref _retryAt, RetryEvery, now)) Retry();
        CheckSitesRead(now);

        // What a job reads is noted here when it is asked for, and again by the job when it has run (Done): a read
        // that takes longer than a tick isn't asked for twice, and one that a notification or an operation caused
        // postpones the timed one.
        if (Due(ref _sitesAt, _active ? SitesEvery : SitesEveryInBackground, now)) _sitesJob.Request();
        if (!_active) return;

        if (Due(ref _statsAt, StatsEvery, now)) ReadWorkerProcesses();
        if (_trafficWanted && Due(ref _trafficAt, TrafficEvery, now)) _trafficJob.Request();
        if (Due(ref _sqlAt, SqlEvery, now)) _sqlJob.Request();
        if (Due(ref _projectsAt, ProjectsEvery, now)) _projectsJob.Request();
        if (Due(ref _sizesAt, SizesEvery, now)) QueueAllSizes();
    }

    /// <summary>
    /// True when <paramref name="every"/> has passed since <paramref name="last"/> - which is then set to now. With
    /// half a tick of slack: the ticks don't come exactly a second apart, and what is due in a millisecond shouldn't
    /// wait a whole tick more.
    /// </summary>
    private static bool Due(ref long last, TimeSpan every, long now)
    {
        var at = Volatile.Read(ref last);
        if (at != Never && now - at < (long)every.TotalMilliseconds - (long)Tick.TotalMilliseconds / 2) return false;
        Volatile.Write(ref last, now);
        return true;
    }

    private static void Done(ref long last) => Volatile.Write(ref last, Now);

    /// <summary>Makes a read done every <paramref name="every"/> due at the first tick <paramref name="after"/> or more from now.</summary>
    private static void DueAfter(ref long last, TimeSpan every, TimeSpan after) =>
        Volatile.Write(ref last, Now - (long)(every - Tick / 2 - after).TotalMilliseconds);

    /// <summary>Tries again what failed: sources that aren't listening, and reads that couldn't be done.</summary>
    private void Retry()
    {
        // Listening (again): what happened meanwhile wasn't reported - read it.
        foreach (var source in _sources)
            if (Attach(source)) OnSourceChanged(source);

        bool failing;
        lock (_gate) failing = _problems.ContainsKey(SitesKey) || _problems.ContainsKey(ProjectsKey);
        if (!failing) return;
        _sitesJob.Request();
        _projectsJob.Request();
    }

    /// <summary>A read of IIS that doesn't come back: said, rather than the table quietly going stale.</summary>
    private void CheckSitesRead(long now)
    {
        var since = Volatile.Read(ref _sitesReadSince);
        if (since == Never || now - since < StuckAfter.TotalMilliseconds) return;
        lock (_gate)
        {
            // It may have come back just now - then it has said (or is about to say) how it went itself.
            if (Volatile.Read(ref _sitesReadSince) == Never) return;
            var events = new List<MonitorEvent>();
            SetProblem(SitesKey, "IIS isn't answering.", events);
            Publish(events);
        }
    }

    // ─── Notifications ────────────────────────────────────────────────────

    /// <summary>How attaching a source has gone, to try a failing one again less and less often.</summary>
    private sealed class SourceState
    {
        /// <summary>Since when it has been listening; Never while it isn't.</summary>
        public long AttachedAt = Never;
        /// <summary>Not before this, after it was lost.</summary>
        public long RetryAt = Never;
        /// <summary>How often in a row it was lost before it had really listened.</summary>
        public int LostEarly;
    }

    /// <summary>Starts listening to a source that isn't. True when it now is.</summary>
    private bool Attach(IChangeSource source)
    {
        var state = _sourceStates[source];
        lock (state)
        {
            var now = Now;
            if (source.IsAttached || (state.RetryAt != Never && now < state.RetryAt)) return false;
            // False when what it listens to isn't there (yet) - asked again at the next retry.
            if (!source.TryAttach()) return false;
            state.AttachedAt = now;
            return true;
        }
    }

    private void OnSourceChanged(IChangeSource source)
    {
        if (source.Kind == ChangeKind.Projects) _projectsJob.Request();
        else _sitesJob.Request();
    }

    private void OnSourceLost(IChangeSource source, string reason)
    {
        var state = _sourceStates[source];
        bool worked;
        lock (state)
        {
            var now = Now;
            // Lost right after it was attached, it never really listened - an event log that can't be subscribed to
            // only says so afterwards. It is tried again, each time later.
            worked = state.AttachedAt != Never && now - state.AttachedAt >= SourceWorkedAfter.TotalMilliseconds;
            state.LostEarly = worked ? 0 : state.LostEarly + 1;
            var wait = RetryEvery.TotalMilliseconds * Math.Pow(2, Math.Min(state.LostEarly, 10));
            state.RetryAt = now + (long)Math.Min(wait, SourceRetryAtMost.TotalMilliseconds);
            state.AttachedAt = Never;
            if (worked || state.LostEarly == 1) _log.LogWarning("No longer notified by {Source}: {Reason}", source.Name, reason);
        }
        // Nothing was missed that the read after attaching didn't see - and nothing to show: the timed reads go on.
        if (!worked) return;

        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            SetProblem(SourceKey(source.Kind), $"Lost the notifications from {source.Name} ({reason.TrimEnd('.')}).", events);
            Publish(events);
        }
        // Changes were missed, and whatever made it stop may have been one: read that part again. Once that has
        // succeeded the state is right again; Retry() gets the notifications back.
        OnSourceChanged(source);
    }

    /// <summary>
    /// The settings were changed - perhaps the projects folder, the sites' address or the SQL Server: the folder's
    /// watcher moves with it, and everything is read again with the new values. What differs comes out as changes.
    /// </summary>
    private void OnOptionsChanged()
    {
        if (_stop.IsCancellationRequested) return;
        Volatile.Write(ref _diskAt, Never);
        _ = Task.Run(async () =>
        {
            foreach (var source in _sources) Attach(source);
            await SyncAsync();
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ = Task.Run(ResumeAsync);
    }

    /// <summary>
    /// After sleep: notifications may have been missed and everything may have changed - read it all again. What is
    /// shown stays meanwhile, marked as reconnecting.
    /// </summary>
    private async Task ResumeAsync()
    {
        if (Interlocked.Exchange(ref _resuming, 1) != 0) return;
        try
        {
            SetResyncing(true);
            foreach (var source in _sources) Attach(source);
            await SyncAsync();
        }
        finally
        {
            SetResyncing(false);
            Done(ref _lastTick);
            Volatile.Write(ref _resuming, 0);
        }
        try { Resumed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "A handler of the resume failed"); }
    }

    private void SetResyncing(bool resyncing)
    {
        lock (_gate)
        {
            _resyncing = resyncing;
            var events = new List<MonitorEvent>();
            RefreshConnection(events);
            Publish(events);
        }
    }

    // ─── The sites and IIS itself ─────────────────────────────────────────

    private Task ReadSitesAsync(CancellationToken ct)
    {
        IisServerState runtime;
        IReadOnlyDictionary<string, IisSiteRuntime>? sites = NoSites;
        Volatile.Write(ref _sitesReadSince, Now);
        try
        {
            runtime = _iis.GetServerState();
            // Without IIS there are no sites - and nothing that can't be read.
            if (runtime != IisServerState.NotInstalled)
            {
                sites = _iis.GetSiteRuntimes();
                // Asked again after the sites: they read as stopped while IIS itself is down, so the two are told
                // together - IIS going down between the two questions would look like every site being stopped.
                runtime = _iis.GetServerState();
            }
        }
        finally
        {
            Volatile.Write(ref _sitesReadSince, Never);
        }

        bool retry = false, added = false;
        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            if (_runtime != runtime)
            {
                _runtime = runtime;
                events.Add(new RuntimeChanged(runtime));
            }

            if (sites is null)
            {
                // Keep what was known: a hiccup must not turn every project into "no site". One failed read is only
                // tried again - the file may just be being written; a second one is a problem to show.
                if (_sitesFailures++ == 0 && _snapshotTaken) retry = true;
                else SetProblem(SitesKey, "IIS's configuration can't be read.", events);
            }
            else
            {
                // IIS is what there is: a row per site, gone with its site.
                _sitesFailures = 0;
                foreach (var (name, site) in sites) added |= ApplySite(name, site, events);
                foreach (var gone in _projects.Keys.Where(name => !sites.ContainsKey(name)).ToList())
                {
                    _projects.Remove(gone);
                    _sizesWanted.Remove(gone);
                    _sizesAsked.TryRemove(gone, out _);
                    _sqlConnections.Remove(gone);
                    _sqlChecks.Remove(gone);
                    events.Add(new ProjectRemoved(gone));
                }
                ClearProblem(SitesKey, events);
                ClearProblem(SourceKey(ChangeKind.Iis), events);
                Synced();
            }
            Publish(events);
        }
        Done(ref _sitesAt);
        // A new site's size: walked now when sizes are shown - or, while resources are saved, once that ends.
        if (added && !_saving && _sizesShown) _sizesJob.Request();
        if (retry) _ = Task.Delay(QuickRetry, ct).ContinueWith(_ => _sitesJob.Request(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes IIS's site <paramref name="name"/> as it is now: a new row for a site not known yet, a change for one
    /// that differs. What its folder holds (DNN version, database) is read when the site turns up or serves another
    /// folder; the timed read of the folders catches what changes inside one. True when it was new.
    /// </summary>
    private bool ApplySite(string name, IisSiteRuntime site, List<MonitorEvent> events)
    {
        var url = site.BrowseUrl ?? "";
        if (!_projects.TryGetValue(name, out var known))
        {
            var folder = ReadFolder(name, site.PhysicalPath);
            ProjectState project = new()
            {
                Name = name,
                Directory = site.PhysicalPath,
                SiteUrl = url,
                InProjectsFolder = folder.InProjectsFolder,
                DnnVersion = folder.DnnVersion,
                DatabaseName = folder.Database,
                DatabaseProblem = folder.Problem,
                DatabaseElsewhere = folder.Elsewhere,
                DatabaseServer = folder.Server,
                DatabaseIsFile = folder.IsFile,
                Site = site,
            };
            project = WithSql(project, TrackConnection(name, folder.Connection));
            _projects[name] = project;
            _sizesWanted.Add(name);
            events.Add(new ProjectAdded(project));
            return true;
        }

        if (known.Name == name && site.SameAs(known.Site)) return false;
        var next = known with { Name = name, Site = site, SiteUrl = url };
        var changed = ProjectFacets.Site;
        if (known.Name != name || url != known.SiteUrl) changed |= ProjectFacets.Metadata;
        if (!string.Equals(site.PhysicalPath, known.Directory, StringComparison.OrdinalIgnoreCase))
        {
            // Another folder: another DNN, database and size.
            var folder = ReadFolder(name, site.PhysicalPath);
            next = next with
            {
                Directory = site.PhysicalPath, InProjectsFolder = folder.InProjectsFolder, DnnVersion = folder.DnnVersion,
                DatabaseName = folder.Database, DatabaseProblem = folder.Problem, DatabaseElsewhere = folder.Elsewhere,
                DatabaseServer = folder.Server, DatabaseIsFile = folder.IsFile, SizeBytes = null
            };
            next = WithSql(next, TrackConnection(name, folder.Connection));
            changed |= ProjectFacets.Metadata | ProjectFacets.Sql | ProjectFacets.Size;
            _sizesWanted.Add(name);
        }
        // Without worker processes there are no figures for them.
        if (known.Stats is not null && site.WorkerProcessIds.Count == 0)
        {
            next = next with { Stats = null };
            changed |= ProjectFacets.Stats;
        }
        // Only the name's capitals can differ here - keep the dictionary's key in step.
        if (known.Name != name) _projects.Remove(known.Name);
        Replace(next, changed, events);
        return false;
    }

    /// <summary>
    /// Follows <paramref name="name"/>'s database at <paramref name="connection"/> (its web.config's) - none to follow
    /// when null. A connection that changed is looked at again soon; until then nothing is known of it. Returns what
    /// is known now.
    /// </summary>
    private SiteDatabaseCheck? TrackConnection(string name, SiteSqlConnection? connection)
    {
        if (connection is null)
        {
            _sqlConnections.Remove(name);
            _sqlChecks.Remove(name);
            return null;
        }
        if (_sqlConnections.TryGetValue(name, out var known) && known == connection) return _sqlChecks.GetValueOrDefault(name);
        _sqlConnections[name] = connection;
        _sqlChecks.Remove(name);
        _sqlJob.Request();
        return null;
    }

    private static ProjectState WithSql(ProjectState project, SiteDatabaseCheck? check) => project with
    {
        SqlReachable = check?.Reachable, DatabaseExists = check?.Exists == true, SqlProblem = check?.Problem
    };
    /// <summary>
    /// A read threw what it wasn't written for: said like anything else that can't be read, and tried again like it
    /// (<see cref="Retry"/>) - not a table that quietly stays as it was.
    /// </summary>
    private void ReadFailed(string key, string what, Exception ex)
    {
        _log.LogWarning(ex, "{What}", what);
        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            SetProblem(key, $"{what}: {ex.Message}", events);
            Publish(events);
        }
    }

    // ─── What the sites' folders hold ─────────────────────────────────────

    /// <summary>
    /// What the folder a site serves holds: a DNN install (its version), the database its web.config names, and
    /// whether it is one of the projects folder's. What the site really has - never what DNN Manager's settings would
    /// make of it: a database web.config doesn't name isn't guessed, the problem says why there is none. Never throws.
    /// </summary>
    /// <param name="Server">The SQL Server web.config names.</param>
    /// <param name="IsFile">A LocalDB file - the site's own while it runs, not asked.</param>
    /// <param name="Connection">How to ask about the database - as web.config connects; null when it can't be asked.</param>
    private sealed record Folder(string? DnnVersion, string? Database, bool InProjectsFolder, bool Elsewhere, string? Problem,
        string? Server, bool IsFile, SiteSqlConnection? Connection);

    private Folder ReadFolder(string name, string directory)
    {
        var inProjectsFolder = IsInProjectsFolder(directory);
        string? version = null, database = null, problem = null, server = null;
        var elsewhere = false;
        var isFile = false;
        SiteSqlConnection? connection = null;
        try
        {
            if (directory.Length == 0)
                problem = "IIS gives the site no physical path";
            else if (!Directory.Exists(directory))
                problem = $"the site's folder isn't there: {directory}";
            else
            {
                version = DnnInstall.Version(directory);
                var webConfig = Path.Combine(directory, "web.config");
                if (!File.Exists(webConfig))
                    problem = "the site's folder has no web.config";
                else
                {
                    var read = _webConfig.ReadDatabaseConnection(webConfig);
                    if (read is not { Success: true, Value: { } c })
                        problem = $"web.config: {read.Error}";
                    // DNN's shipped connection (.\SQLExpress, a User Instance file): DNN isn't installed into a database yet.
                    else if (c.Kind == DatabaseKind.LocalDbFile && !Sql.ConnectionStrings.IsLocalDb(c.Server))
                        problem = "web.config still has DNN's own connection - DNN isn't installed into a database yet";
                    else if (c.Database.Length == 0)
                        problem = "web.config's SiteSqlServer names no database";
                    else
                    {
                        database = c.Database;
                        server = c.Server;
                        isFile = c.Kind == DatabaseKind.LocalDbFile;
                        elsewhere = isFile || c.UsesWindowsAuthentication ||
                                    !LocalSqlContainer.IsContainerServer(c.Server, _options.Docker.ContainerIp, _options.Docker.DefaultPort);
                        // Asked as the site connects: its login - or Windows authentication, as DNN Manager's user.
                        if (!isFile)
                            connection = c.UsesWindowsAuthentication
                                ? new SiteSqlConnection(c.Server, c.Database, "", "")
                                : new SiteSqlConnection(c.Server, c.Database, c.User, c.Password);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not read the folder of site {Site}", name);
            problem = $"the site's folder can't be read: {ex.Message}";
        }
        // Only a DNN site (or a project's folder) is expected to have one - any other site simply has no database.
        if (version is null && !inProjectsFolder) problem = null;
        return new Folder(version, database, inProjectsFolder, elsewhere, problem, server, isFile, connection);
    }

    private bool IsInProjectsFolder(string directory)
    {
        if (directory.Length == 0 || _options.BaseDirectory.Length == 0) return false;
        try
        {
            var root = Path.GetFullPath(_options.BaseDirectory).TrimEnd('\\') + "\\";
            return Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every site's folder again (a DNN installed, a web.config pointed at another database, the projects
    /// folder moved in the settings) and publishes what differs.
    /// </summary>
    private Task ReadProjectsAsync(CancellationToken ct)
    {
        List<(string Name, string Directory)> sites;
        lock (_gate) sites = _projects.Values.Select(p => (p.Name, p.Directory)).ToList();
        var read = sites.ToDictionary(s => s.Name, s => (s.Directory, Folder: ReadFolder(s.Name, s.Directory)), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            foreach (var (name, (directory, folder)) in read)
            {
                // Gone, or moved to another folder meanwhile (the sites read has read that one).
                if (!_projects.TryGetValue(name, out var known) || known.Directory != directory) continue;
                var check = TrackConnection(name, folder.Connection);
                var next = WithSql(known with
                {
                    DnnVersion = folder.DnnVersion, DatabaseName = folder.Database, DatabaseProblem = folder.Problem,
                    InProjectsFolder = folder.InProjectsFolder, DatabaseElsewhere = folder.Elsewhere,
                    DatabaseServer = folder.Server, DatabaseIsFile = folder.IsFile
                }, check);
                if (next == known) continue;
                Replace(next, next.SqlReachable != known.SqlReachable || next.DatabaseExists != known.DatabaseExists ||
                              next.SqlProblem != known.SqlProblem || next.DatabaseIsFile != known.DatabaseIsFile
                    ? ProjectFacets.Metadata | ProjectFacets.Sql : ProjectFacets.Metadata, events);
            }
            ClearProblem(ProjectsKey, events);
            ClearProblem(SourceKey(ChangeKind.Projects), events);
            Publish(events);
        }
        Done(ref _projectsAt);
        return Task.CompletedTask;
    }
    // ─── The SQL Server ───────────────────────────────────────────────────

    /// <summary>Asks each site's SQL Server about its database (<see cref="SiteDatabaseChecks"/>).</summary>
    private async Task ReadSqlAsync(CancellationToken ct)
    {
        Dictionary<string, SiteSqlConnection> sites;
        lock (_gate) sites = new Dictionary<string, SiteSqlConnection>(_sqlConnections, StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, SiteDatabaseCheck> found;
        using (var scope = _scopes.CreateScope())
            found = await SiteDatabaseChecks.AskAsync(scope.ServiceProvider.GetRequiredService<ISqlConnectionTester>(), sites, ct);

        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            foreach (var (name, check) in found)
            {
                // Its web.config changed meanwhile: this was about another database.
                if (!_sqlConnections.TryGetValue(name, out var now) || now != sites[name]) continue;
                _sqlChecks[name] = check;
                if (!_projects.TryGetValue(name, out var project)) continue;
                var next = WithSql(project, check);
                if (next != project) Replace(next, ProjectFacets.Sql, events);
            }
            Publish(events);
        }
        Done(ref _sqlAt);
    }

    // ─── Folder sizes ─────────────────────────────────────────────────────

    /// <summary>
    /// Every folder's size is to be walked again - now when the sizes are shown (<see cref="SizesShown"/>); otherwise,
    /// or while resources are saved (<see cref="SavingResources"/>), the folders stay marked until that changes.
    /// </summary>
    private void QueueAllSizes()
    {
        lock (_gate)
            foreach (var name in _projects.Keys) _sizesWanted.Add(name);
        Done(ref _sizesAt);
        if (!_saving && _sizesShown) _sizesJob.Request();
    }

    /// <summary>The folders of <paramref name="names"/> are to be walked again - as <see cref="QueueAllSizes"/>, only these.</summary>
    private void QueueSizes(IEnumerable<string> names)
    {
        lock (_gate)
            foreach (var name in names)
                if (_projects.ContainsKey(name)) _sizesWanted.Add(name);
        if (!_saving && _sizesShown) _sizesJob.Request();
    }

    // One project at a time: it is a walk over tens of thousands of files, and nobody is waiting for it. Saving
    // resources stops it between two projects - the rest stay marked for later.
    private Task ReadSizesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_saving)
        {
            string name, directory;
            lock (_gate)
            {
                // What was asked for by name first; the rest only while the sizes are shown.
                if (_sizesAsked.Keys.FirstOrDefault() is { } asked && _sizesAsked.TryRemove(asked, out _)) name = asked;
                else if (_sizesShown && _sizesWanted.Count > 0) name = _sizesWanted.First();
                else break;
                _sizesWanted.Remove(name);
                if (!_projects.TryGetValue(name, out var project)) continue;
                directory = project.Directory;
            }

            var size = DirectorySize(directory, ct);
            lock (_gate)
            {
                if (!_projects.TryGetValue(name, out var project) || project.SizeBytes == size) continue;
                var events = new List<MonitorEvent>();
                Replace(project with { SizeBytes = size }, ProjectFacets.Size, events);
                Publish(events);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Total bytes of every file under <paramref name="path"/>. Sums the sizes the directory scan
    /// already returns rather than stat-ing each file separately, skips reparse points (a junction
    /// would double-count its target or loop) and keeps counting past directories it cannot read
    /// instead of losing the whole total to one <c>UnauthorizedAccessException</c>.
    /// </summary>
    private static long DirectorySize(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var lengths = new FileSystemEnumerable<long>(path, (ref FileSystemEntry e) => e.Length, options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory
            };

            long total = 0;
            foreach (var length in lengths)
            {
                total += length;
                if (ct.IsCancellationRequested) break;
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    // ─── Worker processes and traffic ─────────────────────────────────────

    private void ReadWorkerProcesses()
    {
        var groups = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var project in _projects.Values)
                if (project.Site is { WorkerProcessIds.Count: > 0 } site) groups[project.Name] = site.WorkerProcessIds;
        var stats = _processes.Sample(groups);

        // IIS starts a worker on a site's first request and ends it when idle, without telling anyone: a different
        // set of w3wp processes means the sites have to be read again to see whose they are.
        var workers = _workerList.Read();
        if (_workers is not null && !_workers.SetEquals(workers)) _sitesJob.Request();
        _workers = workers;

        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            foreach (var project in _projects.Values.ToList())
            {
                var now = stats.GetValueOrDefault(project.Name);
                if (!Equals(project.Stats, now)) Replace(project with { Stats = now }, ProjectFacets.Stats, events);
            }
            Publish(events);
        }
    }

    private Task ReadTrafficAsync(CancellationToken ct)
    {
        var traffic = _iis.GetSiteTraffic();
        lock (_gate)
        {
            var events = new List<MonitorEvent>();
            foreach (var project in _projects.Values.ToList())
            {
                var now = traffic.GetValueOrDefault(project.Name);
                if (!Equals(project.Traffic, now)) Replace(project with { Traffic = now }, ProjectFacets.Traffic, events);
            }
            Publish(events);
        }
        Done(ref _trafficAt);
        return Task.CompletedTask;
    }

    // ─── State changes (under _gate) ──────────────────────────────────────

    private const string SitesKey = "sites", ProjectsKey = "projects";

    // A lost notification source, per part of the state: cleared once that part has been read again.
    private static string SourceKey(ChangeKind kind) => "source:" + kind;

    private void Replace(ProjectState project, ProjectFacets changed, List<MonitorEvent> events)
    {
        _projects[project.Name] = project;
        events.Add(new ProjectChanged(project, changed));
    }

    private void Synced() => Volatile.Write(ref _lastSync, DateTime.Now.Ticks);

    private void SetProblem(string key, string message, List<MonitorEvent> events)
    {
        _problems[key] = message;
        RefreshConnection(events);
    }

    private void ClearProblem(string key, List<MonitorEvent> events)
    {
        if (_problems.Remove(key)) RefreshConnection(events);
    }

    private void RefreshConnection(List<MonitorEvent> events)
    {
        var connection = !_snapshotTaken ? MonitorConnection.Connecting
            : _resyncing || _problems.Count > 0 ? MonitorConnection.Reconnecting
            : MonitorConnection.Live;
        var detail = _problems.Count > 0 ? string.Join(" ", _problems.Values)
            : _resyncing ? "The PC was asleep - reading everything again." : null;
        if (connection == _connection && detail == _connectionDetail) return;
        _connection = connection;
        _connectionDetail = detail;
        events.Add(new ConnectionChanged(connection, detail));
    }

    private void Publish(List<MonitorEvent> events)
    {
        if (events.Count > 0) Changed?.Invoke(events);
    }
}
