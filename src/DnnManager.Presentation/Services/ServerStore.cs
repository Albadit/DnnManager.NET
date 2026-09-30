using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
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
/// </summary>
public sealed class ServerStore
{
    // After an operation its effects keep arriving for a moment (a site going from Starting to Started): not to be
    // taken for changes made outside the app.
    private static readonly TimeSpan OwnChangesLinger = TimeSpan.FromSeconds(5);
    // After IIS itself started or stopped its sites follow: for this long their changes are its doing.
    private static readonly TimeSpan SitesFollowIis = TimeSpan.FromSeconds(15);

    private readonly ServerStateMonitor _monitor;
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

    public ServerStore(ServerStateMonitor monitor, OperationRunner runner, ActivityLog log, IOptions<AppOptions> options,
        ILogger<ServerStore> logger)
    {
        _monitor = monitor; _runner = runner; _log = log; _logger = logger;
        _runner.PropertyChanged += OnRunnerChanged;
        // Another projects folder in the settings brings other projects: the app's own doing, like an operation's.
        options.Value.Changed += () => Linger(null);
    }

    /// <summary>The projects, by name. Rows stay the same objects while their project exists.</summary>
    public ObservableCollection<ProjectRow> Projects { get; } = new();

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
        _monitor.Start();
    }

    /// <summary>The Projects page is on screen (and the window not minimized): what only it shows is kept current.</summary>
    public void SetWatching(bool watching) => _monitor.Active = watching;

    /// <summary>The network column is shown: the sites' HTTP traffic is read.</summary>
    public void SetTrafficWanted(bool wanted) => _monitor.TrafficWanted = wanted;

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
                NoteOutsideChange($"Project folder '{gone.Name}' is gone.", gone);
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
                // These arrive every two seconds, whatever else happens: the clock for "started 5 minutes ago".
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
        var row = new ProjectRow(project) { IsBusy = _runner.IsBusy };
        _rows[project.Name] = row;
        // Kept in name order, as the folders are listed.
        var index = 0;
        while (index < Projects.Count && string.Compare(Projects[index].Name, project.Name, StringComparison.OrdinalIgnoreCase) < 0) index++;
        Projects.Insert(index, row);
        NoteOutsideChange($"New project folder '{project.Name}'.");
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
    /// A line in the activity log for something that happened without DNN Manager doing it - a folder deleted in
    /// Explorer, IIS stopped in a terminal.
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
        if (now == SiteRunState.NoSite) return $"Site '{name}' was removed from IIS.";
        if (before == SiteRunState.NoSite) return $"Site '{name}' was added to IIS.";
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
    private sealed class Operation(IReadOnlyList<ProjectRow>? rows)
    {
        public IReadOnlyList<ProjectRow>? Rows { get; } = rows;
    }

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        foreach (var row in Projects) row.IsBusy = _runner.IsBusy;
        if (_runner.IsBusy) return;

        Linger(_operation);
        // Someone else's operation (a new project, an import…): read what it may have changed. Its own notifications
        // do the same; this doesn't wait for them. The store's own operations read back themselves.
        if (_operation is null && _started) _ = _monitor.SyncAsync();
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
    private async Task RunAsync(Operation operation, string title,
        Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result>> work, Func<Task> readBack)
    {
        _operation = operation;
        try
        {
            await _runner.RunAsync(title, work);
        }
        finally
        {
            // The runner is free again - for another operation, also while this one's read-back is still on its way.
            if (_operation == operation) _operation = null;
        }
        await readBack();
        // Counted again from here: what was read back may show only the beginning of what it did.
        Linger(operation);
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
            await RunAsync(new Operation(rows), names.Count == 1 ? $"{action} '{names[0]}'" : $"{action} {names.Count} sites",
                (sp, reporter, ct) => sp.GetRequiredService<ControlSitesUseCase>().ExecuteAsync(action, names, reporter, ct),
                _monitor.SyncSitesAsync);
        }
        finally
        {
            foreach (var row in rows) row.Pending = null;
        }
    }

    /// <summary>
    /// Removes the projects of <paramref name="rows"/> - RemoveProjectUseCase asks about the databases and to
    /// confirm; once it has started on them the rows say "Removing…". A removed project's row goes when its folder
    /// is gone; a row that stays (the user said no, or it failed) is as it was.
    /// </summary>
    public async Task RemoveAsync(IReadOnlyList<ProjectRow> rows)
    {
        if (rows.Count == 0 || _runner.IsBusy) return;
        var names = rows.Select(r => r.Name).ToList();
        var finished = false;
        void Started()
        {
            if (finished) return;
            foreach (var row in rows) row.Pending = "Removing…";
        }

        try
        {
            await RunAsync(new Operation(rows), names.Count == 1 ? $"Remove '{names[0]}'" : $"Remove {names.Count} projects",
                (sp, reporter, ct) => sp.GetRequiredService<RemoveProjectUseCase>()
                    .ExecuteAsync(names, new StartSignal(reporter, () => _dispatcher.InvokeAsync(Started)), ct),
                _monitor.SyncAsync);
        }
        finally
        {
            finished = true;
            foreach (var row in rows) row.Pending = null;
        }
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
            await RunAsync(new Operation(null), $"{action} IIS",
                (sp, reporter, ct) => sp.GetRequiredService<IisServerUseCase>()
                    .ExecuteAsync(action, new StartSignal(reporter, () => _dispatcher.InvokeAsync(Started)), ct),
                _monitor.SyncSitesAsync);
        }
        finally
        {
            finished = true;
            RuntimePending = null;
        }
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
    }
}
