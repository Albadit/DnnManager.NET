namespace DnnManager.Application.Abstractions;

/// <summary>
/// Whether DNN Manager keeps a site warm - switched with the flame in its row (or on its overview) - and the site's own
/// interval and pages. A null value follows Settings → Projects → Keep warm.
/// </summary>
/// <param name="Site">The IIS site's name.</param>
/// <param name="PingMinutes">Minutes between two requests to the keep-alive page, at most.</param>
/// <param name="WarmUpPath">The page requested to warm the site up after its worker process ended, e.g. <c>/</c>.</param>
/// <param name="PingPath">The light page requested to keep it warm, e.g. <c>/KeepAlive.aspx</c>.</param>
public sealed record KeepWarmRecord(string Site, bool Enabled, int? PingMinutes = null, string? WarmUpPath = null, string? PingPath = null)
{
    /// <summary>Off, and nothing of the site's own - nothing worth keeping.</summary>
    public bool IsEmpty => !Enabled && PingMinutes is null && WarmUpPath is null && PingPath is null;
}

/// <summary>
/// The <see cref="KeepWarmRecord"/>s, in <c>Documents\DnnManager\state\keep-warm.json</c>. They hold no secrets; a file
/// that can't be read counts as none (no site is kept warm).
/// </summary>
public interface IKeepWarmRecords
{
    /// <summary>The record of site <paramref name="site"/>; null when it has none.</summary>
    KeepWarmRecord? Find(string site);

    IReadOnlyList<KeepWarmRecord> List();

    /// <summary>Saves <paramref name="record"/> - or removes the file when the record is empty.</summary>
    void Save(KeepWarmRecord record);

    void Remove(string site);
}
