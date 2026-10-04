using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.State;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Projects;

/// <summary>Which sites are kept warm, and their own keep-warm values: <c>state\keep-warm.json</c>.</summary>
public sealed class KeepWarmSites : IStateFile
{
    public static string FileName => "keep-warm.json";
    public static int CurrentFormat => 1;
    public int Format { get; set; }

    public List<KeepWarmRecord> Sites { get; set; } = [];
}

/// <summary>
/// <see cref="KeepWarmRecord"/>s in one state file (<see cref="KeepWarmSites"/>), saved the moment one changes - so the
/// next start switches the same sites on again. A file that can't be read is set aside and counts as none: no site is
/// kept warm until it is switched on again.
/// </summary>
public sealed class KeepWarmRecords(AppDataPaths paths, ILogger<KeepWarmRecords> log) : IKeepWarmRecords
{
    private readonly StateStore _state = new(paths.StateDirectory, log);
    // A change is read, changed and written in one go - keep warm and the UI save from different threads.
    private readonly Lock _gate = new();

    public KeepWarmRecord? Find(string site)
    {
        lock (_gate) return _state.Load<KeepWarmSites>().Sites.FirstOrDefault(r => Is(r, site));
    }

    public IReadOnlyList<KeepWarmRecord> List()
    {
        lock (_gate) return _state.Load<KeepWarmSites>().Sites.ToList();
    }

    public void Save(KeepWarmRecord record)
    {
        if (record.IsEmpty)
        {
            Remove(record.Site);
            return;
        }
        lock (_gate)
        {
            var state = _state.Load<KeepWarmSites>();
            state.Sites.RemoveAll(r => Is(r, record.Site));
            state.Sites.Add(record);
            state.Sites.Sort((a, b) => string.Compare(a.Site, b.Site, StringComparison.OrdinalIgnoreCase));
            _state.Save(state);
        }
    }

    public void Remove(string site)
    {
        lock (_gate)
        {
            var state = _state.Load<KeepWarmSites>();
            if (state.Sites.RemoveAll(r => Is(r, site)) > 0) _state.Save(state);
        }
    }

    private static bool Is(KeepWarmRecord record, string site) => record.Site.Equals(site, StringComparison.OrdinalIgnoreCase);
}
