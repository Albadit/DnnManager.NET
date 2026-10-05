using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>DNN Manager's own update: the release feed, the download's checks, the helper's swap, the saved session.</summary>
[TestClass]
public sealed class AppUpdateTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerUpdateTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a process may still hold a file */ }
    }

    // ─── The release feed ─────────────────────────────────────────────────

    private const string LatestJson = """
        {
          "tag_name": "v1.7.0",
          "html_url": "https://github.com/Albadit/DnnManager.NET/releases/tag/v1.7.0",
          "assets": [
            { "name": "DnnManager-1.7.0-x64.exe", "size": 100, "digest": "sha256:ABCDEF", "browser_download_url": "https://example.test/DnnManager-1.7.0-x64.exe" },
            { "name": "DnnManagerSetup-1.7.0-x64.exe", "size": 50, "digest": null, "browser_download_url": "https://example.test/DnnManagerSetup-1.7.0-x64.exe" }
          ]
        }
        """;

    [TestMethod]
    public async Task The_feed_reads_the_release_and_picks_the_file_for_the_kind_of_installation()
    {
        var feed = new AppReleaseFeed(new HttpClient(new Answer(HttpStatusCode.OK, LatestJson)));
        var release = await feed.GetLatestAsync(CancellationToken.None);

        Assert.AreEqual("v1.7.0", release.Tag);
        Assert.AreEqual(new Version(1, 7, 0), release.Version);
        Assert.AreEqual("DnnManagerSetup-1.7.0-x64.exe", release.AssetFor(UpdateKind.Installer)?.Name);
        var portable = release.AssetFor(UpdateKind.Portable);
        Assert.AreEqual("DnnManager-1.7.0-x64.exe", portable?.Name);
        Assert.AreEqual("abcdef", portable?.Sha256);
        Assert.IsNull(release.AssetFor(UpdateKind.Installer)?.Sha256);
    }

    [TestMethod]
    public async Task GitHub_not_answering_is_an_update_exception()
    {
        var feed = new AppReleaseFeed(new HttpClient(new Answer(HttpStatusCode.Forbidden, "rate limited")));
        var error = await Assert.ThrowsExactlyAsync<AppUpdateException>(() => feed.GetLatestAsync(CancellationToken.None));
        StringAssert.Contains(error.Message, "403");

        var offline = new AppReleaseFeed(new HttpClient(new Unreachable()));
        await Assert.ThrowsExactlyAsync<AppUpdateException>(() => offline.GetLatestAsync(CancellationToken.None));
    }

    [TestMethod]
    public void Versions_compare_by_major_minor_and_build()
    {
        Assert.IsTrue(AppReleaseFeed.IsNewer(new Version(1, 7, 0), new Version(1, 6, 0)));
        Assert.IsTrue(AppReleaseFeed.IsNewer(new Version(2, 0), new Version(1, 9, 9)));
        Assert.IsFalse(AppReleaseFeed.IsNewer(new Version(1, 6, 0), new Version(1, 6, 0, 0)));
        Assert.IsFalse(AppReleaseFeed.IsNewer(new Version(1, 5, 9), new Version(1, 6, 0)));
        Assert.IsTrue(AppReleaseFeed.TryParseVersion("v1.7.0-rc.1", out var rc));
        Assert.AreEqual(new Version(1, 7, 0), rc);
        Assert.IsTrue(AppReleaseFeed.TryParseVersion("1.6.0+27f0e36a5f59", out var built));
        Assert.AreEqual(new Version(1, 6, 0), built);
    }

    // ─── The download ─────────────────────────────────────────────────────

    // A file with a version in it: DNN Manager's own assembly (ProductVersion "1.6.0+commit").
    private static (byte[] Bytes, Version Version) VersionedFile()
    {
        var path = typeof(AppUpdater).Assembly.Location;
        Assert.IsTrue(AppReleaseFeed.TryParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion!, out var version));
        return (File.ReadAllBytes(path), version);
    }

    private static AppRelease ReleaseOf(Version version, byte[] bytes, string? sha256 = null) =>
        new($"v{version}", version, "https://example.test", [new AppReleaseAsset("DnnManager-x-x64.exe", bytes.Length,
            sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "https://example.test/file")]);

    [TestMethod]
    public async Task A_download_that_matches_the_release_is_kept()
    {
        var (bytes, version) = VersionedFile();
        var release = ReleaseOf(version, bytes);
        var progress = new List<double>();
        var path = await new UpdateDownloader(new HttpClient(new Answer(HttpStatusCode.OK, bytes)))
            .DownloadAsync(release, release.Assets[0], _dir, new SyncProgress(progress.Add), CancellationToken.None);

        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(1.0, progress[^1]);
        Assert.IsFalse(File.Exists(path + ".partial"));
    }

    [TestMethod]
    public async Task A_download_that_isnt_the_release_is_refused_and_removed()
    {
        var (bytes, version) = VersionedFile();
        var cases = new (AppRelease Release, string Problem)[]
        {
            (ReleaseOf(version, bytes, sha256: new string('0', 64)), "SHA-256"),
            (ReleaseOf(new Version(version.Major + 1, 0, 0), bytes), "version"),
            (ReleaseOf(version, bytes) with { Assets = [new AppReleaseAsset("DnnManager-x-x64.exe", bytes.Length + 1, null, "https://example.test/file")] }, "bytes"),
        };
        foreach (var (release, problem) in cases)
        {
            var downloader = new UpdateDownloader(new HttpClient(new Answer(HttpStatusCode.OK, bytes)));
            var error = await Assert.ThrowsExactlyAsync<AppUpdateException>(() =>
                downloader.DownloadAsync(release, release.Assets[0], _dir, null, CancellationToken.None));
            StringAssert.Contains(error.Message, problem);
            Assert.AreEqual(0, Directory.GetFiles(_dir).Length, $"Nothing is left of a refused download ({problem}).");
        }
    }

    // ─── What is updated ──────────────────────────────────────────────────

    [TestMethod]
    public void The_installation_is_told_from_a_portable_exe_and_a_development_build()
    {
        var installs = new[] { (@"C:\Users\me\AppData\Local\Programs\DnnManager\", false), (@"C:\Program Files\DnnManager", true) };

        Assert.AreEqual(new UpdateTarget(UpdateKind.Installer, @"C:\Users\me\AppData\Local\Programs\DnnManager\DnnManager.exe", false),
            UpdateTarget.Detect(@"C:\Users\me\AppData\Local\Programs\DnnManager\DnnManager.exe", true, installs));
        Assert.AreEqual(new UpdateTarget(UpdateKind.Installer, @"C:\program files\dnnmanager\DnnManager.exe", true),
            UpdateTarget.Detect(@"C:\program files\dnnmanager\DnnManager.exe", true, installs), "Folders compare without case.");
        Assert.AreEqual(new UpdateTarget(UpdateKind.Portable, @"D:\Tools\DnnManager-1.6.0-x64.exe"),
            UpdateTarget.Detect(@"D:\Tools\DnnManager-1.6.0-x64.exe", true, installs));
        Assert.IsNull(UpdateTarget.Detect(@"D:\src\DnnManager.NET\bin\Debug\net10.0-windows\DnnManager.exe", false, installs));
    }

    [TestMethod]
    public void Setup_runs_silently_into_the_installation_it_updates()
    {
        var mine = UpdateHelper.SetupArguments(false, @"C:\t\setup.log");
        CollectionAssert.IsSubsetOf(new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER", @"/LOG=C:\t\setup.log" }, mine.ToList());
        CollectionAssert.Contains(UpdateHelper.SetupArguments(true, "x").ToList(), "/ALLUSERS");
    }

    // ─── The helper ───────────────────────────────────────────────────────

    private UpdatePlan PortablePlan(int waitFor) => new()
    {
        Kind = UpdateKind.Portable, Package = Path.Combine(_dir, "new.exe"), AppExe = Path.Combine(_dir, "DnnManager-1.6.0-x64.exe"),
        WaitForProcessId = waitFor, FromVersion = "1.6.0", ToVersion = "1.7.0",
        LogFile = Path.Combine(_dir, "update.log"), ResultFile = Path.Combine(_dir, "result.json")
    };

    private static int ExitedProcessId()
    {
        using var process = Exit(0);
        process.WaitForExit();
        return process.Id;
    }

    private static Process Exit(int code) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c exit {code}") { CreateNoWindow = true, UseShellExecute = false })!;

    [TestMethod]
    public void The_portable_exe_is_replaced_in_place_and_started_again()
    {
        var plan = PortablePlan(ExitedProcessId());
        File.WriteAllText(plan.AppExe, "old");
        File.WriteAllText(plan.Package, "new");
        var started = new List<string>();

        var code = UpdateHelper.Run(plan, info => { started.Add(info.FileName); return null; }, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, code);
        Assert.AreEqual("new", File.ReadAllText(plan.AppExe), "Under its old name - shortcuts keep working.");
        Assert.IsFalse(File.Exists(plan.AppExe + ".old"), "The backup goes once the new one runs.");
        Assert.IsFalse(File.Exists(plan.AppExe + ".new"));
        CollectionAssert.AreEqual(new[] { plan.AppExe }, started);
        Assert.IsTrue(UpdateResult.TryRead(plan.ResultFile)?.Installed);
    }

    [TestMethod]
    public void A_new_portable_exe_that_fails_to_start_is_swapped_back()
    {
        var plan = PortablePlan(ExitedProcessId());
        File.WriteAllText(plan.AppExe, "old");
        File.WriteAllText(plan.Package, "new");
        var launches = 0;

        // The new version closes with an error at once; the old one, started after, is fine.
        var code = UpdateHelper.Run(plan, _ => ++launches == 1 ? Exit(3) : null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, code);
        Assert.AreEqual("old", File.ReadAllText(plan.AppExe));
        Assert.AreEqual(2, launches, "The old version is started again.");
        var result = UpdateResult.TryRead(plan.ResultFile);
        Assert.IsFalse(result?.Installed);
        StringAssert.Contains(result!.Message, "1.6.0 was put back");
    }

    [TestMethod]
    public void Nothing_is_changed_while_DNN_Manager_is_still_open()
    {
        var plan = PortablePlan(Environment.ProcessId);
        File.WriteAllText(plan.AppExe, "old");
        File.WriteAllText(plan.Package, "new");
        var launches = 0;

        var code = UpdateHelper.Run(plan, _ => { launches++; return null; }, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));

        Assert.AreEqual(1, code);
        Assert.AreEqual("old", File.ReadAllText(plan.AppExe));
        Assert.AreEqual(0, launches, "It is open - nothing is started.");
        Assert.IsFalse(UpdateResult.TryRead(plan.ResultFile)?.Installed);
    }

    [TestMethod]
    public void Setup_failing_starts_the_old_version_again_and_says_why()
    {
        var plan = PortablePlan(ExitedProcessId()) with { Kind = UpdateKind.Installer, Package = Path.Combine(_dir, "DnnManagerSetup-1.7.0-x64.exe") };
        var started = new List<string>();

        var code = UpdateHelper.Run(plan, info =>
        {
            started.Add(Path.GetFileName(info.FileName));
            return info.FileName == plan.Package ? Exit(5) : null;
        }, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, code);
        CollectionAssert.AreEqual(new[] { "DnnManagerSetup-1.7.0-x64.exe", "DnnManager-1.6.0-x64.exe" }, started);
        Assert.AreEqual("Setup was cancelled.", UpdateResult.TryRead(plan.ResultFile)?.Message);
    }

    [TestMethod]
    public void Setup_succeeding_starts_the_installed_version()
    {
        var plan = PortablePlan(ExitedProcessId()) with { Kind = UpdateKind.Installer, Package = Path.Combine(_dir, "DnnManagerSetup-1.7.0-x64.exe") };
        var started = new List<string>();

        var code = UpdateHelper.Run(plan, info =>
        {
            started.Add(Path.GetFileName(info.FileName));
            return info.FileName == plan.Package ? Exit(0) : null;
        }, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, code);
        CollectionAssert.AreEqual(new[] { "DnnManagerSetup-1.7.0-x64.exe", "DnnManager-1.6.0-x64.exe" }, started);
        Assert.IsTrue(UpdateResult.TryRead(plan.ResultFile)?.Installed);
    }

    // ─── The real release ─────────────────────────────────────────────────

    /// <summary>
    /// GitHub's newest DNN Manager release, downloaded and checked like the Update button does, and installed by the
    /// helper over a stand-in for the old portable exe - which then is that release. (The helper isn't let start it:
    /// DNN Manager would ask for Administrator rights.)
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task The_newest_real_release_downloads_checks_and_installs()
    {
        AppRelease release;
        try { release = await new AppReleaseFeed(new HttpClient()).GetLatestAsync(CancellationToken.None); }
        catch (AppUpdateException ex) { Assert.Inconclusive($"GitHub can't be reached: {ex.Message}"); return; }

        var setup = release.AssetFor(UpdateKind.Installer);
        var portable = release.AssetFor(UpdateKind.Portable);
        Assert.IsNotNull(setup, $"{release.Tag} has a Setup for this PC.");
        Assert.IsNotNull(portable, $"{release.Tag} has a portable exe for this PC.");
        Assert.IsNotNull(portable.Sha256, "GitHub lists the file's SHA-256.");

        var package = await new UpdateDownloader(new HttpClient()).DownloadAsync(release, portable, Path.Combine(_dir, "download"), null, CancellationToken.None);
        Assert.IsNull(UpdateDownloader.Problem(package, release, portable));

        var plan = PortablePlan(ExitedProcessId()) with { Package = package, ToVersion = release.Version.ToString() };
        File.WriteAllText(plan.AppExe, "the old version");
        var code = UpdateHelper.Run(plan, _ => null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, code, File.Exists(plan.LogFile) ? File.ReadAllText(plan.LogFile) : "no log");
        Assert.IsTrue(AppReleaseFeed.TryParseVersion(FileVersionInfo.GetVersionInfo(plan.AppExe).ProductVersion!, out var installed));
        Assert.AreEqual(release.Version, installed);
    }

    // ─── Test doubles ─────────────────────────────────────────────────────

    private sealed class Answer(HttpStatusCode status, object body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent((string)body)
            });
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known.");
    }

    // Progress<T> posts to a synchronization context; this reports at once.
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
