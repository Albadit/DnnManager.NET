namespace DnnManager.Infrastructure.Files;

/// <summary>
/// A download's reads with a time limit of their own: a connection that stops sending (a stalled proxy, Wi-Fi gone)
/// doesn't hang the download for ever - HttpClient's own timeout only covers waiting for the answer's headers.
/// </summary>
internal static class StalledRead
{
    /// <summary>How long a read may get nothing before the download counts as stopped.</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Reads into <paramref name="buffer"/>; an <see cref="IOException"/> when nothing arrives for <see cref="After"/> -
    /// a cancel through <paramref name="ct"/> is a cancel, as ever.
    /// </summary>
    public static async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(After);
        try
        {
            return await stream.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"The download stopped: nothing arrived for {After.TotalSeconds:0} seconds. Try again.");
        }
    }
}
