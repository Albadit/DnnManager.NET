using System.Net;
using System.Text;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>New project's DNN versions without internet: the releases saved at the last lookup.</summary>
[TestClass]
public sealed class DnnReleaseOfflineTests
{
    private const string Api = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases";

    private const string ReleasesJson = """
        [
          {
            "tag_name": "v10.0.1", "prerelease": false, "draft": false,
            "assets": [ { "name": "DNN_Platform_10.0.1_Install.zip",
                          "browser_download_url": "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.0.1/DNN_Platform_10.0.1_Install.zip" } ]
          },
          {
            "tag_name": "v9.13.9", "prerelease": false, "draft": false,
            "assets": [ { "name": "DNN_Platform_9.13.9_Install.zip",
                          "browser_download_url": "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v9.13.9/DNN_Platform_9.13.9_Install.zip" } ]
          }
        ]
        """;

    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerReleaseTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private GitHubDnnReleaseService Service(bool online, bool keepPackages = true) =>
        new(new HttpClient(new Stub(online)), Options.Create(new AppOptions { KeepDnnPackages = keepPackages }),
            new AppDataPaths(_dir), NullLogger<GitHubDnnReleaseService>.Instance);

    [TestMethod]
    public async Task Offline_TheReleasesSavedAtTheLastLookup_AreOffered()
    {
        var online = await Service(online: true).ListReleasesAsync(Api, CancellationToken.None);
        Assert.IsTrue(online.Success);
        Assert.IsNull(online.Value!.SavedAt, "From GitHub, not the saved list.");
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "packages", "dnnsoftware.Dnn.Platform", "releases.json")));

        var offline = await Service(online: false).ListReleasesAsync(Api, CancellationToken.None);
        Assert.IsTrue(offline.Success, offline.Error);
        Assert.IsNotNull(offline.Value!.SavedAt);
        CollectionAssert.AreEqual(new[] { "10.0.1", "9.13.9" }, offline.Value.Releases.Select(r => r.Version).ToArray());
        Assert.AreEqual(online.Value.Releases[1], offline.Value.Releases[1], "The same release - its download address too, which locates a kept package.");
    }

    [TestMethod]
    public async Task Offline_AChosenVersion_AndTheLatest_ComeFromTheSavedReleases()
    {
        await Service(online: true).ListReleasesAsync(Api, CancellationToken.None);
        var offline = Service(online: false);

        var chosen = await offline.GetReleaseAsync(Api, "9.13.9", CancellationToken.None);
        Assert.IsTrue(chosen.Success, chosen.Error);
        Assert.AreEqual("v9.13.9", chosen.Value!.TagName);

        var latest = await offline.GetReleaseAsync(Api, null, CancellationToken.None);
        Assert.IsTrue(latest.Success, latest.Error);
        Assert.AreEqual("10.0.1", latest.Value!.Version);

        var unknown = await offline.GetReleaseAsync(Api, "8.0.0", CancellationToken.None);
        Assert.IsFalse(unknown.Success, "A version that wasn't saved can't be looked up offline.");
    }

    [TestMethod]
    public async Task WithoutKeptPackages_NothingIsSaved_AndOfflineFails()
    {
        await Service(online: true, keepPackages: false).ListReleasesAsync(Api, CancellationToken.None);
        Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "packages")));

        var offline = await Service(online: false, keepPackages: false).ListReleasesAsync(Api, CancellationToken.None);
        Assert.IsFalse(offline.Success);
    }

    /// <summary>GitHub: the releases above - or, offline, no answer at all.</summary>
    private sealed class Stub(bool online) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!online) throw new HttpRequestException("No such host is known. (api.github.com:443)");
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path.EndsWith("/releases")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
