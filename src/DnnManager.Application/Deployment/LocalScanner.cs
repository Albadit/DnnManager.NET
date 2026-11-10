using System.Collections.Concurrent;
using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>
/// The project's files as a deployment sees them: every file under its folder that the rules don't ignore, with its
/// SHA-256. A hash is read again only for a file whose size or time changed since it was last hashed (the store keeps
/// them), so a scan of an unchanged project reads no file. Links and junctions aren't gone into - what they point to
/// isn't the project's.
/// </summary>
public sealed class LocalScanner(IDeployStore store)
{
    /// <param name="root">The project's folder.</param>
    /// <param name="warn">Hears of each file left out, and why.</param>
    /// <param name="progress">Hears how many files were hashed of how many.</param>
    /// <param name="unreadable">Hears of each file or folder (ending in <c>/</c>) that couldn't be read - not known, so
    /// never to count as deleted.</param>
    public async Task<IReadOnlyDictionary<string, FileState>> ScanAsync(string root, IgnoreRules rules, Action<string> warn,
        Action<string> unreadable, Action<int, int>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The project's folder {root} isn't there.");
        if (SafePath.IsLink(root))
            throw new IOException($"{root} is a link or junction - DNN Manager doesn't deploy through those.{Environment.NewLine}{SafePath.LinkHint}");

        var files = new List<(string Relative, FileInfo Info)>();
        await Task.Run(() => Walk(root, "", rules, files, warn, unreadable, ct), ct);

        var cached = store.LocalHashes(root);
        var result = new ConcurrentDictionary<string, FileState>(DeployPath.Comparer);
        var fresh = new ConcurrentDictionary<string, CachedHash>(DeployPath.Comparer);
        var hashed = 0;
        var toHash = new List<(string Relative, FileInfo Info)>();
        foreach (var (relative, info) in files)
        {
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (cached.TryGetValue(relative, out var known) && known.Size == info.Length && known.ModifiedTicks == ticks)
            {
                result[relative] = new FileState(info.Length, known.Hash, info.LastWriteTimeUtc);
                fresh[relative] = known;
            }
            else
            {
                toHash.Add((relative, info));
            }
        }

        await Parallel.ForEachAsync(toHash, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (file, token) =>
        {
            try
            {
                var hash = await Hashing.OfFileAsync(file.Info.FullName, token);
                // Its size and time as they were before it was read: a file written meanwhile is hashed again next time.
                result[file.Relative] = new FileState(file.Info.Length, hash, file.Info.LastWriteTimeUtc);
                fresh[file.Relative] = new CachedHash(file.Info.Length, file.Info.LastWriteTimeUtc.Ticks, hash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn($"Left out {file.Relative}: it can't be read ({ex.Message}).");
                unreadable(file.Relative);
            }
            var done = Interlocked.Increment(ref hashed);
            if (done % 50 == 0 || done == toHash.Count) progress?.Invoke(done, toHash.Count);
        });

        if (toHash.Count > 0 || fresh.Count != cached.Count) store.SaveLocalHashes(root, fresh);
        return result;
    }

    private static void Walk(string root, string relativeFolder, IgnoreRules rules, List<(string, FileInfo)> files, Action<string> warn,
        Action<string> unreadable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folder = relativeFolder.Length == 0 ? root : Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        DirectoryInfo directory = new(folder);
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (relativeFolder.Length == 0) throw;
            warn($"Left out {relativeFolder}/: it can't be read ({ex.Message}).");
            unreadable(relativeFolder + "/");
            return;
        }
        foreach (var entry in entries)
        {
            var relative = relativeFolder.Length == 0 ? entry.Name : relativeFolder + "/" + entry.Name;
            var isFolder = entry is DirectoryInfo;
            if (rules.IsIgnored(relative, isFolder)) continue;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                warn($"Left out {relative}{(isFolder ? "/" : "")}: it is a link or junction.");
                continue;
            }
            if (DeployPath.Normalize(relative) is not { } safe)
            {
                warn($"Left out {relative}: its name can't be used on a server.");
                continue;
            }
            if (isFolder) Walk(root, safe, rules, files, warn, unreadable, ct);
            else files.Add((safe, (FileInfo)entry));
        }
    }
}
