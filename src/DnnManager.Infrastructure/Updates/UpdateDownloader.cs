using System.Diagnostics;
using System.Security.Cryptography;

namespace DnnManager.Infrastructure.Updates;

/// <summary>
/// Downloads a release's file and makes sure it is the one GitHub published before anything uses it: its size, its
/// SHA-256 (when GitHub gives one), and the version in the file itself.
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    /// <summary>
    /// <paramref name="asset"/> of <paramref name="release"/> in <paramref name="folder"/>, checked; progress goes from 0 to 1.
    /// A file already there that passes the checks is used as it is. Throws <see cref="AppUpdateException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(AppRelease release, AppReleaseAsset asset, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, asset.Name);
        if (File.Exists(path) && Problem(path, release, asset) is null)
        {
            progress?.Report(1);
            return path;
        }

        var partial = path + ".partial";
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, asset.Url))
            {
                request.Headers.UserAgent.ParseAdd("DnnManager");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) throw new AppUpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {asset.Name}");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await Files.StalledRead.ReadAsync(source, buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (asset.Size > 0) progress?.Report(Math.Min(1, (double)done / asset.Size));
                }
            }

            if (Problem(partial, release, asset) is { } problem) throw new AppUpdateException($"The download of {asset.Name} isn't right: {problem}");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException ||
                                   ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AppUpdateException(ex is TaskCanceledException ? $"The download of {asset.Name} stopped answering" : ex.Message, ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>What is wrong with a downloaded file; null when it is the release's.</summary>
    public static string? Problem(string path, AppRelease release, AppReleaseAsset asset)
    {
        var length = new FileInfo(path).Length;
        if (length != asset.Size) return $"it is {length:N0} bytes, GitHub lists {asset.Size:N0}";
        // It is run with administrator rights: without GitHub's SHA-256 there is nothing to check it against.
        if (asset.Sha256 is not { } expected) return "GitHub lists no SHA-256 for it, so it can't be checked";
        using (var stream = File.OpenRead(path))
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (actual != expected) return "its SHA-256 isn't the one GitHub lists";
        }
        // Setup and the exe carry the release's version (ProductVersion "1.7.0", or "1.7.0+commit").
        var product = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (product is null || !AppReleaseFeed.TryParseVersion(product, out var version) ||
            AppReleaseFeed.IsNewer(version, release.Version) || AppReleaseFeed.IsNewer(release.Version, version))
            return $"it is version {product ?? "(none)"}, not {release.Version}";
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the next cleanup */ }
    }
}
