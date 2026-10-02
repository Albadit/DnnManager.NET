using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Monitoring;

/// <summary>
/// What the monitor knows about one project - an IIS site, and what is in the folder it serves. Immutable - a change
/// makes a new one, sent in a <see cref="ProjectChanged"/> with the facets that differ.
/// </summary>
public sealed record ProjectState
{
    /// <summary>The IIS site's name.</summary>
    public required string Name { get; init; }
    /// <summary>The folder the site serves (its physical path in IIS).</summary>
    public required string Directory { get; init; }
    /// <summary>Where a browser opens it, from its bindings; empty when it has no web binding.</summary>
    public required string SiteUrl { get; init; }
    /// <summary>The folder is one of the projects folder's - a DNN Manager project, whose folder it may delete.</summary>
    public bool InProjectsFolder { get; init; }
    /// <summary>From <c>bin\DotNetNuke.dll</c>; null when the folder holds no DNN.</summary>
    public string? DnnVersion { get; init; }
    /// <summary>
    /// The database the site uses, from its web.config's SiteSqlServer connection - nothing else: not what DNN
    /// Manager's settings would name it. Null when web.config names none (<see cref="DatabaseProblem"/> says why).
    /// </summary>
    public string? DatabaseName { get; init; }
    /// <summary>
    /// Why the database of a DNN site (or a project folder) isn't known: no web.config, one that can't be read, no
    /// SiteSqlServer connection, or DNN's own until it is installed. Null when it is known - or the site has none.
    /// </summary>
    public string? DatabaseProblem { get; init; }
    /// <summary>The folder's size; null until it has been measured.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>The IIS site as IIS has it now.</summary>
    public required IisSiteRuntime Site { get; init; }
    /// <summary>
    /// The site's database isn't on DNN Manager's own SQL container - another SQL Server, Windows authentication or a
    /// LocalDB file.
    /// </summary>
    public bool DatabaseElsewhere { get; init; }
    /// <summary>The SQL Server the site's web.config connects to, e.g. <c>localhost,1433</c>; null when it names none.</summary>
    public string? DatabaseServer { get; init; }
    /// <summary>
    /// The database is a LocalDB file the site attaches in its own LocalDB instance - not asked: DNN Manager can't
    /// open it while the site runs.
    /// </summary>
    public bool DatabaseIsFile { get; init; }
    /// <summary>
    /// Whether the SQL Server answers - asked with the site's own web.config connection (its server and login, or
    /// Windows authentication), never DNN Manager's settings; null until it has been asked.
    /// </summary>
    public bool? SqlReachable { get; init; }
    public bool DatabaseExists { get; init; }
    /// <summary>Why the server doesn't answer, or the database isn't there - e.g. "Login failed for user 'sa'".</summary>
    public string? SqlProblem { get; init; }
    /// <summary>The site's worker processes; null while it has none.</summary>
    public ProcessGroupStats? Stats { get; init; }
    /// <summary>The site's HTTP traffic since IIS started; null while it isn't sampled or IIS doesn't count it.</summary>
    public SiteTraffic? Traffic { get; init; }
}

/// <summary>The parts of a <see cref="ProjectState"/>, each kept up to date in its own way and at its own pace.</summary>
[Flags]
public enum ProjectFacets
{
    None = 0,
    /// <summary>Name, folder, address, DNN version, database name.</summary>
    Metadata = 1,
    /// <summary>The IIS site: its state, bindings, app pool and worker process IDs.</summary>
    Site = 2,
    /// <summary>The worker processes' CPU, memory and I/O.</summary>
    Stats = 4,
    Traffic = 8,
    /// <summary>Whether the SQL Server answers and the database exists.</summary>
    Sql = 16,
    Size = 32,
}

/// <summary>Whether the monitor's view of the system can be trusted right now.</summary>
public enum MonitorConnection
{
    /// <summary>The first snapshot is being taken.</summary>
    Connecting,
    /// <summary>In sync: the snapshot is complete and changes arrive by themselves.</summary>
    Live,
    /// <summary>Something couldn't be read, a change notification was lost, or the PC just woke up - what is shown
    /// stays, and the monitor is synchronising again.</summary>
    Reconnecting,
}

/// <summary>A change in what the monitor knows. They arrive in order, in batches - see <see cref="ServerStateMonitor.Changed"/>.</summary>
public abstract record MonitorEvent;

/// <summary>What the <see cref="ServerStateMonitor"/> tells as it happens - for what follows it besides the window (keep warm).</summary>
public interface IServerStateFeed
{
    /// <summary>What changed, in order - raised on a background thread under the monitor's lock: hand it on and return.</summary>
    event Action<IReadOnlyList<MonitorEvent>>? Changed;

    /// <summary>The PC woke up and everything has been read again.</summary>
    event Action? Resumed;

    /// <summary>Reads the sites again now - e.g. to see the worker process a request just started - and publishes what differs.</summary>
    Task SyncSitesAsync();
}

public sealed record ProjectAdded(ProjectState Project) : MonitorEvent;

/// <param name="Project">The project as it is now.</param>
/// <param name="Changed">What differs from before.</param>
public sealed record ProjectChanged(ProjectState Project, ProjectFacets Changed) : MonitorEvent;

public sealed record ProjectRemoved(string Name) : MonitorEvent;

/// <summary>IIS itself (its web service) started, stopped, or is on its way.</summary>
public sealed record RuntimeChanged(IisServerState State) : MonitorEvent;

/// <summary>This PC's memory, CPU and disk use.</summary>
public sealed record HostStatsChanged(HostResources Stats) : MonitorEvent;

/// <param name="Detail">What is wrong, while reconnecting.</param>
public sealed record ConnectionChanged(MonitorConnection Connection, string? Detail) : MonitorEvent;
