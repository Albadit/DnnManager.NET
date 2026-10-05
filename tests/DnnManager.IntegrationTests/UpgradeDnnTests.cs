using System.IO.Compression;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Dnn;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Upgrade DNN and Restore backup without IIS or SQL Server: the upgrade package's files over a site (never its
/// web.config), what DNN's upgrade page says, and a restore that leaves the folder as the backup has it.
/// </summary>
[TestClass]
public sealed class UpgradeDnnTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerUpgradeTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [TestMethod]
    public void TheUpgradePackage_IsBesideTheInstallPackage()
    {
        var release = new DnnRelease("10.3.3", "v10.3.3", "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.3.3/DNN_Platform_10.3.3_Install.zip");
        Assert.AreEqual("https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.3.3/DNN_Platform_10.3.3_Upgrade.zip", release.UpgradeUrl);
        Assert.IsNull((release with { DownloadUrl = "https://example.com/dnn.zip" }).UpgradeUrl, "Not named as DNN names it: none.");
        Assert.AreEqual(new Version(10, 1, 0), DnnInstall.Number("v10.1.0-rc1"));
    }

    [TestMethod]
    public async Task TheUpgradePackage_GoesOverTheSite_ButNeverOverItsWebConfig()
    {
        var site = Directory.CreateDirectory(Path.Combine(_dir, "site")).FullName;
        File.WriteAllText(Path.Combine(site, "web.config"), "<configuration>the site's own</configuration>");
        Directory.CreateDirectory(Path.Combine(site, "bin"));
        File.WriteAllText(Path.Combine(site, "bin", "DotNetNuke.dll"), "old");

        // A kept package: no download.
        var paths = new AppDataPaths(Path.Combine(_dir, "data"));
        var release = new DnnRelease("10.3.3", "v10.3.3", "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.3.3/DNN_Platform_10.3.3_Install.zip");
        var kept = Path.Combine(paths.PackagesDirectory, "dnnsoftware.Dnn.Platform", "DNN_Platform_10.3.3_Upgrade.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        using (var zip = ZipFile.Open(kept, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            Add("web.config", "<configuration>the package's</configuration>");
            Add("bin/DotNetNuke.dll", "new");
            Add("Install/Install.aspx", "installer");
            Add("../outside.txt", "never");
        }

        var installer = new DnnPackageInstaller(new HttpClient(), Options.Create(new AppOptions { KeepDnnPackages = true }), paths,
            NullLogger<DnnPackageInstaller>.Instance);
        var result = await installer.ExtractUpgradeAsync(release, site, new RecordingReporter(), CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(site, "bin", "DotNetNuke.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(site, "Install", "Install.aspx")));
        Assert.AreEqual("<configuration>the site's own</configuration>", File.ReadAllText(Path.Combine(site, "web.config")), "web.config stays the site's.");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "outside.txt")), "Nothing outside the site's folder.");
        Assert.IsTrue(File.Exists(kept), "A kept package stays kept.");
    }

    [TestMethod]
    public async Task ARestore_DeletesOnlyWhatWasAddedSinceTheBackup()
    {
        var site = Directory.CreateDirectory(Path.Combine(_dir, "site")).FullName;
        File.WriteAllText(Path.Combine(site, "web.config"), "<configuration />");
        File.WriteAllText(Path.Combine(site, "default.aspx"), "page");
        Directory.CreateDirectory(Path.Combine(site, "App_Data", "Search"));
        var copier = new ProjectFileCopier();
        var backup = Path.Combine(_dir, "backup.zip");
        var reporter = new RecordingReporter();
        Assert.IsTrue((await copier.CreateZipAsync(site, backup, ["App_Data\\Search"], reporter, CancellationToken.None)).Success);
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddMinutes(-1));

        // After the backup: an upgrade added a file, the search index (left out of backups) one of its own.
        File.WriteAllText(Path.Combine(site, "added.dll"), "new");
        File.WriteAllText(Path.Combine(site, "App_Data", "Search", "index.dat"), "index");
        // A file older than the backup that it doesn't hold (in use while the backup was made): it stays.
        var skipped = Path.Combine(site, "in-use.log");
        File.WriteAllText(skipped, "log");
        File.SetCreationTimeUtc(skipped, DateTime.UtcNow.AddHours(-1));

        var removed = await copier.RemoveFilesNotInZipAsync(backup, site, ["App_Data\\Search"], reporter, CancellationToken.None);

        Assert.AreEqual(1, removed.Value, reporter.Text);
        Assert.IsFalse(File.Exists(Path.Combine(site, "added.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(site, "default.aspx")));
        Assert.IsTrue(File.Exists(Path.Combine(site, "App_Data", "Search", "index.dat")), "What backups leave out isn't touched.");
        Assert.IsTrue(File.Exists(skipped));
    }

    [TestMethod]
    public void DnnsUpgradePage_IsReadForWhatItSays()
    {
        static DnnInstallOutput Page(string html)
        {
            var output = new DnnInstallOutput();
            output.Add(html);
            output.Finish();
            return output;
        }

        Assert.IsTrue(Page("<h1>Upgrading DNN</h1><h2>Upgrade Status Report</h2>00:00:01.000 - Upgrading to Version: 10.3.3<font color='green'>Success</font><br><h2>Upgrade Complete</h2>")
            .UpgradeOutcome().Success);
        Assert.IsTrue(Page("<h1>Upgrade</h1><h2>Current Assembly Version and current Database Version are identical</h2>").UpgradeOutcome().Success,
            "Nothing to upgrade: the version check after it tells.");
        var failed = Page("<h1>Upgrading DNN</h1>00:00:01.000 - Upgrading to Version: 10.3.3<font color='red'>Error!</font><br><h2>Upgrade Complete</h2>").UpgradeOutcome();
        Assert.IsFalse(failed.Success, "An error, even when it says Complete.");
        StringAssert.Contains(failed.Error, "Upgrading to Version: 10.3.3");
        Assert.IsFalse(Page("<h1>Upgrading DNN</h1>00:00:01.000 - Upgrading to Version: 10.3.3<br>").UpgradeOutcome().Success, "Cut off before the end.");
    }

    [TestMethod]
    public void WhatIsKept_IsNotTakenBack()
    {
        var undo = new OperationUndo();
        var takenBack = new List<string>();
        undo.Add("the backup", () => { takenBack.Add("backup"); return Result.Ok(); });
        undo.Keep();
        undo.Add("the files", () => { takenBack.Add("files"); return Result.Ok(); });

        Assert.IsTrue(undo.RunAsync(new RecordingReporter()).Result);
        CollectionAssert.AreEqual(new[] { "files" }, takenBack);
    }
}
