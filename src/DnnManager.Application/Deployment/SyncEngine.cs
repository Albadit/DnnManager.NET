using System.Collections.Concurrent;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>What a file is expected to be before it is changed: absent, or present with this content and/or this listing.</summary>
public sealed record ExpectedState(bool Exists, string? Hash = null, long? Size = null, DateTime? ModifiedUtc = null)
{
    public static ExpectedState Absent { get; } = new(false);

    public static ExpectedState Of(FileState? state) => state is null ? Absent : new(true, state.Hash, state.Size, state.ModifiedUtc);
}

/// <summary>
/// A change to make to one file: write the content kept as <paramref name="Content"/> (from the
/// <see cref="ISnapshotStore"/>), or delete it (null) - only when it is still as <paramref name="Expected"/>.
/// </summary>
public sealed record FileChange(string Path, string? Content, long ContentSize, ExpectedState Expected);

/// <summary>What happened to one file: its hash before and after (null: no file), and for the server how it lists it now.</summary>
public sealed record FileOutcome(string Path, FileAction Action, string? Before, string? After, RemoteFile? Listing, int Attempts);

/// <summary>One transfer's progress, for the window: the file, how far it is, and how far the operation is.</summary>
public sealed record TransferUpdate(string Path, long Done, long Total, int FilesDone, int FilesTotal, string Phase);

/// <summary>
/// Applies changes to one side of a connection - the server's folder (a push, a rollback of one) or the project's (a
/// pull, a resolution, a rollback of one) - the same careful way, in three passes:
/// <list type="number">
/// <item>Check: every file is still as the scan saw it. One that changed since - on the server, or the user's own work in
/// the project - stops it all before anything is written: nobody's change is overwritten unseen.</item>
/// <item>Back up: what is about to be overwritten or deleted is kept in the snapshot store (<see cref="DeployConnection.KeepBackups"/>).</item>
/// <item>Apply: each file is written beside its place and then moved into it (atomic on SFTP and NTFS), checked by its
/// hash, given the permissions of the file it replaces; in parallel on the server, with network failures tried again
/// and a file sent in part resumed.</item>
/// </list>
/// Before the third pass it notes in <see cref="OperationUndo"/> how to put back what it changes, so a failed or
/// cancelled operation leaves both sides as they were.
/// </summary>
public sealed class SyncEngine(ISnapshotStore snapshots, IRemoteFileSystemFactory factory, IDeployStore store, OperationUndo undo)
{
    /// <summary>How long putting a whole operation's files back may take.</summary>
    private static readonly TimeSpan UndoLimit = TimeSpan.FromMinutes(15);

