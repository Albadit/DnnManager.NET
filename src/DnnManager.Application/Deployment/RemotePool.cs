using System.Collections.Concurrent;
using DnnManager.Domain;

namespace DnnManager.Application.Deployment;

/// <summary>
/// The connections of one operation to a server: up to <see cref="DeployConnection.Parallel"/> of them, made as they are
/// needed, each used by one transfer at a time. A transfer that fails on the network is tried again on a new connection
/// (<see cref="RunAsync{T}"/>) - up to <see cref="Attempts"/> times in all, waiting longer each time.
/// </summary>
public sealed class RemotePool(IRemoteFileSystemFactory factory, DeployConnection connection, DeploySecrets secrets) : IAsyncDisposable
{
    /// <summary>How often a transfer is tried, in all.</summary>
    public const int Attempts = 4;

    private readonly ConcurrentBag<IRemoteFileSystem> _idle = [];
    private readonly ConcurrentBag<IRemoteFileSystem> _all = [];
    private readonly SemaphoreSlim _slots = new(Math.Clamp(connection.Parallel, 1, 16));
    private int _retries;

    public DeployConnection Connection { get; } = connection;

    /// <summary>How many transfers were tried again, in all.</summary>
    public int Retries => _retries;

    /// <summary>How many transfers can run at the same time.</summary>
    public int Size => Math.Clamp(Connection.Parallel, 1, 16);

    /// <summary>
    /// Runs <paramref name="work"/> on a connection of the pool - again on a new one after a network failure, after 1, 3
    /// and 9 seconds (a little more or less, so parallel transfers don't retry together). <paramref name="retrying"/> hears of
    /// each new try and why. What isn't worth trying again - refused, not found, a changed identity - fails at once.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<IRemoteFileSystem, Task<T>> work, CancellationToken ct, Action<int, string>? retrying = null)
    {
        await _slots.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var fs = await TakeAsync(ct);
                try
                {
                    var result = await work(fs);
                    _idle.Add(fs);
                    return result;
                }
                catch (RemoteException ex) when (ex.Transient && attempt < Attempts && !ct.IsCancellationRequested)
                {
                    // The connection may be broken: it isn't used again.
                    await Discard(fs);
                    Interlocked.Increment(ref _retries);
                    retrying?.Invoke(attempt + 1, ex.Message);
                    var wait = TimeSpan.FromSeconds(Math.Pow(3, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4));
                    if (ex.RetryAfter is { } after && after > wait) wait = after;
                    await Task.Delay(wait, ct);
                }
                catch
                {
                    await Discard(fs);
                    throw;
                }
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    public Task RunAsync(Func<IRemoteFileSystem, Task> work, CancellationToken ct, Action<int, string>? retrying = null) =>
        RunAsync(async fs =>
        {
            await work(fs);
            return true;
        }, ct, retrying);

    private async Task<IRemoteFileSystem> TakeAsync(CancellationToken ct)
    {
        if (_idle.TryTake(out var idle)) return idle;
        var fs = factory.Create(Connection, secrets);
        _all.Add(fs);
        try
        {
            await fs.ConnectAsync(ct);
        }
        catch
        {
            await Discard(fs);
            throw;
        }
        return fs;
    }

    private static async Task Discard(IRemoteFileSystem fs)
    {
        try { await fs.DisposeAsync(); }
        catch (Exception) { /* a broken connection may fail to close - it is gone either way */ }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var fs in _all) await Discard(fs);
        _slots.Dispose();
    }
}
