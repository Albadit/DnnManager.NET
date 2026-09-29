using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Services;

/// <summary>
/// The DNN releases of each repository in the settings, asked of GitHub once - in the background when the app
/// starts - and kept for the rest of the run, so New project shows its versions at once. <see cref="GetAsync"/>
/// with <c>refresh</c> asks again. A failed lookup isn't kept, so the next request tries again.
/// </summary>
public sealed class DnnReleaseCatalog
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Task<Result<IReadOnlyList<DnnRelease>>>> _lists = new(StringComparer.OrdinalIgnoreCase);

    public DnnReleaseCatalog(IServiceProvider services) => _services = services;

    /// <summary>Starts loading every repository's releases, without waiting for them.</summary>
    public void Preload()
    {
        foreach (var api in _services.GetRequiredService<IDnnReleaseService>().KnownReleaseApis)
            _ = GetAsync(api);
    }

    /// <summary>The releases of <paramref name="api"/>, highest version first - from the earlier lookup unless <paramref name="refresh"/>.</summary>
    public Task<Result<IReadOnlyList<DnnRelease>>> GetAsync(string api, bool refresh = false)
    {
        lock (_lists)
        {
            if (!refresh && _lists.TryGetValue(api, out var known)) return known;
            var lookup = LoadAsync(api);
            _lists[api] = lookup;
            // Forget a failed lookup (unless a newer one has replaced it meanwhile), so the next request tries again.
            _ = lookup.ContinueWith(done =>
            {
                if (done.Result.Success) return;
                lock (_lists)
                    if (_lists.TryGetValue(api, out var current) && current == done) _lists.Remove(api);
            }, TaskScheduler.Default);
            return lookup;
        }
    }

    private async Task<Result<IReadOnlyList<DnnRelease>>> LoadAsync(string api)
    {
        try
        {
            // IDnnReleaseService is a typed HttpClient (transient) - take a fresh one for each lookup.
            return await _services.GetRequiredService<IDnnReleaseService>().ListReleasesAsync(api, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DnnRelease>>.Fail(ex.Message);
        }
    }
}
