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
/// scan kept it, otherwise read from the server - once, and kept), and the baseline's when it was kept. Read only - it
/// changes nothing on either side.
/// </summary>
public sealed class DeployFiles(IDeployStore store, ISnapshotStore snapshots, IRemoteFileSystemFactory factory)
{
    public async Task<FileVersions> VersionsAsync(SyncScan scan, SyncItem item, CancellationToken ct)
    {
        var tooLarge = (item.Local?.Size ?? 0) > TextContent.MaxBytes || (item.Remote?.Size ?? 0) > TextContent.MaxBytes;
        if (tooLarge) return new FileVersions(null, null, null, item.Remote?.Hash) { TooLarge = true };

        byte[]? local = null;
        if (item.Local is not null && DeployPath.Local(scan.LocalRoot, item.Path) is { } file && File.Exists(file))
            local = await File.ReadAllBytesAsync(file, ct);

        byte[]? remote = null;
        var remoteHash = item.Remote?.Hash;
        if (item.Remote is not null)
        {
            if (remoteHash is not null) remote = await snapshots.ReadAsync(remoteHash, TextContent.MaxBytes, ct);
            if (remote is null)
            {
                await using var fs = factory.Create(scan.Connection, store.ReadSecrets(scan.Connection.Id));
                await fs.ConnectAsync(ct);
                await using var writer = snapshots.BeginAdd();
                await fs.DownloadAsync(item.Path, writer.Stream, 0, null, ct);
                remoteHash = await writer.CompleteAsync(_ => true, ct);
                remote = await snapshots.ReadAsync(remoteHash, TextContent.MaxBytes, ct);
            }
        }

        var baseBytes = item.Base is { } b ? await snapshots.ReadAsync(b.Hash, TextContent.MaxBytes, ct) : null;
        var binary = (local is not null && TextContent.From(local) is null) || (remote is not null && TextContent.From(remote) is null);
        return new FileVersions(local, remote, baseBytes, remoteHash) { IsBinary = binary };
    }

    /// <summary>The content kept as <paramref name="hash"/> - a version in the history - or null when it isn't kept.</summary>
    public Task<byte[]?> KeptAsync(string? hash, CancellationToken ct) =>
        hash is null ? Task.FromResult<byte[]?>(null) : snapshots.ReadAsync(hash, TextContent.MaxBytes, ct);

    public bool IsKept(string? hash) => hash is not null && snapshots.Contains(hash);

    public IReadOnlyList<DeployOperation> History(Guid connectionId) => store.Operations(connectionId);

    public IReadOnlyList<DeployOperationFile> Files(long operationId) => store.Files(operationId);
}
