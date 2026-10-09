using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>
/// An IIS site as IIS has it configured and running - what the Projects table is made of: every site in IIS is a
/// row, whatever folder it serves.
/// </summary>
/// <param name="State">IIS's state of the site: "Started", "Starting", "Stopping", "Stopped" or "Unknown".</param>
/// <param name="WorkerProcessIds">The app pool's w3wp.exe processes - none until the site gets its first request.</param>
/// <param name="Bindings">How it is reached: protocol, address, port and host name of each binding.</param>
/// <param name="PhysicalPath">The folder its root application serves (environment variables expanded).</param>
public sealed record IisSiteRuntime(
    long Id,
    string State,
    string AppPool,
    string? AppPoolState,
    IReadOnlyList<int> WorkerProcessIds,
    IReadOnlyList<IisBinding> Bindings,
    string PhysicalPath)
{
    /// <summary>The ports of its bindings, lowest first.</summary>
    public IReadOnlyList<int> Ports { get; } = Bindings.Select(b => b.Port).OfType<int>().Distinct().Order().ToList();

    /// <summary>
    /// How long its app pool's worker process may go without a request before IIS shuts it down (20 minutes by
    /// default); zero when it never does, null when it isn't known.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// Where a browser opens it: an https binding with a certificate first, then an http one, then an https one
    /// without a certificate - each with a host name before one without. Null for a site without web bindings.
    /// </summary>
    public string? BrowseUrl =>
        Bindings.Where(b => b.IsWeb)
            .OrderBy(b => (b.IsHttps && b.HasCertificate ? 0 : b.IsHttps ? 2 : 1) + (b.Host.Length > 0 ? 0 : 3))
            .FirstOrDefault()?.Url;

    /// <summary>The same site in the same state - the lists compared by what is in them.</summary>
    public bool SameAs(IisSiteRuntime? other) =>
        other is not null && Id == other.Id && State == other.State && AppPool == other.AppPool &&
        AppPoolState == other.AppPoolState && PhysicalPath == other.PhysicalPath && IdleTimeout == other.IdleTimeout &&
        Bindings.SequenceEqual(other.Bindings) && WorkerProcessIds.SequenceEqual(other.WorkerProcessIds);
}

/// <summary>IIS's states of a site or an app pool, as IIS names them (<c>ObjectState</c>).</summary>
public static class IisStates
{
    public const string Started = "Started", Starting = "Starting", Stopping = "Stopping", Stopped = "Stopped", Unknown = "Unknown";

    /// <summary>Started - however it is capitalised.</summary>
    public static bool IsStarted(string? state) => string.Equals(state, Started, StringComparison.OrdinalIgnoreCase);
}
