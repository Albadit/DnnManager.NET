using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>The three versions of a file a diff or a merge needs - each null where there is none (or it isn't text).</summary>
public sealed record FileVersions(byte[]? Local, byte[]? Remote, byte[]? Base, string? RemoteHash)
{
    public bool IsBinary { get; init; }
    public bool TooLarge { get; init; }
}

/// <summary>
/// The versions of a file to show: the project's (read from its folder), the server's (from the snapshot store when the
/// scan kept it, otherwise read from the server - once per scan, and kept), and the baseline's when it was kept. Read only
/// - it changes nothing on either side.
/// </summary>
/// <remarks>
/// Fast as an editor's diff: one connection to the server stays open while files are looked at - a sign-in each (TLS,
/// Azure's limit on sign-ins) cost a second or more per file - and closes after a minute without use; what was read is
/// known by the scan and path, so it is read once; and <see cref="PrefetchAsync"/> reads the next files ahead.
/// </remarks>
public sealed class DeployFiles(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory) : IAsyncDisposable
{
    // The server's version of a file as read since its scan: (connection, scan time, path) -> the snapshot's hash.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, DateTime, string), string> _read = new();
    // One read from the server at a time, on the one connection kept.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRemoteFileSystem? _session;
    private DeployConnection? _sessionFor;
    private Timer? _idle;

    // How long the connection stays open without a file read.
    private static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(1);

    public async Task<FileVersions> VersionsAsync(SyncScan scan, SyncItem item, CancellationToken ct)
    {
        var tooLarge = (item.Local?.Size ?? 0) > TextContent.MaxBytes || (item.Remote?.Size ?? 0) > TextContent.MaxBytes;
        if (tooLarge) return new FileVersions(null, null, null, item.Remote?.Hash) { TooLarge = true };

        byte[]? local = null;
        if (item.Local is not null && DeployPath.Local(scan.LocalRoot, item.Path) is { } file && File.Exists(file))
            local = await File.ReadAllBytesAsync(file, ct);

        byte[]? remote = null;
        string? remoteHash = null;
        if (item.Remote is not null)
        {
            remoteHash = await RemoteHashAsync(scan, item, ct);
            remote = await snapshots.ReadAsync(remoteHash, TextContent.MaxBytes, ct);
        }

        var baseBytes = item.Base is { } b ? await snapshots.ReadAsync(b.Hash, TextContent.MaxBytes, ct) : null;
        var binary = (local is not null && TextContent.From(local) is null) || (remote is not null && TextContent.From(remote) is null);
        return new FileVersions(local, remote, baseBytes, remoteHash) { IsBinary = binary };
    }

    /// <summary>
    /// Reads the server's versions of <paramref name="items"/> ahead, one after the other, so they show at once when chosen -
    /// stopping between files when <paramref name="ct"/> is cancelled (a file half read would cost the connection).
    /// Failures are left for when the file is chosen.
    /// </summary>
    public async Task PrefetchAsync(SyncScan scan, IEnumerable<SyncItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            if (item.Remote is null || item.Remote.Size > TextContent.MaxBytes || IsRead(scan, item)) continue;
            try
            {
                await RemoteHashAsync(scan, item, CancellationToken.None);
            }
            catch (Exception ex) when (ex is RemoteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return;
            }
        }
    }

    private bool IsRead(SyncScan scan, SyncItem item) =>
        item.Remote?.Hash is { } hash && snapshots.Contains(hash) || _read.ContainsKey((scan.Connection.Id, scan.ScannedUtc, item.Path));

    /// <summary>The server's version of <paramref name="item"/> in the snapshot store: kept by the scan, read since, or read now.</summary>
    private async Task<string> RemoteHashAsync(SyncScan scan, SyncItem item, CancellationToken ct)
    {
        if (item.Remote?.Hash is { } known && snapshots.Contains(known)) return known;
        var key = (scan.Connection.Id, scan.ScannedUtc, item.Path);
        if (_read.TryGetValue(key, out var read) && snapshots.Contains(read)) return read;
        await _gate.WaitAsync(ct);
        try
        {
            if (_read.TryGetValue(key, out read) && snapshots.Contains(read)) return read;
            _idle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // Once begun, a read is finished even when the file is no longer wanted (another chosen meanwhile): stopped
            // halfway, the connection would be lost - and the next file need a sign-in again. Files are at most
            // TextContent.MaxBytes here.
            for (var attempt = 1; ; attempt++)
            {
                var fs = await SessionAsync(scan.Connection, CancellationToken.None);
                try
                {
                    await using var writer = snapshots.BeginAdd();
                    await fs.DownloadAsync(item.Path, writer.Stream, 0, null, CancellationToken.None);
                    var hash = await writer.CompleteAsync(_ => true, CancellationToken.None);
                    _read[key] = hash;
                    return hash;
                }
                catch (Exception ex)
                {
                    // The kept connection may have been closed by the server meanwhile: once more on a new one.
                    await CloseSessionAsync();
                    if (attempt >= 2 || ex is not (RemoteException { Transient: true } or IOException)) throw;
                }
            }
        }
        finally
        {
            _idle ??= new Timer(_ => _ = CloseIdleAsync());
            _idle.Change(IdleClose, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>The connection kept for <paramref name="connection"/> - made anew for another server, or another scan's settings.</summary>
    private async Task<IRemoteFileSystem> SessionAsync(DeployConnection connection, CancellationToken ct)
    {
        if (_session is not null && ReferenceEquals(_sessionFor, connection)) return _session;
        await CloseSessionAsync();
        var fs = factory.Create(connection, store.ReadSecrets(connection.Id));
        try
        {
            await fs.ConnectAsync(ct);
        }
        catch
        {
            await fs.DisposeAsync();
            throw;
        }
        _session = fs;
        _sessionFor = connection;
        return fs;
    }

    private async Task CloseSessionAsync()
    {
        var fs = _session;
        _session = null;
        _sessionFor = null;
        if (fs is null) return;
        try { await fs.DisposeAsync(); }
        catch (Exception) { /* closing a connection the server may have dropped */ }
    }

    private async Task CloseIdleAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try { await CloseSessionAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _idle?.Dispose();
        await _gate.WaitAsync();
        try { await CloseSessionAsync(); }
        finally { _gate.Release(); }
    }

    /// <summary>The content kept as <paramref name="hash"/> - a version in the history - or null when it isn't kept.</summary>
    public Task<byte[]?> KeptAsync(string? hash, CancellationToken ct) =>
        hash is null ? Task.FromResult<byte[]?>(null) : snapshots.ReadAsync(hash, TextContent.MaxBytes, ct);

    public bool IsKept(string? hash) => hash is not null && snapshots.Contains(hash);

    public IReadOnlyList<DeployOperation> History(Guid connectionId) => store.Operations(connectionId);

    public IReadOnlyList<DeployOperationFile> Files(long operationId) => store.Files(operationId);
}
