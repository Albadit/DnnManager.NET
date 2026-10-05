using System.Net;
using System.Text.Json;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// DNN's install and upgrade packages of any release, as GitHub publishes them - downloaded once into
/// <c>%LOCALAPPDATA%\DnnManagerTests\cache\&lt;version&gt;\</c> and used from there by every later run.
/// </summary>
public static class DnnPackages
{
    private const string Api = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases/tags/";

    /// <summary>
    /// DNN <paramref name="version"/> (e.g. 9.13.9) as GitHub lists it - its install package's address - with both its
    /// packages in the cache: (release, install zip, upgrade zip).
    /// </summary>
    public static async Task<(DnnRelease Release, string Install, string Upgrade)> GetAsync(string version)
    {
        var folder = Path.Combine(TestEnvironment.Root, "cache", version);
        Directory.CreateDirectory(folder);
        var known = Path.Combine(folder, "release.txt");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DnnManager-IntegrationTests");
        if (!File.Exists(known))
        {
            // The assets' names vary: DNN_Platform_9.3.2.24_Install.zip, DNN_Platform_07.04.02_Upgrade.zip…
            List<string> assets;
            try
            {
                using var release = JsonDocument.Parse(await http.GetStringAsync(Api + "v" + version));
                assets = release.RootElement.GetProperty("assets").EnumerateArray()
                    .Select(a => a.GetProperty("browser_download_url").GetString()!)
                    .Where(u => u.Contains("/DNN_Platform", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                // GitHub's API allows 60 requests an hour without signing in - its release page lists the same files.
                var page = await http.GetStringAsync($"https://github.com/dnnsoftware/Dnn.Platform/releases/expanded_assets/v{version}");
                assets = System.Text.RegularExpressions.Regex.Matches(page, @"href=""(/dnnsoftware/Dnn\.Platform/releases/download/[^""]+/DNN_Platform[^""]+\.zip)""",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    .Select(m => "https://github.com" + m.Groups[1].Value).Distinct().ToList();
            }
            File.WriteAllLines(known, [
                assets.First(u => u.EndsWith("_Install.zip", StringComparison.OrdinalIgnoreCase)),
                assets.First(u => u.EndsWith("_Upgrade.zip", StringComparison.OrdinalIgnoreCase))
            ]);
        }
        var urls = File.ReadAllLines(known);
        var files = new string[2];
        for (var i = 0; i < 2; i++)
        {
            files[i] = Path.Combine(folder, urls[i][(urls[i].LastIndexOf('/') + 1)..]);
            if (File.Exists(files[i])) continue;
            var download = files[i] + ".download";
            await using (var target = File.Create(download))
            await using (var source = await http.GetStreamAsync(urls[i]))
                await source.CopyToAsync(target);
            File.Move(download, files[i], overwrite: true);
        }
        return (new DnnRelease(version, $"v{version}", urls[0]), files[0], files[1]);
    }

    /// <summary>
    /// DNN releases <paramref name="versions"/> served from the cache to DNN Manager's own package installer - so the
    /// tests install and upgrade exactly as DNN Manager does, without downloading again.
    /// </summary>
    public static async Task<CachedDnnReleases> ReleasesAsync(string packagesDirectory, params string[] versions)
    {
        var releases = new List<DnnRelease>();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            var (release, install, upgrade) = await GetAsync(version);
            releases.Add(release);
            files[release.DownloadUrl] = install;
            files[release.UpgradeUrl!] = upgrade;
        }
        return new CachedDnnReleases(releases, files, packagesDirectory);
    }
}

/// <summary>DNN's releases as GitHub has them, with DNN Manager's package installer reading their packages from the cache.</summary>
public sealed class CachedDnnReleases : IDnnReleaseService, IDnnPackageInstaller
{
    private readonly IReadOnlyList<DnnRelease> _releases;
    private readonly DnnPackageInstaller _installer;

    public CachedDnnReleases(IReadOnlyList<DnnRelease> releases, IReadOnlyDictionary<string, string> files, string dataDirectory)
    {
        _releases = releases.OrderByDescending(r => Version.Parse(r.Version)).ToList();
        _installer = new DnnPackageInstaller(new HttpClient(new FromCache(files)), Options.Create(new AppOptions { KeepDnnPackages = false }),
            new AppDataPaths(dataDirectory), NullLogger<DnnPackageInstaller>.Instance);
    }

    public IReadOnlyList<string> KnownReleaseApis => ["https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases"];

    public Task<Result<DnnRelease>> GetReleaseAsync(string apiUrl, string? version, CancellationToken ct) =>
        Task.FromResult(_releases.FirstOrDefault(r => version is null || r.Version == version.TrimStart('v')) is { } release
            ? Result<DnnRelease>.Ok(release)
            : Result<DnnRelease>.Fail($"Release {version} not found."));

    public Task<Result<DnnReleaseList>> ListReleasesAsync(string apiUrl, CancellationToken ct) =>
        Task.FromResult(Result<DnnReleaseList>.Ok(new DnnReleaseList(_releases)));

    public Task<Result> DownloadAndExtractAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct) =>
        _installer.DownloadAndExtractAsync(release, projectDirectory, reporter, ct);

    public Task<Result> ExtractUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct) =>
        _installer.ExtractUpgradeAsync(release, projectDirectory, reporter, ct);

    public Task<Result> ExtractLocalUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct) =>
        _installer.ExtractLocalUpgradeAsync(release, projectDirectory, reporter, ct);

    public bool IsKept(DnnRelease release) => false;

    /// <summary>GitHub's download addresses answered from the cached files.</summary>
    private sealed class FromCache(IReadOnlyDictionary<string, string> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsoluteUri, out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
