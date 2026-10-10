using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnManager.Application;
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
                return Result<DnnRelease>.Ok(ToRelease(rel, asset));
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

    // Pages of 100 releases read at most - 2 000 releases, far more than any DNN source has.
    private const int MaxReleasePages = 20;

    /// <summary>The next page's address from GitHub's <c>Link</c> header (<c>&lt;…&gt;; rel="next"</c>); null on the last page.</summary>
    internal static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var link in values.SelectMany(v => v.Split(',')))
        {
            var parts = link.Split(';');
            if (parts.Length < 2 || !parts.Skip(1).Any(p => p.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase))) continue;
            var target = parts[0].Trim();
            if (target.StartsWith('<') && target.EndsWith('>')) return target[1..^1];
        }
        return null;
    }

    public async Task<Result<DnnReleaseList>> ListReleasesAsync(string apiUrl, CancellationToken ct)
    {
        try
        {
            // GitHub returns 30 releases a page by default, at most 100 - and DNN has more than 100 with its release
            // candidates: every page is read (GitHub's Link header names the next), so the oldest versions are there too.
            var releases = new List<GhRelease>();
            string? url = apiUrl + (apiUrl.Contains('?') ? "&" : "?") + "per_page=100";
            for (var page = 0; url is not null && page < MaxReleasePages; page++)
            {
                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                releases.AddRange(await response.Content.ReadFromJsonAsync<List<GhRelease>>(ct) ?? []);
                url = NextPage(response);
            }
            if (releases.Count == 0) return Result<DnnReleaseList>.Fail("Empty release list.");
            var usable = releases
                .Where(r => !r.Draft)
                .Select(r => (Release: r, Asset: FindAsset(r)))
                .Where(r => r.Asset is not null)
                .Select(r => ToRelease(r.Release, r.Asset!))
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

    /// <summary>
    /// Saves the list whole, in place of the one before - with GitHub's SHA-256s of the packages, so a kept package used
    /// offline is checked as it is online. Best effort: it's only for offline use.
    /// </summary>
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
            {
                var release = releases[i];
                AppDatabase.Execute(connection,
                    "INSERT INTO dnn_releases (api, position, version, tag, url, prerelease, saved_utc, sha256, upgrade_sha256) " +
                    "VALUES ($api, $i, $version, $tag, $url, $pre, $saved, $sha, $upgradeSha)",
                    ("$api", apiUrl), ("$i", i), ("$version", release.Version), ("$tag", release.TagName),
                    ("$url", release.DownloadUrl), ("$pre", release.Prerelease), ("$saved", now),
                    ("$sha", (object?)release.Sha256 ?? DBNull.Value), ("$upgradeSha", (object?)release.UpgradeSha256 ?? DBNull.Value));
            }
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
                "SELECT version, tag, url, prerelease, saved_utc, sha256, upgrade_sha256 FROM dnn_releases WHERE api = $api ORDER BY position",
                ("$api", apiUrl));
            using var reader = command.ExecuteReader();
            var releases = new List<DnnRelease>();
            DateTime? savedAt = null;
            while (reader.Read())
            {
                // The saved list is in Documents, which any program of the user can change: an address that isn't https
                // (to GitHub, or this computer) isn't downloaded from.
                if (!SettingRules.IsReleaseSource(reader.GetString(2))) continue;
                releases.Add(new DnnRelease(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
                    Sha256Of(reader, 5), Sha256Of(reader, 6)));
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

    /// <summary>A saved SHA-256 - 64 hex digits, lowercase - or null when there is none (or it isn't one).</summary>
    private static string? Sha256Of(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column)) return null;
        var value = reader.GetString(column).Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
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
    /// <summary>The release as DNN Manager uses it: its install package, and the SHA-256s GitHub gives for it and its upgrade package.</summary>
    private static DnnRelease ToRelease(GhRelease r, GhAsset install)
    {
        var release = new DnnRelease(r.TagName.TrimStart('v'), r.TagName, install.BrowserDownloadUrl, r.Prerelease, install.Sha256);
        var upgrade = release.UpgradeUrl is { } url
            ? r.Assets.FirstOrDefault(a => a.BrowserDownloadUrl.Equals(url, StringComparison.OrdinalIgnoreCase))
            : null;
        return release with { UpgradeSha256 = upgrade?.Sha256 };
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
        // "sha256:677af2…" - GitHub's digest of the file; none on assets uploaded before GitHub kept them.
        [JsonPropertyName("digest")] public string? Digest { get; set; }

        public string? Sha256 => Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && d.Length == 7 + 64
            ? d[7..].ToLowerInvariant() : null;
    }
}
