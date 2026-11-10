using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>A push: the files of <paramref name="Scan"/> to send to its server (each one to push), with the deployment's message.</summary>
/// <param name="AllowSensitive">The user agreed to send files that look like secrets or configuration (web.config, keys).</param>
public sealed record PushRequest(SyncScan Scan, IReadOnlyList<string> Paths, string Message, bool AllowSensitive = false,
    Action<TransferUpdate>? Progress = null);

/// <summary>A pull: the files of <paramref name="Scan"/> to fetch from its server (each one to pull).</summary>
/// <param name="ReviewFolder">A new folder to fetch them into instead of the project - to look at first; nothing else changes.</param>
public sealed record PullRequest(SyncScan Scan, IReadOnlyList<string> Paths, string Message, bool AllowSensitive = false,
    string? ReviewFolder = null, Action<TransferUpdate>? Progress = null);

/// <summary>How a conflict is resolved.</summary>
public enum ConflictChoice
{
    /// <summary>The project's version stays - and is the one to push (the server's change is set aside).</summary>
    KeepLocal,
    /// <summary>The server's version replaces the project's (which is kept, to roll back to).</summary>
    TakeRemote,
    /// <summary>A merge of both, written in the project - and the one to push.</summary>
    Merged
}

/// <param name="Merged">For <see cref="ConflictChoice.Merged"/>: the merged file's bytes.</param>
/// <param name="RemoteHash">The server's content the merge was made from - when it changed since, the merge is refused.</param>
public sealed record ResolveRequest(SyncScan Scan, string Path, ConflictChoice Choice, byte[]? Merged = null, string? RemoteHash = null);

public sealed record RollbackRequest(Guid ConnectionId, long OperationId, string LocalRoot, string Message, Action<TransferUpdate>? Progress = null);

/// <summary>Puts one file of the project back as it was in an operation of the history (its content <paramref name="Hash"/>).</summary>
public sealed record RestoreVersionRequest(Guid ConnectionId, string LocalRoot, string Path, string Hash, long FromOperation);

