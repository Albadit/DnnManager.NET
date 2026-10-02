using System.ComponentModel;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>A project's IIS site as the Projects table shows it.</summary>
public enum SiteRunState { Stopped, Starting, Running, Stopping, Unknown }

/// <summary>
/// How a site's keep-warm flame looks: an outline while off; filled while on - in the flame's colour when warm, pulsing
/// while a request is on its way or due, grey while paused, with a red dot while failing.
/// </summary>
public enum KeepWarmLook { Off, Warm, Busy, Paused, Problem }

/// <summary>
/// One row of the Projects table: an IIS site. It lives in the <see cref="ServerStore"/> for as long as the site exists and is
/// changed in place: <see cref="Apply"/> takes the project as the monitor now knows it and raises change
/// notifications for the facet that differs only - so one site stopping redraws one row's state, not the table. The
/// check box, the expanded details and what the row's buttons may do right now are the row's own.
/// </summary>
public sealed class ProjectRow : INotifyPropertyChanged
{
    private const string None = "-";
    // Not known yet: the database state and the folder size arrive after the row is shown.
    private const string NotYet = "…";

    // The properties each facet shows, for the change notifications.
    private static readonly string[] MetadataProperties =
        [nameof(Name), nameof(Url), nameof(HasUrl), nameof(Path), nameof(IsDnn), nameof(Dnn), nameof(Database)];
    private static readonly string[] SiteProperties =
    [
        nameof(IdText), nameof(IdSort), nameof(PortsText), nameof(AppPoolText), nameof(PidText), nameof(BindingsText),
        nameof(HostsText)
    ];
    private static readonly string[] StateProperties =
    [
        nameof(State), nameof(StateText), nameof(IsTransitioning), nameof(ShowStart), nameof(ShowStop), nameof(ShowProgress)
    ];
    // What the row's buttons may do - also depends on whether an operation is running anywhere.
    private static readonly string[] ActionProperties = [nameof(CanStart), nameof(CanStop), nameof(CanRestart), nameof(CanRemove)];
    private static readonly string[] StatsProperties =
    [
        nameof(CpuText), nameof(CpuSort), nameof(MemoryText), nameof(MemorySort), nameof(MemoryPercentText),
        nameof(DiskText), nameof(DiskSort)
    ];
    private static readonly string[] StartedProperties = [nameof(LastStartedSort), nameof(LastStartedTip)];
    private static readonly string[] KeepWarmProperties =
    [
        nameof(KeepWarm), nameof(KeepWarmOn), nameof(KeepWarmPending), nameof(KeepWarmLook), nameof(KeepWarmBusy), nameof(KeepWarmText),
        nameof(KeepWarmTip), nameof(KeepWarmAction), nameof(CanToggleKeepWarm), nameof(Details)
    ];

    private ProjectState _project;
    private bool _isChecked;
    private bool _isExpanded;
    private bool _busy;
    private string? _pending;
    private KeepWarmStatus _keepWarm = KeepWarmStatus.Off;
    private bool _keepWarmPending;
    // "5 minutes ago" changes with time, not with the state: kept to tell when it has to be shown again.
    private string _lastStarted;
    // Put together when first asked for, and again after a change to what it is made of.
    private string? _searchKey;

    public ProjectRow(ProjectState project)
    {
        _project = project;
        _lastStarted = LastStartedNow();
    }

    /// <summary>The project as the monitor last reported it.</summary>
    public ProjectState Project => _project;

    public string Name => _project.Name;
    /// <summary>Where a browser opens the site, from its bindings; "-" without a web binding.</summary>
    public string Url => _project.SiteUrl.Length > 0 ? _project.SiteUrl : None;
    public bool HasUrl => _project.SiteUrl.Length > 0;
    /// <summary>The folder the site serves - its physical path in IIS.</summary>
    public string Path => _project.Directory;
    public bool IsDnn => _project.DnnVersion is not null;
    public string Dnn => _project.DnnVersion ?? "(none)";
    public string Database => _project.DatabaseName ?? None;
    // "Live" when the database is on the SQL Server, "Offline" when the server doesn't answer, "(none)" when the
    // server is up but the database doesn't exist; "External" for one on another server (or a LocalDB file), which
    // isn't followed; "-" for a site without a database.
    public string Sql => _project.DatabaseName is null ? None : _project.DatabaseElsewhere ? "External" : _project.SqlReachable switch
    {
        null => NotYet,
        false => "Offline",
        true => _project.DatabaseExists ? "Live" : "(none)"
    };
    public string Size => _project.SizeBytes is { } bytes ? $"{bytes / 1024d / 1024d:N1} MB" : NotYet;
    public long SizeBytes => _project.SizeBytes ?? -1;

