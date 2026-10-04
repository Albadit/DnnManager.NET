using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Github;

/// <summary>
/// The DNN releases of a GitHub repository. Each list GitHub returns - asked at every start (DnnReleaseCatalog) - is
/// saved in DNN Manager's database (<c>dnn_releases</c>) in place of the one before, so New project still offers the
/// versions - and sets up the kept packages (<see cref="AppOptions.KeepDnnPackages"/>) - when GitHub can't be reached.
/// </summary>
public sealed class GitHubDnnReleaseService : IDnnReleaseService
{
    private readonly HttpClient _http;
    private readonly AppOptions _opts;
    private readonly AppDatabase _database;
    private readonly ILogger<GitHubDnnReleaseService> _log;

    public GitHubDnnReleaseService(HttpClient http, IOptions<AppOptions> opts, AppDatabase database, ILogger<GitHubDnnReleaseService> log)
    {
        _http = http; _opts = opts.Value; _database = database; _log = log;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.Add("User-Agent", "DnnManager-NET");
    }

    public IReadOnlyList<string> KnownReleaseApis => _opts.GitHubReleaseApis;

    public async Task<Result<DnnRelease>> GetReleaseAsync(string apiUrl, string? version, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                var list = await ListReleasesAsync(apiUrl, ct);
                if (!list.Success) return Result<DnnRelease>.Fail(list.Error ?? "Could not list the releases.");
                // The latest means the latest release - a pre-release only when the repository has nothing else.
                var releases = list.Value!.Releases;
                var latest = releases.FirstOrDefault(r => !r.Prerelease) ?? releases.FirstOrDefault();
                return latest is not null
                    ? Result<DnnRelease>.Ok(latest)
                    : Result<DnnRelease>.Fail("No suitable DNN release found.");
            }

            // DNN tags its releases "v10.0.1"; a tag without the "v" works too.
            foreach (var tag in new[] { "v" + version.TrimStart('v'), version })
            {
                using var response = await _http.GetAsync($"{apiUrl}/tags/{tag}", ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                var rel = await response.Content.ReadFromJsonAsync<GhRelease>(ct);
                if (rel is null) break;
                var asset = FindAsset(rel);
                if (asset is null) return Result<DnnRelease>.Fail($"Release {rel.TagName} has no DNN Install ZIP asset.");
                return Result<DnnRelease>.Ok(new DnnRelease(rel.TagName.TrimStart('v'), rel.TagName, asset.BrowserDownloadUrl, rel.Prerelease));
            }
            return Result<DnnRelease>.Fail($"Release {version} not found.");
        }
        // Not reached, or timed out (a timeout is an OperationCanceledException too - but not a cancel).
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Offline: the release as saved at the last lookup.
            var saved = LoadSavedList(apiUrl);
            var known = version is null ? null : saved?.Releases.FirstOrDefault(r =>
                string.Equals(r.Version, version.TrimStart('v'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.TagName, version, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                _log.LogWarning("GitHub can't be reached ({Error}) - DNN {Version} as saved on {SavedAt:g}", ex.Message, known.Version, saved!.SavedAt);
                return Result<DnnRelease>.Ok(known);
            }
            _log.LogError(ex, "GitHub release lookup failed");
            return Result<DnnRelease>.Fail($"GitHub can't be reached: {ex.Message}");
        }
    }

