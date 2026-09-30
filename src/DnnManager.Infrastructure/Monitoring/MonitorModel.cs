using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Monitoring;

/// <summary>
/// What the monitor knows about one project. Immutable - a change makes a new one, sent in a
/// <see cref="ProjectChanged"/> with the facets that differ.
/// </summary>
public sealed record ProjectState
{
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required string SiteUrl { get; init; }
    /// <summary>From <c>bin\DotNetNuke.dll</c>; null when the folder holds no DNN.</summary>
    public string? DnnVersion { get; init; }
    /// <summary>The database the site uses: from its web.config, else the one named like the project.</summary>
    public required string DatabaseName { get; init; }
    /// <summary>The folder's size; null until it has been measured.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>The project's IIS site; null when it has none.</summary>
    public IisSiteRuntime? Site { get; init; }
    /// <summary>False while IIS's sites haven't been read yet (they couldn't be) - no <see cref="Site"/> then says nothing.</summary>
    public bool SiteKnown { get; init; } = true;
    /// <summary>Whether the SQL Server answers; null until it has been asked.</summary>
    public bool? SqlReachable { get; init; }
    public bool DatabaseExists { get; init; }
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
    /// <summary>Name, folder, DNN version, database name.</summary>
    Metadata = 1,
    /// <summary>The IIS site: whether it exists, its state, ports, app pool and worker process IDs.</summary>
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
