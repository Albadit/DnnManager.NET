using System.Collections.Concurrent;
using System.Threading.Channels;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.KeepWarm;

/// <summary>
/// Keeps the sites switched to "keep warm" warm while DNN Manager runs - minimized too: requests each one before IIS
/// would shut its worker process down for being idle, and warms it up again as soon as that process is gone (a
/// recycle, a crash, IIS or the site started again) - so its next page opens at once instead of after DNN starting up.
/// See <see cref="KeepWarmRules"/> for why a local site goes cold and the numbers this goes by.
///
/// <para><b>What it costs.</b> No browser and no process of its own: one loop in DNN Manager that sleeps until the next
/// request is due - no timer at all while no site is kept warm. A request is DNN's tiny <c>KeepAlive.aspx</c> (no
/// database work), sent no more often than the site's app pool needs (<see cref="KeepWarmRules.Interval"/>) - and not
/// while the site is in use anyway (IIS's request counter went up meanwhile). A full warm-up (the home page) only goes
/// to a site without a worker process, one at a time, or to one whose keep-alive answer showed DNN had to start.</para>
///
/// <para><b>When it holds back.</b> Nothing is sent to a site that is stopped, while IIS is stopped, while one of DNN
/// Manager's operations runs on the site (or on IIS itself), while a debugger is attached to the site's worker process
/// (its breakpoints would stop the request), or - for a warm-up - while the SQL Server container its database is on
/// doesn't answer. While another operation runs, no site is warmed up from cold - running sites are still kept warm. A
/// site that keeps failing is tried again after 1, 2 and 5 minutes, then left alone until it is started again, Check
/// now is pressed, the settings change or the PC wakes up; one whose worker process keeps ending right after it was
/// warmed up (a crashing site) too.</para>
///
/// <para><b>How it works.</b> One loop owns all state; everything else posts messages to it: the monitor's changes
/// (<see cref="IServerStateFeed"/>: a site's state, worker processes, bindings, IIS itself), the PC waking up,
/// operations starting and ending, the user switching a site on or off, the settings changing, and the requests'
/// answers. Each site has at most one request on its way; an answer that comes back after what it was for changed
/// (the site was stopped, switched off) is dropped without counting, and so is a failure an operation may have
/// caused.</para>
/// </summary>
public sealed class KeepWarmService(IServerStateFeed feed, IIisManager iis, IKeepWarmRecords store, IOptions<AppOptions> options,
    ILogger<KeepWarmService> log) : IDisposable
{
    private const long Never = long.MaxValue;

    // How long a site waits before a debugger, a failure an operation may have caused or the SQL Server container is
    // looked at again.
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SqlRecheckAfter = TimeSpan.FromMinutes(1);
    // A worker process turning up this soon after a successful request was started by that request.
    private static readonly TimeSpan OwnWorkerWithin = TimeSpan.FromMinutes(1);
    // Changes to a site this soon after an operation ended are that operation's doing.
    private static readonly TimeSpan OperationLinger = TimeSpan.FromSeconds(30);
    // The request counters are read once for all sites whose request is due at about the same time.
    private static readonly TimeSpan CountersFreshFor = TimeSpan.FromSeconds(5);
    // The loop never spins: when something is due "now" but nothing could be done, it looks again after this.
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(250);

    private readonly IServerStateFeed _feed = feed;
    private readonly IIisManager _iis = iis;
    private readonly IKeepWarmRecords _store = store;
    private readonly AppOptions _options = options.Value;
    private readonly ILogger<KeepWarmService> _log = log;
    private readonly KeepWarmRequester _requester = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    // Read by any thread, written by the loop (and by Start, before it runs).
    private readonly ConcurrentDictionary<string, KeepWarmRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, KeepWarmStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    // ── The loop's own ──
    private readonly Dictionary<string, Site> _sites = new(StringComparer.OrdinalIgnoreCase);
    private KeepWarmSettings _settings = new();
    private IisServerState? _iisState;
    // What the operation running now holds back; null while none runs.
    private KeepWarmPause? _pause;
    // Counts the operations: an answer to a request sent before the latest one began says little about now.
    private int _pauseEpoch;
    private long _startedAt;
    private long? _operationEndedAt;
    private int _inFlight, _coldInFlight;
    private int _started;
    // The records of sites that were gone from IIS when DNN Manager started have been dropped.
    private bool _recordsReconciled;

    private readonly object _countersLock = new();
    private IReadOnlyDictionary<string, long>? _counters;
    private long _countersAt;

    /// <summary>How long after <see cref="Start"/> sites without a worker process are first warmed up (tests make it shorter).</summary>
    internal TimeSpan StartUpDelay { get; init; } = KeepWarmRules.StartUpDelay;

    /// <summary>A site's status changed. Raised on the service's own thread - a handler hands it on and returns.</summary>
    public event Action<string, KeepWarmStatus>? StatusChanged;

    /// <summary>Something worth a line in the activity log. Raised on the service's own thread.</summary>
    public event Action<KeepWarmNotice>? Noticed;

    /// <summary>
    /// Loads which sites are kept warm and starts following the monitor - before it starts, so the first snapshot of the
    /// sites is seen. Once.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _settings = _options.KeepWarm;
        foreach (var record in _store.List()) _records[record.Site] = record;
        _startedAt = Now;
        _feed.Changed += OnFeedChanged;
        _feed.Resumed += OnFeedResumed;
        _options.Changed += OnOptionsChanged;
        _ = Task.Run(RunAsync);
    }

    /// <summary>How <paramref name="site"/>'s keep warm stands now.</summary>
    public KeepWarmStatus StatusOf(string site) =>
        _statuses.TryGetValue(site, out var status) ? status
        : _records.TryGetValue(site, out var record) && record.Enabled ? new KeepWarmStatus(KeepWarmState.Waiting, "Waiting")
        : KeepWarmStatus.Off;

    /// <summary>Whether <paramref name="site"/> is kept warm; null when it isn't.</summary>
    public KeepWarmRecord? RecordOf(string site) => _records.GetValueOrDefault(site);

    /// <summary>Switches keep warm on or off for <paramref name="site"/> (and remembers it). Switched on, it is checked at once.</summary>
    public void SetEnabled(string site, bool enabled) => Post(new Enable(site, enabled));

    /// <summary>Requests <paramref name="site"/> now - also after it failed too often - and shows how it answers.</summary>
    public void CheckNow(string site) => Post(new CheckNowMessage(site));

    /// <summary>
    /// One of DNN Manager's operations runs: what it may be changing is held back until <see cref="Resume"/> - see
    /// <see cref="KeepWarmPause"/>. Another call replaces what is held back.
    /// </summary>
    public void Pause(KeepWarmPause pause) => Post(new PauseMessage(pause));

    /// <summary>The operation has ended and what it changed has been read: keep warm goes on as before.</summary>
    public void Resume() => Post(new PauseMessage(null));

    /// <summary>
    /// What an operation was about - <paramref name="sites"/>, or every site when null - is checked again at once (a site
    /// it restarted is warmed up), with a new chance: it may have fixed them.
    /// </summary>
    public void Recheck(IReadOnlyCollection<string>? sites) => Post(new RecheckMessage(sites));

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _inbox.Writer.TryComplete();
        _feed.Changed -= OnFeedChanged;
        _feed.Resumed -= OnFeedResumed;
        _options.Changed -= OnOptionsChanged;
        _requester.Dispose();
    }

    // ─── Messages ─────────────────────────────────────────────────────────

    private abstract record Message;
    private sealed record Batch(IReadOnlyList<MonitorEvent> Events) : Message;
    private sealed record ResumedMessage : Message;
    private sealed record PauseMessage(KeepWarmPause? Pause) : Message;
    private sealed record RecheckMessage(IReadOnlyCollection<string>? Sites) : Message;
    private sealed record Enable(string Site, bool Enabled) : Message;
    private sealed record OptionsMessage : Message;
    private sealed record CheckNowMessage(string Site) : Message;
    private sealed record Completed(Request Request, KeepWarmOutcome Outcome) : Message;

    /// <summary>A request on its way, with what it was sent for.</summary>
    private sealed record Request(
        string Site, int Generation, int Epoch, KeepWarmRequestKind Kind, bool Cold, LocalSiteTarget Target, string Path,
        (string Host, int Port)? Sql, bool CheckInUse, long? Baseline, int Own);

    private void Post(Message message) => _inbox.Writer.TryWrite(message);

    // On the monitor's thread, under its lock: only what keep warm looks at is handed over - not the figures that
    // change every two seconds.
    private void OnFeedChanged(IReadOnlyList<MonitorEvent> events)
    {
        List<MonitorEvent>? relevant = null;
        foreach (var e in events)
            if (e is ProjectAdded or ProjectRemoved or RuntimeChanged or ConnectionChanged { Connection: MonitorConnection.Live } ||
                e is ProjectChanged { Changed: var facets } && (facets & (ProjectFacets.Site | ProjectFacets.Metadata)) != 0)
                (relevant ??= []).Add(e);
        if (relevant is not null) Post(new Batch(relevant));
    }

    private void OnFeedResumed() => Post(new ResumedMessage());

    private void OnOptionsChanged() => Post(new OptionsMessage());

    // ─── The loop ─────────────────────────────────────────────────────────

    private static long Now => Environment.TickCount64;

    private static long Ms(TimeSpan span) => (long)span.TotalMilliseconds;

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (_inbox.Reader.TryRead(out var message)) Handle(message);
                var wake = Pump();
                // Nothing due: wait for the next message, however long that takes - no timer ticks for nothing.
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (wake != Never) waiting.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(wake - Now, Ms(MinWait))));
                try
                {
                    Handle(await _inbox.Reader.ReadAsync(waiting.Token));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Something is due.
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Keep warm stopped");
        }
    }

    private void Handle(Message message)
    {
        try
        {
            switch (message)
            {
                case Batch batch:
                    foreach (var e in batch.Events) OnMonitorEvent(e);
                    break;
                case ResumedMessage:
                    // After sleep the workers may be about to idle out, and everything may have changed: check them all.
                    foreach (var site in EnabledSites()) Recheck(site, rearm: true);
                    break;
                case PauseMessage pause:
                    OnPause(pause.Pause);
                    break;
                case RecheckMessage recheck:
                    foreach (var site in EnabledSites())
                        if (recheck.Sites?.Contains(site.Name, StringComparer.OrdinalIgnoreCase) != false) Recheck(site, rearm: true);
                    break;
                case Enable enable:
                    OnEnable(enable.Site, enable.Enabled);
                    break;
                case OptionsMessage:
                    OnSettingsChanged();
                    break;
                case CheckNowMessage check:
                    if (_sites.TryGetValue(check.Site, out var checkedSite) && IsEnabled(checkedSite))
                    {
                        Recheck(checkedSite, rearm: true);
                        checkedSite.UserAsked = true;
                    }
                    break;
                case Completed completed:
                    OnCompleted(completed.Request, completed.Outcome);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Keep warm couldn't handle {Message}", message.GetType().Name);
        }
    }

    // ─── What a site is ───────────────────────────────────────────────────

    private sealed class Site(string name)
    {
        public string Name { get; } = name;
        /// <summary>The site as the monitor last reported it; null until it has.</summary>
        public ProjectState? Project;
        /// <summary>Changed whenever an answer on its way can no longer tell anything (stopped, switched off).</summary>
        public int Generation;
        public KeepWarmRequestKind? InFlight;

        /// <summary>A request as soon as it may be sent: switched on, started, resumed, Check now…</summary>
        public bool ProbeWanted;
        /// <summary>The warm-up page next: the keep-alive page answered slowly (DNN had to start), or doesn't exist.</summary>
        public bool WarmUpWanted;
        /// <summary>The user asked (switched it on, Check now): no waiting for the start-up delay or a failure's backoff.</summary>
        public bool UserAsked;

        public int Failures;
        public bool Stopped;
        public string? FailureReason;
        /// <summary>Nothing is sent before this (a failure's backoff, a debugger or the database looked at again).</summary>
        public long NotBefore;
        public KeepWarmStatus? WaitStatus;
        /// <summary>
        /// Its worker process ended soon after DNN started, this many times in a row - a crashing site. Not reset by an
        /// answer (each warm-up answers before the next crash), only by a worker process that lasts, or a new chance.
        /// </summary>
        public int EarlyDeaths;
        /// <summary>It failed while an operation ran - maybe the operation's doing: tried again once that has ended.</summary>
        public bool RetryAfterOperation;

        /// <summary>The worker processes the last answer came from.</summary>
        public IReadOnlyList<int> WarmPids = [];
        /// <summary>
        /// A warm-up of a site without a worker process started one, which the monitor hasn't reported yet: it is taken
        /// to be there until then - not warmed up again and again meanwhile.
        /// </summary>
        public long? AssumeWorkerUntil;
        /// <summary>When the last request was sent, or the site was last found in use - the interval counts from then.</summary>
        public long? LastRequestAt;
        public long? LastOkAt;
        /// <summary>When DNN last started for one of its requests (a cold warm-up, or a slow answer).</summary>
        public long? LastWarmUpOkAt;
        public TimeSpan LastElapsed;
        public DateTime? LastOkLocal;
        public string? LastNote;
        public bool InUse;
        /// <summary>The site's request counter at the last look, and how many requests were sent since.</summary>
        public long? Baseline;
        public int Own;
        /// <summary>The keep-alive page answered 404 this many times in a row - twice, and it is taken to be missing.</summary>
        public int PingMisses;
        public bool PingPathMissing;
        /// <summary>Has answered since it was switched on - the first answer is worth a line in the log.</summary>
        public bool WasWarm;
        public KeepWarmStatus Status = KeepWarmStatus.Off;
    }

    private Site GetOrAdd(string name)
    {
        if (!_sites.TryGetValue(name, out var site)) _sites[name] = site = new Site(name);
        return site;
    }

    private bool IsEnabled(Site site) => _records.TryGetValue(site.Name, out var record) && record.Enabled;

    private IEnumerable<Site> EnabledSites() => _sites.Values.Where(IsEnabled).ToList();

    // Started, and its app pool not stopped: it can answer.
    private static bool Running(IisSiteRuntime site) =>
        site.State == "Started" && site.AppPoolState is not ("Stopped" or "Stopping");

    private static void Rearm(Site site)
    {
        site.Failures = 0;
        site.Stopped = false;
        site.FailureReason = null;
        site.NotBefore = 0;
        site.WaitStatus = null;
    }

    /// <summary>
    /// Checks <paramref name="site"/> as soon as it may be - when <paramref name="rearm"/>, with a new chance: its failures,
    /// a missing keep-alive page and quick ends of its worker process are forgotten.
    /// </summary>
    private static void Recheck(Site site, bool rearm)
    {
        if (rearm)
        {
            Rearm(site);
            site.PingMisses = 0;
            site.PingPathMissing = false;
            site.EarlyDeaths = 0;
        }
        site.ProbeWanted = true;
    }

    /// <summary>The running operation holds <paramref name="site"/> back entirely: it is about the site, or about IIS itself.</summary>
    private bool HeldBack(Site site) =>
        _pause is { } pause && (pause.Everything || pause.Sites?.Contains(site.Name, StringComparer.OrdinalIgnoreCase) == true);

    // An operation ended this recently: what changes now may be its doing.
    private bool RecentlyOperated(long now) => _operationEndedAt is { } ended && now - ended < Ms(OperationLinger);

    // ─── What changed ─────────────────────────────────────────────────────

    private void OnMonitorEvent(MonitorEvent e)
    {
        switch (e)
        {
            case ProjectAdded added:
                // Read again: the record may have gone with a site removed meanwhile (RemoveProjectUseCase).
                if (_store.Find(added.Project.Name) is { } record) _records[record.Site] = record;
                else _records.TryRemove(added.Project.Name, out _);
                OnProject(added.Project);
                break;
            case ProjectChanged changed:
                OnProject(changed.Project);
                break;
            case ProjectRemoved removed:
                if (_sites.Remove(removed.Name, out var gone)) gone.Generation++;
                // Gone from IIS (removed or renamed there): a new site of the same name starts cold, so its record goes
                // too - not when IIS itself isn't there (then every site reads as gone).
                if (_records.TryRemove(removed.Name, out _) && _iisState is not IisServerState.NotInstalled)
                    _store.Remove(removed.Name);
                _statuses.TryRemove(removed.Name, out _);
                _requester.ForgetCookies(removed.Name);
                break;
            case ConnectionChanged when !_recordsReconciled && _iisState is not (null or IisServerState.NotInstalled):
                // The first complete read of IIS: a record of a site that isn't there (removed or renamed in IIS while DNN
                // Manager was closed) goes - a new site of that name starts cold.
                _recordsReconciled = true;
                foreach (var stale in _store.List().Where(r => !_sites.ContainsKey(r.Site)))
                {
                    _records.TryRemove(stale.Site, out _);
                    _statuses.TryRemove(stale.Site, out _);
                    _store.Remove(stale.Site);
                }
                break;
            case RuntimeChanged runtime:
                var was = _iisState;
                _iisState = runtime.State;
                if (runtime.State != IisServerState.Running)
                {
                    foreach (var site in _sites.Values) site.Generation++;
                }
                else if (was is not null && was != IisServerState.Running)
                {
                    // IIS started again: its sites are cold.
                    foreach (var site in EnabledSites()) Recheck(site, rearm: true);
                }
                break;
        }
    }

    private void OnProject(ProjectState project)
    {
        var site = GetOrAdd(project.Name);
        var before = site.Project;
        site.Project = project;
        var now = Now;

        // Seen for the first time (DNN Manager started, the site added): find out how it is.
        if (before is null)
        {
            site.ProbeWanted = true;
            return;
        }

        var wasRunning = Running(before.Site);
        var running = Running(project.Site);
        if (wasRunning && !running)
        {
            // Stopped: what is on its way says nothing anymore; nothing is sent until it runs again.
            site.Generation++;
            site.ProbeWanted = site.WarmUpWanted = false;
        }
        else if (!wasRunning && running)
        {
            // Started (by anyone): a new chance, and a warm-up.
            Recheck(site, rearm: true);
        }

        var oldPids = before.Site.WorkerProcessIds;
        var newPids = project.Site.WorkerProcessIds;
        if (oldPids.SequenceEqual(newPids)) return;
        // What the monitor reports now is what there is.
        site.AssumeWorkerUntil = null;
        if (newPids.Count == 0)
        {
            // Its sessions went with it.
            _requester.ForgetCookies(site.Name);
            if (oldPids.Count > 0 && DiedSoon(site, now))
            {
                // Once can be a recycle; again is a site that keeps crashing - given up on like any failing site, its
                // backoff growing from one quick end to the next (an answer in between doesn't reset it).
                site.EarlyDeaths++;
                if (site.EarlyDeaths >= 2)
                {
                    site.Failures = Math.Max(site.Failures, site.EarlyDeaths - 2);
                    Fail(site, "its worker process keeps ending soon after it was warmed up - it may be crashing", now);
                }
            }
            // A warm-up follows: it has no worker process now.
            return;
        }
        if (site.InFlight is not null || newPids.SequenceEqual(site.WarmPids)) return;
        // The worker process our own warm-up started - the monitor only now saw it.
        if (site.WarmPids.Count == 0 && site.LastOkAt is { } ok && now - ok < Ms(OwnWorkerWithin))
        {
            site.WarmPids = newPids;
            return;
        }
        // A new worker process (a recycle, someone's request): it may not have started DNN yet.
        site.ProbeWanted = true;
    }

    /// <summary>
    /// Its worker process ended within minutes of DNN starting for one of keep warm's requests, while it should be
    /// running and nothing of DNN Manager's did it: the site may be crashing - not something to warm up again and again.
    /// </summary>
    private bool DiedSoon(Site site, long now) =>
        IsEnabled(site) && site.LastWarmUpOkAt is { } at && now - at < Ms(KeepWarmRules.DiedSoonAfter) &&
        _pause is null && !RecentlyOperated(now) &&
        _iisState == IisServerState.Running && site.Project is { } p && Running(p.Site);

    private void OnPause(KeepWarmPause? pause)
    {
        _pause = pause;
        if (pause is not null)
        {
            _pauseEpoch++;
            return;
        }
        // What the operation was about is rechecked when the store says so (Recheck) - not after an operation that never
        // began. What failed meanwhile is tried again now.
        _operationEndedAt = Now;
        foreach (var site in _sites.Values) site.RetryAfterOperation = false;
    }

    private void OnEnable(string name, bool enabled)
    {
        var record = new KeepWarmRecord(name, enabled);
        Save(record);
        var site = GetOrAdd(name);
        // Whatever is on its way was sent before.
        site.Generation++;
        site.WarmUpWanted = false;
        site.WasWarm = false;
        Recheck(site, rearm: true);
        if (enabled)
        {
            site.UserAsked = true;
        }
        else
        {
            site.ProbeWanted = site.UserAsked = false;
        }
    }

    private void OnSettingsChanged()
    {
        var now = _options.KeepWarm;
        if (now.PingMinutes == _settings.PingMinutes && now.WarmUpPath == _settings.WarmUpPath && now.PingPath == _settings.PingPath)
            return;
        _settings = now;
        // Other pages, another interval: checked with them right away.
        foreach (var site in EnabledSites()) Recheck(site, rearm: true);
    }

    private void Save(KeepWarmRecord record)
    {
        _store.Save(record);
        if (record.IsEmpty) _records.TryRemove(record.Site, out _);
        else _records[record.Site] = record;
    }

    // ─── Sending ──────────────────────────────────────────────────────────

    /// <summary>Sends what is due, sets every site's status, and says when something is due next.</summary>
    private long Pump()
    {
        var wake = Never;
        // Asked for by the user first; then by name, so the same site doesn't always wait for the others.
        foreach (var site in _sites.Values.OrderByDescending(s => s.UserAsked).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            try
            {
                var at = Evaluate(site, Now);
                if (at < wake) wake = at;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Keep warm couldn't look at {Site}", site.Name);
            }
        }
        return wake;
    }

    /// <summary>What <paramref name="site"/> needs now: sends its request when one is due and may go. Returns when to look again.</summary>
    private long Evaluate(Site site, long now)
    {
        if (!IsEnabled(site))
        {
            SetStatus(site, KeepWarmStatus.Off);
            return Never;
        }
        if (site.Project is not { } project)
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Waiting, "Waiting - IIS's sites are being read"));
            return Never;
        }
        // Its status says what is on its way.
        if (site.InFlight is not null) return Never;
        if (Blocker(project) is { } blocked)
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Paused, $"Paused - {blocked}"));
            return Never;
        }
        if (HeldBack(site))
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Paused,
                _pause!.Everything ? "Paused - IIS is being started, stopped or restarted" : "Paused - an operation on the site is running"));
            return Never;
        }
        if (site.RetryAfterOperation && _pause is not null)
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Waiting, "Waiting - didn't answer while an operation ran; tried again once it has ended"));
            return Never;
        }
        if (site.Stopped && !site.UserAsked)
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Stopped,
                $"Stopped - {site.FailureReason} ({site.Failures} times in a row). Check now, or start the site again, to try again."));
            return Never;
        }
        if (now < site.NotBefore && !site.UserAsked)
        {
            if (site.WaitStatus is { } waiting) SetStatus(site, waiting);
            return site.NotBefore;
        }

        var plan = KeepWarmPlan.For(_settings, project.Site);
        if (plan.Target is not { } target)
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Paused, "Paused - the site has no http or https binding to request"));
            return Never;
        }

        // The worker process a warm-up just started counts as there until the monitor says otherwise - then, if it still
        // reports none, it is warmed up again.
        var assumed = project.Site.WorkerProcessIds.Count == 0 && site.AssumeWorkerUntil is { } until && now < until;
        var noWorker = project.Site.WorkerProcessIds.Count == 0 && !assumed;
        var periodic = false;
        KeepWarmRequestKind kind;
        if (noWorker || site.WarmUpWanted) kind = KeepWarmRequestKind.WarmUp;
        else if (site.ProbeWanted || site.UserAsked) kind = KeepWarmRequestKind.Ping;
        else
        {
            var due = (site.LastRequestAt ?? now) + Ms(plan.Interval) + Ms(KeepWarmRules.Jitter(site.Name, plan.Interval));
            if (now < due)
            {
                SetStatus(site, WarmStatus(site));
                return assumed ? Math.Min(due, site.AssumeWorkerUntil!.Value) : due;
            }
            kind = KeepWarmRequestKind.Ping;
            periodic = true;
        }

        // A cold start costs seconds of CPU: not while an operation runs, one at a time, and not right after DNN Manager
        // (maybe Windows) started.
        var cold = kind == KeepWarmRequestKind.WarmUp && noWorker;
        if (cold && _pause is not null)
        {
            SetStatus(site, new KeepWarmStatus(KeepWarmState.Waiting, "Waiting - warms up once the running operation has ended"));
            return Never;
        }
        if (cold && !site.UserAsked)
        {
            var allowedAt = _startedAt + Ms(StartUpDelay);
            if (now < allowedAt)
            {
                SetStatus(site, new KeepWarmStatus(KeepWarmState.Waiting, "Waiting - warms up a minute after DNN Manager started"));
                return allowedAt;
            }
            if (_coldInFlight > 0)
            {
                SetStatus(site, new KeepWarmStatus(KeepWarmState.Waiting, "Waiting - another site is warming up"));
                return Never;
            }
        }
        // The answers wake the loop up again.
        if (_inFlight >= KeepWarmRules.MaxInFlight) return Never;

        // Someone is debugging the site: a request would stop at their breakpoints.
        if (!noWorker && KeepWarmRequester.DebuggerAttached(project.Site.WorkerProcessIds))
        {
            site.NotBefore = now + Ms(RecheckAfter);
            site.WaitStatus = new KeepWarmStatus(KeepWarmState.Paused, "Paused - a debugger is attached to the site's worker process");
            SetStatus(site, site.WaitStatus);
            return site.NotBefore;
        }

        Send(site, project, kind, cold, periodic, plan, target, now);
        return Never;
    }

    /// <summary>Why nothing may be sent to <paramref name="project"/>'s site now; null when something may.</summary>
    private string? Blocker(ProjectState project)
    {
        switch (_iisState)
        {
            case IisServerState.NotInstalled: return "IIS isn't installed";
            case null or IisServerState.Running: break;
            default: return "IIS is stopped";
        }
        var site = project.Site;
        if (site.AppPoolState is "Stopped" or "Stopping") return "the site's app pool is stopped";
        return site.State == "Started" ? null : "the site is stopped";
    }

    /// <summary>
    /// Where a TCP connection reaches <paramref name="server"/> (as a connection string writes it): "localhost,1433",
    /// "10.0.0.5" (1433), "tcp:sql,1444". Null for what isn't reached that way - a named instance (.\SQLEXPRESS),
    /// LocalDB, a pipe.
    /// </summary>
    internal static (string Host, int Port)? SqlEndpoint(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;
        var s = server.Trim();
        if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.Contains('\\') || s.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = s.IndexOf(',');
        var host = (comma < 0 ? s : s[..comma]).Trim();
        var port = 1433;
        if (comma >= 0 && !int.TryParse(s[(comma + 1)..].Trim(), out port)) return null;
        if (host is "." or "(local)") host = "127.0.0.1";
        return host.Length == 0 ? null : (host, port);
    }

    private void Send(Site site, ProjectState project, KeepWarmRequestKind kind, bool cold, bool periodic, KeepWarmPlan plan,
        LocalSiteTarget target, long now)
    {
        // Without a keep-alive page, its warm-up page keeps it warm - asked for as a warm-up, so a 404 there isn't one again.
        if (kind == KeepWarmRequestKind.Ping && site.PingPathMissing) kind = KeepWarmRequestKind.WarmUp;
        var path = kind == KeepWarmRequestKind.WarmUp ? plan.WarmUpPath : plan.PingPath;
        // DNN can't start without its database: a warm-up waits for the SQL Server the site's web.config connects to
        // (when it is reached over TCP - host,port or a host on 1433).
        (string, int)? sql = cold && project.DatabaseName is not null && !project.DatabaseIsFile ? SqlEndpoint(project.DatabaseServer) : null;
        // Skipped while in use only when the next request still comes within the idle time-out.
        var checkInUse = periodic && !site.UserAsked && KeepWarmRules.MaySkipWhenInUse(site.Name, plan.Interval, plan.IdleTimeout);
        var request = new Request(site.Name, site.Generation, _pauseEpoch, kind, cold, target, path, sql, checkInUse, site.Baseline, site.Own);

        site.InFlight = kind;
        site.ProbeWanted = site.WarmUpWanted = site.UserAsked = false;
        site.LastRequestAt = now;
        _inFlight++;
        if (cold) _coldInFlight++;
        // A light request to a warm site says nothing new until it has answered; anything else is shown.
        if (kind == KeepWarmRequestKind.WarmUp)
            SetStatus(site, new KeepWarmStatus(KeepWarmState.WarmingUp, cold ? "Warming up - DNN is starting…" : "Warming up…"));
        else if (site.Status.State is not KeepWarmState.Warm)
            SetStatus(site, new KeepWarmStatus(KeepWarmState.WarmingUp, "Checking whether the site answers…"));

        _ = Task.Run(() => RequestAsync(request));
    }

    private async Task RequestAsync(Request request)
    {
        KeepWarmOutcome outcome;
        try
        {
            outcome = await SendOrSkipAsync(request);
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // DNN Manager is closing.
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A keep-warm request to {Site} failed", request.Site);
            outcome = new KeepWarmOutcome(KeepWarmOutcomeKind.NotAnswering, Reason: $"the request failed: {ex.Message}");
        }
        Post(new Completed(request, outcome));
    }

    private async Task<KeepWarmOutcome> SendOrSkipAsync(Request request)
    {
        if (request.Sql is { } sql && !await KeepWarmRequester.SqlAnswersAsync(sql.Host, sql.Port, _stop.Token))
            return new KeepWarmOutcome(KeepWarmOutcomeKind.SqlDown,
                Reason: "the SQL Server container doesn't answer - DNN can't start without its database");

        long? served = null;
        if (request.CheckInUse && ReadCounters() is { } counters && counters.TryGetValue(request.Site, out var count))
        {
            served = count;
            // More requests than ours since the last look: it is in use, and IIS counts that as not idle.
            if (request.Baseline is { } baseline && count >= baseline && count - baseline > request.Own)
                return new KeepWarmOutcome(KeepWarmOutcomeKind.InUse, RequestsServed: count);
        }
        var outcome = await _requester.SendAsync(request.Site, request.Target, request.Path, request.Kind, _stop.Token);
        return served is null ? outcome : outcome with { RequestsServed = served };
    }

    /// <summary>Every site's request counter, read once for the sites whose request is due at about the same time.</summary>
    private IReadOnlyDictionary<string, long>? ReadCounters()
    {
        lock (_countersLock)
            if (_counters is not null && Now - _countersAt < Ms(CountersFreshFor)) return _counters.Count > 0 ? _counters : null;
        var counters = _iis.GetRequestsServed();
        lock (_countersLock)
        {
            _counters = counters;
            _countersAt = Now;
        }
        return counters.Count > 0 ? counters : null;
    }

    // ─── Answers ──────────────────────────────────────────────────────────

    private void OnCompleted(Request request, KeepWarmOutcome outcome)
    {
        _inFlight--;
        if (request.Cold) _coldInFlight--;
        if (!_sites.TryGetValue(request.Site, out var site)) return;
        site.InFlight = null;

        // Sent before something changed that it can't tell about (the site stopped, switched off, IIS stopped), or about
        // a site an operation now holds back: tried again once things have settled, not counted.
        var project = site.Project;
        if (request.Generation != site.Generation || !IsEnabled(site) || project is null || !Running(project.Site) ||
            _iisState is not (null or IisServerState.Running) || HeldBack(site))
        {
            if (IsEnabled(site)) site.ProbeWanted = true;
            return;
        }

        var now = Now;
        if (outcome.RequestsServed is { } served)
        {
            site.Baseline = served;
            site.Own = outcome.Requests;
        }
        else if (site.Baseline is not null)
        {
            site.Own += outcome.Requests;
        }

        var failed = outcome.Kind is KeepWarmOutcomeKind.Failed or KeepWarmOutcomeKind.NotAnswering;
        // A failure while an operation runs may be the operation's doing (it restarts a site, the SQL container…): not
        // counted, and tried again once the operation has ended - not again and again meanwhile.
        if (failed && _pause is not null)
        {
            site.RetryAfterOperation = true;
            site.ProbeWanted = true;
            return;
        }
        // One that began before an operation, or came right after one: not counted either - looked at again in a moment.
        if (failed && (request.Epoch != _pauseEpoch || RecentlyOperated(now)))
        {
            site.NotBefore = now + Ms(RecheckAfter);
            site.WaitStatus = new KeepWarmStatus(KeepWarmState.Waiting, "Waiting - didn't answer right after an operation; tried again in a moment");
            site.ProbeWanted = true;
            return;
        }
        if (outcome.TimedOut && !request.Cold)
        {
            // No answer from a running worker process a debugger is attached to: it is probably stopped at a breakpoint -
            // not the site's fault, and asking again soon would only pile requests up behind the breakpoint.
            if (KeepWarmRequester.DebuggerAttached(project.Site.WorkerProcessIds))
            {
                site.NotBefore = now + Ms(KeepWarmPlan.For(_settings, project.Site).Interval);
                site.WaitStatus = new KeepWarmStatus(KeepWarmState.Paused,
                    "Paused - a debugger is attached and the site didn't answer; it may be stopped at a breakpoint");
                site.ProbeWanted = true;
                return;
            }
            // DNN may be starting slowly (after a rebuild): the warm-up page next, with a warm-up's longer time limit -
            // only that one not answering counts.
            if (request.Kind == KeepWarmRequestKind.Ping)
            {
                site.WarmUpWanted = true;
                return;
            }
        }

        switch (outcome.Kind)
        {
            case KeepWarmOutcomeKind.InUse:
                site.InUse = true;
                site.WarmPids = project.Site.WorkerProcessIds;
                break;

            case KeepWarmOutcomeKind.SqlDown:
                // Not the site's fault: looked at again in a minute, without counting.
                site.NotBefore = now + Ms(SqlRecheckAfter);
                site.WaitStatus = new KeepWarmStatus(KeepWarmState.Paused, $"Paused - {outcome.Reason}");
                site.WarmUpWanted = request.Kind == KeepWarmRequestKind.WarmUp;
                break;

            case KeepWarmOutcomeKind.PingPathMissing:
                // The warm-up page now. A single 404 can come from something else answering for a moment (the site
                // stopped while IIS's Default Web Site answers): only a second one in a row means the page isn't there.
                site.PingMisses++;
                site.WarmUpWanted = true;
                if (site.PingMisses >= 2 && !site.PingPathMissing)
                {
                    site.PingPathMissing = true;
                    Notice(site, $"'{site.Name}' has no {request.Path} - keep warm requests its warm-up page instead.", false);
                }
                break;

            case KeepWarmOutcomeKind.Ok:
                // A worker process that outlived the time a crashing one lasts: what ended earlier was no crash loop.
                if (site.EarlyDeaths > 0 && site.LastWarmUpOkAt is { } warmedUp && now - warmedUp >= Ms(KeepWarmRules.DiedSoonAfter) &&
                    project.Site.WorkerProcessIds.Count > 0 && project.Site.WorkerProcessIds.SequenceEqual(site.WarmPids))
                    site.EarlyDeaths = 0;
                var recovered = (site.Failures > 0 || site.Stopped) && site.EarlyDeaths == 0;
                Rearm(site);
                site.InUse = false;
                site.LastOkAt = now;
                site.LastOkLocal = DateTime.Now;
                site.LastElapsed = outcome.Elapsed;
                site.LastNote = outcome.Note;
                site.WarmPids = project.Site.WorkerProcessIds;
                if (request.Cold && project.Site.WorkerProcessIds.Count == 0)
                {
                    // It started a worker process the monitor doesn't know yet: read the sites now, rather than at its
                    // next look (half a minute away while the window is minimized) - and don't warm it up again meanwhile.
                    site.AssumeWorkerUntil = now + Ms(OwnWorkerWithin);
                    _ = Task.Run(SyncSitesAsync);
                }
                if (request.Kind == KeepWarmRequestKind.Ping) site.PingMisses = 0;
                // DNN started for this answer - the time from which a crash would show.
                if (request.Cold || outcome.Slow) site.LastWarmUpOkAt = now;
                // DNN had to start for a keep-alive request: its warm-up page gets compiled too.
                if (request.Kind == KeepWarmRequestKind.Ping && outcome.Slow) site.WarmUpWanted = true;
                if (recovered) Notice(site, $"Keep warm: '{site.Name}' answers again.", false);
                else if (!site.WasWarm)
                    Notice(site, $"'{site.Name}' is warm - it answered in {KeepWarmRules.Duration(outcome.Elapsed)}.", false);
                site.WasWarm = true;
                break;

            default:
                Fail(site, outcome.Reason ?? "the site didn't answer", now);
                break;
        }
    }

    private async Task SyncSitesAsync()
    {
        try { await _feed.SyncSitesAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "Reading the sites after a warm-up failed"); }
    }

    private void Fail(Site site, string reason, long now)
    {
        site.Failures++;
        site.FailureReason = reason;
        site.WarmUpWanted = false;
        site.ProbeWanted = true;
        site.InUse = false;
        if (site.Failures >= KeepWarmRules.FailuresBeforeStop)
        {
            site.Stopped = true;
            Notice(site, $"Keep warm stopped trying '{site.Name}' after {site.Failures} failures in a row: {reason}. " +
                         "Check now on its overview (IIS), or starting the site, tries again.", true);
            return;
        }
        var wait = KeepWarmRules.Backoff(site.Failures);
        site.NotBefore = now + Ms(wait);
        site.WaitStatus = new KeepWarmStatus(KeepWarmState.Failing, $"Failing - {reason}. Tried again at {DateTime.Now + wait:HH:mm}.");
        if (site.Failures == 1) Notice(site, $"Keep warm: '{site.Name}' - {reason}. Tried again in {KeepWarmRules.Span(wait)}.", true);
    }

    private static KeepWarmStatus WarmStatus(Site site)
    {
        if (site.InUse) return new KeepWarmStatus(KeepWarmState.Warm, "Warm - the site is in use");
        if (site.LastOkLocal is not { } at) return new KeepWarmStatus(KeepWarmState.Warm, "Warm");
        var text = $"Warm - answered in {KeepWarmRules.Duration(site.LastElapsed)} at {at:HH:mm}";
        return new KeepWarmStatus(KeepWarmState.Warm, site.LastNote is { } note ? $"{text} - {note}" : text, site.LastElapsed);
    }

    private void SetStatus(Site site, KeepWarmStatus status)
    {
        if (site.Status == status) return;
        site.Status = status;
        _statuses[site.Name] = status;
        try { StatusChanged?.Invoke(site.Name, status); }
        catch (Exception ex) { _log.LogWarning(ex, "A keep-warm status handler failed"); }
    }

    private void Notice(Site site, string message, bool warning)
    {
        try { Noticed?.Invoke(new KeepWarmNotice(site.Name, message, warning)); }
        catch (Exception ex) { _log.LogWarning(ex, "A keep-warm notice handler failed"); }
    }
}
