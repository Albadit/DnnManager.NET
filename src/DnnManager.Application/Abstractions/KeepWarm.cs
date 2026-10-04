namespace DnnManager.Application.Abstractions;

/// <summary>
/// Whether DNN Manager keeps a site warm - switched with the flame in its row (or on its overview). How it is kept warm
/// (interval, pages) is the same for every site: Settings → Projects → Keep warm.
/// </summary>
/// <param name="Site">The IIS site's name.</param>
public sealed record KeepWarmRecord(string Site, bool Enabled)
{
    /// <summary>Off - nothing worth keeping.</summary>
    public bool IsEmpty => !Enabled;
}

/// <summary>
/// The <see cref="KeepWarmRecord"/>s, one row per site in DNN Manager's database (<c>keep_warm</c>). They hold no
/// secrets; when they can't be read there are none (no site is kept warm).
/// </summary>
public interface IKeepWarmRecords
{
    /// <summary>The record of site <paramref name="site"/>; null when it has none.</summary>
    KeepWarmRecord? Find(string site);

    IReadOnlyList<KeepWarmRecord> List();

    /// <summary>Saves <paramref name="record"/> - or removes the site's record when it is empty.</summary>
    void Save(KeepWarmRecord record);

    void Remove(string site);
}