    // ─── IIS site ─────────────────────────────────────────────────────────

    private IisSiteRuntime Site => _project.Site;

    // A started site whose app pool is stopped answers 503: for the user that is a stopped site (Start starts the pool).
    private bool PoolStopped => Site is { State: "Started", AppPoolState: "Stopped" };

    public SiteRunState State
    {
        get
        {
            var site = Site;
            // An app pool on its way down is the site on its way down, whatever the site says itself: until the
            // worker process has ended it can't be started again.
            if (site.AppPoolState == "Stopping") return SiteRunState.Stopping;
            return site.State switch
            {
                "Started" => site.AppPoolState switch
                {
                    "Stopped" => SiteRunState.Stopped,
                    "Starting" => SiteRunState.Starting,
                    _ => SiteRunState.Running
                },
                "Stopped" => SiteRunState.Stopped,
                "Starting" => SiteRunState.Starting,
                "Stopping" => SiteRunState.Stopping,
                _ => SiteRunState.Unknown
            };
        }
    }

    /// <summary>What the row is doing now ("Starting…", "Removing…"), set while its operation runs.</summary>
    public string? Pending
    {
        get => _pending;
        set
        {
            if (_pending == value) return;
            _pending = value;
            Raise(nameof(Pending));
            Raise(StateProperties);
            Raise(ActionProperties);
        }
    }

    public string StateText => Pending ?? SettledStateText;

    private string SettledStateText => State switch
    {
        SiteRunState.Running => "Running",
        SiteRunState.Stopped => PoolStopped ? "App pool stopped" : "Stopped",
        SiteRunState.Starting => "Starting…",
        SiteRunState.Stopping => "Stopping…",
        // IIS runs but doesn't tell this site's state (it doesn't know the site yet).
        _ => "State unknown"
    };

    /// <summary>For the status dot: amber while something is changing.</summary>
    public bool IsTransitioning => Pending is not null || State is SiteRunState.Starting or SiteRunState.Stopping;