    // ─── The server ────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<FileOutcome>>> ApplyRemoteAsync(RemotePool pool, IReadOnlyList<FileChange> changes,
        IProgressReporter reporter, Action<TransferUpdate>? progress, CancellationToken ct)
    {
        var connection = pool.Connection;
        var current = new ConcurrentDictionary<string, RemoteFile?>(DeployPath.Comparer);
        var currentHash = new ConcurrentDictionary<string, string?>(DeployPath.Comparer);
        var problems = new ConcurrentBag<string>();

        reporter.Step("Checking nothing changed on the server since the scan", "Check");
        var checkedCount = 0;
        await Parallel.ForEachAsync(changes, Options(pool.Size, ct), async (change, token) =>
        {
            var now = await pool.RunAsync(fs => fs.StatAsync(change.Path, token), token, Retrying(reporter, change.Path));
            current[change.Path] = now;
            var expected = change.Expected;
            if (!expected.Exists)
            {
                if (now is not null) problems.Add($"{change.Path} was added on the server since the scan.");
            }
            else if (now is null)
            {
                problems.Add($"{change.Path} was deleted from the server since the scan.");
            }
            else if (expected.Size is not null && expected.ModifiedUtc is not null && now.Size == expected.Size && now.ModifiedUtc == expected.ModifiedUtc)
            {
                currentHash[change.Path] = expected.Hash;
            }
            else if (expected.Hash is not null)
            {
                // Listed differently (or never listed - a rollback): its content decides.
                var hash = await RemoteHashAsync(pool, change.Path, token, reporter);
                currentHash[change.Path] = hash;
                if (!Hashing.Same(hash, expected.Hash)) problems.Add($"{change.Path} was changed on the server since the scan.");
            }
            else
            {
                problems.Add($"{change.Path} was changed on the server since the scan.");
            }
            progress?.Invoke(new TransferUpdate(change.Path, 0, 0, Interlocked.Increment(ref checkedCount), changes.Count, "Checking"));
        });
        if (!problems.IsEmpty) return Changed(problems, "server");
        reporter.Success($"All {changes.Count} files are as the scan saw them.");

        // What is about to be overwritten or deleted, kept first.
        var before = new ConcurrentDictionary<string, string?>(DeployPath.Comparer);
        var existing = changes.Where(c => current[c.Path] is not null).ToList();
        if (existing.Count > 0 && connection.KeepBackups)
        {
            reporter.Step($"Keeping a copy of the {existing.Count} files about to be replaced or deleted", "Back up");
            var kept = 0;
            await Parallel.ForEachAsync(existing, Options(pool.Size, ct), async (change, token) =>
            {
                var known = currentHash.GetValueOrDefault(change.Path);
                if (known is not null && snapshots.Contains(known))
                {
                    before[change.Path] = known;
                }
                else
                {
                    var hash = await pool.RunAsync(async fs =>
                    {
                        await using var writer = snapshots.BeginAdd();
                        await fs.DownloadAsync(change.Path, writer.Stream, 0, null, token);
                        return await writer.CompleteAsync(_ => true, token);
                    }, token, Retrying(reporter, change.Path));
                    if (known is not null && !Hashing.Same(hash, known))
                        problems.Add($"{change.Path} was changed on the server while DNN Manager was reading it.");
                    before[change.Path] = hash;
                }
                progress?.Invoke(new TransferUpdate(change.Path, 0, 0, Interlocked.Increment(ref kept), existing.Count, "Backing up"));
            });
            if (!problems.IsEmpty) return Changed(problems, "server");
            reporter.Success($"Kept {existing.Count} files - a rollback puts them back.");
        }
        else
        {
            foreach (var change in existing) before[change.Path] = currentHash.GetValueOrDefault(change.Path);
        }

        // From here on files change: how to put each back is noted first.
        var touched = new ConcurrentDictionary<string, bool>(DeployPath.Comparer);
        NoteRemoteUndo(connection, changes, before, touched, current);

        reporter.Step($"Sending {changes.Count(c => c.Content is not null)} files and deleting {changes.Count(c => c.Content is null)}", "Transfer");
        var outcomes = new ConcurrentBag<FileOutcome>();
        var failures = new ConcurrentBag<string>();
        var done = 0;
        long bytesDone = 0;
        var bytesTotal = changes.Sum(c => c.ContentSize);
        // The first file that fails for good stops the others: the operation is put back anyway.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(changes, Options(pool.Size, stop.Token), async (change, token) =>
            {
                var attempts = 1;
                try
                {
                    var listing = await pool.RunAsync(async fs =>
                    {
                        if (change.Content is null)
                        {
                            await EnsureUnchangedAsync(fs, change.Path, current[change.Path], token);
                            // Only a file this operation changed is put back by its undo - never one someone else changed.
                            touched[change.Path] = true;
                            await fs.DeleteAsync(change.Path, token);
                            return null;
                        }
                        return await SendAsync(fs, change.Path, change.Content, change.ContentSize, connection.Verify,
                            current[change.Path]?.Permissions, token, new TargetCheck(current[change.Path], () => touched[change.Path] = true), sent =>
                            {
                                Interlocked.Add(ref bytesDone, sent);
                                progress?.Invoke(new TransferUpdate(change.Path, sent, change.ContentSize, Volatile.Read(ref done), changes.Count, "Sending"));
                            });
                    }, token, (attempt, why) =>
                    {
                        attempts = attempt;
                        reporter.Warn($"{change.Path}: trying again (try {attempt}) - {why}");
                    });
                    var action = change.Content is null ? FileAction.Deleted : current[change.Path] is null ? FileAction.Added : FileAction.Modified;
                    outcomes.Add(new FileOutcome(change.Path, action, before.GetValueOrDefault(change.Path), change.Content, listing, attempts));
                    var n = Interlocked.Increment(ref done);
                    reporter.Progress($"{n} of {changes.Count} files done · {SyncScanner.Bytes(Interlocked.Read(ref bytesDone))} of {SyncScanner.Bytes(bytesTotal)}");
                    progress?.Invoke(new TransferUpdate(change.Path, change.ContentSize, change.ContentSize, n, changes.Count, "Sent"));
                }
                catch (Exception ex) when (ex is RemoteException or InvalidDataException)
                {
                    failures.Add($"{change.Path}: {ex.Message}");
                    await stop.CancelAsync();
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !failures.IsEmpty)
        {
            // Stopped by a failure, not by the user - reported below.
        }
        if (!failures.IsEmpty)
            return Result<IReadOnlyList<FileOutcome>>.Fail(
                $"{failures.Count} of {changes.Count} files could not be sent - the files sent are put back:{Environment.NewLine}" +
                string.Join(Environment.NewLine, failures.OrderBy(f => f).Take(20)));
        await RemoveEmptiedFoldersAsync(pool, changes, ct);
        reporter.Success($"{changes.Count} files done on the server.");
        return Result<IReadOnlyList<FileOutcome>>.Ok([.. outcomes.OrderBy(o => o.Path, DeployPath.Comparer)]);
    }

    /// <summary>
    /// Sends the content kept as <paramref name="hash"/> to <paramref name="path"/>: under a name of its own beside it
    /// (resumed when a part of it is there from an interrupted try), checked, given <paramref name="permissions"/>, then
    /// moved into its place. Returns how the server lists it now.
    /// </summary>
    internal async Task<RemoteFile> SendAsync(IRemoteFileSystem fs, string path, string hash, long size, VerifyMode verify, int? permissions,
        CancellationToken ct, TargetCheck? check = null, Action<long>? sent = null)
    {
        var part = DeployPath.PartName(path, hash);
        await using var content = snapshots.OpenVerified(hash)
            ?? throw new InvalidDataException($"The content to send ({hash[..12]}) isn't kept any more.");
        long offset = 0;
        if (await fs.StatAsync(part, ct) is { } partial)
        {
            if (fs.CanResumeUpload && partial.Size > 0 && partial.Size < size)
            {
                // The part sent before: its bytes are read (and hashed) here, not sent again.
                offset = partial.Size;
                await SkipAsync(content, offset, ct);
            }
            else
            {
                await fs.DeleteAsync(part, ct);
            }
        }
        await fs.UploadAsync(content, part, offset, sent, ct);
        // The whole content was read: had it changed in the store, reading it to its end threw.
        await DrainAsync(content, ct);

        if (await fs.StatAsync(part, ct) is not { } uploaded || uploaded.Size != size)
            throw new RemoteException($"{path} arrived incomplete on the server.", transient: true);
        // A resumed part is checked by its content whatever the setting: what was there before isn't known to be whole.
        if (verify == VerifyMode.Checksum || offset > 0)
        {
            var onServer = await RemoteHashAsync(fs, part, ct);
            if (!Hashing.Same(onServer, hash))
            {
                await fs.DeleteAsync(part, ct);
                throw new RemoteException($"{path} arrived damaged on the server (its SHA-256 differs) - sending it again.", transient: true);
            }
        }
        if (permissions is { } mode) await fs.SetPermissionsAsync(part, mode, ct);
        // As the part is now - a rename keeps its time - so a write landing on the file after the rename is a change.
        var listing = await fs.StatAsync(part, ct) ?? throw new RemoteException($"{path} isn't on the server after it was sent.", transient: true);
        // Someone may have changed the file while it was being sent: checked again right before it is replaced.
        if (check is not null)
        {
            await EnsureUnchangedAsync(fs, path, check.Expected, ct);
            check.Replacing?.Invoke();
        }
        await fs.ReplaceAsync(part, path, ct);
        return listing with { Path = path };
    }

    /// <summary>What a file to be replaced must still be, right before it is - null: it must not be there.</summary>
    /// <param name="Replacing">Told right before the file is replaced - from then on it is this operation's to put back.</param>
    internal sealed record TargetCheck(RemoteFile? Expected, Action? Replacing = null);

    /// <summary>Throws (not to be tried again) when the server's file isn't as it was when the operation checked it.</summary>
    private static async Task EnsureUnchangedAsync(IRemoteFileSystem fs, string path, RemoteFile? expected, CancellationToken ct)
    {
        var now = await fs.StatAsync(path, ct);
        var same = expected is null ? now is null : now is not null && now.Size == expected.Size && now.ModifiedUtc == expected.ModifiedUtc;
        if (!same) throw new RemoteException($"{path} was changed on the server while this operation ran - it is left as it is, and everything is put back.", false);
    }

    /// <summary>
    /// The folders deletions left empty - after every transfer, and never one a file was sent into: removing a folder while
    /// another transfer writes into it could take that file with it.
    /// </summary>
    private static async Task RemoveEmptiedFoldersAsync(RemotePool pool, IReadOnlyList<FileChange> changes, CancellationToken ct)
    {
        var written = changes.Where(c => c.Content is not null).Select(c => DeployPath.Folder(c.Path)).ToHashSet(DeployPath.Comparer);
        var folders = changes.Where(c => c.Content is null).Select(c => DeployPath.Folder(c.Path))
            .Where(f => f.Length > 0 && !written.Any(w => DeployPath.Comparer.Equals(w, f) || w.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)))
            .Distinct(DeployPath.Comparer).OrderByDescending(f => f.Length).ToList();
        foreach (var folder in folders)
        {
            try { await pool.RunAsync(fs => fs.DeleteEmptyFoldersAsync(folder, ct), ct); }
            catch (RemoteException) { /* an empty folder left is no harm */ }
        }
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (n == 0) throw new InvalidDataException("The content to send is shorter than the part already sent.");
            count -= n;
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, ct) > 0) { }
    }

    /// <summary>The SHA-256 of a server's file - computed by the server when it can, otherwise read back.</summary>
    public static async Task<string> RemoteHashAsync(IRemoteFileSystem fs, string path, CancellationToken ct)
    {
        if (await fs.TryHashAsync([path], ct) is { } hashes && hashes.TryGetValue(path, out var hash)) return hash;
        await using var sink = new HashSink();
        await fs.DownloadAsync(path, sink, 0, null, ct);
        return sink.Finish();
    }

    private static Task<string> RemoteHashAsync(RemotePool pool, string path, CancellationToken ct, IProgressReporter reporter) =>
        pool.RunAsync(fs => RemoteHashAsync(fs, path, ct), ct, Retrying(reporter, path));

    private void NoteRemoteUndo(DeployConnection connection, IReadOnlyList<FileChange> changes, IReadOnlyDictionary<string, string?> before,
        ConcurrentDictionary<string, bool> touched, IReadOnlyDictionary<string, RemoteFile?> current)
    {
        var missing = changes.Where(c => before.GetValueOrDefault(c.Path) is not { } h || !snapshots.Contains(h))
            .Where(c => c.Expected.Exists).Select(c => c.Path).ToList();
        foreach (var path in missing) undo.CannotUndo($"{path} on the server (no copy of it was kept - backups are off for this connection)");
        undo.Add($"Put back the files changed on the server ({changes.Count})", async ct =>
        {
            var restore = changes.Where(c => touched.ContainsKey(c.Path) && !missing.Contains(c.Path, DeployPath.Comparer)).ToList();
            var failed = new ConcurrentBag<string>();
            await using var pool = new RemotePool(factory, connection, store.ReadSecrets(connection.Id));
            // Parts sent and never moved into place go, whatever became of their files.
            foreach (var change in changes.Where(c => c.Content is not null && !touched.ContainsKey(c.Path)))
            {
                try { await pool.RunAsync(fs => fs.DeleteAsync(DeployPath.PartName(change.Path, change.Content!), ct), ct); }
                catch (RemoteException) { /* a part left is resumed or replaced next time */ }
            }
            if (restore.Count == 0) return Result.Ok();
            await Parallel.ForEachAsync(restore, Options(pool.Size, ct), async (change, token) =>
            {
                try
                {
                    await pool.RunAsync(async fs =>
                    {
                        // A part sent and not moved into place yet goes too.
                        if (change.Content is not null) await fs.DeleteAsync(DeployPath.PartName(change.Path, change.Content), token);
                        if (before.GetValueOrDefault(change.Path) is { } hash)
                        {
                            var size = snapshots.SizeOf(hash) ?? 0;
                            await SendAsync(fs, change.Path, hash, size, connection.Verify, current.GetValueOrDefault(change.Path)?.Permissions, token);
                        }
                        else
                        {
                            await fs.DeleteAsync(change.Path, token);
                        }
                    }, token);
                }
                catch (Exception ex) when (ex is RemoteException or InvalidDataException or IOException)
                {
                    failed.Add($"{change.Path}: {ex.Message}");
                }
            });
            // What the push added is gone again: the folders it made for it too.
            await RemoveEmptiedFoldersAsync(pool, [.. restore.Where(c => before.GetValueOrDefault(c.Path) is null).Select(c => c with { Content = null })], ct);
            return failed.IsEmpty ? Result.Ok() : Result.Fail($"{failed.Count} not put back - {string.Join("; ", failed.Take(5))}");
        }, UndoLimit);
    }

    // ─── The project ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes or deletes files in <paramref name="root"/> (the project's folder, or a review folder): each only when it
    /// is still as expected - the user's own changes since the scan are never overwritten.
    /// </summary>
    public async Task<Result<IReadOnlyList<FileOutcome>>> ApplyLocalAsync(string root, bool keepBackups, IReadOnlyList<FileChange> changes,
        IProgressReporter reporter, Action<TransferUpdate>? progress, CancellationToken ct)
    {
        var problems = new List<string>();
        var targets = new Dictionary<string, string>(DeployPath.Comparer);
        var existed = new Dictionary<string, bool>(DeployPath.Comparer);
        var stamps = new Dictionary<string, LocalStamp?>(DeployPath.Comparer);
        reporter.Step("Checking nothing changed in the project since the scan", "Check");
        var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            if (DeployPath.Normalize(change.Path) is not { } safe ||
                SafePath.Under(root, safe.Replace('/', Path.DirectorySeparatorChar), checkedFolders) is not { } target)
            {
                problems.Add($"{change.Path} can't be written in {root}: it would land outside it or go through a link or junction.");
                continue;
            }
            targets[change.Path] = target;
            // Its size and time before it is hashed: whatever changes them later - an editor saving it - is seen again right
            // before the file is replaced.
            stamps[change.Path] = LocalStamp.Of(target);
            var exists = File.Exists(target);
            existed[change.Path] = exists;
            if (Directory.Exists(target))
                problems.Add($"{change.Path} is a folder in the project.");
            else if (!change.Expected.Exists && exists)
                problems.Add($"{change.Path} was added in the project since the scan - it is left as it is.");
            else if (change.Expected.Exists && !exists)
                problems.Add($"{change.Path} was deleted from the project since the scan.");
            else if (change.Expected.Exists && change.Expected.Hash is { } expected && !Hashing.Same(await Hashing.OfFileAsync(target, ct), expected))
                problems.Add($"{change.Path} was changed in the project since the scan - your change is kept, nothing was overwritten.");
        }
        if (problems.Count > 0) return Changed(problems, "project");
        reporter.Success($"All {changes.Count} files are as the scan saw them.");

        var before = new Dictionary<string, string?>(DeployPath.Comparer);
        var withBackup = changes.Where(c => existed[c.Path]).ToList();
        if (withBackup.Count > 0 && keepBackups) reporter.Step($"Keeping a copy of the {withBackup.Count} files about to be replaced or deleted", "Back up");
        foreach (var change in withBackup)
        {
            if (!keepBackups)
            {
                before[change.Path] = change.Expected.Hash;
                continue;
            }
            var hash = await snapshots.AddFileAsync(targets[change.Path], ct);
            if (change.Expected.Hash is { } expected && !Hashing.Same(hash, expected))
                return Changed([$"{change.Path} was changed in the project while DNN Manager was reading it - your change is kept."], "project");
            before[change.Path] = hash;
        }
        if (withBackup.Count > 0 && keepBackups) reporter.Success($"Kept {withBackup.Count} files - a rollback puts them back.");

        foreach (var change in withBackup.Where(c => before.GetValueOrDefault(c.Path) is not { } h || !snapshots.Contains(h)))
            undo.CannotUndo($"{targets[change.Path]} (no copy of it was kept)");
        var touched = new HashSet<string>(DeployPath.Comparer);
        undo.Add($"Put back the files changed in the project ({changes.Count})", async ct2 =>
        {
            var failed = new List<string>();
            foreach (var change in changes.Where(c => touched.Contains(c.Path)))
            {
                try
                {
                    if (before.GetValueOrDefault(change.Path) is { } hash)
                    {
                        if (snapshots.Contains(hash)) await WriteLocalAsync(root, targets[change.Path], hash, ct2);
                    }
                    else
                    {
                        DeleteLocal(root, targets[change.Path]);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failed.Add($"{change.Path}: {ex.Message}");
                }
            }
            return failed.Count == 0 ? Result.Ok() : Result.Fail($"{failed.Count} not put back - {string.Join("; ", failed.Take(5))}");
        }, UndoLimit);

        reporter.Step($"Writing {changes.Count(c => c.Content is not null)} files and deleting {changes.Count(c => c.Content is null)} in {root}", "Transfer");
        var outcomes = new List<FileOutcome>();
        var done = 0;
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            var target = targets[change.Path];
            if (LocalStamp.Of(target) != stamps[change.Path])
                return Result<IReadOnlyList<FileOutcome>>.Fail(
                    $"{change.Path} was changed in the project while this operation ran - your change is kept, and the files written are put back.");
            touched.Add(change.Path);
            try
            {
                if (change.Content is null) DeleteLocal(root, target);
                else await WriteLocalAsync(root, target, change.Content, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Result<IReadOnlyList<FileOutcome>>.Fail($"{change.Path} could not be written in the project: {ex.Message} - the files written are put back.");
            }
            var action = change.Content is null ? FileAction.Deleted : existed[change.Path] ? FileAction.Modified : FileAction.Added;
            outcomes.Add(new FileOutcome(change.Path, action, before.GetValueOrDefault(change.Path), change.Content, null, 1));
            done++;
            progress?.Invoke(new TransferUpdate(change.Path, change.ContentSize, change.ContentSize, done, changes.Count, "Written"));
        }
        reporter.Success($"{changes.Count} files done in the project.");
        return Result<IReadOnlyList<FileOutcome>>.Ok(outcomes);
    }

    /// <summary>A local file's size and time - null when there is none.</summary>
    private sealed record LocalStamp(long Length, DateTime ModifiedUtc)
    {
        public static LocalStamp? Of(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? new LocalStamp(info.Length, info.LastWriteTimeUtc) : null;
        }
    }

    /// <summary>
    /// Writes the content kept as <paramref name="hash"/> to <paramref name="target"/>: beside it first, then moved into its
    /// place - a file is never left half written. Tried again while another program has it open.
    /// </summary>
    internal async Task WriteLocalAsync(string root, string target, string hash, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        SafePath.EnsureNoLink(root, folder);
        var part = Path.Combine(folder, $".{Path.GetFileName(target)}.{hash[..12]}{DeployPath.PartSuffix}");
        try
        {
            await using (var content = snapshots.OpenVerified(hash) ?? throw new InvalidDataException($"The content to write ({hash[..12]}) isn't kept any more."))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, ct);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    File.Move(part, target, overwrite: true);
                    return;
                }
                catch (IOException ex) when (FileInUse.Is(ex) && attempt < 10)
                {
                    await Task.Delay(500, ct);
                }
            }
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void DeleteLocal(string root, string target)
    {
        if (File.Exists(target))
        {
            File.SetAttributes(target, FileAttributes.Normal);
            File.Delete(target);
        }
        // The folders it leaves empty go too - up to the project's folder, never it.
        for (var folder = Path.GetDirectoryName(target); folder is not null && SafePath.IsInside(folder, root); folder = Path.GetDirectoryName(folder))
        {
            if (!Directory.Exists(folder) || SafePath.IsLink(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) break;
            Directory.Delete(folder);
        }
    }

    // ─── Fetching from the server ─────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="files"/> from the server into the snapshot store, in parallel: each must still be what the
    /// scan saw - its hash when known, otherwise its listing. Returns each one's hash and size.
    /// </summary>
    public async Task<Result<IReadOnlyDictionary<string, (string Hash, long Size, RemoteFile Listing)>>> FetchAsync(RemotePool pool,
        IReadOnlyList<(string Path, ExpectedState Expected)> files, IProgressReporter reporter, Action<TransferUpdate>? progress, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, (string, long, RemoteFile)>(DeployPath.Comparer);
        var problems = new ConcurrentBag<string>();
        var done = 0;
        await Parallel.ForEachAsync(files, Options(pool.Size, ct), async (file, token) =>
        {
            var fetched = await pool.RunAsync(async fs =>
            {
                var listing = await fs.StatAsync(file.Path, token);
                if (listing is null) return ((string, long, RemoteFile)?)null;
                await using var writer = snapshots.BeginAdd();
                // Resuming isn't needed here: a failed read starts again, the store keeps nothing half read.
                await fs.DownloadAsync(file.Path, writer.Stream, 0, read =>
                    progress?.Invoke(new TransferUpdate(file.Path, read, listing.Size, Volatile.Read(ref done), files.Count, "Fetching")), token);
                var hash = await writer.CompleteAsync(_ => true, token);
                return (hash, listing.Size, listing);
            }, token, Retrying(reporter, file.Path));
            if (fetched is not { } got)
            {
                problems.Add($"{file.Path} was deleted from the server since the scan.");
                return;
            }
            var expected = file.Expected;
            var same = expected.Hash is not null
                ? Hashing.Same(got.Item1, expected.Hash)
                : got.Item3.Size == expected.Size && got.Item3.ModifiedUtc == expected.ModifiedUtc;
            if (!same) problems.Add($"{file.Path} was changed on the server since the scan.");
            else result[file.Path] = got;
            var n = Interlocked.Increment(ref done);
            reporter.Progress($"Fetched {n} of {files.Count} files");
        });
        if (!problems.IsEmpty)
            return Result<IReadOnlyDictionary<string, (string, long, RemoteFile)>>.Fail(
                $"Scan again: {string.Join(Environment.NewLine, problems.OrderBy(p => p).Take(20))}");
        return Result<IReadOnlyDictionary<string, (string, long, RemoteFile)>>.Ok(result);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static ParallelOptions Options(int parallel, CancellationToken ct) => new() { MaxDegreeOfParallelism = parallel, CancellationToken = ct };

    private static Action<int, string> Retrying(IProgressReporter reporter, string path) =>
        (attempt, why) => reporter.Warn($"{path}: trying again (try {attempt}) - {why}");

    private static Result<IReadOnlyList<FileOutcome>> Changed(IEnumerable<string> problems, string where)
    {
        var list = problems.OrderBy(p => p).ToList();
        return Result<IReadOnlyList<FileOutcome>>.Fail(
            $"Nothing was changed: {list.Count} file(s) are no longer as the scan saw them in the {where}. Scan again to see what changed." +
            Environment.NewLine + string.Join(Environment.NewLine, list.Take(20)) + (list.Count > 20 ? $"{Environment.NewLine}… and {list.Count - 20} more." : ""));
    }
}
