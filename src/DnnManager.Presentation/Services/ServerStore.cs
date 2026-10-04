using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Presentation.Pages.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Services;

/// <summary>
/// The state the window shows, on the UI thread - the one source of truth the Projects table, the IIS indicator and
/// the status bar's figures read from. It follows the <see cref="ServerStateMonitor"/>: each batch of events is
/// applied here to what is already shown - a row is added, removed or told what changed (<see cref="ProjectRow.Apply"/>)
/// - so nothing is ever cleared and reloaded, and each part of the window only hears about its own part:
/// <see cref="Projects"/>, <see cref="Runtime"/>, <see cref="SystemStats"/>, <see cref="Connection"/>.
/// <para>
/// It also runs what the user does to the sites: the row says "Starting…" at once, the operation runs, and the sites
/// are read again straight after - the row then shows what IIS says, whether that is the wished state or, after a
/// failure, the old one.
/// </para>
/// <para>
/// And it follows the <see cref="KeepWarmService"/>: each row shows its site's keep warm (<see cref="ProjectRow.KeepWarm"/>),
/// the service pauses while an operation runs, and what it has to say goes to the activity log.
/// </para>
/// </summary>
public sealed class ServerStore
{
    // After an operation its effects keep arriving for a moment (a site going from Starting to Started): not to be
    // taken for changes made outside the app.
    private static readonly TimeSpan OwnChangesLinger = TimeSpan.FromSeconds(5);
    // After IIS itself started or stopped its sites follow: for this long their changes are its doing.
    private static readonly TimeSpan SitesFollowIis = TimeSpan.FromSeconds(15);

    private readonly ServerStateMonitor _monitor;
    private readonly KeepWarmService _keepWarm;
    private readonly AppOptions _options;
    private readonly OperationRunner _runner;
    private readonly ActivityLog _log;
    private readonly ILogger<ServerStore> _logger;
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private readonly Dictionary<string, ProjectRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    // The operation of this store that the runner is running right now; null while it runs none, or someone else's.
    private Operation? _operation;
    // Until when a change is DNN Manager's own doing: anything (after an operation that can change anything), a row
    // (after an operation on it), and every site's state after IIS itself changed.
    private DateTime _ownUntil, _sitesFollowIisUntil;
    private readonly Dictionary<string, DateTime> _ownRows = new(StringComparer.OrdinalIgnoreCase);
    // Sites that are starting or stopping for a reason known here: where they end up is then no news either.
    private readonly HashSet<string> _explained = new(StringComparer.OrdinalIgnoreCase);
    private string? _runtimePending;

    public ServerStore(ServerStateMonitor monitor, KeepWarmService keepWarm, OperationRunner runner,
        ActivityLog log, IOptions<AppOptions> options, ILogger<ServerStore> logger)
    {
        _monitor = monitor; _keepWarm = keepWarm; _runner = runner; _log = log; _logger = logger;
        _options = options.Value;
        _runner.PropertyChanged += OnRunnerChanged;
        // Other settings (the projects folder) change what the sites' folders are read as: the app's own doing.
        options.Value.Changed += () => Linger(null);
    }

    /// <summary>The projects, by name. Rows stay the same objects while their project exists.</summary>
    public ObservableCollection<ProjectRow> Projects { get; } = [];

    /// <summary>The first snapshot has arrived - before that there is nothing to show yet.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>IIS itself.</summary>
    public IisServerState Runtime { get; private set; } = IisServerState.Unknown;

