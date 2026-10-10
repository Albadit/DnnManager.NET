using System.IO.Compression;
using System.Text.Json;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.WebConfigs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Github;

/// <summary>
/// Downloads a DNN install package and extracts it into a project. With <see cref="AppOptions.KeepDnnPackages"/> on,
/// the package is kept in <c>Documents\DnnManager\packages\&lt;owner&gt;.&lt;repo&gt;\</c> and used again next time
/// instead of downloading it; otherwise it is downloaded into DNN Manager's own temporary folder and deleted after
/// extracting - also when extracting fails.
/// </summary>
public sealed class DnnPackageInstaller(HttpClient http, IOptions<AppOptions> opts, AppDataPaths paths, ILogger<DnnPackageInstaller> log) : IDnnPackageInstaller
{
    private readonly HttpClient _http = http;
    private readonly AppOptions _opts = opts.Value;
    private readonly AppDataPaths _paths = paths;
    private readonly ILogger<DnnPackageInstaller> _log = log;

    /// <summary>
    /// A package that isn't used: not from an https address, or not the file GitHub lists (its SHA-256 differs). Not
    /// "damaged" - said as it is, and a kept package isn't deleted for it.
    /// </summary>
    internal sealed class RefusedException(string message) : IOException(message);

    public bool IsKept(DnnRelease release) => _opts.KeepDnnPackages && File.Exists(KeptPath(release.DownloadUrl, $"DNN_Platform_{release.Version}_Install.zip"));

    /// <summary>A package on disk to unpack: kept (in Documents) or downloaded for this one use (deleted after it).</summary>
    private sealed record Package(string Path, bool Kept);

