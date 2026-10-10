using System.Diagnostics;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Configuration;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The settings and the data in Documents\DnnManager: a saved value a newer rule refuses goes back to its default
/// without stopping the start, <c>DNNMANAGER_*</c> overrides are held to every rule the saved settings are, the rules
/// themselves (host names, the projects folder, Docker names), old backups deleted when asked to - never through a link -,
/// the old settings files cleaned up, and a data folder in OneDrive told apart. All in a folder of the test's own.
/// </summary>
[TestClass]
public sealed class SettingsResilienceTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerSettingsTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        // Junctions first, so deleting the tree doesn't go through them.
        foreach (var link in Directory.Exists(_dir)
                     ? Directory.EnumerateDirectories(_dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                         .Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList()
                     : [])
            try { Directory.Delete(link); } catch (IOException) { /* gone */ }
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* SQLite may hold a file for a moment */ }
    }

    private AppDataPaths Paths => new(Path.Combine(_dir, "data"));

    private void SetRow(string key, string value)
    {
        using var connection = new AppDatabase(Paths).Open();
        AppDatabase.Execute(connection, "INSERT OR REPLACE INTO settings (key, value) VALUES ($key, $value)", ("$key", key), ("$value", value));
    }

    // ─── Settings a newer rule refuses ────────────────────────────────────

    [TestMethod]
    public void Saved_values_that_arent_allowed_go_back_to_their_defaults_and_the_rest_is_kept()
    {
        var store = new SettingsStore(Paths);
        store.Load();
        SetRow("appearance.theme", "dark");
        SetRow("projects.sitePort", "8080");
        SetRow("projects.hostnameSuffix", "dev.me-");      // allowed before RFC 1123 labels
        SetRow("docker.containerName", "dnn sqlserver");   // allowed before Docker's name rule
        SetRow("terminal.fontSize", "not a number");

        var loaded = store.Load();

        var settings = loaded.Settings;
        Assert.AreEqual(new ProjectSettings().HostnameSuffix, settings.Projects.HostnameSuffix);
        Assert.AreEqual(new DockerSettings().ContainerName, settings.Docker.ContainerName);
        Assert.AreEqual(new TerminalSettings().FontSize, settings.Terminal.FontSize);
        Assert.AreEqual("dark", settings.Appearance.Theme, "A value that is allowed is kept.");
        Assert.AreEqual(8080, settings.Projects.SitePort, "A value that is allowed is kept.");
        var notice = loaded.Notices.Single(n => n.IsWarning && n.Message.Contains("back at the default"));
        foreach (var key in new[] { "projects.hostnameSuffix", "docker.containerName", "terminal.fontSize" })
            StringAssert.Contains(notice.Message, key);
        StringAssert.DoesNotMatch(notice.Message, new System.Text.RegularExpressions.Regex("appearance\\.theme|projects\\.sitePort"));

        // Saved so: the next start - and the Settings page - reads them without a word.
        Assert.AreEqual("dnndev.me", store.Read().Projects.HostnameSuffix);
        Assert.IsFalse(store.Load().Notices.Any(n => n.IsWarning), "Reset again at the next start.");
    }

    [TestMethod]
    public void A_login_put_back_takes_the_authentication_that_needs_it_along()
    {
        var store = new SettingsStore(Paths);
        store.Load();
        SetRow("sqlServer.type", "sqlServer");
        SetRow("sqlServer.authentication", "sql");
        SetRow("sqlServer.userName", "");

        var settings = store.Load().Settings;

        Assert.AreEqual("sqlServer", settings.SqlServer.Type, "The connection type is kept.");
        Assert.IsFalse(settings.SqlServer.UsesSqlAuthentication, "Windows authentication: no login needed.");
        Assert.AreEqual(0, settings.Validate().Count);
    }

    [TestMethod]
    public void Settings_of_a_newer_version_still_stop_the_start()
    {
        var store = new SettingsStore(Paths);
        store.Load();
        SetRow("version", (UserSettings.CurrentVersion + 1).ToString());
        SetRow("projects.hostnameSuffix", "dev.me-");
        Assert.ThrowsExactly<SettingsException>(() => store.Load());
    }

    // ─── Environment variables ────────────────────────────────────────────

    [TestMethod]
    public void Overrides_are_held_to_every_rule_of_the_saved_settings()
    {
        var saved = new UserSettings();
        Assert.AreEqual(0, saved.ToAppOptions().Problems(saved).Count);

        void Refused(Action<AppOptions> change, string key)
        {
            var options = saved.ToAppOptions();
            change(options);
            var problems = options.Normalize().Problems(saved);
            Assert.IsTrue(problems.Any(p => p.Contains($"({key})")), $"{key} was allowed: {string.Join(" | ", problems)}");
        }
        Refused(o => o.Docker.ContainerIp = " ", "sqlServer.host");
        Refused(o => o.Docker.ContainerName = "x; docker rm -f y", "docker.containerName");
        Refused(o => o.Docker.VolumeName = "-v", "docker.volumeName");
        Refused(o => o.Docker.SaPassword = "", "sqlServer.saPassword");
        Refused(o => o.DatabaseServer = new DatabaseServerOptions { Type = "elsewhere" }, "sqlServer.type");
        Refused(o => o.DatabaseServer = new DatabaseServerOptions { Type = "sqlServer", Server = " " }, "sqlServer.server");
        Refused(o => o.KeepWarm = new KeepWarmSettings { PingPath = "/Install/InstallWizard.aspx" }, "projects.keepWarm.pingPath");
        Refused(o => o.KeepWarm = new KeepWarmSettings { PingMinutes = 0 }, "projects.keepWarm.pingMinutes");
        Refused(o => o.DnnDefaults = new DnnDefaultsSettings { Language = "xx-XX" }, "projects.dnnDefaults.language");
        Refused(o => o.HostnameSuffix = "1.2.3.4", "projects.hostnameSuffix");
        Refused(o => o.BackupKeepDays = -1, "backups.keepDays");
    }

    [TestMethod]
    public void Overrides_are_used_as_the_app_uses_them_or_not_at_all()
    {
        var saved = new UserSettings();
        static IConfiguration Environment(params (string Key, string Value)[] values) => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>($"{AppOptions.SectionName}:{v.Key}", v.Value)))
            .Build();

        var allowed = LiveSettings.WithOverrides(saved, Environment(("HostnameSuffix", " dev.example. "), ("SitePort", "8080")), out var none);
        Assert.AreEqual(0, none.Count, string.Join(" | ", none));
        Assert.AreEqual("dev.example", allowed.HostnameSuffix, "Used without the spaces and dots around it.");
        Assert.AreEqual("http://shop.dev.example:8080", allowed.SiteUrlFor("shop"));

        var refused = LiveSettings.WithOverrides(saved, Environment(("SitePort", "8080"), ("Docker:ContainerName", "dnn sqlserver")), out var problems);
        Assert.IsTrue(problems.Any(p => p.StartsWith("Docker__ContainerName", StringComparison.Ordinal)), string.Join(" | ", problems));
        Assert.AreEqual(80, refused.SitePort, "None of them is used when one isn't allowed.");
        Assert.AreEqual(saved.Docker.ContainerName, refused.Docker.ContainerName);

        var nothing = LiveSettings.WithOverrides(saved, Environment(), out var noProblems);
        Assert.AreEqual(0, noProblems.Count);
        Assert.AreEqual(saved.Projects.HostnameSuffix, nothing.HostnameSuffix);
    }

    // ─── The rules ────────────────────────────────────────────────────────

    [TestMethod]
    public void A_hostname_suffix_is_made_of_RFC_1123_labels_and_isnt_an_address()
    {
        foreach (var good in new[] { "dnndev.me", "dev-1.example.com", "localhost", "a.b.c", " dnndev.me. ", "xn--bcher-kva.example" })
            Assert.IsTrue(SettingRules.IsHostnameSuffix(good), $"'{good}' was refused.");
        foreach (var bad in new[] { "dev.me-", "dev-.me", "-dev.me", "1.2.3.4", "dev.123", "a..b", new string('a', 64) + ".me", "dev_me.com", "dév.me", "" })
            Assert.IsFalse(SettingRules.IsHostnameSuffix(bad), $"'{bad}' was allowed.");
    }

    [TestMethod]
    public void The_projects_folder_isnt_DNN_Managers_data_programs_or_Windows_own_folders()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var users = Path.GetDirectoryName(profile)!;
        foreach (var refused in new[]
                 {
                     Path.Combine(documents, "DnnManager"), Path.Combine(documents, "DnnManager", "projects"),
                     Path.Combine(profile, "Documents", "DnnManager"),
                     Path.Combine(local, "Programs"), Path.Combine(local, "Programs", "DnnManager", "sites"),
                     @"C:\$Recycle.Bin\S-1-5-21\DNN", @"C:\System Volume Information\DNN", @"D:\$RECYCLE.BIN\x", users
                 })
            Assert.IsNotNull(SettingRules.ProjectsFolderProblem(refused), $"'{refused}' was allowed.");

        StringAssert.Contains(SettingRules.ProjectsFolderProblem(users), "itself");
        StringAssert.Contains(SettingRules.ProjectsFolderProblem(Path.Combine(users, "Public", "DNN")), "another account");

        foreach (var allowed in new[] { @"C:\DNN", Path.Combine(profile, "DNN"), Path.Combine(documents, "Sites"), Path.Combine(local, "DnnManagerTests", "run") })
            Assert.IsNull(SettingRules.ProjectsFolderProblem(allowed), $"'{allowed}' was refused.");
    }

    [TestMethod]
    public void Docker_names_are_names_Docker_allows()
    {
        foreach (var good in new[] { "dnn-sqlserver", "dnn_sqlserver_data", "sql.2022", "A1" })
            Assert.IsTrue(SettingRules.IsDockerName(good), $"'{good}' was refused.");
        foreach (var bad in new[] { "-x", "_x", ".x", "a b", "a;b", "a/b", "a$(x)", "", new string('a', 129) })
            Assert.IsFalse(SettingRules.IsDockerName(bad), $"'{bad}' was allowed.");

        var settings = new UserSettings();
        settings.Docker.ContainerName = "dnn sqlserver";
        settings.Docker.VolumeName = "--rm";
        var keys = settings.Validate().Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "docker.containerName");
        CollectionAssert.Contains(keys, "docker.volumeName");
    }

    // ─── Keeping backups for a while ──────────────────────────────────────

    [TestMethod]
    public void Old_backups_and_packages_are_deleted_when_asked_to_and_never_through_a_link()
    {
        var paths = Paths;
        paths.EnsureCreated();
        var now = new DateTime(2026, 10, 9, 12, 0, 0);
        string Made(string folder, string file)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, file), "data");
            return folder;
        }
        var oldBackup = Made(Path.Combine(paths.BackupsDirectory, "shop", "shop_20260801_100000"), "shop.zip");
        var newBackup = Made(Path.Combine(paths.BackupsDirectory, "shop", "shop_20261001_100000"), "shop.zip");
        var onlyOld = Made(Path.Combine(paths.BackupsDirectory, "blog", "blog_20250101_000000"), "blog.bacpac");
        var oldPackage = Made(Path.Combine(paths.DeploymentsDirectory, "shop_20260101_090000"), "DEPLOY.txt");
        var newPackage = Made(Path.Combine(paths.DeploymentsDirectory, "shop_20261008_090000"), "DEPLOY.txt");
        // Links, named like old backups: what they point to isn't DNN Manager's.
        var elsewhere = Made(Path.Combine(_dir, "elsewhere"), "keep.txt");
        Made(Path.Combine(_dir, "elsewhere", "shop_20200101_000000"), "keep.zip");
        Junction(Path.Combine(paths.BackupsDirectory, "shop", "shop_20200101_000000"), elsewhere);
        Junction(Path.Combine(paths.BackupsDirectory, "linked"), elsewhere);
        Junction(Path.Combine(paths.DeploymentsDirectory, "shop_20200101_000000"), elsewhere);

        var cleaner = new AppDataCleaner(paths);
        Assert.AreEqual(0, cleaner.DeleteExpired(0, now).Deleted.Count, "0 keeps them for good.");
        Assert.IsTrue(Directory.Exists(oldBackup));

        var result = cleaner.DeleteExpired(30, now);

        CollectionAssert.AreEquivalent(new[] { oldBackup, onlyOld, oldPackage }, result.Deleted.ToList());
        Assert.AreEqual(0, result.Skipped);
        Assert.IsFalse(Directory.Exists(oldBackup));
        Assert.IsFalse(Directory.Exists(oldPackage));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(onlyOld)), "A project with no backup left keeps no empty folder.");
        Assert.IsTrue(File.Exists(Path.Combine(newBackup, "shop.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(newPackage, "DEPLOY.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "keep.txt")), "Deleted through a junction.");
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "shop_20200101_000000", "keep.zip")), "Deleted through a junction.");
    }

    [TestMethod]
    public void Nothing_is_deleted_when_the_backups_folder_itself_is_a_link()
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.Root);
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "shop", "shop_20200101_000000"));
        File.WriteAllText(Path.Combine(elsewhere, "shop", "shop_20200101_000000", "shop.zip"), "data");
        Junction(paths.BackupsDirectory, elsewhere);

        var result = new AppDataCleaner(paths).DeleteExpired(1, DateTime.Now);

        Assert.AreEqual(0, result.Deleted.Count);
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "shop", "shop_20200101_000000", "shop.zip")));
    }

    // ─── Old settings files ───────────────────────────────────────────────

    [TestMethod]
    public void Clean_up_deletes_old_settings_files_and_damaged_databases_but_not_the_database()
    {
        var paths = Paths;
        new SettingsStore(paths).Load();
        foreach (var name in new[] { "settings.json", "appsettings.json", "docker-compose.yml", "dnnmanager.damaged-20250101-120000.db" })
            File.WriteAllText(Path.Combine(paths.Root, name), "{ \"SaPassword\": \"secret\" }");
        File.WriteAllText(Path.Combine(paths.Root, "notes.txt"), "the user's");
        Directory.CreateDirectory(Path.Combine(paths.Root, "state"));
        File.WriteAllText(Path.Combine(paths.Root, "state", "window.json"), "{}");
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "settings.json"), "not DNN Manager's");
        Junction(Path.Combine(paths.Root, "linked"), elsewhere);

        var cleaner = new AppDataCleaner(paths);
        Assert.IsTrue(cleaner.Measure(AppDataKind.OldSettingsFiles) > 0);
        var result = cleaner.Clean(AppDataKind.OldSettingsFiles);

        Assert.AreEqual(0, result.Skipped);
        foreach (var name in new[] { "settings.json", "appsettings.json", "docker-compose.yml", "dnnmanager.damaged-20250101-120000.db" })
            Assert.IsFalse(File.Exists(Path.Combine(paths.Root, name)), $"{name} is still there.");
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, "state")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, "dnnmanager.db")), "The database itself was deleted.");
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, "notes.txt")), "A file of the user's was deleted.");
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "settings.json")), "Deleted through a junction.");
        Assert.AreEqual(0, cleaner.Measure(AppDataKind.OldSettingsFiles));
    }

    // ─── OneDrive ─────────────────────────────────────────────────────────

    [TestMethod]
    public void A_data_folder_in_OneDrive_or_on_a_share_is_told_apart()
    {
        string?[] roots = [null, "", "OneDrive", @"C:\Users\ann\OneDrive", @"C:\Users\ann\OneDrive - Contoso\"];
        Assert.AreEqual(@"C:\Users\ann\OneDrive", AppDataPaths.OneDriveFolderOf(@"C:\Users\ann\OneDrive\Documents\DnnManager", roots));
        Assert.AreEqual(@"C:\Users\ann\OneDrive - Contoso",
            AppDataPaths.OneDriveFolderOf(@"c:\users\ann\onedrive - contoso\Documents\DnnManager", roots));
        Assert.IsNull(AppDataPaths.OneDriveFolderOf(@"C:\Users\ann\Documents\DnnManager", roots));
        Assert.IsNull(AppDataPaths.OneDriveFolderOf(@"C:\Users\ann\OneDriveBackup\Documents\DnnManager", roots), "Only inside the folder.");

        StringAssert.Contains(AppDataPaths.SyncNotice(@"C:\Users\ann\OneDrive\Documents\DnnManager", @"C:\Users\ann\OneDrive"), "OneDrive");
        StringAssert.Contains(AppDataPaths.SyncNotice(@"\\server\home\ann\Documents\DnnManager", null), "network share");
        Assert.IsNull(AppDataPaths.SyncNotice(@"C:\Users\ann\Documents\DnnManager", null));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false })!;
        mklink.WaitForExit();
        Assert.IsTrue(Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), $"Could not make the junction {link}.");
    }
}
