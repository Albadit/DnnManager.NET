using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Hosts;

/// <summary>
/// Keeps the host names of the IIS sites - <c>mysite.dnndev.me</c>, or a custom domain like <c>shop.test</c> - in the
/// Windows hosts file, so the sites open without internet, and a custom domain opens at all.
///
/// <para><b>Why.</b> dnndev.me is a public domain whose DNS answers 127.0.0.1 for every name under it: a browser finds
/// a local site only by asking DNS servers on the internet. Offline - or behind a DNS server that drops answers pointing
/// to 127.0.0.1 (DNS rebinding protection) - the name doesn't resolve, though IIS and DNN would answer. Windows reads
/// the hosts file before asking DNS, and so do browsers with a resolver of their own. DNN Manager's own requests (the
/// installer, keep warm, the upgrade checks) go to 127.0.0.1 with the host name and never needed DNS. A custom domain
/// (<c>shop.test</c>, <c>www.customer.com</c>) has no DNS pointing here at all - without a line in the hosts file it
/// doesn't open, online or not.</para>
///
/// <para><b>What it writes.</b> One line per host name a site is bound to, to the address the binding listens on
/// (<see cref="HostsFile.EntriesFor"/>), in a block of DNN Manager's own (<see cref="HostsFile"/>). A name the user maps
/// in the file themselves keeps their line. A real domain then opens the local site on this PC - while a site is bound
/// to it, not the live one.
/// The file is written only when the names change: after the monitor's first complete read of IIS, then when a site is
/// added, removed or gets other bindings - also one made outside DNN Manager.</para>
///
/// <para><b>What stays.</b> The lines are kept when DNN Manager closes: the sites open offline after a restart of the
/// PC too, with nothing of DNN Manager's running - no resolver, no service. A site removed while DNN Manager was closed
/// leaves a line pointing to this PC until the next start.</para>
/// </summary>
public sealed class HostsFileService(IServerStateFeed feed, ILogger<HostsFileService> log) : IDisposable
{
    // Antivirus or the DNS client may have the file open for a moment.
    private static readonly TimeSpan[] RetryAfter = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    private readonly IServerStateFeed _feed = feed;
    private readonly ILogger<HostsFileService> _log = log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    // The sites as the monitor last told them, by name.
    private readonly Dictionary<string, IisSiteRuntime> _sites = new(StringComparer.OrdinalIgnoreCase);
    private CoalescedJob? _job;
    // The monitor has read IIS completely: until then the sites known may be only some of them.
    private bool _complete;
    private int _started;

    // ── The job's own ──
    // What the file was last made to hold; null until then, or after a write failed.
    private IReadOnlyList<HostsEntry>? _written;
    private string? _lastProblem;

    /// <summary>The hosts file (tests give one of their own).</summary>
    internal string FilePath { get; init; } = HostsFile.DefaultPath;

    /// <summary>How long a burst of changes settles before the file is written (tests make it shorter).</summary>
    internal TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Something worth a line in the activity log: the message, and whether it is a warning. On a background thread.</summary>
    public event Action<string, bool>? Noticed;

    /// <summary>Starts following the monitor - before it starts, so the first snapshot of the sites is seen. Once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _job = new CoalescedJob(_ => { Sync(); return Task.CompletedTask; }, Settle,
            ex => _log.LogError(ex, "Keeping the hosts file failed"), _stop.Token);
        _feed.Changed += OnFeedChanged;
    }

    /// <summary>Writes the file now if the names differ from what it was last made to hold - for tests.</summary>
    internal Task SyncNowAsync() => _job?.RunNowAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _feed.Changed -= OnFeedChanged;
    }

    // On the monitor's thread, under its lock: noted, and the write handed to the job.
    private void OnFeedChanged(IReadOnlyList<MonitorEvent> events)
    {
        var relevant = false;
        lock (_lock)
        {
            foreach (var e in events)
            {
                switch (e)
                {
                    case ProjectAdded added:
                        _sites[added.Project.Name] = added.Project.Site;
                        relevant = true;
                        break;
                    case ProjectChanged { Changed: var facets } changed when (facets & ProjectFacets.Site) != 0:
                        _sites[changed.Project.Name] = changed.Project.Site;
                        relevant = true;
                        break;
                    case ProjectRemoved removed:
                        relevant |= _sites.Remove(removed.Name);
                        break;
                    case ConnectionChanged { Connection: MonitorConnection.Live }:
                        _complete = true;
                        relevant = true;
                        break;
                }
            }
            if (!relevant || !_complete) return;
        }
        _job?.Request();
    }

    // On the job's thread - one at a time.
    private void Sync()
    {
        IReadOnlyList<HostsEntry> entries;
        lock (_lock)
        {
            if (!_complete) return;
            entries = HostsFile.EntriesFor(_sites.Values);
        }
        if (_written is not null && entries.SequenceEqual(_written)) return;

        var previous = _written;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var changed = HostsFile.Write(FilePath, entries);
                _written = entries;
                _lastProblem = null;
                if (changed) Report(previous, entries);
                return;
            }
            catch (IOException) when (attempt < RetryAfter.Length && !_stop.IsCancellationRequested)
            {
                Thread.Sleep(RetryAfter[attempt]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Tried again with the next change of the sites; said once per problem, not at every change.
                _written = null;
                var problem = $"The hosts file ({FilePath}) can't be written: {ex.Message} Sites under dnndev.me only open while this PC has internet, and custom domains not at all.";
                if (problem == _lastProblem) return;
                _lastProblem = problem;
                _log.LogDebug(ex, "The hosts file {Path} can't be written", FilePath);
                Notice(problem, true);
                return;
            }
        }
    }

    private void Notice(string message, bool warning)
    {
        try { Noticed?.Invoke(message, warning); }
        catch (Exception ex) { _log.LogWarning(ex, "A hosts file notice handler failed"); }
    }

    private void Report(IReadOnlyList<HostsEntry>? before, IReadOnlyList<HostsEntry> now)
    {
        string Names(IEnumerable<HostsEntry> entries) => string.Join(", ", entries.Select(e => e.Address == HostsFile.Loopback ? e.Host : $"{e.Host} ({e.Address})"));
        string message;
        if (before is null)
        {
            // The first write of this start: the file held what the sites were before, or what was edited by hand.
            message = now.Count > 0 ? $"Hosts file: {Names(now)} - these sites open without internet." : "Hosts file: DNN Manager's lines removed - no site uses them.";
        }
        else
        {
            var parts = new List<string>();
            if (now.Except(before).ToList() is { Count: > 0 } added) parts.Add($"{Names(added)} added");
            if (before.Except(now).ToList() is { Count: > 0 } removed) parts.Add($"{Names(removed)} removed");
            message = $"Hosts file: {string.Join("; ", parts)}.";
        }
        Notice(message, false);
    }
}