    /// <summary>What is being done to IIS right now ("IIS restarting…"), while that operation runs.</summary>
    public string? RuntimePending
    {
        get => _runtimePending;
        private set
        {
            if (_runtimePending == value) return;
            _runtimePending = value;
            RuntimeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>This PC's memory, CPU and disk use; null until first measured.</summary>
    public HostResources? SystemStats { get; private set; }

    public MonitorConnection Connection { get; private set; } = MonitorConnection.Connecting;

    /// <summary>What is wrong, while <see cref="Connection"/> isn't live.</summary>
    public string? ConnectionDetail { get; private set; }

    /// <summary>When the sites and the projects were last read successfully.</summary>
    public DateTime? LastSync => _monitor.LastSync;

    public event EventHandler? RuntimeChanged;
    public event EventHandler? SystemStatsChanged;
    public event EventHandler? ConnectionChanged;

    /// <summary>Starts the monitor and follows it. Once - the window does it when it is shown.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        // Handed to the UI thread and applied there in the order they were raised.
        _monitor.Changed += events => _dispatcher.InvokeAsync(() => Apply(events));
        _keepWarm.StatusChanged += (site, status) => _dispatcher.InvokeAsync(() =>
        {
            if (_rows.TryGetValue(site, out var row)) row.KeepWarm = status;
        });
        // Background activity: the Output tab shows each site's keep warm on its own, not among the operations.
        _keepWarm.Noticed += notice => _log.Background(notice.Message, notice.IsWarning);
        // Before the monitor: it sees the first snapshot of the sites too.
        _keepWarm.Start();
        _monitor.Start();
    }

    /// <summary>The Projects page is on screen (and the window not minimized): what only it shows is kept current.</summary>
    public void SetWatching(bool watching) => _monitor.Active = watching;

    /// <summary>The network column is shown: the sites' HTTP traffic is read.</summary>
    public void SetTrafficWanted(bool wanted) => _monitor.TrafficWanted = wanted;

    /// <summary>The Size column is shown: the projects' folders are walked for their size.</summary>
    public void SetSizesShown(bool shown) => _monitor.SizesShown = shown;

    /// <summary>Walks one project's folder for its size now - for its overview.</summary>
    public void MeasureSize(string name) => _monitor.MeasureSize(name);

    /// <summary>
    /// The window is minimized and resources are to be saved (<see cref="EfficiencyMode"/>): this PC's figures aren't
    /// read and folder sizes wait until it is restored. The sites are followed as always.
    /// </summary>
    public void SetSaving(bool saving) => _monitor.SavingResources = saving;

    // ─── Applying what the monitor reports ────────────────────────────────

    private void Apply(IReadOnlyList<MonitorEvent> events)
    {
        foreach (var e in events)
        {
            // One change that can't be shown must not take the rest of the batch with it: the monitor has told them
            // all, and won't again.
            try { Apply(e); }
            catch (Exception ex) { _logger.LogError(ex, "Could not show a change ({Change})", e.GetType().Name); }
        }
    }

    private void Apply(MonitorEvent e)
    {
        switch (e)
        {
            case ProjectAdded added:
                Add(added.Project);
                break;
            case ProjectChanged changed when _rows.TryGetValue(changed.Project.Name, out var row):
                var before = row.State;
                row.Apply(changed.Project, changed.Changed);
                if (row.State != before) NoteSiteChange(row, before);
                break;
            case ProjectRemoved removed when _rows.Remove(removed.Name, out var gone):
                Projects.Remove(gone);
                _explained.Remove(gone.Name);
                NoteOutsideChange($"Site '{gone.Name}' was removed from IIS.", gone);
                break;
            case Infrastructure.Monitoring.RuntimeChanged runtime:
                var was = Runtime;
                Runtime = runtime.State;
                // Its sites follow: what they report in the next moments is IIS's doing, not something done to each.
                if (IsLoaded) _sitesFollowIisUntil = DateTime.UtcNow + SitesFollowIis;
                RuntimeChanged?.Invoke(this, EventArgs.Empty);
                NoteOutsideChange(RuntimeChange(was, runtime.State));
                break;
            case HostStatsChanged host:
                SystemStats = host.Stats;
                SystemStatsChanged?.Invoke(this, EventArgs.Empty);
                // These arrive every two seconds, whatever else happens: the clock for "started 5 minutes ago". (Not
                // while resources are saved - the first one after that brings every row up to date.)
                foreach (var shown in Projects) shown.UpdateLastStarted();
                break;
            case Infrastructure.Monitoring.ConnectionChanged connection:
                Connection = connection.Connection;
                ConnectionDetail = connection.Detail;
                if (connection.Connection != MonitorConnection.Connecting) IsLoaded = true;
                ConnectionChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void Add(ProjectState project)
    {
        if (_rows.ContainsKey(project.Name)) return;
        var row = new ProjectRow(project) { IsBusy = _runner.IsBusy, KeepWarm = _keepWarm.StatusOf(project.Name) };
        _rows[project.Name] = row;
        // Kept in name order.
        var index = 0;
        while (index < Projects.Count && string.Compare(Projects[index].Name, project.Name, StringComparison.OrdinalIgnoreCase) < 0) index++;
        Projects.Insert(index, row);
        NoteOutsideChange($"Site '{project.Name}' was added to IIS.");
    }

    // ─── Changes made outside the app ─────────────────────────────────────

    /// <summary>
    /// Whether what just changed is DNN Manager's own doing: an operation that can change anything (a new project, an
    /// import, IIS itself) is running or has just run - or, for something on <paramref name="row"/>, one on that row.
    /// What its own operations change is theirs to report.
    /// </summary>
    private bool IsOwn(ProjectRow? row)
    {
        var now = DateTime.UtcNow;
        if (now < _ownUntil || (_runner.IsBusy && _operation?.Rows is null)) return true;
        if (row is null) return false;
        return row.Pending is not null || _operation?.Rows?.Contains(row) == true ||
               (_ownRows.TryGetValue(row.Name, out var until) && now < until);
    }

    /// <summary>
    /// A line in the activity log for something that happened without DNN Manager doing it - a site removed in
    /// IIS Manager, IIS stopped in a terminal.
    /// </summary>
    /// <param name="row">The row the change is on, if it is on one.</param>
    private void NoteOutsideChange(string? message, ProjectRow? row = null)
    {
        // Before the first snapshot nothing has changed yet - it is all being read for the first time.
        if (message is null || !IsLoaded || IsOwn(row)) return;
        _log.Info(message);
    }

    /// <summary>
    /// A site's state changed: noted in the activity log when nothing known here explains it - not an operation of
    /// DNN Manager's, not IIS itself stopping or starting (its sites follow, and read as stopped while it is down),
    /// and not the end of a start or stop that began as one of those (a site still stopping when its operation ended
    /// is, some seconds later, stopped).
    /// </summary>
    private void NoteSiteChange(ProjectRow row, SiteRunState before)
    {
        var state = row.State;
        var explained = IsOwn(row) || Runtime != IisServerState.Running || DateTime.UtcNow < _sitesFollowIisUntil ||
                        (OnItsWay(before) && _explained.Contains(row.Name));
        if (explained && OnItsWay(state)) _explained.Add(row.Name);
        else _explained.Remove(row.Name);
        if (!explained && IsLoaded && SiteChange(row.Name, before, state) is { } message) _log.Info(message);
    }

    private static bool OnItsWay(SiteRunState state) => state is SiteRunState.Starting or SiteRunState.Stopping;

    // Only a site going from one outcome to another - not the "starting" and "stopping" on the way there, and not
    // from a state IIS couldn't tell.
    private static string? SiteChange(string name, SiteRunState before, SiteRunState now)
    {
        if (before == SiteRunState.Unknown || now == SiteRunState.Unknown || OnItsWay(now)) return null;
        return now == SiteRunState.Running
            ? $"Site '{name}' is running - started outside DNN Manager."
            : $"Site '{name}' stopped - outside DNN Manager.";
    }

    private static string? RuntimeChange(IisServerState before, IisServerState now) => now switch
    {
        IisServerState.Running when before != IisServerState.Unknown => "IIS is running - started outside DNN Manager.",
        IisServerState.Stopped => "IIS stopped - outside DNN Manager.",
        _ => null
    };

    // ─── Operations ───────────────────────────────────────────────────────

    /// <summary>What one of the store's operations is about: some rows, or - none given - anything (IIS itself).</summary>
    /// <param name="began">It changes things from the start - it asks nothing first.</param>
    private sealed class Operation(IReadOnlyList<ProjectRow>? rows, bool began = false)
    {
        private volatile bool _began = began;

        public IReadOnlyList<ProjectRow>? Rows { get; } = rows;

        /// <summary>It has begun - its question, if it asks one, was answered yes. Set from the operation's thread.</summary>
        public bool Began
        {
            get => _began;
            set => _began = value;
        }
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        foreach (var row in Projects) row.IsBusy = _runner.IsBusy;
        if (_runner.IsBusy)
        {
            // Keep warm leaves alone what the operation may be changing: its rows, IIS itself - or, for someone else's
            // operation, it only doesn't warm a site up from cold meanwhile.
            _keepWarm.Pause(_operation is null ? KeepWarmPause.WarmUps
                : _operation.Rows is { } rows ? KeepWarmPause.For(rows.Select(r => r.Name).ToList())
                : KeepWarmPause.All);
            return;
        }

        Linger(_operation);
        // Someone else's operation (a new project, an import…): read what it may have changed. Its own notifications
        // do the same; this doesn't wait for them. The store's own operations read back themselves - and resume keep
        // warm then (RunAsync).
        if (_operation is not null) return;
        if (_started) _ = ResumeKeepWarmAfterAsync(_monitor.SyncAsync(), recheck: null);
        else _keepWarm.Resume();
    }

    /// <summary>
    /// Once <paramref name="read"/> - the read-back after an operation - has published what the operation left: keep warm
    /// checks again what it was about (<paramref name="recheck"/>; a site it restarted is warmed up at once) and resumes.
    /// Not before - it would still see a site the operation stopped as running, and request it. The check also when
    /// another operation has started meanwhile (its pause replaced this one's), the resume only when none has.
    /// </summary>
    private async Task ResumeKeepWarmAfterAsync(Task read, Operation? recheck)
    {
        try { await read; }
        catch (Exception ex) { _logger.LogDebug(ex, "The read-back after an operation failed"); }
        if (recheck is not null) _keepWarm.Recheck(recheck.Rows?.Select(r => r.Name).ToList());
        if (!_runner.IsBusy) _keepWarm.Resume();
    }

    /// <summary>
    /// What an operation did keeps arriving for a moment after it: for that long it isn't taken for a change made
    /// outside - on its rows, or anywhere when it wasn't about some rows (or wasn't the store's: null).
    /// </summary>
    private void Linger(Operation? operation)
    {
        var now = DateTime.UtcNow;
        if (operation?.Rows is not { } rows)
        {
            _ownUntil = now + OwnChangesLinger;
            return;
        }
        foreach (var name in _ownRows.Where(r => r.Value < now).Select(r => r.Key).ToList()) _ownRows.Remove(name);
        foreach (var row in rows) _ownRows[row.Name] = now + OwnChangesLinger;
    }

    /// <summary>
    /// Runs one of the store's operations. It is <see cref="_operation"/> for exactly as long as the runner runs it -
    /// what changes meanwhile is known to be its doing - and what it changed is read back before this returns.
    /// </summary>
    private async Task<bool> RunAsync(Operation operation, string title,
        Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result>> work, Func<Task> readBack)
    {
        _operation = operation;
        bool done;
        try
        {
            done = await _runner.RunAsync(title, work);
        }
        finally
        {
            // The runner is free again - for another operation, also while this one's read-back is still on its way.
            if (_operation == operation) _operation = null;
        }
        // What it was about gets a new chance - unless it never began (its question was answered no): nothing changed.
        await ResumeKeepWarmAfterAsync(readBack(), operation.Began ? operation : null);
        // Counted again from here: what was read back may show only the beginning of what it did.
        Linger(operation);
        return done;
    }

    /// <summary>
    /// Starts, stops or restarts the sites of <paramref name="rows"/> as one operation. The rows say so at once;
    /// when it has run, the sites are read again and the rows show what IIS says.
    /// </summary>
    public async Task ControlSitesAsync(SiteAction action, IReadOnlyList<ProjectRow> rows)
    {
        if (rows.Count == 0 || _runner.IsBusy) return;
        var names = rows.Select(r => r.Name).ToList();
        var pending = action switch
        {
            SiteAction.Start => "Starting…",
            SiteAction.Stop => "Stopping…",
            _ => "Restarting…"
        };
        foreach (var row in rows) row.Pending = pending;
        try
        {
            await RunAsync(new Operation(rows, began: true), names.Count == 1 ? $"{action} '{names[0]}'" : $"{action} {names.Count} sites",
                (sp, reporter, ct) => sp.GetRequiredService<ControlSitesUseCase>().ExecuteAsync(action, names, reporter, ct),
                _monitor.SyncSitesAsync);
        }
        finally
        {
            foreach (var row in rows) row.Pending = null;
        }
    }

    /// <summary>
    /// Removes the projects of <paramref name="rows"/> - RemoveProjectUseCase asks once to confirm (each goes
    /// with its database); once it has started on them the rows say "Removing…". A removed project's row goes when its IIS site
    /// is gone; a row that stays (the user said no, or it failed) is as it was.
    /// </summary>
    public async Task RemoveAsync(IReadOnlyList<ProjectRow> rows)
    {
        if (rows.Count == 0 || _runner.IsBusy) return;
        var names = rows.Select(r => r.Name).ToList();
        var sites = rows.Select(r => new SiteToRemove(r.Name, r.Path, r.Project.InProjectsFolder)).ToList();
        var finished = false;
        void Started()
        {
            if (finished) return;
            foreach (var row in rows) row.Pending = "Removing…";
        }

        try
        {
            var operation = new Operation(rows);
            await RunAsync(operation, names.Count == 1 ? $"Remove '{names[0]}'" : $"Remove {names.Count} projects",
                (sp, reporter, ct) => sp.GetRequiredService<RemoveProjectUseCase>()
                    .ExecuteAsync(sites, new StartSignal(reporter, () => Begin(operation, Started)), ct),
                _monitor.SyncAsync);
        }
        finally
        {
            finished = true;
            foreach (var row in rows) row.Pending = null;
        }
    }

    /// <summary>
    /// Clears the cache of <paramref name="row"/>'s site - ClearSiteCacheUseCase asks first; once it has started the
    /// row says "Clearing cache…". The table stays as it is: only that site is read again.
    /// </summary>
    public async Task ClearCacheAsync(ProjectRow row)
    {
        if (_runner.IsBusy) return;
        var (name, path) = (row.Name, row.Path);
        var finished = false;
        void Started()
        {
            if (!finished) row.Pending = "Clearing cache…";
        }

        bool cleared;
        try
        {
            var operation = new Operation([row]);
            cleared = await RunAsync(operation, $"Clear the cache of '{name}'",
                (sp, reporter, ct) => sp.GetRequiredService<ClearSiteCacheUseCase>()
                    .ExecuteAsync(name, path, new StartSignal(reporter, () => Begin(operation, Started)), ct),
                _monitor.SyncSitesAsync);
        }
        finally
        {
            finished = true;
            row.Pending = null;
        }
        if (cleared) Toast.Show($"The cache of '{name}' was cleared.", ToastKind.Success);
    }

    /// <summary>
    /// Starts, stops or restarts IIS itself. IisServerUseCase asks before stopping or restarting: the indicator says
    /// "IIS stopping…" once the answer was yes.
    /// </summary>
    public async Task ControlIisAsync(IisServerAction action)
    {
        if (_runner.IsBusy) return;
        var pending = action switch
        {
            IisServerAction.Start => "IIS starting…",
            IisServerAction.Stop => "IIS stopping…",
            _ => "IIS restarting…"
        };
        var finished = false;
        void Started()
        {
            if (!finished) RuntimePending = pending;
        }

        // Starting asks nothing.
        if (action == IisServerAction.Start) Started();
        try
        {
            var operation = new Operation(null, began: action == IisServerAction.Start);
            await RunAsync(operation, $"{action} IIS",
                (sp, reporter, ct) => sp.GetRequiredService<IisServerUseCase>()
                    .ExecuteAsync(action, new StartSignal(reporter, () => Begin(operation, Started)), ct),
                _monitor.SyncSitesAsync);
        }
        finally
        {
            finished = true;
            RuntimePending = null;
        }
    }

    // ─── Keep warm ────────────────────────────────────────────────────────

    /// <summary>Switches keep warm on or off for <paramref name="row"/>'s site - at once, nothing is asked.</summary>
    public void ToggleKeepWarm(ProjectRow row)
    {
        var name = row.Name;
        if (row.KeepWarmOn)
        {
            _keepWarm.SetEnabled(name, false);
            // At once - the service confirms it a moment later.
            row.KeepWarm = KeepWarmStatus.Off;
            _log.Background($"No longer keeping '{name}' warm.", false);
            return;
        }
        if (!row.CanToggleKeepWarm) return;

        _keepWarm.SetEnabled(name, true);
        row.KeepWarm = new KeepWarmStatus(KeepWarmState.Waiting, "Waiting");
        var minutes = _options.KeepWarm.PingMinutes;
        _log.Background($"Keeping '{name}' warm - while DNN Manager runs it requests the site at least every " +
                  $"{KeepWarmRules.Span(TimeSpan.FromMinutes(minutes))} (sooner when its app pool needs it) and warms it up again after a recycle.", false);
    }

    /// <summary>Requests <paramref name="row"/>'s site now - also after it failed too often.</summary>
    public void CheckKeepWarm(ProjectRow row) => _keepWarm.CheckNow(row.Name);

    /// <summary>The operation's first report (on its thread): it has begun - the rest of <paramref name="started"/> on the UI thread.</summary>
    private void Begin(Operation operation, Action started)
    {
        operation.Began = true;
        _dispatcher.InvokeAsync(started);
    }

    /// <summary>
    /// Passes an operation's reports on, and tells when the first one comes: the operation has then asked what it
    /// had to ask, and begun.
    /// </summary>
    private sealed class StartSignal(IProgressReporter inner, Action started) : IProgressReporter
    {
        private int _signalled;

        private void Signal()
        {
            if (Interlocked.Exchange(ref _signalled, 1) == 0) started();
        }

        public void Step(string title) { Signal(); inner.Step(title); }
        public void Info(string message) { Signal(); inner.Info(message); }
        public void Success(string message) { Signal(); inner.Success(message); }
        public void Fail(string message) { Signal(); inner.Fail(message); }
        public void Warn(string message) { Signal(); inner.Warn(message); }
        public void Progress(string message) { Signal(); inner.Progress(message); }
        public void Step(string title, string name) { Signal(); inner.Step(title, name); }
        public void Fail(string message, IReadOnlyList<string> details, string? hint) { Signal(); inner.Fail(message, details, hint); }
        // What the operation is about - not yet a sign that it has begun.
        public void Plan(params string[] stages) => inner.Plan(stages);
        public void Context(string text) => inner.Context(text);
        public void Fact(string name, string value) => inner.Fact(name, value);
        public void Link(string url) => inner.Link(url);
    }
}
