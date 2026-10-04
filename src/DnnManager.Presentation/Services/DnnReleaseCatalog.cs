using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Services;

/// <summary>
/// The DNN releases of each repository in the settings, asked of GitHub once - in the background when the app
/// starts, which also saves them in the database for offline use - and kept for the rest of the run, so New project
/// shows its versions at once. A failed lookup isn't kept, nor one answered from the releases saved for offline use,
/// so the next request (New project shown again, another repository picked) tries GitHub again.
/// </summary>
public sealed class DnnReleaseCatalog(IServiceProvider services)
{
    private readonly IServiceProvider _services = services;
    private readonly Dictionary<string, Task<Result<DnnReleaseList>>> _lists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts loading every repository's releases, without waiting for them.</summary>
    public void Preload()
    {
        foreach (var api in _services.GetRequiredService<IDnnReleaseService>().KnownReleaseApis)
            _ = GetAsync(api);
    }

    /// <summary>The releases of <paramref name="api"/>, highest version first - from the earlier lookup when it reached GitHub.</summary>
    public Task<Result<DnnReleaseList>> GetAsync(string api)
    {
        lock (_lists)
        {
            if (_lists.TryGetValue(api, out var known)) return known;
            var lookup = LoadAsync(api);
            _lists[api] = lookup;
            // Forget a failed lookup, or one answered from the saved releases (offline) - unless a newer one has replaced
            // it meanwhile - so the next request tries GitHub again.
            _ = lookup.ContinueWith(done =>
            {
                if (done.Result is { Success: true, Value.SavedAt: null }) return;
                lock (_lists)
                    if (_lists.TryGetValue(api, out var current) && current == done) _lists.Remove(api);
            }, TaskScheduler.Default);
            return lookup;
        }
    }

    private async Task<Result<DnnReleaseList>> LoadAsync(string api)
    {
        try
        {
            // IDnnReleaseService is a typed HttpClient (transient) - take a fresh one for each lookup.
            return await _services.GetRequiredService<IDnnReleaseService>().ListReleasesAsync(api, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Result<DnnReleaseList>.Fail(ex.Message);
        }
    }
}