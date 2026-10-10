using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Clears a website's cache - the three-dot menu's "Clear website cache": for a DNN site its file cache
/// (<c>Portals\_default\Cache</c>) and its bundled CSS / JavaScript (<c>App_Data\ClientDependency</c>), then for any
/// site its app pool is recycled, which empties what ASP.NET and DNN keep in memory. The site restarts; its next
/// request builds everything anew.
/// </summary>
public sealed class ClearSiteCacheUseCase(IIisManager iis, IUserPrompt prompt)
{
    // DNN's caches on disk, inside the site's folder. Their files are deleted; the folders stay.
    private static readonly string[] CacheFolders = [@"Portals\_default\Cache", @"App_Data\ClientDependency"];

    private readonly IIisManager _iis = iis;
    private readonly IUserPrompt _prompt = prompt;

    public async Task<Result> ExecuteAsync(string siteName, string directory, IProgressReporter reporter, CancellationToken ct)
    {
        var isDnn = File.Exists(Path.Combine(directory, "bin", "DotNetNuke.dll"));
        var what = isDnn
            ? "DNN's cached files and bundled CSS / JavaScript are deleted and its app pool is recycled"
            : "Its app pool is recycled, which empties what it holds in memory";
        if (!await _prompt.ConfirmAsync($"Clear the cache of '{siteName}'?{Environment.NewLine}{Environment.NewLine}{what} - " +
                                        "the site restarts, and its next request takes longer while it builds everything anew.",
                                        "Clear cache", "Cancel", false, ct))
            return Result.Aborted();

        if (isDnn)
        {
            reporter.Step("Deleting DNN's cached files");
            // The site's folder itself a link or junction: its cache isn't the site's - nothing is deleted there, and that is
            // said rather than passed over as done.
            if (SafePath.IsLink(directory))
                return Result.Fail($"{directory} is a link or junction - DNN Manager doesn't delete through those: what it points to " +
                                   $"isn't necessarily the site's cache.{Environment.NewLine}{SafePath.LinkHint}");
            var deleted = 0;
            var locked = 0;
            foreach (var folder in CacheFolders.Select(f => Path.Combine(directory, f)).Where(Directory.Exists))
            {
                // Not through a junction or link - the cache folder itself, a folder on the way, or one in it: the app
                // pool can write here, and what one points to isn't the cache.
                if (SafePath.HasLink(directory, folder))
                {
                    reporter.Warn($"Left {folder} as it is: it is a link or junction, or reached through one - deleting there could reach a folder outside the site." +
                                  $"{Environment.NewLine}{SafePath.LinkHint}");
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(folder, "*", SafePath.Recursive))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        locked++; // in use - the recycle below lets go of it, and DNN writes it anew
                    }
                }
                // A link among them is only unlinked, never gone into.
                foreach (var sub in Directory.EnumerateDirectories(folder))
                    try { SafePath.DeleteTree(sub); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            reporter.Success(locked == 0 ? $"Deleted {deleted} cached file(s)." : $"Deleted {deleted} cached file(s); {locked} in use were left.");
        }

        reporter.Step("Recycling the app pool");
        var recycle = _iis.RecycleAppPool(siteName);
        if (!recycle.Success) return Result.Fail($"Could not recycle the app pool of '{siteName}': {recycle.Error}");
        reporter.Success($"App pool of '{siteName}' recycled - its memory cache is empty.");
        return Result.Ok();
    }
}