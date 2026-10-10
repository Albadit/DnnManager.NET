using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Github;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Terminal;
using DnnManager.Infrastructure.WebConfigs;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// What DNN Manager does with its administrator rights in folders others can write to - paths, zips, links, hard links,
/// a site's XML - and the programs it starts: the pure rules, and the extractions that use them.
/// </summary>
[TestClass]
public sealed class SecurityHardeningTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerHardeningTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { SafePath.DeleteTree(_dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    // ─── SafePath ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Under_is_strictly_inside_the_root_or_nothing()
    {
        var root = Folder("site");
        Assert.AreEqual(Path.Combine(root, "bin", "x.dll"), SafePath.Under(root, "bin/x.dll"));
        Assert.AreEqual(Path.Combine(root, "y.txt"), SafePath.Under(root, "x/../y.txt"), "Inside after .. is still inside.");
        Assert.AreEqual(Path.Combine(root, "a"), SafePath.Under(root + "\\", "a"), "A root with its separator.");
        // The root itself, outside, a stream, a drive, a server, nothing.
        foreach (var refused in new[] { "./", ".", "a/../", "a/..", "...", "..", "../site2/x", @"..\outside.dll", "C:x", @"C:\Windows\x.dll",
                                        "web.config::$DATA", "file.txt:stream", @"\\server\share\x", "", "a\0b" })
            Assert.IsNull(SafePath.Under(root, refused), $"'{refused}' was allowed.");
    }

    [TestMethod]
    public void A_drive_is_a_root_like_any_folder()
    {
        var drive = Path.GetPathRoot(_dir)!;
        Assert.IsTrue(SafePath.IsSameOrInside(_dir, drive));
        Assert.IsTrue(SafePath.IsInside(_dir, drive));
        Assert.IsTrue(SafePath.IsSameOrInside(drive, drive));
        Assert.IsFalse(SafePath.IsInside(drive, drive));
        Assert.AreEqual(Path.Combine(drive, "some-folder-that-isnt-there", "x"), SafePath.Under(drive, @"some-folder-that-isnt-there\x"));
        Assert.IsFalse(SafePath.HasLink(drive, _dir), "The temporary folder is reached through no link.");

        Assert.IsTrue(SafePath.IsSameOrInside(@"C:\DNN\site\..\other", @"C:\DNN"));
        Assert.IsFalse(SafePath.IsSameOrInside(@"C:\DNNX\site", @"C:\DNN"), "A folder whose name only starts with it.");
        Assert.IsFalse(SafePath.IsSameOrInside(@"C:\DNN\..\x", @"C:\DNN"));
    }

    [TestMethod]
    public void A_link_on_the_way_is_found_and_the_folders_checked_are_remembered()
    {
        var root = Folder("site");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        var remembered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.IsFalse(SafePath.HasLink(root, Path.Combine(root, "bin", "new", "x.dll"), remembered));
        CollectionAssert.Contains(remembered.ToList(), Path.Combine(root, "bin"));
        Assert.IsFalse(remembered.Contains(Path.Combine(root, "bin", "new")), "A folder that isn't there yet isn't remembered.");

        Junction(Path.Combine(root, "linked"), Folder("elsewhere"));
        Assert.IsTrue(SafePath.IsLink(Path.Combine(root, "linked")));
        Assert.IsTrue(SafePath.HasLink(root, Path.Combine(root, "linked", "x"), remembered));
        Assert.IsFalse(SafePath.IsLink(Path.Combine(root, "missing")), "What isn't there isn't a link.");
    }

    [TestMethod]
    public void DeleteTree_unlinks_a_junction_without_touching_what_it_points_to()
    {
        var root = Folder("tree");
        var elsewhere = Folder("elsewhere");
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllText(Path.Combine(root, "a", "b", "x.txt"), "x");
        File.SetAttributes(Path.Combine(root, "a", "b", "x.txt"), FileAttributes.ReadOnly);
        Junction(Path.Combine(root, "a", "link"), elsewhere);

        SafePath.DeleteTree(root);
        Assert.IsFalse(Directory.Exists(root));
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "keep.txt")));
    }

    [TestMethod]
    public void A_file_with_another_name_is_known_and_replaced_not_written_into()
    {
        var root = Folder("site");
        var outside = Path.Combine(Folder("outside"), "protected.config");
        File.WriteAllText(outside, "the administrator's");
        var inSite = Path.Combine(root, "web.config");
        if (!CreateHardLink(inSite, outside, IntPtr.Zero)) Assert.Inconclusive($"No hard link could be made here (error {Marshal.GetLastWin32Error()}).");
        Assert.IsTrue(SafePath.IsHardLinked(inSite));
        Assert.IsFalse(SafePath.IsHardLinked(Path.Combine(root, "missing")));

        var zip = Zip(("web.config", "the package's"));
        using (var archive = ZipFile.OpenRead(zip))
            SafeZip.ExtractEntry(root, archive.Entries[0], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual("the package's", File.ReadAllText(inSite));
        Assert.AreEqual("the administrator's", File.ReadAllText(outside), "The other name's file was written into.");
    }

    // ─── SafeZip ──────────────────────────────────────────────────────────

    [TestMethod]
    public void A_zip_entry_on_another_files_stream_or_outside_is_refused()
    {
        var root = Folder("site");
        File.WriteAllText(Path.Combine(root, "web.config"), "the site's");
        foreach (var bad in new[] { "web.config::$DATA", "web.config:evil", "../outside.txt", "./", "a/../" })
            Assert.ThrowsExactly<IOException>(() => SafeZip.Target(root, bad), $"'{bad}' was allowed.");
        Assert.AreEqual("web.config", SafeZip.Relative(root, SafeZip.Target(root, "./web.config")));
        Assert.AreEqual("web.config", SafeZip.Relative(root, SafeZip.Target(root, "x/../web.config")));
        Assert.AreEqual("the site's", File.ReadAllText(Path.Combine(root, "web.config")));
    }

    [TestMethod]
    public void A_short_name_is_compared_as_the_file_it_stands_for()
    {
        var root = Folder("site");
        var webConfig = Path.Combine(root, "web.config");
        File.WriteAllText(webConfig, "the site's");
        var shortName = ShortName(webConfig);
        if (shortName is null || shortName.Equals("web.config", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("This volume makes no short (8.3) names.");
        Assert.AreEqual("web.config", SafeZip.Relative(root, SafeZip.Target(root, shortName)));
    }

    [TestMethod]
    public void A_zip_that_unpacks_to_more_than_the_disk_has_is_refused()
    {
        SafeZip.EnsureFits(10, SafeZip.Headroom + 100, _dir);
        Assert.ThrowsExactly<IOException>(() => SafeZip.EnsureFits(long.MaxValue / 2, 10L * 1024 * 1024 * 1024, _dir));
        Assert.ThrowsExactly<IOException>(() => SafeZip.EnsureFits(100, SafeZip.Headroom + 50, _dir));
        SafeZip.EnsureFits(long.MaxValue, null, _dir); // free space unknown: not refused for it
    }

    [TestMethod]
    public async Task Restoring_a_backup_with_a_stream_entry_writes_nothing_there()
    {
        var site = Folder("site");
        File.WriteAllText(Path.Combine(site, "web.config"), "the site's");
        var zip = Zip(("web.config", "<configuration />"), ("web.config::$DATA", "the package's"));
        var result = await new ProjectFileCopier().ExtractZipAsync(zip, site, new RecordingReporter(), CancellationToken.None);
        Assert.IsFalse(result.Success, "A stream entry was extracted.");
        StringAssert.Contains(result.Error, "unsafe path");
    }

    [TestMethod]
    public async Task A_package_with_a_path_outside_the_site_writes_nothing()
    {
        var site = Folder("site");
        File.WriteAllText(Path.Combine(site, "web.config"), "<configuration>the site's own</configuration>");
        Directory.CreateDirectory(Path.Combine(site, "bin"));
        File.WriteAllText(Path.Combine(site, "bin", "DotNetNuke.dll"), "old");
        var paths = new AppDataPaths(Folder("data"));
        var release = new DnnRelease("10.3.3", "v10.3.3", "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.3.3/DNN_Platform_10.3.3_Install.zip");
        var kept = Path.Combine(paths.PackagesDirectory, "dnnsoftware.Dnn.Platform", "DNN_Platform_10.3.3_Upgrade.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.Move(Zip(("bin/DotNetNuke.dll", "new"), ("../outside.txt", "never")), kept);

        var installer = new DnnPackageInstaller(new HttpClient(), Options.Create(new AppOptions { KeepDnnPackages = true }), paths,
            NullLogger<DnnPackageInstaller>.Instance);
        var result = await installer.ExtractUpgradeAsync(release, site, new RecordingReporter(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "unsafe path");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "outside.txt")), "A file landed outside the site's folder.");
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(site, "bin", "DotNetNuke.dll")), "Files went in before the unsafe one was found.");
    }

    [TestMethod]
    public void DNNs_local_upgrade_leaves_out_what_is_excluded_however_the_zip_spells_it()
    {
        var site = Folder("site");
        const string config = "<configuration><runtime /></configuration>";
        File.WriteAllText(Path.Combine(site, "web.config"), config);
        Directory.CreateDirectory(Path.Combine(site, "App_Data"));
        File.WriteAllText(Path.Combine(site, "App_Data", "Database.mdf"), "the site's data");
        var zip = Zip(("./web.config", "<configuration>the package's</configuration>"),
            ("x/../web.config", "<configuration>the package's</configuration>"),
            ("./App_Data/Database.mdf", "empty"),
            ("Resources/new.js", "new"));

        var (_, files) = DnnPackageInstaller.LocalUpgradeOver(zip, site, new RecordingReporter(), CancellationToken.None);

        Assert.AreEqual(config, File.ReadAllText(Path.Combine(site, "web.config")), "web.config was overwritten.");
        Assert.AreEqual("the site's data", File.ReadAllText(Path.Combine(site, "App_Data", "Database.mdf")), "The database file was overwritten.");
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(site, "Resources", "new.js")));
        Assert.AreEqual(1, files);
    }

    // ─── A site's XML ─────────────────────────────────────────────────────

    [TestMethod]
    public void A_web_config_with_a_DTD_isnt_read()
    {
        var file = Path.Combine(_dir, "web.config");
        File.WriteAllText(file, """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
            <configuration><appSettings><add key="x" value="&lol2;" /></appSettings></configuration>
            """);
        Assert.ThrowsExactly<System.Xml.XmlException>(() => SiteXml.Load(file));
        var result = new WebConfigService(NullLogger<WebConfigService>.Instance).ReadSiteSqlServer(file);
        Assert.IsFalse(result.Success);

        File.WriteAllText(file, "<configuration><connectionStrings /></configuration>");
        Assert.AreEqual("configuration", SiteXml.Load(file).Root!.Name.LocalName);
    }

    // ─── The saved DNN releases ───────────────────────────────────────────

    private const string Api = "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases";
    private static readonly string InstallSha = new('a', 64), UpgradeSha = new('b', 64);

    private static string ReleasesJson => $$"""
        [ { "tag_name": "v10.0.1", "prerelease": false, "draft": false,
            "assets": [
              { "name": "DNN_Platform_10.0.1_Install.zip", "digest": "sha256:{{InstallSha}}",
                "browser_download_url": "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.0.1/DNN_Platform_10.0.1_Install.zip" },
              { "name": "DNN_Platform_10.0.1_Upgrade.zip", "digest": "sha256:{{UpgradeSha}}",
                "browser_download_url": "https://github.com/dnnsoftware/Dnn.Platform/releases/download/v10.0.1/DNN_Platform_10.0.1_Upgrade.zip" } ] } ]
        """;

    [TestMethod]
    public async Task The_saved_releases_keep_GitHubs_SHA256s_for_offline_use()
    {
        var data = Folder("db");
        var database = new AppDatabase(data);
        // The step that adds the two columns (AppDatabase.Steps) - added here when the build doesn't have it yet.
        using (var connection = database.Open())
            if (AppDatabase.Scalar<long>(connection, "SELECT COUNT(*) FROM pragma_table_info('dnn_releases') WHERE name = 'sha256'") == 0)
                AppDatabase.Execute(connection, "ALTER TABLE dnn_releases ADD COLUMN sha256 TEXT; ALTER TABLE dnn_releases ADD COLUMN upgrade_sha256 TEXT;");

        GitHubDnnReleaseService Service(bool online) => new(new HttpClient(new Releases(online)), Options.Create(new AppOptions()), database,
            NullLogger<GitHubDnnReleaseService>.Instance);
        var online = await Service(true).ListReleasesAsync(Api, CancellationToken.None);
        Assert.IsTrue(online.Success, online.Error);
        Assert.AreEqual(InstallSha, online.Value!.Releases[0].Sha256);

        var offline = await Service(false).ListReleasesAsync(Api, CancellationToken.None);
        Assert.IsTrue(offline.Success, offline.Error);
        Assert.IsNotNull(offline.Value!.SavedAt);
        Assert.AreEqual(InstallSha, offline.Value.Releases[0].Sha256, "The install package's SHA-256 wasn't kept.");
        Assert.AreEqual(UpgradeSha, offline.Value.Releases[0].UpgradeSha256, "The upgrade package's SHA-256 wasn't kept.");

        // Changed in the database (Documents, the user's to change) to something that isn't a SHA-256: none, not that.
        using (var connection = database.Open()) AppDatabase.Execute(connection, "UPDATE dnn_releases SET sha256 = 'not-a-hash'");
        Assert.IsNull((await Service(false).ListReleasesAsync(Api, CancellationToken.None)).Value!.Releases[0].Sha256);
    }

    [TestMethod]
    public void A_download_sent_on_to_an_address_that_isnt_https_is_said_so()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://github.com/x/y/releases/download/v1/a.zip");
        using var toHttp = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
        toHttp.Headers.Location = new Uri("http://evil.example/a.zip");
        Assert.AreEqual("http://evil.example/a.zip", DnnPackageInstaller.RedirectNotHttps(toHttp));

        using var toHttps = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
        toHttps.Headers.Location = new Uri("https://objects.githubusercontent.com/a.zip");
        Assert.IsNull(DnnPackageInstaller.RedirectNotHttps(toHttps));

        using var ok = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        Assert.IsNull(DnnPackageInstaller.RedirectNotHttps(ok));
    }

    private sealed class Releases(bool online) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!online) throw new HttpRequestException("No such host is known. (api.github.com:443)");
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/releases")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    // ─── SQL Server addresses ─────────────────────────────────────────────

    [TestMethod]
    public void This_PCs_addresses_are_read_once_and_again_after_a_change()
    {
        SqlServerAddress.LocalNames.Forget();
        Assert.IsFalse(SqlServerAddress.LocalNames.IsKept);
        Assert.IsTrue(SqlServerAddress.IsOnThisMachine("127.0.0.1,1433"));
        Assert.IsFalse(SqlServerAddress.IsOnThisMachine("203.0.113.7"), "A documentation address (TEST-NET-3) isn't this PC's.");
        Assert.IsTrue(SqlServerAddress.LocalNames.IsKept, "Read for an address, and kept.");
        var first = SqlServerAddress.LocalNames.Current;
        Assert.AreSame(first, SqlServerAddress.LocalNames.Current, "Read again without a change.");
        SqlServerAddress.LocalNames.Forget();
        Assert.AreNotSame(first, SqlServerAddress.LocalNames.Current);
    }

    [TestMethod]
    public void Signing_in_as_the_user_is_for_this_PC_and_the_chosen_server_only()
    {
        var chosen = new DatabaseServerOptions { Type = "sqlServer", Server = "db.example.com,1433" };
        Assert.IsTrue(SqlServerAddress.MaySignInAsUser(@".\SQLEXPRESS", chosen));
        Assert.IsTrue(SqlServerAddress.MaySignInAsUser("db.example.com", chosen));
        Assert.IsFalse(SqlServerAddress.MaySignInAsUser("evil.example", chosen));
        Assert.IsFalse(SqlServerAddress.MaySignInAsUser(@"\\evil.example\pipe\sql\query", chosen));
        Assert.IsFalse(SqlServerAddress.MaySignInAsUser("db.example.com", new DatabaseServerOptions { Type = SqlServerSettings.ContainerType, Server = "db.example.com" }),
            "The container chosen: a server elsewhere isn't it.");
        StringAssert.Contains(SqlServerAddress.SignInQuestion("evil.example", "Opening it"), "evil.example");
    }

    // ─── Programs ─────────────────────────────────────────────────────────

    [TestMethod]
    public void A_program_on_the_computers_PATH_is_found_by_its_own_path()
    {
        var cmd = TrustedPrograms.OnMachinePath("cmd").FirstOrDefault();
        Assert.IsNotNull(cmd, "cmd.exe isn't on the computer's PATH.");
        StringAssert.EndsWith(cmd, "cmd.exe", StringComparison.OrdinalIgnoreCase);
        if (!TrustedPrograms.IsElevated)
        {
            // Not elevated: no boundary - the name as it is.
            Assert.AreEqual("cmd", TrustedPrograms.Resolve("cmd").Path);
            return;
        }
        var resolved = TrustedPrograms.Resolve("cmd");
        Assert.IsNotNull(resolved.Path, resolved.Reason);
        Assert.AreEqual(TrustedPrograms.Trust.AdminOnly, TrustedPrograms.AdminOnly(resolved.Path));
    }

    [TestMethod]
    public void A_quick_question_to_a_program_never_waits_longer_than_its_time()
    {
        Assert.AreEqual("hello", ElevatedStart.Output(Cmd, ["/d", "/c", "echo hello"], TimeSpan.FromSeconds(20))?.Trim());
        var watch = Stopwatch.StartNew();
        // Prints nothing for half a minute - ReadToEnd alone would wait for all of it.
        Assert.IsNull(ElevatedStart.Output(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromSeconds(1)));
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"It waited {watch.Elapsed.TotalSeconds:0} s.");
    }

    [TestMethod]
    public async Task A_terminal_without_administrator_rights_runs_and_answers()
    {
        // The token: Administrators only deny in it.
        using (var token = UnelevatedToken.Create())
            Assert.IsFalse(UnelevatedToken.IsAdministrator(token), "The token still has administrator rights.");

        // Started with it in a pseudo console (as DNN Manager does elevated - here the rights are dropped all the same).
        PseudoConsole.DropRightsAlways = true;
        try
        {
            using var console = PseudoConsole.Start($"\"{Cmd}\" /d /c echo started-without-rights", _dir, 80, 25);
            var output = new StringBuilder();
            var buffer = new byte[4096];
            _ = Task.Run(() =>
            {
                int n;
                try
                {
                    while ((n = console.Output.Read(buffer, 0, buffer.Length)) > 0)
                        lock (output) output.Append(Encoding.UTF8.GetString(buffer, 0, n));
                }
                catch (IOException) { /* closed */ }
            });
            await Task.WhenAny(console.Exited, Task.Delay(TimeSpan.FromSeconds(20)));
            await Task.Delay(500);
            lock (output) StringAssert.Contains(output.ToString(), "started-without-rights");
        }
        finally
        {
            PseudoConsole.DropRightsAlways = false;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private string Zip(params (string Name, string Text)[] entries)
    {
        var zip = Path.Combine(Folder("zips"), $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }
        return zip;
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.IsTrue(Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), $"Could not make the junction {link}.");
    }

    private static string? ShortName(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length == 0 ? null : Path.GetFileName(buffer.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int length);

}
