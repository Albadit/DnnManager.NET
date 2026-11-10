using System.Collections.Concurrent;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>
/// A comparison of a project with one of its servers: every file and where it stands (<see cref="SyncItem"/>), as it
/// was at <see cref="ScannedUtc"/>. A push or pull checks again, file by file, that nothing changed since.
/// </summary>
public sealed record SyncScan(DeployConnection Connection, string LocalRoot, IReadOnlyList<SyncItem> Items,
    IReadOnlyDictionary<string, RemoteFile> Remote, DateTime ScannedUtc, IReadOnlyList<string> Warnings)
{
    /// <summary>The files that aren't the same in both.</summary>
    public IEnumerable<SyncItem> Changes => Items.Where(i => i.Status != SyncStatus.Synchronized);

    public int Count(SyncDirection direction) => Items.Count(i => i.Direction == direction);

    /// <summary>How many of the server's files were read to compare them, and how many bytes that was.</summary>
    public int Fetched { get; init; }
    public long FetchedBytes { get; init; }

    /// <summary>The server computed the hashes itself (sha256sum, HASH) - nothing had to be fetched for them.</summary>
    public bool ServerHashes { get; init; }

    public SyncItem? Find(string path) => Items.FirstOrDefault(i => DeployPath.Comparer.Equals(i.Path, path));
}

/// <summary>
/// Compares a project with a server through their baseline (<see cref="SyncComparer"/>), reading as little as it can:
/// the project's hashes come from the store unless a file's size or time changed (<see cref="LocalScanner"/>); a
/// server's file listed as it was at the baseline still holds the baseline's content; one whose size differs from the
/// project's and the baseline's is known to differ without reading it. Only the rest is hashed - by the server when it
/// can, otherwise read, in parallel, and what differs from the project kept to show its diff. Files that turn out the same
/// on both sides become the baseline; ones gone from both are dropped from it.
/// </summary>
public sealed class SyncScanner(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, LocalScanner localScanner)
{
    // So many paths a sha256sum.
    private const int HashBatch = 100;