    public string IdText => Site.Id.ToString();
    public long IdSort => Site.Id;
    public string PortsText => Site.Ports.Count > 0 ? string.Join(", ", Site.Ports) : None;
    public string AppPoolText => Site.AppPool.Length == 0 ? None : Site.AppPoolState is { } state ? $"{Site.AppPool} - {state}" : Site.AppPool;
    public string PidText => Site.WorkerProcessIds.Count > 0 ? string.Join(", ", Site.WorkerProcessIds) : None;
    /// <summary>Each binding as "https *:443:example.com", one per line.</summary>
    public string BindingsText => Site.Bindings.Count > 0 ? string.Join(Environment.NewLine, Site.Bindings) : None;
    /// <summary>The host names the site answers to.</summary>
    public string HostsText => string.Join(", ", Site.Bindings.Select(b => b.Host).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The IIS site as the monitor last read it - its bindings, app pool and folder.</summary>
    public IisSiteRuntime IisSite => Site;

    // ─── Worker processes ────────────────────────────────────────────────

    private ProcessGroupStats? Stats => _project.Stats;

    public double CpuSort => Stats?.CpuPercent ?? -1;
    public string CpuText => Stats?.CpuPercent is { } cpu ? $"{cpu:0.00}%" : None;
    public long MemorySort => Stats?.MemoryBytes ?? -1;
    public string MemoryText => Stats is { } s ? ByteSize.Format(s.MemoryBytes) : None;
    public string MemoryPercentText => Stats is { } s ? $"{s.MemoryPercent:0.00}%" : None;
    // Read / written by the worker process since it started.
    public long DiskSort => Stats is { } s ? s.ReadBytes + s.WriteBytes : -1;
    public string DiskText => Stats is { } s ? $"{ByteSize.Format(s.ReadBytes)} / {ByteSize.Format(s.WriteBytes)}" : None;
    // Received / sent by the site over HTTP since IIS started.
    public long NetworkSort => _project.Traffic is { } t ? t.BytesReceived + t.BytesSent : -1;
    public string NetworkText => _project.Traffic is { } t ? $"{ByteSize.Format(t.BytesReceived)} / {ByteSize.Format(t.BytesSent)}" : None;
    public DateTime LastStartedSort => Stats?.Started ?? DateTime.MinValue;
    public string LastStartedText => _lastStarted;
    public string LastStartedTip => Stats?.Started is { } started
        ? $"The site's worker process started {started:g}."
        : "No worker process - IIS starts one on the site's first request.";

    private string LastStartedNow() => Stats?.Started is { } started ? Ago(DateTime.Now - started) : None;

    /// <summary>"5 minutes ago" moves on without anything changing: shows it again when it reads differently now.</summary>
    public void UpdateLastStarted()
    {
        var lastStarted = LastStartedNow();
        if (lastStarted == _lastStarted) return;
        _lastStarted = lastStarted;
        Raise(nameof(LastStartedText));
    }

    // ─── Keep warm ────────────────────────────────────────────────────────

    /// <summary>The site's keep warm, as the keep-warm service last reported it.</summary>
    public KeepWarmStatus KeepWarm
    {
        get => _keepWarm;
        set
        {
            if (_keepWarm == value) return;
            _keepWarm = value;
            Raise(KeepWarmProperties);
        }
    }

    public bool KeepWarmOn => _keepWarm.IsOn;

    /// <summary>Being switched on: what has to be asked first (the site's mail server) is being read.</summary>
    public bool KeepWarmPending
    {
        get => _keepWarmPending;
        set
        {
            if (_keepWarmPending == value) return;
            _keepWarmPending = value;
            Raise(KeepWarmProperties);
        }
    }

    public KeepWarmLook KeepWarmLook => KeepWarmPending ? KeepWarmLook.Busy : _keepWarm.State switch
    {
        KeepWarmState.Off => KeepWarmLook.Off,
        KeepWarmState.Warm => KeepWarmLook.Warm,
        KeepWarmState.Waiting or KeepWarmState.WarmingUp => KeepWarmLook.Busy,
        KeepWarmState.Paused => KeepWarmLook.Paused,
        _ => KeepWarmLook.Problem
    };

    /// <summary>For the flame's pulse.</summary>
    public bool KeepWarmBusy => KeepWarmLook == KeepWarmLook.Busy;

    public string KeepWarmText => KeepWarmPending ? "Checking the site's e-mail settings…" : _keepWarm.Text;

    /// <summary>What switching it does: "Keep warm" or "Stop keeping warm".</summary>
    public string KeepWarmAction => KeepWarmOn ? "Stop keeping warm" : "Keep warm";

    /// <summary>The flame's tooltip: what a click does, then how it is going.</summary>
    public string KeepWarmTip => KeepWarmPending ? KeepWarmText
        : KeepWarmOn
        ? $"Stop keeping the site warm{Environment.NewLine}{KeepWarmText}"
        : CanToggleKeepWarm
            ? $"Keep the site warm - while DNN Manager runs, it requests the site every few minutes so IIS doesn't shut it down, and its next page opens at once{Environment.NewLine}Off"
            : "Keep warm - the site has no http or https binding to request";

    /// <summary>A site with an address can be kept warm; one that is kept warm can always be switched off.</summary>
    public bool CanToggleKeepWarm => !KeepWarmPending && (KeepWarmOn || HasUrl);

    // ─── Row state ───────────────────────────────────────────────────────

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; Raise(nameof(IsChecked)); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); }
    }

    /// <summary>An operation is running (only one runs at a time) - every row's buttons wait for it.</summary>
    public bool IsBusy
    {
        get => _busy;
        set { if (_busy == value) return; _busy = value; Raise(ActionProperties); }
    }

    private bool Idle => !_busy && Pending is null;

    // Running (or starting): Stop and Restart. Stopped (or stopping, or unknown): Start.
    public bool ShowStart => Pending is null && State is SiteRunState.Stopped or SiteRunState.Stopping or SiteRunState.Unknown;
    public bool ShowStop => Pending is null && State is SiteRunState.Running or SiteRunState.Starting;
    public bool ShowProgress => Pending is not null;

    public bool CanStart => Idle && State is SiteRunState.Stopped or SiteRunState.Unknown;
    public bool CanStop => Idle && State is SiteRunState.Running;
    public bool CanRestart => Idle && State is SiteRunState.Running;
    public bool CanRemove => Idle;

    /// <summary>The expanded row's facts.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details =>
    [
        new("Site", Url),
        new("Physical path", Path),
        new("App pool", AppPoolText),
        new("Bindings", BindingsText),
        // Its IIS log folder is named after it (W3SVC<id>).
        new("Site ID", IdText),
        new("Database", _project.DatabaseName is null ? None : _project.DatabaseElsewhere ? $"{Database} (not on the local SQL container)" : _project.SqlReachable switch
        {
            null => Database,
            false => $"{Database} (the SQL Server doesn't answer)",
            true => _project.DatabaseExists ? $"{Database} (live)" : $"{Database} (doesn't exist)"
        }),
        new("DNN version", Dnn),
        new("Worker process", PidText == None ? "none - started on the first request" : $"PID {PidText}, since {Stats?.Started:g}"),
        new("Keep warm", KeepWarmText),
    ];

    /// <summary>
    /// Everything the search box looks in. A change of it is a reason to look again whether the row still matches -
    /// the table's view does that for this one row. The state is the settled one: a row doesn't leave a search for
    /// "running" the moment Stop is pressed, only once it has stopped.
    /// </summary>
    public string SearchKey => _searchKey ??=
        string.Join('\n', Name, Url, HostsText, Path, Dnn, Database, Sql, SettledStateText, IdText, PortsText, PidText);

    public bool Matches(string text) => SearchKey.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The project changed: takes it over and announces what shows <paramref name="changed"/>.</summary>
    public void Apply(ProjectState project, ProjectFacets changed)
    {
        var workerStarted = Stats?.Started;
        _project = project;

        if (changed.HasFlag(ProjectFacets.Metadata))
        {
            Raise(MetadataProperties);
            // Whether it has an address to keep warm.
            Raise(nameof(CanToggleKeepWarm));
            Raise(nameof(KeepWarmTip));
        }
        if (changed.HasFlag(ProjectFacets.Site))
        {
            Raise(SiteProperties);
            Raise(StateProperties);
            Raise(ActionProperties);
        }
        if (changed.HasFlag(ProjectFacets.Sql)) Raise(nameof(Sql));
        if (changed.HasFlag(ProjectFacets.Size))
        {
            Raise(nameof(Size));
            Raise(nameof(SizeBytes));
        }
        if (changed.HasFlag(ProjectFacets.Traffic))
        {
            Raise(nameof(NetworkText));
            Raise(nameof(NetworkSort));
        }
        if (changed.HasFlag(ProjectFacets.Stats))
        {
            Raise(StatsProperties);
            if (Stats?.Started != workerStarted) Raise(StartedProperties);
            UpdateLastStarted();
        }

        const ProjectFacets shown = ProjectFacets.Metadata | ProjectFacets.Site | ProjectFacets.Sql;
        if ((changed & shown) != 0)
        {
            _searchKey = null;
            Raise(nameof(SearchKey));
        }
        if ((changed & shown) != 0 || Stats?.Started != workerStarted) Raise(nameof(Details));
    }

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => Plural((int)span.TotalMinutes, "minute"),
        { TotalDays: < 1 } => Plural((int)span.TotalHours, "hour"),
        _ => Plural((int)span.TotalDays, "day")
    };

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Raise(string[] names)
    {
        if (PropertyChanged is not { } handler) return;
        foreach (var name in names) handler(this, new PropertyChangedEventArgs(name));
    }
}
