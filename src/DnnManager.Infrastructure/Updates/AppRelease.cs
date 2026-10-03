using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DnnManager.Infrastructure.Updates;

/// <summary>A file of a DNN Manager release on GitHub, with the SHA-256 GitHub computed for it (null when it gives none).</summary>
public sealed record AppReleaseAsset(string Name, long Size, string? Sha256, string Url);

/// <summary>A published DNN Manager release: its tag, version, page and files.</summary>
public sealed record AppRelease(string Tag, Version Version, string PageUrl, IReadOnlyList<AppReleaseAsset> Assets)
{
    /// <summary>
    /// The file that updates this kind of DNN Manager: Setup for an installed one, the portable exe for a portable one -
    /// for this process's architecture, as the release workflow names them (<c>DnnManagerSetup-1.7.0-x64.exe</c>,
    /// <c>DnnManager-1.7.0-x64.exe</c>). Null when the release has none.
    /// </summary>
    public AppReleaseAsset? AssetFor(UpdateKind kind)
    {
        var prefix = kind == UpdateKind.Installer ? "DnnManagerSetup-" : "DnnManager-";
        var suffix = $"-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}.exe";
        return Assets.FirstOrDefault(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                          a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Why the newest release couldn't be found, downloaded or installed - in words for the user.</summary>
public sealed class AppUpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The newest DNN Manager release, from GitHub's "latest release" - which is never a draft or a pre-release.
/// </summary>
public sealed class AppReleaseFeed(HttpClient http, string latestReleaseUrl = AppReleaseFeed.LatestReleaseUrl)
{
    public const string Repository = "https://github.com/Bond-for-web-solutions/DnnManager.NET";
    public const string LatestReleaseUrl = "https://api.github.com/repos/Bond-for-web-solutions/DnnManager.NET/releases/latest";

    /// <summary>The newest release. Throws <see cref="AppUpdateException"/> when GitHub can't be reached or its answer can't be used.</summary>
    public async Task<AppRelease> GetLatestAsync(CancellationToken ct)
    {
        GhRelease? release;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("DnnManager");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new AppUpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
            release = await response.Content.ReadFromJsonAsync<GhRelease>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException ||
                                   ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AppUpdateException(ex is TaskCanceledException ? "GitHub didn't answer in time" : ex.Message, ex);
        }

        if (release?.Tag is not { } tag || !TryParseVersion(tag, out var version))
            throw new AppUpdateException("GitHub's answer has no version");
        var assets = (release.Assets ?? [])
            .Where(a => a.Name is not null && a.Url is not null)
            .Select(a => new AppReleaseAsset(a.Name!, a.Size, Sha256Of(a.Digest), a.Url!))
            .ToList();
        return new AppRelease(tag, version, release.Url ?? $"{Repository}/releases/tag/{tag}", assets);
    }

    /// <summary>"v1.7.0" or "1.7.0" - a pre-release suffix ("-rc.1") is left out.</summary>
    public static bool TryParseVersion(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out version!);

    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="current"/> - by major, minor and build only.</summary>
    public static bool IsNewer(Version latest, Version current) =>
        Normalize(latest) > Normalize(current);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // "sha256:677af2…" - GitHub's digest of the file.
    private static string? Sha256Of(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;

    private sealed record GhRelease(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("html_url")] string? Url,
        [property: JsonPropertyName("assets")] List<GhAsset>? Assets);

    private sealed record GhAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? Url);
}