    public async Task<Result<DnnReleaseList>> ListReleasesAsync(string apiUrl, CancellationToken ct)
    {
        try
        {
            // GitHub returns 30 releases a page by default - 100 covers years of DNN releases.
            var url = apiUrl + (apiUrl.Contains('?') ? "&" : "?") + "per_page=100";
            var releases = await _http.GetFromJsonAsync<List<GhRelease>>(url, ct);
            if (releases is null) return Result<DnnReleaseList>.Fail("Empty release list.");
            var usable = releases
                .Where(r => !r.Draft)
                .Select(r => (Release: r, Asset: FindAsset(r)))
                .Where(r => r.Asset is not null)
                .Select(r => new DnnRelease(r.Release.TagName.TrimStart('v'), r.Release.TagName, r.Asset!.BrowserDownloadUrl,
                    r.Release.Prerelease))
                // GitHub lists by release date, and a 9.x patch can come out after a 10.x release: order by version,
                // highest first, a release before a pre-release of the same version ("10.1.0" before "10.1.0-rc1").
                // Tags that aren't a version number keep GitHub's order, after the numbered ones.
                .Select((r, index) => (Release: r, Index: index, Number: VersionNumber(r.Version)))
                .OrderByDescending(r => r.Number is not null)
                .ThenByDescending(r => r.Number)
                .ThenBy(r => r.Release.Prerelease)
                .ThenBy(r => r.Index)
                .Select(r => r.Release)
                .ToList();
            SaveList(apiUrl, usable);
            return Result<DnnReleaseList>.Ok(new DnnReleaseList(usable));
        }
        // Not reached, or timed out (a timeout is an OperationCanceledException too - but not a cancel).
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var saved = LoadSavedList(apiUrl);
            if (saved is not null)
            {
                _log.LogWarning("GitHub can't be reached ({Error}) - the releases of {Api} as saved on {SavedAt:g}", ex.Message, apiUrl, saved.SavedAt);
                return Result<DnnReleaseList>.Ok(saved);
            }
            _log.LogWarning("GitHub release list failed: {Error}", ex.Message);
            return Result<DnnReleaseList>.Fail(ex.Message);
        }
    }

    // ── The list saved for offline use ──

    /// <summary>Saves the list whole, in place of the one before. Best effort: it's only for offline use.</summary>
    private void SaveList(string apiUrl, List<DnnRelease> releases)
    {
        try
        {
            using var connection = _database.Open();
            // One list or the other, never half: a connection closed before COMMIT takes it all back.
            AppDatabase.Execute(connection, "BEGIN IMMEDIATE");
            AppDatabase.Execute(connection, "DELETE FROM dnn_releases WHERE api = $api", ("$api", apiUrl));
            var now = DateTime.Now.ToString("O");
            for (var i = 0; i < releases.Count; i++)
                AppDatabase.Execute(connection,
                    "INSERT INTO dnn_releases (api, position, version, tag, url, prerelease, saved_utc) VALUES ($api, $i, $version, $tag, $url, $pre, $saved)",
                    ("$api", apiUrl), ("$i", i), ("$version", releases[i].Version), ("$tag", releases[i].TagName),
                    ("$url", releases[i].DownloadUrl), ("$pre", releases[i].Prerelease), ("$saved", now));
            AppDatabase.Execute(connection, "COMMIT");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save the DNN releases of {Api}: {Error}", apiUrl, ex.Message);
        }
    }

    private DnnReleaseList? LoadSavedList(string apiUrl)
    {
        try
        {
            using var connection = _database.Open();
            using var command = AppDatabase.Command(connection,
                "SELECT version, tag, url, prerelease, saved_utc FROM dnn_releases WHERE api = $api ORDER BY position", ("$api", apiUrl));
            using var reader = command.ExecuteReader();
            var releases = new List<DnnRelease>();
            DateTime? savedAt = null;
            while (reader.Read())
            {
                releases.Add(new DnnRelease(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
                savedAt ??= DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            return releases.Count > 0 ? new DnnReleaseList(releases, savedAt) : null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or FormatException)
        {
            _log.LogWarning("Could not read the saved DNN releases of {Api}: {Error}", apiUrl, ex.Message);
            return null;
        }
    }

    /// <summary>The number part of a version - <c>10.1.0</c> of <c>10.1.0-rc1</c> - or null when it doesn't start with one.</summary>
    private static Version? VersionNumber(string version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(version, @"^\d+(\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var v) ? v : null;
    }

    private static GhAsset? FindAsset(GhRelease r)
        => r.Assets.FirstOrDefault(a => System.Text.RegularExpressions.Regex.IsMatch(a.Name, "DNN_Platform.*Install\\.zip$"));

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = [];
    }
    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    }
}

/// <summary>
/// Downloads a DNN install package and extracts it into a project. With <see cref="AppOptions.KeepDnnPackages"/> on,
/// the package is kept in <c>Documents\DnnManager\packages\&lt;owner&gt;.&lt;repo&gt;\</c> and used again next time
/// instead of downloading it; otherwise it is downloaded into the project and deleted after extracting.
/// </summary>
public sealed class DnnPackageInstaller(HttpClient http, IOptions<AppOptions> opts, AppDataPaths paths, ILogger<DnnPackageInstaller> log) : IDnnPackageInstaller
{
    private readonly HttpClient _http = http;
    private readonly AppOptions _opts = opts.Value;
    private readonly AppDataPaths _paths = paths;
    private readonly ILogger<DnnPackageInstaller> _log = log;

    public bool IsKept(DnnRelease release) => _opts.KeepDnnPackages && File.Exists(KeptPath(release));

    public async Task<Result> DownloadAndExtractAsync(DnnRelease release, string projectDirectory,
        IProgressReporter reporter, CancellationToken ct)
    {
        var keep = _opts.KeepDnnPackages;
        var zipPath = keep
            ? KeptPath(release)
            : Path.Combine(projectDirectory, $"DNN_Platform_{release.Version}_Install.zip");
        try
        {
            if (keep && File.Exists(zipPath))
            {
                reporter.Info($"Using the kept package {zipPath} - no download needed.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                await DownloadAsync(release, zipPath, reporter, ct);
                reporter.Success(keep ? $"Downloaded and kept: {zipPath}" : $"Downloaded: {zipPath}");
            }

            reporter.Info("Extracting…");
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, projectDirectory, overwriteFiles: true);
            }
            catch (InvalidDataException) when (keep)
            {
                // A damaged kept package would fail every time - drop it so the next try downloads it again.
                File.Delete(zipPath);
                return Result.Fail($"The kept package {zipPath} is damaged and has been deleted - try again to download it.");
            }
            if (!keep) File.Delete(zipPath);
            reporter.Success("Extraction complete.");
            return Result.Ok();
        }
        catch (HttpRequestException ex)
        {
            // Offline, most likely: only a kept package installs without a download.
            _log.LogError(ex, "DNN download failed");
            return Result.Fail($"DNN {release.Version} could not be downloaded ({ex.Message}). " + (keep
                ? "Without internet only a version marked \"kept, no download\" can be set up."
                : "To set up projects without internet, turn on Settings → DNN releases → Keep downloaded DNN install packages while online."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "DNN install failed");
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>Downloads the package through a temporary file, so a failed or cancelled download never looks complete.</summary>
    private async Task DownloadAsync(DnnRelease release, string zipPath, IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Info($"Downloading DNN {release.Version}…");
        var tmp = zipPath + ".download";
        try
        {
            using var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long done = 0;
                var next = 0L;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (Environment.TickCount64 < next) continue;
                    next = Environment.TickCount64 + 200;
                    reporter.Progress(total is > 0
                        ? $"Downloading DNN {release.Version}: {done * 100 / total.Value}% ({done / 1048576d:N1} of {total.Value / 1048576d:N1} MB)"
                        : $"Downloading DNN {release.Version}: {done / 1048576d:N1} MB");
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary><c>packages\&lt;owner&gt;.&lt;repo&gt;\&lt;asset&gt;</c> - per repository, since forks may ship different packages under one name.</summary>
    private string KeptPath(DnnRelease release)
    {
        var folder = "other";
        var fileName = $"DNN_Platform_{release.Version}_Install.zip";
        if (Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri))
        {
            // https://github.com/<owner>/<repo>/releases/download/<tag>/<asset>
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) folder = $"{parts[0]}.{parts[1]}";
            if (parts.Length > 0 && parts[^1].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) fileName = parts[^1];
        }
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            folder = folder.Replace(bad, '_');
            fileName = fileName.Replace(bad, '_');
        }
        return Path.Combine(_paths.PackagesDirectory, folder, fileName);
    }
}

public sealed class HttpConnectivityChecker : IHttpConnectivityChecker
{
    public async Task<Result<int>> CheckAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            using var resp = await http.GetAsync(url, ct);
            return Result<int>.Ok((int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            return Result<int>.Fail(ex.Message);
        }
    }
}
