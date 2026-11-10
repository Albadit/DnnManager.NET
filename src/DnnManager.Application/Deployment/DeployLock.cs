using System.Globalization;
using System.Text;
using DnnManager.Application.UseCases;
using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>
/// The lock a push, pull or rollback holds on the server's folder - a file, <see cref="DeployPath.LockFileName"/>, made
/// only when there is none (atomically on SFTP) - so two DNN Managers, on two PCs, never change the same folder at the
/// same time. While the operation runs, a heartbeat rewrites it every <see cref="Heartbeat"/>; one not rewritten for
/// <see cref="Stale"/> was left by a DNN Manager that ended without letting go, and is taken over. Its age is measured
/// with the server's clock (its time against a file made there just now), never this PC's against another's.
/// </summary>
public static class DeployLock
{
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);

    /// <summary>Who is running this operation: the Windows account and the PC.</summary>
    public static string Owner => $"{Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName}";

    /// <summary>
    /// Takes the lock, and notes in <paramref name="undo"/> that it is let go of when the operation is undone - the undo's
    /// last step, after what it changed is put back, on a connection of its own. The caller lets go of it itself when it
    /// succeeds (<see cref="Held.ReleaseAsync"/>), and disposes it in any case (which stops the heartbeat).
    /// </summary>
    public static async Task<Result<Held>> AcquireAsync(RemotePool pool, IRemoteFileSystemFactory factory, DeploySecrets secrets,
        OperationUndo undo, CancellationToken ct)
    {
        var content = $"{Owner}\n{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}\n{Guid.NewGuid():N}\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var connection = pool.Connection;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await pool.RunAsync(fs => fs.TryCreateAsync(DeployPath.LockFileName, bytes, ct), ct))
            {
                if (attempt > 0)
                {
                    // Taken over: another DNN Manager may have taken over the same stale lock in the same moment - whoever's
                    // content is there a moment later has it.
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    if (await ReadAsync(pool, ct) != content)
                        return Result<Held>.Fail("Another DNN Manager took over the server's lock at the same moment - try again in a minute.");
                }
                undo.Add("Let go of the lock on the server's folder", async token =>
                {
                    await using var own = new RemotePool(factory, connection, secrets);
                    await ReleaseAsync(own, content, token);
                    return Result.Ok();
                });
                return Result<Held>.Ok(new Held(pool, factory, secrets, content));
            }
            var held = await ReadAsync(pool, ct);
            if (held is null) continue;
            var age = await AgeAsync(pool, ct);
            var lines = held.Split('\n');
            var who = lines[0].Length > 0 ? lines[0] : "Another DNN Manager";
            if (age is null || age < Stale)
                return Result<Held>.Fail(
                    $"{who} is deploying to this folder{(age is { } a ? $" (its lock was renewed {(int)a.TotalMinutes} min ago)" : "")} - wait until it is done. " +
                    $"If nothing is running, delete {DeployPath.LockFileName} in the server's folder.");
            // Left behind by an operation that ended without letting go: taken over.
            await pool.RunAsync(fs => fs.DeleteAsync(DeployPath.LockFileName, ct), ct);
        }
        return Result<Held>.Fail($"The lock on the server's folder ({DeployPath.LockFileName}) couldn't be taken.");
    }

    /// <summary>
    /// How long ago the lock was last written, by the server's clock: its time against a file made there now - null when
    /// the server doesn't say a time.
    /// </summary>
    private static Task<TimeSpan?> AgeAsync(RemotePool pool, CancellationToken ct) =>
        pool.RunAsync(async fs =>
        {
            if (await fs.StatAsync(DeployPath.LockFileName, ct) is not { ModifiedUtc: { } written }) return null;
            var probe = $".dnnmanager.clock.{Guid.NewGuid():N}{DeployPath.PartSuffix}";
            if (!await fs.TryCreateAsync(probe, [], ct)) return null;
            try
            {
                return await fs.StatAsync(probe, ct) is { ModifiedUtc: { } now } ? now - written : (TimeSpan?)null;
            }
            finally
            {
                await fs.DeleteAsync(probe, ct);
            }
        }, ct);

    /// <summary>Lets go of the lock - only when it is still this operation's. Never fails: a lock left goes stale.</summary>
    public static async Task ReleaseAsync(RemotePool pool, string content, CancellationToken ct)
    {
        try
        {
            if (await ReadAsync(pool, ct) == content) await pool.RunAsync(fs => fs.DeleteAsync(DeployPath.LockFileName, ct), ct);
        }
        catch (Exception ex) when (ex is RemoteException or OperationCanceledException)
        {
            // Not let go of: it goes stale, and the next operation takes it over.
        }
    }

    private static Task<string?> ReadAsync(RemotePool pool, CancellationToken ct) =>
        pool.RunAsync(async fs =>
        {
            if (await fs.StatAsync(DeployPath.LockFileName, ct) is null) return null;
            using var memory = new MemoryStream();
            await fs.DownloadAsync(DeployPath.LockFileName, memory, 0, null, ct);
            return (string?)Encoding.UTF8.GetString(memory.ToArray());
        }, ct);

    /// <summary>The lock, held: its heartbeat runs on a connection of its own until it is disposed.</summary>
    public sealed class Held : IAsyncDisposable
    {
        private readonly RemotePool _pool;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _heartbeat;

        internal Held(RemotePool pool, IRemoteFileSystemFactory factory, DeploySecrets secrets, string content)
        {
            _pool = pool;
            Content = content;
            _heartbeat = Task.Run(() => BeatAsync(factory, pool.Connection, secrets, content, _stop.Token));
        }

        /// <summary>What tells this lock apart from another's.</summary>
        public string Content { get; }

        private static async Task BeatAsync(IRemoteFileSystemFactory factory, DeployConnection connection, DeploySecrets secrets, string content,
            CancellationToken stop)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Heartbeat, stop);
                    await using var fs = factory.Create(connection, secrets);
                    await fs.ConnectAsync(stop);
                    // Rewritten only while it is still this operation's.
                    using var memory = new MemoryStream();
                    await fs.DownloadAsync(DeployPath.LockFileName, memory, 0, null, stop);
                    if (Encoding.UTF8.GetString(memory.ToArray()) != content) return;
                    await fs.UploadAsync(new MemoryStream(bytes), DeployPath.LockFileName, 0, null, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (RemoteException)
                {
                    // The network: the next beat tries again - the lock is stale only after several missed.
                }
            }
        }

        public Task ReleaseAsync() => DeployLock.ReleaseAsync(_pool, Content, CancellationToken.None);

        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            await _stop.CancelAsync();
            try { await _heartbeat; }
            catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
