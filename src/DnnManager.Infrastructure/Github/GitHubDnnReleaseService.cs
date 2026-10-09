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

    public bool IsKept(DnnRelease release) => _opts.KeepDnnPackages && File.Exists(KeptPath(release.DownloadUrl, $"DNN_Platform_{release.Version}_Install.zip"));

    public async Task<Result> ExtractUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct)
    {
        if (release.UpgradeUrl is not { } url) return Result.Fail($"DNN {release.Version}'s release has no upgrade package beside its install package.");
        var keep = _opts.KeepDnnPackages;
        var zipPath = keep
            ? KeptPath(url, $"DNN_Platform_{release.Version}_Upgrade.zip")
            : Path.Combine(Files.PrivateTemp.Path, $"DnnManager-DNN_Platform_{release.Version}_Upgrade-{Guid.NewGuid():N}.zip");
        try
        {
            if (keep && File.Exists(zipPath)) reporter.Info($"Using the kept upgrade package {zipPath} - no download needed.");
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                await DownloadAsync(url, $"DNN {release.Version}'s upgrade package", zipPath, reporter, ct);
                reporter.Success(keep ? $"Downloaded and kept: {zipPath}" : "Downloaded.");
            }

            reporter.Info("Putting the new files in…");
            int count;
            try
            {
                count = await Task.Run(() => ExtractOver(zipPath, projectDirectory, reporter, ct), ct);
            }
            catch (InvalidDataException) when (keep)
            {
                File.Delete(zipPath);
                return Result.Fail($"The kept upgrade package {zipPath} is damaged and has been deleted - try again to download it.");
            }
            reporter.Success($"{count:N0} files of DNN {release.Version} are in.");
            return Result.Ok();
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "DNN upgrade package download failed");
            return Result.Fail(ex.StatusCode == System.Net.HttpStatusCode.NotFound
                ? $"DNN {release.Version}'s release has no upgrade package ({url})."
                : $"DNN {release.Version}'s upgrade package could not be downloaded ({ex.Message}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Putting in DNN {Version}'s upgrade package failed", release.Version);
            return Result.Fail(ex.Message);
        }
        finally
        {
            if (!keep) try { File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Every file of the package over <paramref name="directory"/>, overwriting - but never web.config (the site's own
    /// settings, connection string and machine keys) and never outside the folder. The number of files written.
    /// </summary>
    private static int ExtractOver(string zipPath, string directory, IProgressReporter reporter, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var count = 0;
        var next = 0L;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(target, root + "web.config", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, target, overwrite: true);
            count++;
            if (Environment.TickCount64 < next) continue;
            next = Environment.TickCount64 + 250;
            reporter.Progress($"Putting the new files in: {count:N0} of {zip.Entries.Count:N0}");
        }
        return count;
    }

    // ─── DNN 10.2+'s local upgrade ───────────────────────────────────────

    /// <summary>What DNN's LocalUpgradeService leaves out when the package has no upgrade.json of its own.</summary>
    private static readonly string[] DefaultUpgradeExclude =
        ["App_Data/Database.mdf", "Config/DotNetNuke.config", "Install/InstallWizard", "favicon.ico", "robots.txt", "web.config"];

    /// <summary>The version range DNN's AssemblyInstaller redirects from: everything.</summary>
    private const string RedirectFrom = "0.0.0.0-32767.32767.32767.32767";

    public async Task<Result> ExtractLocalUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct)
    {
        var keep = _opts.KeepDnnPackages;
        var zipPath = keep
            ? KeptPath(release.DownloadUrl, $"DNN_Platform_{release.Version}_Install.zip")
            : Path.Combine(Files.PrivateTemp.Path, $"DnnManager-DNN_Platform_{release.Version}_Install-{Guid.NewGuid():N}.zip");
        try
        {
            if (keep && File.Exists(zipPath)) reporter.Info($"Using the kept package {zipPath} - no download needed.");
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                await DownloadAsync(release.DownloadUrl, $"DNN {release.Version}'s install package", zipPath, reporter, ct);
                reporter.Success(keep ? $"Downloaded and kept: {zipPath}" : "Downloaded.");
            }
            reporter.Info("Putting the new files in as DNN's own upgrade does: each assembly with its binding redirect, then the rest…");
            var (assemblies, files) = await Task.Run(() => LocalUpgradeOver(zipPath, projectDirectory, reporter, ct), ct);
            reporter.Success($"{assemblies:N0} assemblies (with their binding redirects) and {files:N0} other files of DNN {release.Version} are in.");
            return Result.Ok();
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "DNN install package download failed");
            return Result.Fail($"DNN {release.Version}'s install package could not be downloaded ({ex.Message}).");
        }
        catch (InvalidDataException ex)
        {
            if (keep) File.Delete(zipPath);
            return Result.Fail($"DNN {release.Version}'s install package is damaged ({ex.Message}){(keep ? " and has been deleted - try again to download it" : "")}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Putting in DNN {Version}'s files failed", release.Version);
            return Result.Fail(ex.Message);
        }
        finally
        {
            if (!keep) try { File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// DNN's <c>LocalUpgradeService.StartLocalUpgrade</c>, done from outside the site: bin's assemblies first - each copied
    /// in and, when it is strong-named, web.config's binding redirect for it pointed at its version (DNN's
    /// <c>BindingRedirect.config</c> merge) - then every other file but the excluded ones. The numbers written.
    /// </summary>
    internal static (int Assemblies, int Files) LocalUpgradeOver(string zipPath, string directory, IProgressReporter reporter, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var exclude = DefaultUpgradeExclude;
        var minimum = (string?)null;
        if (zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("App_Data/Upgrade/upgrade.json", StringComparison.OrdinalIgnoreCase)) is { } info)
        {
            using var stream = info.Open();
            using var json = JsonDocument.Parse(stream);
            // The package's own list, with the site's web.config always on it: its machineKey and connection are the site's.
            if (json.RootElement.TryGetProperty("upgradeExclude", out var list))
                exclude = list.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).Append("web.config")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (json.RootElement.TryGetProperty("minimumDnnVersion", out var min)) minimum = min.GetString();
        }
        if (minimum is not null && Version.TryParse(minimum, out var needs) && Application.UseCases.DnnInstall.Version(directory) is { } current &&
            Version.TryParse(current, out var have) && have < new Version(needs.Major, needs.Minor, Math.Max(needs.Build, 0)))
            throw new InvalidOperationException($"This package upgrades DNN {minimum} or newer only - the site runs {current}.");

        string Name(System.IO.Compression.ZipArchiveEntry e) => e.FullName.Replace('\\', '/');
        var files = zip.Entries.Where(e => !Name(e).EndsWith('/')).ToList();
        var dlls = files.Where(e => Path.GetDirectoryName(Name(e))!.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                                    Name(e).EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToHashSet();

        // The assemblies, and the binding redirects that make the site use them.
        var webConfig = Path.Combine(root, "web.config");
        var config = System.Xml.Linq.XDocument.Load(webConfig, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var redirects = 0;
        foreach (var entry in dlls)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(root, "bin", entry.Name);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, target, overwrite: true);
            if (StrongName(target) is { } assembly && SetBindingRedirect(config, assembly.Name, assembly.Token, assembly.Version)) redirects++;
        }
        if (redirects > 0)
        {
            using var writer = System.Xml.XmlWriter.Create(webConfig, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = config.Declaration is null, Indent = false });
            config.Save(writer);
        }
        reporter.Info($"{dlls.Count:N0} assemblies in bin, {redirects:N0} binding redirects in web.config set to them.");

        // Everything else, as DNN unzips it.
        var count = 0;
        var next = 0L;
        foreach (var entry in files.Where(e => !dlls.Contains(e) && !exclude.Any(x => Name(e).StartsWith(x, StringComparison.OrdinalIgnoreCase))))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, Name(entry)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, target, overwrite: true);
            count++;
            if (Environment.TickCount64 < next) continue;
            next = Environment.TickCount64 + 250;
            reporter.Progress($"Putting the new files in: {count:N0} of {files.Count - dlls.Count:N0}");
        }
        return (dlls.Count, count);
    }

    /// <summary>A strong-named assembly's name, public key token and version - null when it isn't .NET or not strong-named.</summary>
    private static (string Name, string Token, Version Version)? StrongName(string file)
    {
        try
        {
            var name = System.Reflection.AssemblyName.GetAssemblyName(file);
            var token = name.GetPublicKeyToken();
            return token is { Length: > 0 } && name.Name is { } n && name.Version is { } v ? (n, Convert.ToHexStringLower(token), v) : null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// web.config's binding redirect for an assembly, as DNN's BindingRedirect.config merge sets it: in every
    /// <c>dependentAssembly</c> with its name and token (case doesn't matter), in whichever <c>assemblyBinding</c> it is, the
    /// binding redirect set - or added when it has none - to redirect every version to <paramref name="version"/>; what else
    /// it holds (a <c>codeBase</c>, its <c>culture</c>) stays. A new <c>dependentAssembly</c> only when there is none. True
    /// when it changed.
    /// </summary>
    internal static bool SetBindingRedirect(System.Xml.Linq.XDocument config, string name, string token, Version version)
    {
        System.Xml.Linq.XNamespace ab = "urn:schemas-microsoft-com:asm.v1";
        var configuration = config.Root!;
        var runtime = configuration.Element("runtime") ?? new System.Xml.Linq.XElement("runtime");
        if (runtime.Parent is null) configuration.Add(runtime);
        var newVersion = version.ToString();
        var matching = runtime.Elements(ab + "assemblyBinding").SelectMany(b => b.Elements(ab + "dependentAssembly")).Where(d =>
            string.Equals((string?)d.Element(ab + "assemblyIdentity")?.Attribute("name"), name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string?)d.Element(ab + "assemblyIdentity")?.Attribute("publicKeyToken"), token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
        {
            var binding = runtime.Element(ab + "assemblyBinding");
            if (binding is null)
            {
                binding = new System.Xml.Linq.XElement(ab + "assemblyBinding");
                runtime.Add(binding);
            }
            binding.Add(new System.Xml.Linq.XElement(ab + "dependentAssembly",
                new System.Xml.Linq.XElement(ab + "assemblyIdentity", new System.Xml.Linq.XAttribute("name", name), new System.Xml.Linq.XAttribute("publicKeyToken", token)),
                new System.Xml.Linq.XElement(ab + "bindingRedirect", new System.Xml.Linq.XAttribute("oldVersion", RedirectFrom), new System.Xml.Linq.XAttribute("newVersion", newVersion))));
            return true;
        }
        var changed = false;
        foreach (var dependent in matching)
        {
            if (dependent.Element(ab + "bindingRedirect") is { } redirect)
            {
                if ((string?)redirect.Attribute("newVersion") == newVersion && (string?)redirect.Attribute("oldVersion") == RedirectFrom) continue;
                redirect.SetAttributeValue("oldVersion", RedirectFrom);
                redirect.SetAttributeValue("newVersion", newVersion);
            }
            else
            {
                // Right after its identity, where a redirect goes.
                var added = new System.Xml.Linq.XElement(ab + "bindingRedirect", new System.Xml.Linq.XAttribute("oldVersion", RedirectFrom), new System.Xml.Linq.XAttribute("newVersion", newVersion));
                if (dependent.Element(ab + "assemblyIdentity") is { } identity) identity.AddAfterSelf(added);
                else dependent.Add(added);
            }
            changed = true;
        }
        return changed;
    }

    public async Task<Result> DownloadAndExtractAsync(DnnRelease release, string projectDirectory,
        IProgressReporter reporter, CancellationToken ct)
    {
        var keep = _opts.KeepDnnPackages;
        var zipPath = keep
            ? KeptPath(release.DownloadUrl, $"DNN_Platform_{release.Version}_Install.zip")
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
                await DownloadAsync(release.DownloadUrl, $"DNN {release.Version}", zipPath, reporter, ct);
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

    /// <summary>Downloads a package (<paramref name="what"/>: "DNN 10.3.3") through a temporary file, so a failed or cancelled download never looks complete.</summary>
    private async Task DownloadAsync(string url, string what, string zipPath, IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Info($"Downloading {what}…");
        var tmp = zipPath + ".download";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long done = 0;
                var next = 0L;
                int read;
                while ((read = await Files.StalledRead.ReadAsync(input, buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (Environment.TickCount64 < next) continue;
                    next = Environment.TickCount64 + 250;
                    reporter.Progress(total is > 0
                        ? $"Downloading {what}: {done * 100 / total.Value}% ({done / 1048576d:N1} of {total.Value / 1048576d:N1} MB)"
                        : $"Downloading {what}: {done / 1048576d:N1} MB");
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

    /// <summary>
    /// <c>packages\&lt;owner&gt;.&lt;repo&gt;\&lt;asset&gt;</c> for the package at <paramref name="url"/> - per repository,
    /// since forks may ship different packages under one name; <paramref name="fallbackName"/> when the URL names no zip.
    /// </summary>
    private string KeptPath(string url, string fallbackName)
    {
        var folder = "other";
        var fileName = fallbackName;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
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
