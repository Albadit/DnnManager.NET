using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>
/// Decides where each file stands from three states - the project's (local), the server's (remote) and the baseline,
/// where the two last agreed - as git does with a merge base. A side has changed when its content differs from the
/// baseline's; which side is newer by its time is never asked: a server's clock and a PC's don't agree.
/// <list type="table">
/// <listheader><term>Local</term><description>Remote → result</description></listheader>
/// <item><term>changed</term><description>unchanged → push</description></item>
/// <item><term>unchanged</term><description>changed → pull</description></item>
/// <item><term>changed</term><description>changed, the same way → synchronized</description></item>
/// <item><term>changed</term><description>changed, differently → conflict</description></item>
/// <item><term>new</term><description>missing → push (added)</description></item>
/// <item><term>missing</term><description>new → pull (added)</description></item>
/// <item><term>deleted</term><description>unchanged → push the deletion</description></item>
/// <item><term>unchanged</term><description>deleted → pull the deletion</description></item>
/// </list>
/// The project's file and the baseline always have their hash. The server's may have none when its size already tells it
/// apart from both the project's file and the baseline (<see cref="SyncScanner"/> reads only what it must): it differs
/// from them, whatever it holds.
/// </summary>
public static class SyncComparer
{
    /// <param name="local">The project's files, by relative path, each with its hash.</param>
    /// <param name="remote">The server's files, each with its hash.</param>
    /// <param name="baseline">The baseline, by relative path.</param>
    /// <returns>Every file present somewhere, synchronized ones too, sorted by path.</returns>
    public static IReadOnlyList<SyncItem> Compare(IReadOnlyDictionary<string, FileState> local, IReadOnlyDictionary<string, FileState> remote,
        IReadOnlyDictionary<string, BaselineEntry> baseline)
    {
        var paths = new SortedSet<string>(local.Keys, DeployPath.Comparer);
        paths.UnionWith(remote.Keys);
        var items = new List<SyncItem>(paths.Count);
        foreach (var path in paths)
        {
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            baseline.TryGetValue(path, out var b);
            items.Add(Classify(path, l, r, b));
        }
        return items;
    }

    /// <summary>Where one file stands. Both sides absent can't be asked for: there is no file.</summary>
    public static SyncItem Classify(string path, FileState? local, FileState? remote, BaselineEntry? baseline)
    {
        if (local is null && remote is null) throw new ArgumentException($"{path} is in neither place.");
        if (local is not null && local.Hash is null) throw new ArgumentException($"{path}: the project's file has no hash.");
        if (remote is { Hash: null } && (remote.Size == local?.Size || remote.Size == baseline?.Size))
            throw new ArgumentException($"{path}: the server's file has no hash, and its size doesn't tell it apart.");
        var sensitive = IgnoreRules.IsSensitive(path);
        SyncItem Item(SyncStatus status, ConflictKind conflict = ConflictKind.None) => new(path, status, conflict, local, remote, baseline, sensitive);

        // The same in both - whatever the baseline says, there is nothing to do.
        if (local is not null && remote is not null && Same(local.Hash, remote.Hash)) return Item(SyncStatus.Synchronized);

        if (baseline is null)
        {
            if (remote is null) return Item(SyncStatus.LocalAdded);
            if (local is null) return Item(SyncStatus.RemoteAdded);
            return Item(SyncStatus.Conflict, ConflictKind.BothAdded);
        }

        var localChanged = local is null || !Same(local.Hash, baseline.Hash);
        var remoteChanged = remote is null || !Same(remote.Hash, baseline.Hash);
        return (localChanged, remoteChanged) switch
        {
            (true, false) => Item(local is null ? SyncStatus.LocalDeleted : SyncStatus.LocalModified),
            (false, true) => Item(remote is null ? SyncStatus.RemoteDeleted : SyncStatus.RemoteModified),
            // Both changed: one of them is there (both gone isn't asked), and they differ (the same was handled above).
            _ => Item(SyncStatus.Conflict, local is null ? ConflictKind.LocalDeletedRemoteModified
                : remote is null ? ConflictKind.LocalModifiedRemoteDeleted
                : ConflictKind.BothModified)
        };
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