/// <summary>
/// What every operation of a connection shares: the connection as saved now, its pool, the remote lock, and its record
/// in the history - begun before anything changes, ended however it ends (a cancel or an exception too).
/// </summary>
public abstract class DeployOperationBase(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, SyncEngine engine,
    OperationUndo undo, Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
{
    protected IDeployStore Store { get; } = store;
    protected ISnapshotStore Snapshots { get; } = snapshots;
    protected IRemoteFileSystemFactory Factory { get; } = factory;
    protected SyncEngine Engine { get; } = engine;
    protected OperationUndo Undo { get; } = undo;

    /// <summary>
    /// How long the history keeps what its operations replaced and wrote - the snapshot store forgets the rest (Settings →
    /// Deploy; 90 days by default, null: for good).
    /// </summary>
    protected TimeSpan? KeepSnapshots => (options?.Value.Deploy.KeepVersionsDays ?? 90) is var days && days > 0 ? TimeSpan.FromDays(days) : null;

    protected async Task<Result<DeployOperation>> RunAsync(DeployConnection connection, DeployOperationKind kind, string message, bool needsLock,
        IProgressReporter reporter, CancellationToken ct, Func<RemotePool, long, Task<Result<(IReadOnlyList<DeployOperationFile> Files, string? Review)>>> work,
        long? rollbackOf = null)
    {
        var secrets = Store.ReadSecrets(connection.Id);
        await using var pool = new RemotePool(Factory, connection, secrets);
        DeployLock.Held? held = null;
        if (needsLock)
        {
            reporter.Step($"Locking {connection.RemoteRoot} on {connection.Host} for this {kind.ToString().ToLowerInvariant()}", "Lock");
            var locked = await DeployLock.AcquireAsync(pool, Factory, secrets, Undo, ct);
            if (!locked.Success) return Result<DeployOperation>.Fail(locked.Error!);
            held = locked.Value;
            reporter.Success("Locked - no other DNN Manager changes it until this is done.");
        }

        var operation = new DeployOperation
        {
            ConnectionId = connection.Id, Site = connection.Site, ConnectionName = connection.Name, Environment = connection.Environment,
            Kind = kind, StartedUtc = DateTime.UtcNow, User = $"{Environment.UserDomainName}\\{Environment.UserName}", Message = message.Trim(),
            Status = DeployOperationStatus.Running, RollbackOf = rollbackOf
        };
        var id = Store.Begin(operation);
        try
        {
            var result = await work(pool, id);
            if (!result.Success)
            {
                Store.End(id, DeployOperationStatus.Failed, result.Error, pool.Retries);
                return Result<DeployOperation>.Fail(result.Error!);
            }
            // Done, and the baseline says so: from here on nothing may put the files back - a failure to record it in the
            // history is said, not undone (undoing would leave the files and the baseline disagreeing).
            Undo.Keep();
            reporter.Step("Recording it in the history", "Record");
            try
            {
                Store.AddFiles(id, result.Value.Files);
                if (result.Value.Review is { } folder) Store.SetReviewFolder(id, folder);
                Store.End(id, DeployOperationStatus.Completed, null, pool.Retries);
                Store.RecordSync(connection.Id, DateTime.UtcNow);
                if (rollbackOf is { } of) Store.MarkRolledBack(of, id);
                reporter.Success($"Recorded as #{id}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
            {
                reporter.Warn($"The files are done, but recording it in the history failed: {ex.Message}");
            }
            if (held is not null)
            {
                await held.DisposeAsync();
                await held.ReleaseAsync();
            }
            Prune(reporter);
            return Result<DeployOperation>.Ok(Store.Operation(id) ?? operation with { Id = id, Status = DeployOperationStatus.Completed });
        }
        catch (OperationCanceledException)
        {
            Store.End(id, DeployOperationStatus.Cancelled, "Cancelled - what it changed is put back.", pool.Retries);
            throw;
        }
        catch (Exception ex)
        {
            Store.End(id, DeployOperationStatus.Failed, ex.Message, pool.Retries);
            throw;
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
        }
    }

    /// <summary>What no operation of the last <see cref="KeepSnapshots"/> or baseline refers to is forgotten.</summary>
    private void Prune(IProgressReporter reporter)
    {
        if (KeepSnapshots is not { } keep) return;
        try
        {
            var forgotten = Snapshots.Prune(Store.ReferencedHashes(DateTime.UtcNow - keep), TimeSpan.FromDays(1));
            if (forgotten > 0) reporter.Info($"Forgot {forgotten} kept file versions no operation of the last {keep.Days} days needs.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException) { /* next time */ }
    }

    protected static Result<SyncItem[]> Pick(SyncScan scan, IReadOnlyList<string> paths, SyncDirection direction, bool allowSensitive)
    {
        var items = new List<SyncItem>();
        foreach (var path in paths.Distinct(DeployPath.Comparer))
        {
            if (scan.Find(path) is not { } item) return Result<SyncItem[]>.Fail($"{path} isn't in the scan - scan again.");
            if (item.Direction == SyncDirection.Conflict)
                return Result<SyncItem[]>.Fail($"{path} changed in both places - resolve the conflict first (Conflicts).");
            if (item.Direction != direction)
                return Result<SyncItem[]>.Fail($"{path} has nothing to {direction.ToString().ToLowerInvariant()} ({item.Describe().ToLowerInvariant()}).");
            items.Add(item);
        }
        if (items.Count == 0) return Result<SyncItem[]>.Fail($"No files to {direction.ToString().ToLowerInvariant()} were chosen.");
        var sensitive = items.Where(i => i.Sensitive).Select(i => i.Path).ToList();
        if (sensitive.Count > 0 && !allowSensitive)
            return Result<SyncItem[]>.Fail($"These look like secrets or one environment's configuration, and weren't agreed to: {string.Join(", ", sensitive)}.");
        return Result<SyncItem[]>.Ok([.. items]);
    }

    /// <summary>The connection as saved now - its settings may have changed since the scan.</summary>
    protected Result<DeployConnection> Current(Guid id) =>
        Store.Connection(id) is { } connection ? Result<DeployConnection>.Ok(connection) : Result<DeployConnection>.Fail("The connection was removed.");

    protected static ExpectedState RemoteExpected(SyncScan scan, SyncItem item) =>
        item.Remote is null ? ExpectedState.Absent
            : new ExpectedState(true, item.Remote.Hash, scan.Remote.GetValueOrDefault(item.Path)?.Size ?? item.Remote.Size,
                scan.Remote.GetValueOrDefault(item.Path)?.ModifiedUtc ?? item.Remote.ModifiedUtc);
}

/// <summary>
/// Push: sends the chosen files of a scan to the server - only those, only when the server still has what the scan saw.
/// The project's files are kept in the snapshot store first, and what is sent is that copy, checked by its hash: what
/// arrives is exactly what was reviewed, even if the file is edited meanwhile. What the server had is kept too, to roll
/// back to. Then the baseline of each file is what was sent.
/// </summary>
public sealed class PushChangesUseCase(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, SyncEngine engine,
    OperationUndo undo, Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
    : DeployOperationBase(store, snapshots, factory, engine, undo, options)
{
    public async Task<Result<DeployOperation>> ExecuteAsync(PushRequest request, IProgressReporter reporter, CancellationToken ct)
    {
        if (Current(request.Scan.Connection.Id) is not { Success: true, Value: { } connection }) return Result<DeployOperation>.Fail("The connection was removed.");
        var picked = Pick(request.Scan, request.Paths, SyncDirection.Push, request.AllowSensitive);
        if (!picked.Success) return Result<DeployOperation>.Fail(picked.Error!);
        var items = picked.Value!;
        reporter.Plan("Prepare", "Lock", "Check", "Back up", "Transfer", "Record");
        reporter.Context(connection.Describe());

        reporter.Step($"Taking the {items.Length} files to send as they are now", "Prepare");
        var changes = new List<FileChange>();
        var problems = new List<string>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Status == SyncStatus.LocalDeleted)
            {
                // Really gone - not a file the scan couldn't read, which would delete the server's copy.
                if (DeployPath.Local(request.Scan.LocalRoot, item.Path) is not { } gone || File.Exists(gone))
                {
                    problems.Add($"{item.Path} is in the project again.");
                    continue;
                }
                changes.Add(new FileChange(item.Path, null, 0, RemoteExpected(request.Scan, item)));
                continue;
            }
            if (DeployPath.Local(request.Scan.LocalRoot, item.Path) is not { } file || !File.Exists(file))
            {
                problems.Add($"{item.Path} isn't in the project any more.");
                continue;
            }
            var hash = await Snapshots.AddFileAsync(file, ct);
            if (!Hashing.Same(hash, item.Local!.Hash)) problems.Add($"{item.Path} was changed in the project since the scan.");
            changes.Add(new FileChange(item.Path, hash, Snapshots.SizeOf(hash) ?? item.Local.Size, RemoteExpected(request.Scan, item)));
        }
        if (problems.Count > 0)
            return Result<DeployOperation>.Fail($"Nothing was sent - scan again to review what changed:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
        reporter.Success($"{changes.Count(c => c.Content is not null)} to send, {changes.Count(c => c.Content is null)} to delete on the server.");

        return await RunAsync(connection, DeployOperationKind.Push, request.Message, true, reporter, ct, async (pool, _) =>
        {
            var applied = await Engine.ApplyRemoteAsync(pool, changes, reporter, request.Progress, ct);
            if (!applied.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(applied.Error!);
            var byPath = items.ToDictionary(i => i.Path, DeployPath.Comparer);
            var set = new List<BaselineEntry>();
            var remove = new List<string>();
            foreach (var outcome in applied.Value!)
            {
                if (outcome.After is { } after && outcome.Listing is { } listing)
                    set.Add(new BaselineEntry(outcome.Path, after, listing.Size, listing.Size, listing.ModifiedUtc));
                else remove.Add(outcome.Path);
            }
            Store.UpdateBaseline(connection.Id, set, remove);
            return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Ok((Rows(applied.Value!, SyncSide.Remote, byPath), null));
        });
    }

    internal static IReadOnlyList<DeployOperationFile> Rows(IReadOnlyList<FileOutcome> outcomes, SyncSide side, IReadOnlyDictionary<string, SyncItem> items) =>
    [
        .. outcomes.Select(o => new DeployOperationFile
        {
            Path = o.Path, Side = side, Action = o.Action, BeforeHash = o.Before, AfterHash = o.After,
            BaseBefore = items.GetValueOrDefault(o.Path)?.Base?.Hash, BaseBeforeSize = items.GetValueOrDefault(o.Path)?.Base?.Size,
            Attempts = o.Attempts
        })
    ];
}

/// <summary>
/// Pull: fetches the chosen files of a scan from the server into the project - only those, and only where the project
/// still has what the scan saw: a file the user changed since is never overwritten. What the project had is kept to roll
/// back to. Or into a review folder of its own, where nothing is overwritten and nothing else changes.
/// </summary>
public sealed class PullChangesUseCase(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, SyncEngine engine,
    OperationUndo undo, Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
    : DeployOperationBase(store, snapshots, factory, engine, undo, options)
{
    public async Task<Result<DeployOperation>> ExecuteAsync(PullRequest request, IProgressReporter reporter, CancellationToken ct)
    {
        if (Current(request.Scan.Connection.Id) is not { Success: true, Value: { } connection }) return Result<DeployOperation>.Fail("The connection was removed.");
        var picked = Pick(request.Scan, request.Paths, SyncDirection.Pull, request.AllowSensitive);
        if (!picked.Success) return Result<DeployOperation>.Fail(picked.Error!);
        var items = picked.Value!;
        var review = request.ReviewFolder;
        if (review is not null && Directory.Exists(review) && Directory.EnumerateFileSystemEntries(review).Any())
            return Result<DeployOperation>.Fail($"The review folder {review} isn't empty - choose a new one.");
        reporter.Plan("Lock", "Fetch", "Check", "Back up", "Transfer", "Record");
        reporter.Context(connection.Describe());

        return await RunAsync(connection, DeployOperationKind.Pull, request.Message, true, reporter, ct, async (pool, _) =>
        {
            var toFetch = items.Where(i => i.Status != SyncStatus.RemoteDeleted).Select(i => (i.Path, RemoteExpected(request.Scan, i))).ToList();
            reporter.Step($"Fetching {toFetch.Count} files from the server", "Fetch");
            var fetched = await Engine.FetchAsync(pool, toFetch, reporter, request.Progress, ct);
            if (!fetched.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(fetched.Error!);
            reporter.Success($"Fetched {toFetch.Count} files ({SyncScanner.Bytes(fetched.Value!.Values.Sum(f => f.Size))}).");

            var target = review ?? request.Scan.LocalRoot;
            if (review is not null) Directory.CreateDirectory(review);
            var changes = items
                .Where(i => review is null || i.Status != SyncStatus.RemoteDeleted)
                .Select(i => i.Status == SyncStatus.RemoteDeleted
                    ? new FileChange(i.Path, null, 0, ExpectedState.Of(i.Local))
                    : new FileChange(i.Path, fetched.Value[i.Path].Hash, fetched.Value[i.Path].Size, review is null ? ExpectedState.Of(i.Local) : ExpectedState.Absent))
                .ToList();
            var applied = await Engine.ApplyLocalAsync(target, review is null && connection.KeepBackups, changes, reporter, request.Progress, ct);
            if (!applied.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(applied.Error!);

            if (review is not null)
            {
                var deleted = items.Where(i => i.Status == SyncStatus.RemoteDeleted).Select(i => i.Path).ToList();
                if (deleted.Count > 0)
                    await File.WriteAllLinesAsync(Path.Combine(review, "deleted-on-server.txt"),
                        ["Deleted from the server, still in the project:", .. deleted], ct);
                reporter.Info($"In {review} - the project and the baseline weren't changed.");
                return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Ok(
                    (PushChangesUseCase.Rows(applied.Value!, SyncSide.Local, items.ToDictionary(i => i.Path, DeployPath.Comparer)), review));
            }

            var set = new List<BaselineEntry>();
            var remove = new List<string>();
            foreach (var item in items)
            {
                if (item.Status == SyncStatus.RemoteDeleted) remove.Add(item.Path);
                else
                {
                    var (hash, size, listing) = fetched.Value[item.Path];
                    set.Add(new BaselineEntry(item.Path, hash, size, listing.Size, listing.ModifiedUtc));
                }
            }
            Store.UpdateBaseline(connection.Id, set, remove);
            return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Ok(
                (PushChangesUseCase.Rows(applied.Value!, SyncSide.Local, items.ToDictionary(i => i.Path, DeployPath.Comparer)), null));
        });
    }
}

/// <summary>
/// Resolves a conflict - a file changed in both places - in the project, never on the server: keep the project's version,
/// take the server's, or write a merge of both. The baseline becomes the server's version, so what is left is an ordinary
/// change to push (the project's or the merge) - or nothing at all (the server's taken). The server changes only with a push.
/// </summary>
public sealed class ResolveConflictUseCase(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, SyncEngine engine,
    OperationUndo undo, Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
    : DeployOperationBase(store, snapshots, factory, engine, undo, options)
{
    public async Task<Result<DeployOperation>> ExecuteAsync(ResolveRequest request, IProgressReporter reporter, CancellationToken ct)
    {
        if (Current(request.Scan.Connection.Id) is not { Success: true, Value: { } connection }) return Result<DeployOperation>.Fail("The connection was removed.");
        if (request.Scan.Find(request.Path) is not { Status: SyncStatus.Conflict } item)
            return Result<DeployOperation>.Fail($"{request.Path} has no conflict any more - scan again.");
        var (choice, conflict) = (request.Choice, item.Describe());
        reporter.Context(connection.Describe());
        var message = choice switch
        {
            ConflictChoice.KeepLocal => $"Kept the project's {item.Path}",
            ConflictChoice.TakeRemote => $"Took the server's {item.Path}",
            _ => $"Merged {item.Path}"
        };

        return await RunAsync(connection, DeployOperationKind.Resolve, message, choice != ConflictChoice.KeepLocal, reporter, ct, async (pool, _) =>
        {
            // The server's version as it is now: from the scan, or read when the scan only knew it differs by its size.
            string? remoteHash = null;
            RemoteFile? listing = null;
            if (item.Remote is not null)
            {
                reporter.Step("Reading the server's version", "Fetch");
                var fetched = await Engine.FetchAsync(pool, [(item.Path, RemoteExpected(request.Scan, item))], reporter, null, ct);
                if (!fetched.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(fetched.Error!);
                (remoteHash, _, listing) = fetched.Value![item.Path];
                if (request.RemoteHash is not null && !Hashing.Same(request.RemoteHash, remoteHash))
                    return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail($"{item.Path} changed on the server while you merged it - merge again.");
            }

            FileChange? change = choice switch
            {
                ConflictChoice.TakeRemote when remoteHash is not null => new FileChange(item.Path, remoteHash, listing!.Size, ExpectedState.Of(item.Local)),
                ConflictChoice.TakeRemote => new FileChange(item.Path, null, 0, ExpectedState.Of(item.Local)),
                ConflictChoice.Merged => new FileChange(item.Path, await Snapshots.AddAsync(new MemoryStream(request.Merged ?? []), ct),
                    request.Merged?.LongLength ?? 0, ExpectedState.Of(item.Local)),
                _ => null
            };
            if (choice == ConflictChoice.Merged && request.Merged is null)
                return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail("There is no merge to write.");

            IReadOnlyList<FileOutcome> outcomes = [];
            if (change is not null)
            {
                var applied = await Engine.ApplyLocalAsync(request.Scan.LocalRoot, connection.KeepBackups, [change], reporter, null, ct);
                if (!applied.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(applied.Error!);
                outcomes = applied.Value!;
            }
            else if (item.Local is not null && !File.Exists(DeployPath.Local(request.Scan.LocalRoot, item.Path) ?? ""))
            {
                return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail($"{item.Path} isn't in the project any more - scan again.");
            }

            // The server's version is the baseline now: what differs from it in the project is the change to push.
            if (remoteHash is not null)
                Store.UpdateBaseline(connection.Id, [new BaselineEntry(item.Path, remoteHash, listing!.Size, listing.Size, listing.ModifiedUtc)], []);
            else
                Store.UpdateBaseline(connection.Id, [], [item.Path]);
            reporter.Success(choice switch
            {
                ConflictChoice.KeepLocal => $"{item.Path}: the project's version stays - push it to replace the server's.",
                ConflictChoice.TakeRemote => $"{item.Path}: the project has the server's version now.",
                _ => $"{item.Path}: the merge is in the project - push it to send it to the server."
            });

            var outcome = outcomes.FirstOrDefault();
            var row = new DeployOperationFile
            {
                Path = item.Path, Side = SyncSide.Local,
                Action = outcome?.Action ?? FileAction.Modified,
                BeforeHash = outcome?.Before ?? item.Local?.Hash, AfterHash = outcome is null ? item.Local?.Hash : outcome.After,
                BaseBefore = item.Base?.Hash, BaseBeforeSize = item.Base?.Size, Conflict = conflict, Resolution = message
            };
            return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Ok(([row], null));
        });
    }
}

/// <summary>
/// Rolls back an operation of the history: every file it changed gets back the content it had before (a file it added
/// is deleted), from the snapshot store - on the server for a push, in the project for a pull or a resolution - and its
/// baseline goes back too. Only when each file is still as the operation left it: a file changed since is someone's
/// newer work, and stops the rollback before anything changes. The rollback is an operation of the history itself.
/// </summary>
public sealed class RollbackOperationUseCase(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, SyncEngine engine,
    OperationUndo undo, Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
    : DeployOperationBase(store, snapshots, factory, engine, undo, options)
{
    public async Task<Result<DeployOperation>> ExecuteAsync(RollbackRequest request, IProgressReporter reporter, CancellationToken ct)
    {
        if (Current(request.ConnectionId) is not { Success: true, Value: { } connection }) return Result<DeployOperation>.Fail("The connection was removed.");
        if (Store.Operation(request.OperationId) is not { } operation || operation.ConnectionId != connection.Id)
            return Result<DeployOperation>.Fail($"Operation #{request.OperationId} isn't in this connection's history.");
        if (operation.Status != DeployOperationStatus.Completed) return Result<DeployOperation>.Fail($"#{operation.Id} didn't complete - there is nothing of it to roll back.");
        if (operation.RolledBackBy is { } by) return Result<DeployOperation>.Fail($"#{operation.Id} was rolled back already, by #{by}.");
        if (operation.ReviewFolder is not null) return Result<DeployOperation>.Fail($"#{operation.Id} fetched into a review folder - it changed nothing to roll back.");

        var files = Store.Files(operation.Id).Where(f => f.Done).ToList();
        var changed = files.Where(f => !Hashing.Same(f.BeforeHash, f.AfterHash) || (f.BeforeHash is null) != (f.AfterHash is null)).ToList();
        // Only what the operation changed is put back - and only that file's baseline: a baseline reset alone, with no check
        // of the file, could make a later change look like none (or a conflict).
        if (changed.Count == 0) return Result<DeployOperation>.Fail($"#{operation.Id} changed no file - there is nothing of it to put back.");
        var lost = changed.Where(f => f.BeforeHash is { } h && !Snapshots.Contains(h)).Select(f => f.Path).ToList();
        if (lost.Count > 0)
            return Result<DeployOperation>.Fail($"#{operation.Id} can't be rolled back: no copy was kept of what it replaced in {string.Join(", ", lost.Take(10))}" +
                                                 (lost.Count > 10 ? $" and {lost.Count - 10} more" : "") + ".");
        reporter.Plan("Lock", "Check", "Back up", "Transfer", "Record");
        reporter.Context(connection.Describe());
        var message = request.Message.Trim().Length > 0 ? request.Message : $"Roll back #{operation.Id}: {operation.Message}";

        return await RunAsync(connection, DeployOperationKind.Rollback, message, true, reporter, ct, async (pool, _) =>
        {
            FileChange Back(DeployOperationFile f) => new(f.Path, f.BeforeHash, f.BeforeHash is { } h ? Snapshots.SizeOf(h) ?? 0 : 0,
                f.AfterHash is null ? ExpectedState.Absent : new ExpectedState(true, f.AfterHash));
            var remote = changed.Where(f => f.Side == SyncSide.Remote).Select(Back).ToList();
            var local = changed.Where(f => f.Side == SyncSide.Local).Select(Back).ToList();
            var outcomes = new List<(FileOutcome Outcome, SyncSide Side)>();
            if (remote.Count > 0)
            {
                var applied = await Engine.ApplyRemoteAsync(pool, remote, reporter, request.Progress, ct);
                if (!applied.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(applied.Error!);
                outcomes.AddRange(applied.Value!.Select(o => (o, SyncSide.Remote)));
            }
            if (local.Count > 0)
            {
                var applied = await Engine.ApplyLocalAsync(request.LocalRoot, connection.KeepBackups, local, reporter, request.Progress, ct);
                if (!applied.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(applied.Error!);
                outcomes.AddRange(applied.Value!.Select(o => (o, SyncSide.Local)));
            }

            // Each file's baseline as it was before the operation. The server's listing goes with it only when the server
            // holds that content now - otherwise the next scan reads the file to know.
            var baseline = Store.Baseline(connection.Id);
            var set = new List<BaselineEntry>();
            var remove = new List<string>();
            var rows = new List<DeployOperationFile>();
            foreach (var file in changed)
            {
                var outcome = outcomes.FirstOrDefault(o => DeployPath.Comparer.Equals(o.Outcome.Path, file.Path)).Outcome;
                if (file.BaseBefore is { } baseHash)
                {
                    var size = file.BaseBeforeSize ?? Snapshots.SizeOf(baseHash);
                    var listing = file.Side == SyncSide.Remote && Hashing.Same(file.BeforeHash, baseHash) ? outcome?.Listing : null;
                    var current = baseline.GetValueOrDefault(file.Path);
                    if (file.Side == SyncSide.Local && current is not null && Hashing.Same(current.Hash, baseHash)) listing = null;
                    if (size is { } known) set.Add(new BaselineEntry(file.Path, baseHash, known, listing?.Size, listing?.ModifiedUtc));
                    else remove.Add(file.Path);
                }
                else
                {
                    remove.Add(file.Path);
                }
                if (outcome is not null)
                {
                    var current = baseline.GetValueOrDefault(file.Path);
                    rows.Add(new DeployOperationFile
                    {
                        Path = file.Path, Side = file.Side, Action = outcome.Action, BeforeHash = outcome.Before, AfterHash = outcome.After,
                        BaseBefore = current?.Hash, BaseBeforeSize = current?.Size, Attempts = outcome.Attempts
                    });
                }
            }
            Store.UpdateBaseline(connection.Id, set, remove);
            reporter.Success($"#{operation.Id} rolled back - {rows.Count} files put back.");
            return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Ok((rows, null));
        }, operation.Id);
    }
}

/// <summary>
/// Writes one earlier version of a file - kept by an operation of the history - into the project, keeping the project's
/// current version first. Nothing on the server changes: the restored version is a change to push like any other.
/// </summary>
public sealed class RestoreVersionUseCase(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory, SyncEngine engine,
    OperationUndo undo, Microsoft.Extensions.Options.IOptions<Configuration.AppOptions>? options = null)
    : DeployOperationBase(store, snapshots, factory, engine, undo, options)
{
    public async Task<Result<DeployOperation>> ExecuteAsync(RestoreVersionRequest request, IProgressReporter reporter, CancellationToken ct)
    {
        if (Current(request.ConnectionId) is not { Success: true, Value: { } connection }) return Result<DeployOperation>.Fail("The connection was removed.");
        if (!Snapshots.Contains(request.Hash)) return Result<DeployOperation>.Fail("That version isn't kept any more.");
        if (DeployPath.Local(request.LocalRoot, request.Path) is not { } file)
            return Result<DeployOperation>.Fail($"{request.Path} can't be written in {request.LocalRoot}.");
        var exists = File.Exists(file);
        var current = exists ? await Hashing.OfFileAsync(file, ct) : null;
        if (Hashing.Same(current, request.Hash)) return Result<DeployOperation>.Fail($"The project's {request.Path} is that version already.");
        reporter.Context(connection.Describe());
        var message = $"Restored {request.Path} as of #{request.FromOperation}";
        return await RunAsync(connection, DeployOperationKind.Rollback, message, false, reporter, ct, async (_, _) =>
        {
            var applied = await Engine.ApplyLocalAsync(request.LocalRoot, true,
                [new FileChange(request.Path, request.Hash, Snapshots.SizeOf(request.Hash) ?? 0, exists ? new ExpectedState(true, current) : ExpectedState.Absent)],
                reporter, null, ct);
            if (!applied.Success) return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Fail(applied.Error!);
            var outcome = applied.Value![0];
            var baseline = Store.Baseline(connection.Id).GetValueOrDefault(request.Path);
            return Result<(IReadOnlyList<DeployOperationFile>, string?)>.Ok(([new DeployOperationFile
            {
                Path = request.Path, Side = SyncSide.Local, Action = outcome.Action, BeforeHash = outcome.Before, AfterHash = outcome.After,
                BaseBefore = baseline?.Hash, BaseBeforeSize = baseline?.Size, Resolution = message
            }], null));
        });
    }
}