    /// <summary>
    /// The package at <paramref name="url"/> on disk: the kept one when it is there and still the file GitHub lists, or
    /// downloaded - kept when <see cref="AppOptions.KeepDnnPackages"/> is on, otherwise into the admin-only temporary
    /// folder. A failure (mismatch, download) is a <see cref="Result"/> or an exception the caller turns into one.
    /// </summary>
    private async Task<Result<Package>> PackageAsync(string url, string? sha256, string what, string keptName, IProgressReporter reporter,
        CancellationToken ct)
    {
        var keep = _opts.KeepDnnPackages;
        var zipPath = keep
            ? KeptPath(url, keptName)
            // Not in the project: the files DNN Manager unpacks with administrator rights come from where only
            // administrators can change them.
            : Path.Combine(PrivateTemp.Path, $"DnnManager-{Path.GetFileNameWithoutExtension(keptName)}-{Guid.NewGuid():N}.zip");
        if (keep && File.Exists(zipPath))
        {
            if (KeptMismatch(zipPath, sha256) is { } changed) return Result<Package>.Fail(changed);
            reporter.Info($"Using the kept {what} {zipPath} - no download needed.");
            return Result<Package>.Ok(new Package(zipPath, true));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        await DownloadAsync(url, what, zipPath, sha256, reporter, ct);
        reporter.Success(keep ? $"Downloaded and kept: {zipPath}" : "Downloaded.");
        return Result<Package>.Ok(new Package(zipPath, keep));
    }

    /// <summary>Deletes a package downloaded for one use - kept ones stay.</summary>
    private static void Discard(Package? package)
    {
        if (package is { Kept: false }) try { File.Delete(package.Path); } catch { /* best effort */ }
    }

    public async Task<Result> ExtractUpgradeAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct)
    {
        if (release.UpgradeUrl is not { } url) return Result.Fail($"DNN {release.Version}'s release has no upgrade package beside its install package.");
        Package? package = null;
        try
        {
            var got = await PackageAsync(url, release.UpgradeSha256, $"DNN {release.Version}'s upgrade package",
                $"DNN_Platform_{release.Version}_Upgrade.zip", reporter, ct);
            if (!got.Success) return Result.Fail(got.Error!);
            package = got.Value!;

            reporter.Info("Putting the new files in…");
            int count;
            try
            {
                count = await Task.Run(() => ExtractOver(package.Path, projectDirectory, reporter, ct), ct);
            }
            catch (InvalidDataException) when (package.Kept)
            {
                File.Delete(package.Path);
                return Result.Fail($"The kept upgrade package {package.Path} is damaged and has been deleted - try again to download it.");
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
            Discard(package);
        }
    }

    /// <summary>
    /// Every file of the package over <paramref name="directory"/>, overwriting - but never web.config (the site's own
    /// settings, connection string and machine keys), never outside the folder, on another file's stream or through a
    /// link (<see cref="SafeZip"/>). The number of files written.
    /// </summary>
    private static int ExtractOver(string zipPath, string directory, IProgressReporter reporter, CancellationToken ct, bool keepWebConfig = true)
    {
        var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => !SafeZip.IsFolder(e)).ToList();
        SafeZip.EnsureFits(files, directory);
        // Every entry's place first: a package with one unsafe path writes nothing.
        var targets = files.Select(e => (Entry: e, Target: SafeZip.Target(directory, SafeZip.Name(e), checkedFolders)))
            // Compared as the path it resolves to: "./web.config", "x/../web.config" and WEB~1.CON are web.config too.
            .Where(t => !keepWebConfig || !SafeZip.Relative(directory, t.Target).Equals("web.config", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var count = 0;
        var next = 0L;
        foreach (var (entry, target) in targets)
        {
            ct.ThrowIfCancellationRequested();
            SafeZip.Write(directory, entry, target, checkedFolders);
            count++;
            if (Environment.TickCount64 < next) continue;
            next = Environment.TickCount64 + 250;
            reporter.Progress($"Putting the new files in: {count:N0} of {files.Count:N0}");
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
        Package? package = null;
        try
        {
            var got = await PackageAsync(release.DownloadUrl, release.Sha256, $"DNN {release.Version}'s install package",
                $"DNN_Platform_{release.Version}_Install.zip", reporter, ct);
            if (!got.Success) return Result.Fail(got.Error!);
            package = got.Value!;
            reporter.Info("Putting the new files in as DNN's own upgrade does: each assembly with its binding redirect, then the rest…");
            var (assemblies, files) = await Task.Run(() => LocalUpgradeOver(package.Path, projectDirectory, reporter, ct), ct);
            reporter.Success($"{assemblies:N0} assemblies (with their binding redirects) and {files:N0} other files of DNN {release.Version} are in.");
            return Result.Ok();
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "DNN install package download failed");
            return Result.Fail($"DNN {release.Version}'s install package could not be downloaded ({ex.Message}).");
        }
        catch (RefusedException ex)
        {
            return Result.Fail(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            if (package is { Kept: true }) File.Delete(package.Path);
            return Result.Fail($"DNN {release.Version}'s install package is damaged ({ex.Message}){(package is { Kept: true } ? " and has been deleted - try again to download it" : "")}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Putting in DNN {Version}'s files failed", release.Version);
            return Result.Fail(ex.Message);
        }
        finally
        {
            Discard(package);
        }
    }

    /// <summary>
    /// DNN's <c>LocalUpgradeService.StartLocalUpgrade</c>, done from outside the site: bin's assemblies first - each copied
    /// in and, when it is strong-named, web.config's binding redirect for it pointed at its version (DNN's
    /// <c>BindingRedirect.config</c> merge) - then every other file but the excluded ones. The numbers written.
    /// </summary>
    /// <remarks>
    /// The excluded names (DNN's <c>upgradeExclude</c>) are compared with the path each entry resolves to in the site -
    /// "./web.config", "x/../web.config", a short name (<c>WEB~1.CON</c>) or "./App_Data/Database.mdf" are excluded as
    /// what they are.
    /// </remarks>
    internal static (int Assemblies, int Files) LocalUpgradeOver(string zipPath, string directory, IProgressReporter reporter, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        using var zip = ZipFile.OpenRead(zipPath);
        var exclude = DefaultUpgradeExclude;
        var minimum = (string?)null;
        if (zip.Entries.FirstOrDefault(e => SafeZip.Name(e).Equals("App_Data/Upgrade/upgrade.json", StringComparison.OrdinalIgnoreCase)) is { } info)
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
        // As DNN compares them: the start of the path from the site's folder, '/' between its folders.
        var excluded = exclude.Select(x => x.Replace('\\', '/').TrimStart('.', '/')).Where(x => x.Length > 0).ToArray();
        bool Excluded(string relative) => excluded.Any(x => relative.StartsWith(x, StringComparison.OrdinalIgnoreCase));

        var files = zip.Entries.Where(e => !SafeZip.IsFolder(e)).ToList();
        SafeZip.EnsureFits(files, root);
        var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Where each entry lands - resolved once, so "bin/./x.dll" is an assembly of bin like "bin/x.dll".
        var targets = files.Select(e => (Entry: e, Target: SafeZip.Target(root, SafeZip.Name(e), checkedFolders))).ToList();
        var dlls = targets.Where(t => Path.GetDirectoryName(SafeZip.Relative(root, t.Target))!.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                                      t.Target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();

        // The assemblies, and the binding redirects that make the site use them.
        var webConfig = Path.Combine(root, "web.config");
        var config = SiteXml.Load(webConfig, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var redirects = 0;
        foreach (var (entry, target) in dlls)
        {
            ct.ThrowIfCancellationRequested();
            SafeZip.Write(root, entry, target, checkedFolders);
            if (StrongName(target) is { } assembly && SetBindingRedirect(config, assembly.Name, assembly.Token, assembly.Version)) redirects++;
        }
        if (redirects > 0)
        {
            using var writer = System.Xml.XmlWriter.Create(webConfig, new System.Xml.XmlWriterSettings { OmitXmlDeclaration = config.Declaration is null, Indent = false });
            config.Save(writer);
        }
        reporter.Info($"{dlls.Count:N0} assemblies in bin, {redirects:N0} binding redirects in web.config set to them.");

        // Everything else, as DNN unzips it.
        var rest = targets.Except(dlls).Where(t => !Excluded(SafeZip.Relative(root, t.Target))).ToList();
        var count = 0;
        var next = 0L;
        foreach (var (entry, target) in rest)
        {
            ct.ThrowIfCancellationRequested();
            SafeZip.Write(root, entry, target, checkedFolders);
            count++;
            if (Environment.TickCount64 < next) continue;
            next = Environment.TickCount64 + 250;
            reporter.Progress($"Putting the new files in: {count:N0} of {rest.Count:N0}");
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
        Package? package = null;
        try
        {
            var got = await PackageAsync(release.DownloadUrl, release.Sha256, $"DNN {release.Version}",
                $"DNN_Platform_{release.Version}_Install.zip", reporter, ct);
            if (!got.Success) return Result.Fail(got.Error!);
            package = got.Value!;

            reporter.Info("Extracting…");
            try
            {
                Directory.CreateDirectory(projectDirectory);
                await Task.Run(() => ExtractOver(package.Path, projectDirectory, reporter, ct, keepWebConfig: false), ct);
            }
            catch (InvalidDataException) when (package.Kept)
            {
                // A damaged kept package would fail every time - drop it so the next try downloads it again.
                File.Delete(package.Path);
                return Result.Fail($"The kept package {package.Path} is damaged and has been deleted - try again to download it.");
            }
            reporter.Success("Extraction complete.");
            return Result.Ok();
        }
        catch (HttpRequestException ex)
        {
            // Offline, most likely: only a kept package installs without a download.
            _log.LogError(ex, "DNN download failed");
            return Result.Fail($"DNN {release.Version} could not be downloaded ({ex.Message}). " + (_opts.KeepDnnPackages
                ? "Without internet only a version marked \"kept, no download\" can be set up."
                : "To set up projects without internet, turn on Settings → DNN releases → Keep downloaded DNN install packages while online."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "DNN install failed");
            return Result.Fail(ex.Message);
        }
        finally
        {
            // Downloaded for this one use: gone whether it worked or not - it isn't left in the temporary folder.
            Discard(package);
        }
    }

    /// <summary>Downloads a package (<paramref name="what"/>: "DNN 10.3.3") through a temporary file, so a failed or cancelled download never looks complete.</summary>
    /// <remarks>
    /// Only from https (or this computer): over plain http anyone on the way could change the package that becomes the
    /// site - also when GitHub's address sends it on (a redirect) to one that isn't https. With GitHub's SHA-256 of the
    /// file (<paramref name="sha256"/>) it must match - releases from before GitHub kept one have none.
    /// </remarks>
    private async Task DownloadAsync(string url, string what, string zipPath, string? sha256, IProgressReporter reporter, CancellationToken ct)
    {
        if (!SettingRules.IsReleaseSource(url)) throw new RefusedException($"{what} isn't downloaded from {url}: it isn't https - only https addresses are.");
        reporter.Info($"Downloading {what}…");
        var tmp = zipPath + ".download";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            // Sent on to an address that isn't https: HttpClient doesn't follow it (it answers with the redirect), or did.
            if (RedirectNotHttps(response) is { } elsewhere)
                throw new RefusedException($"{what} isn't downloaded: {url} sends it on to {elsewhere}, which isn't https - only https addresses are.");
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long done = 0;
                var next = 0L;
                int read;
                while ((read = await StalledRead.ReadAsync(input, buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (Environment.TickCount64 < next) continue;
                    next = Environment.TickCount64 + 250;
                    reporter.Progress(total is > 0
                        ? $"Downloading {what}: {done * 100 / total.Value}% ({done / 1048576d:N1} of {total.Value / 1048576d:N1} MB)"
                        : $"Downloading {what}: {done / 1048576d:N1} MB");
                }
            }
            if (sha256 is not null && !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(sha256, StringComparison.Ordinal))
                throw new RefusedException($"{what} from {url} isn't the file GitHub lists for the release (its SHA-256 differs) - it was changed on the way, and isn't used.");
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>The address a response was sent on to when that isn't https (or this computer); null when it wasn't sent on so.</summary>
    internal static string? RedirectNotHttps(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage?.RequestUri ?? new Uri("https://localhost/"), location);
            return SettingRules.IsReleaseSource(target.AbsoluteUri) ? null : target.AbsoluteUri;
        }
        // Followed already: where it ended up.
        return response.RequestMessage?.RequestUri is { } final && !SettingRules.IsReleaseSource(final.AbsoluteUri) ? final.AbsoluteUri : null;
    }

    /// <summary>
    /// Why the kept package at <paramref name="zipPath"/> can't be used - it isn't the file GitHub lists any more (it is in
    /// Documents, which any program of the user can change); it is deleted then, so the next try downloads it again.
    /// Null when it matches, or when GitHub gives no SHA-256 for it.
    /// </summary>
    private static string? KeptMismatch(string zipPath, string? sha256)
    {
        if (sha256 is null) return null;
        using (var stream = File.OpenRead(zipPath))
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)).Equals(sha256, StringComparison.Ordinal))
                return null;
        File.Delete(zipPath);
        return $"The kept package {zipPath} isn't the file GitHub lists for the release any more (its SHA-256 differs) - it has been deleted; try again to download it.";
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