    public async Task<SyncScan> ScanAsync(DeployConnection connection, string localRoot, IProgressReporter reporter, CancellationToken ct)
    {
        var rules = IgnoreRules.Parse(connection.IgnoreRules);
        var warnings = new ConcurrentQueue<string>();
        void Warn(string message) => warnings.Enqueue(message);
        // What couldn't be read, on either side: not known - left out on both, never taken for deleted.
        var unreadable = new ConcurrentBag<string>();
        void Unreadable(string path) => unreadable.Add(path);

        reporter.Step($"Reading the project's files in {localRoot}", "Project");
        var local = await localScanner.ScanAsync(localRoot, rules, Warn, Unreadable, (done, total) => reporter.Progress($"Hashed {done} of {total} changed files"), ct);
        reporter.Success($"{local.Count} files in the project.");

        await using var pool = new RemotePool(factory, connection, store.ReadSecrets(connection.Id));
        reporter.Step($"Listing the server's files in {connection.RemoteRoot}", "Server");
        var listing = await pool.RunAsync(fs => fs.ListAsync(folder => rules.IsIgnored(folder, true), Warn, Unreadable, ct), ct,
            (attempt, why) => reporter.Warn($"Listing again (try {attempt}): {why}"));
        var remoteFiles = new Dictionary<string, RemoteFile>(DeployPath.Comparer);
        foreach (var file in listing)
        {
            if (DeployPath.IsOwnFile(file.Path) || rules.IsIgnored(file.Path, false)) continue;
            // Two names that differ only in case (a Linux server allows it): one file on Windows - which is meant isn't known.
            if (remoteFiles.TryGetValue(file.Path, out var other))
            {
                Warn($"Left out {other.Path} and {file.Path} on the server: their names differ only in case, which Windows can't hold apart.");
                Unreadable(file.Path);
                continue;
            }
            remoteFiles[file.Path] = file;
        }
        // The project and the server spelling a name differently: the server's path isn't the project's - left out, said.
        foreach (var path in local.Keys)
            if (remoteFiles.TryGetValue(path, out var remoteFile) && !string.Equals(remoteFile.Path, path, StringComparison.Ordinal))
            {
                Warn($"Left out {path}: the server spells it {remoteFile.Path} - rename one of them so they match.");
                Unreadable(path);
            }
        bool Unknown(string path) => unreadable.Any(u => u.EndsWith('/')
            ? path.StartsWith(u, StringComparison.OrdinalIgnoreCase) || u == "/"
            : DeployPath.Comparer.Equals(u, path));
        if (!unreadable.IsEmpty)
        {
            foreach (var path in remoteFiles.Keys.Where(Unknown).ToList()) remoteFiles.Remove(path);
            local = local.Where(l => !Unknown(l.Key)).ToDictionary(l => l.Key, l => l.Value, DeployPath.Comparer);
        }
        reporter.Success($"{remoteFiles.Count} files on the server.");

        reporter.Step("Comparing", "Compare");
        var baseline = store.Baseline(connection.Id);
        var remote = new ConcurrentDictionary<string, FileState>(DeployPath.Comparer);
        var unknown = new List<RemoteFile>();
        foreach (var file in remoteFiles.Values)
        {
            baseline.TryGetValue(file.Path, out var b);
            local.TryGetValue(file.Path, out var l);
            if (b is { RemoteModifiedUtc: { } seen } && b.RemoteSize == file.Size && file.ModifiedUtc == seen)
                remote[file.Path] = new FileState(file.Size, b.Hash, file.ModifiedUtc);
            else if (file.Size != l?.Size && file.Size != b?.Size)
                remote[file.Path] = new FileState(file.Size, null, file.ModifiedUtc);
            else
                unknown.Add(file);
        }

        var fetched = 0;
        long fetchedBytes = 0;
        var serverHashes = false;
        if (unknown.Count > 0)
        {
            // The server's own hashes, when it gives them: nothing is sent.
            var hashes = await pool.RunAsync(async fs =>
            {
                var all = new Dictionary<string, string>(DeployPath.Comparer);
                foreach (var batch in unknown.Chunk(HashBatch))
                {
                    if (await fs.TryHashAsync([.. batch.Select(f => f.Path)], ct) is not { } some) return null;
                    foreach (var (path, hash) in some) all[path] = hash;
                    reporter.Progress($"The server hashed {all.Count} of {unknown.Count} files");
                }
                return all;
            }, ct);
            if (hashes is not null)
            {
                serverHashes = true;
                foreach (var file in unknown.Where(f => hashes.ContainsKey(f.Path)))
                    remote[file.Path] = new FileState(file.Size, hashes[file.Path], file.ModifiedUtc);
                unknown = [.. unknown.Where(f => !hashes.ContainsKey(f.Path))];
            }

            if (unknown.Count > 0)
            {
                var total = unknown.Sum(f => f.Size);
                reporter.Info(baseline.Count == 0
                    ? $"First comparison: {unknown.Count} files ({Bytes(total)}) have the same size in the project and on the server - their " +
                      "content is read once to compare it. After this, only what changes is read."
                    : $"{unknown.Count} files ({Bytes(total)}) changed on the server since the last sync, or match the project's size - reading them to compare.");
                await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = pool.Size, CancellationToken = ct }, async (file, token) =>
                {
                    local.TryGetValue(file.Path, out var l);
                    var hash = await pool.RunAsync(async fs =>
                    {
                        await using var writer = snapshots.BeginAdd();
                        await fs.DownloadAsync(file.Path, writer.Stream, 0, null, token);
                        // What the project has too isn't kept twice.
                        return await writer.CompleteAsync(h => !Hashing.Same(h, l?.Hash), token);
                    }, token, (attempt, why) => reporter.Warn($"{file.Path}: reading again (try {attempt}) - {why}"));
                    remote[file.Path] = new FileState(file.Size, hash, file.ModifiedUtc);
                    var n = Interlocked.Increment(ref fetched);
                    Interlocked.Add(ref fetchedBytes, file.Size);
                    reporter.Progress($"Compared {n} of {unknown.Count} files");
                });
            }
        }

        var items = SyncComparer.Compare(local, remote, baseline);
        UpdateBaseline(connection.Id, items, baseline, remote, local, path => rules.IsIgnored(path, false) || Unknown(path));
        store.RecordCheck(connection.Id, DateTime.UtcNow, null);

        var scan = new SyncScan(connection, localRoot, items, remoteFiles, DateTime.UtcNow, [.. warnings])
        {
            Fetched = fetched, FetchedBytes = fetchedBytes, ServerHashes = serverHashes
        };
        foreach (var warning in scan.Warnings.Take(20)) reporter.Warn(warning);
        if (scan.Warnings.Count > 20) reporter.Warn($"… and {scan.Warnings.Count - 20} more files left out.");
        reporter.Fact("To push", scan.Count(SyncDirection.Push).ToString());
        reporter.Fact("To pull", scan.Count(SyncDirection.Pull).ToString());
        reporter.Fact("Conflicts", scan.Count(SyncDirection.Conflict).ToString());
        reporter.Success($"{scan.Count(SyncDirection.Push)} to push, {scan.Count(SyncDirection.Pull)} to pull, " +
                         $"{scan.Count(SyncDirection.Conflict)} conflicts, {scan.Count(SyncDirection.None)} the same" +
                         (fetched > 0 ? $" - {fetched} files ({Bytes(fetchedBytes)}) read from the server." : "."));
        return scan;
    }

    /// <summary>
    /// What both sides agree on becomes the baseline; a file the server lists differently but still holds the baseline's
    /// content gets its new listing (it isn't read again); one gone from both sides is dropped.
    /// </summary>
    private void UpdateBaseline(Guid id, IReadOnlyList<SyncItem> items, IReadOnlyDictionary<string, BaselineEntry> baseline,
        IReadOnlyDictionary<string, FileState> remote, IReadOnlyDictionary<string, FileState> local, Func<string, bool> notLookedAt)
    {
        var set = new List<BaselineEntry>();
        foreach (var item in items)
        {
            if (item.Remote is not { Hash: { } remoteHash } r) continue;
            var agreed = item.Status == SyncStatus.Synchronized;
            if (!agreed && !(item.Base is { } b && Hashing.Same(b.Hash, remoteHash))) continue;
            var entry = new BaselineEntry(item.Path, remoteHash, r.Size, r.Size, r.ModifiedUtc);
            if (item.Base != entry) set.Add(entry);
        }
        // An ignored file isn't gone - it wasn't looked at: its baseline stays for when it isn't ignored any more.
        var gone = baseline.Keys.Where(p => !remote.ContainsKey(p) && !local.ContainsKey(p) && !notLookedAt(p)).ToList();
        if (set.Count > 0 || gone.Count > 0) store.UpdateBaseline(id, set, gone);
    }

    internal static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
