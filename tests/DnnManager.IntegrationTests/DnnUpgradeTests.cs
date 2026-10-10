using System.Text.Json;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Upgrades;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Dnn;
using DnnManager.IntegrationTests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Upgrade DNN on real sites, end to end - DNN Manager's own UpgradeDnnUseCase with IIS Express playing IIS and a SQL
/// Server container of the run's own: a site with realistic content (users, a role, pages, a module, a second portal, a
/// custom web.config setting) taken through DNN's upgrade path one step at a time, checked after each; and a step that
/// fails in the database, which must stop the chain and put the site back to the last version that worked. Each takes
/// several minutes - the packages come from GitHub once and are kept in %LOCALAPPDATA%\DnnManagerTests\cache.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DnnUpgradeTests
{
    private static string _run = "";
    private static string? _containerName, _container, _saPassword;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        if (!IisExpressSites.Installed || !TestEnvironment.DockerAvailable()) return;
        _run = TestEnvironment.NewRunDirectory();
        _containerName = "dnnup-mssql-" + Path.GetFileName(_run);
        _saPassword = TestEnvironment.Password(24, "-_");
        var port = Enumerable.Range(14330, 60).First(p => !IisExpressSites.PortOpen(p));
        _container = await TestEnvironment.StartSqlContainerAsync(_containerName, port, _saPassword);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (_containerName is not null) TestEnvironment.RemoveContainer(_containerName);
        if (_run.Length > 0) TestEnvironment.DeleteDirectory(_run);
    }

    private static void Prerequisites()
    {
        if (!IisExpressSites.Installed) Assert.Inconclusive("IIS Express isn't installed (or unpacked into the tests' folder).");
        if (_container is null) Assert.Inconclusive("Docker with Linux containers isn't available for a SQL Server container.");
    }

    // ─── The whole path, with content ─────────────────────────────────────

    [TestMethod]
    public async Task A_92_site_with_content_goes_through_every_step_to_10_3_3()
    {
        Prerequisites();
        var install = new RecordingReporter();
        await using var site = await DnnTestSite.InstallAsync(_run, "uppath", "9.3.2", _containerName!, _container!, _saPassword!, install, "9.13.9", "10.2.5", "10.3.3");
        var content = await AddContentAsync(site);
        var before = await site.Services.GetRequiredService<IDnnSiteInspector>().CountAsync(site.Directory, site.Database, CancellationToken.None);
        TestContext.WriteLine($"Before: {before}");

        var reporter = new RecordingReporter();
        var result = await UpgradeAsync(site, "10.3.3", reporter, content.Accounts);
        TestContext.WriteLine(reporter.Text);
        Assert.IsTrue(result.Success, $"{result.Error}{Environment.NewLine}{reporter.Text}");

        // The plan, then each step - backed up, upgraded, restarted, checked.
        StringAssert.Contains(reporter.Text, "Required path:");
        foreach (var step in new[] { "Step 1 passed", "Step 2 passed", "Step 3 passed" })
            StringAssert.Contains(reporter.Text, step);
        StringAssert.Contains(reporter.Text, "DNN's local upgrade", "From 10.2.5 on, DNN's own local upgrade.");
        Assert.IsFalse(reporter.Text.Contains(site.Host.Password, StringComparison.Ordinal), "The host's password is in the Output.");

        // The site: DNN 10.3.3 in files and database, nothing lost, Telerik gone with DNN 10, the custom setting kept.
        var version = await site.Services.GetRequiredService<IDnnInstaller>().CheckVersionAsync(site.Directory, site.Database, CancellationToken.None);
        Assert.AreEqual("10.3.3", version.Value, version.Error);
        var after = await site.Services.GetRequiredService<IDnnSiteInspector>().CountAsync(site.Directory, site.Database, CancellationToken.None);
        Assert.AreEqual(0, after!.Lost(before!).Count, string.Join(", ", after.Lost(before!)));
        Assert.IsFalse(File.Exists(Path.Combine(site.Directory, "bin", "Telerik.Web.UI.dll")), "DNN 10 should have removed Telerik.");
        StringAssert.Contains(File.ReadAllText(Path.Combine(site.Directory, "web.config")), "Acme.ApiKey", "The site's own appSetting is gone.");
        Assert.IsFalse(File.Exists(Path.Combine(site.Directory, "installBlocker.lock")));

        // What visitors and users see.
        var browser = new DnnBrowser(site.Url);
        Assert.AreEqual(200, (await browser.HomeAsync()).Status);
        Assert.IsTrue((await browser.SignInAsync(site.Host.UserName, site.Host.Password)).SignedIn, "The host doesn't sign in.");
        if (content.User is { } user) Assert.IsTrue((await browser.SignInAsync(user.UserName, user.Password)).SignedIn, "The regular user doesn't sign in.");
        if (content.ChildPortal is { } child)
            Assert.AreEqual(200, (await browser.PageAsync(child + "/")).Status, "The second portal doesn't answer.");

        // A backup before each step, named for it, with DNN's raw output beside it.
        var backups = ProjectBackups.List(site.Services.GetRequiredService<IProjectRepository>().Build(site.Name, site.Directory));
        CollectionAssert.AreEquivalent(new[] { "Before upgrading DNN 09.03.02 → 09.13.09", "Before upgrading DNN 09.13.09 → 10.02.05", "Before upgrading DNN 10.02.05 → 10.03.03" },
            backups.Select(b => b.Note?.Split(" - ")[0]).ToArray());
        Assert.IsTrue(backups.All(b => b.IsComplete && Directory.EnumerateFiles(b.Folder, "dnn-upgrade-output-*.html").Any() && File.Exists(Path.Combine(b.Folder, "web.config.before"))));
        Assert.AreEqual("10.3.3", site.Services.GetRequiredService<IProjectRecords>().Find(site.Name)?.DnnVersion);
    }

    // ─── A new project at each version, upgraded ──────────────────────────

    /// <summary>
    /// As a user would: New project at an older DNN, then Upgrade DNN to 10.3.3 - every version DNN Manager can install
    /// and upgrade from (GitHub has packages from 7.4.2 on; 9.3.2 is the content test's). Each runs every step of the path.
    /// </summary>
    [TestMethod]
    [DataRow("7.4.2")]
    [DataRow("8.0.4")]
    [DataRow("9.1.1")]
    [DataRow("9.13.9")]
    [DataRow("10.2.5")]
    public async Task A_new_project_at_each_version_upgrades_to_10_3_3(string start)
    {
        Prerequisites();
        // DNNMANAGER_TEST_UPGRADE_FROM=7.4.2,8.0.4 runs only those - each row takes minutes.
        if (Environment.GetEnvironmentVariable("DNNMANAGER_TEST_UPGRADE_FROM") is { Length: > 0 } only && !only.Split(',').Contains(start))
            Assert.Inconclusive($"Not in DNNMANAGER_TEST_UPGRADE_FROM ({only}).");
        var steps = DnnUpgradePath.Chain(Version.Parse(start), new Version(10, 3, 3));
        var path = steps.Select(s => $"{s.To.Major}.{s.To.Minor}.{s.To.Build}").ToArray();
        TestContext.WriteLine($"{start} → {string.Join(" → ", path)}");
        await using var site = await DnnTestSite.InstallAsync(_run, "new" + start.Replace(".", ""), start, _containerName!, _container!, _saPassword!, new RecordingReporter(), path);

        var reporter = new RecordingReporter();
        var result = await UpgradeAsync(site, "10.3.3", reporter, [new DnnTestAccount(site.Host.UserName, site.Host.Password, IsHost: true)]);
        TestContext.WriteLine(reporter.Text);
        Assert.IsTrue(result.Success, $"{start} → 10.3.3: {result.Error}{Environment.NewLine}{reporter.Text}");
        for (var i = 1; i <= steps.Count; i++) StringAssert.Contains(reporter.Text, $"Step {i} passed");

        var version = await site.Services.GetRequiredService<IDnnInstaller>().CheckVersionAsync(site.Directory, site.Database, CancellationToken.None);
        Assert.AreEqual("10.3.3", version.Value, version.Error);
        Assert.IsFalse(File.Exists(Path.Combine(site.Directory, "installBlocker.lock")));
        var browser = new DnnBrowser(site.Url);
        Assert.AreEqual(200, (await browser.HomeAsync()).Status);
        Assert.IsTrue((await browser.SignInAsync(site.Host.UserName, site.Host.Password)).SignedIn, "The host doesn't sign in.");
    }

    // ─── A step that fails ────────────────────────────────────────────────

    [TestMethod]
    public async Task A_step_that_fails_stops_the_chain_and_puts_the_site_back_to_the_last_version_that_worked()
    {
        Prerequisites();
        await using var site = await DnnTestSite.InstallAsync(_run, "upfail", "9.13.9", _containerName!, _container!, _saPassword!, new RecordingReporter(), "10.2.5", "10.3.3");
        // Its database imported from a bacpac, as New project → An existing site does: its files are named
        // <name>_Primary.mdf - where putting a backup back under the same name collided (seen on a real site).
        await ReimportDatabaseAsync(site);
        // A third-party trigger that refuses DNN 10.3's version row - as an audit or replication trigger on DNN's tables can.
        await using (var conn = new SqlConnection(Infrastructure.Sql.ConnectionStrings.ForApp(site.Database)))
        {
            await conn.OpenAsync();
            await using var trigger = new SqlCommand(
                "CREATE TRIGGER dbo.Acme_AuditVersion ON dbo.[Version] AFTER INSERT AS " +
                "IF EXISTS (SELECT 1 FROM inserted WHERE Major = 10 AND Minor = 3) BEGIN RAISERROR('Acme audit: version change refused', 16, 1); ROLLBACK; END", conn);
            await trigger.ExecuteNonQueryAsync();
        }

        var reporter = new RecordingReporter();
        var result = await UpgradeAsync(site, "10.3.3", reporter, [new DnnTestAccount(site.Host.UserName, site.Host.Password, IsHost: true)]);
        TestContext.WriteLine(reporter.Text);

        Assert.IsFalse(result.Success, "The upgrade should have failed at its second step.");
        StringAssert.Contains(result.Error, "Step 2 (10.02.05 → 10.03.03) failed");
        StringAssert.Contains(reporter.Text, "Step 1 passed");

        // Back at 10.2.5 - the last version that worked - and working.
        var version = await site.Services.GetRequiredService<IDnnInstaller>().CheckVersionAsync(site.Directory, site.Database, CancellationToken.None);
        Assert.AreEqual("10.2.5", version.Value, version.Error);
        var browser = new DnnBrowser(site.Url);
        Assert.AreEqual(200, (await browser.HomeAsync()).Status, "The site doesn't answer after being put back.");
        Assert.IsTrue((await browser.SignInAsync(site.Host.UserName, site.Host.Password)).SignedIn, "The host doesn't sign in after being put back.");

        // What happened is kept with the failed step's backup.
        var failed = ProjectBackups.List(site.Services.GetRequiredService<IProjectRepository>().Build(site.Name, site.Directory))
            .Single(b => b.Note?.StartsWith("Before upgrading DNN 10.02.05", StringComparison.Ordinal) == true);
        var kept = Path.Combine(failed.Folder, "failed-upgrade", "what-happened.txt");
        Assert.IsTrue(File.Exists(kept), "No what-happened.txt");
        TestContext.WriteLine(File.ReadAllText(kept));
        // A copy outside the run's folder, which goes with the run - to read what DNN logged.
        var copy = Path.Combine(TestEnvironment.Root, "last-failed-upgrade");
        TestEnvironment.DeleteDirectory(copy);
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.EnumerateFiles(failed.Folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".zip") && !f.EndsWith(".bacpac")))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), overwrite: true);

        // Why: DNN's failed script, and the error it hit.
        StringAssert.Contains(reporter.Text, "Likely cause: One of DNN's database scripts failed", "The diagnosis should find the failed script.");
        StringAssert.Contains(reporter.Text, "Acme audit", "The script's error should be shown.");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>The site's database exported and imported again under its own name, with SqlPackage's file names.</summary>
    private async Task ReimportDatabaseAsync(DnnTestSite site)
    {
        var reporter = new RecordingReporter();
        var bacpac = Path.Combine(_run, site.Name + "-reimport.bacpac");
        var exported = await site.Services.GetRequiredService<ExportProjectUseCase>().ExecuteAsync(new ExportProjectRequest
        {
            ProjectName = site.Name, ProjectDirectory = site.Directory, ZipPath = Path.Combine(_run, site.Name + "-reimport.zip"), BacpacPath = bacpac
        }, reporter, CancellationToken.None);
        Assert.IsTrue(exported.Success, $"{exported.Error}{Environment.NewLine}{reporter.Text}");
        site.Iis.StopSiteAndWait(site.Name, TimeSpan.FromSeconds(60));
        var db = site.Database;
        Assert.IsTrue((await site.Services.GetRequiredService<IDatabaseProvisioner>().DropDatabaseAsync(db, CancellationToken.None)).Success);
        var imported = await site.Services.GetRequiredService<IBacpacService>().ImportAsync(db.Server, db.User, db.Password, db.Database, bacpac, reporter, CancellationToken.None);
        Assert.IsTrue(imported.Success, $"{imported.Error}{Environment.NewLine}{reporter.Text}");
        site.Iis.StartSite(site.Name);
        Assert.AreEqual(200, (await new DnnBrowser(site.Url).HomeAsync()).Status, "The site doesn't answer with its imported database.");
    }

    private static async Task<Result> UpgradeAsync(DnnTestSite site, string target, RecordingReporter reporter, IReadOnlyList<DnnTestAccount> accounts)
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UpgradeDnnUseCase>().ExecuteAsync(new UpgradeDnnRequest
        {
            SiteName = site.Name, Directory = site.Directory, ReleaseApiUrl = DnnTestSite.ReleasesApi, Version = "v" + target, Accounts = accounts
        }, reporter, CancellationToken.None);
    }

    private sealed record Content(DnnTestAccount? User, string? ChildPortal, IReadOnlyList<DnnTestAccount> Accounts);

    /// <summary>
    /// Realistic content through DNN's own Persona Bar, as the host: three users, a role one of them is in, two pages, an
    /// HTML module, a child portal - and a setting of the site's own in web.config. What couldn't be made is said.
    /// </summary>
    private async Task<Content> AddContentAsync(DnnTestSite site)
    {
        var alias = $"localhost:{site.Port}";
        var (prompt, detail) = await DnnPrompt.SignInAsync(alias, site.Port, site.Host.UserName, site.Host.Password);
        Assert.IsNotNull(prompt, $"The host can't sign in to make content: {detail}");
        using var _ = prompt;
        DnnTestAccount? user = null;
        var userPassword = "Ed1tor#" + TestEnvironment.Password(10, "-_");
        foreach (var name in new[] { "ann.editor", "bob.writer", "carl.reader" })
        {
            var made = await prompt.PostAsync("API/PersonaBar/Users/CreateUser", new
            {
                firstName = name.Split('.')[0], lastName = name.Split('.')[1], userName = name, email = $"{name}@dnnit.example",
                password = userPassword, question = "", answer = "", randomPassword = false, authorize = true, notify = false
            });
            TestContext.WriteLine($"user {name}: {made.Answer}");
            Assert.IsTrue(made.Ok, $"User {name} wasn't made: {made.Answer}");
            user ??= new DnnTestAccount(name, userPassword, IsHost: false);
        }
        Log(await prompt.RunAsync("new-role --name Editors --description \"Content editors\" --public false --autoAssign false"), "role", must: true);
        var users = await prompt.RunAsync("list-users");
        var annId = System.Text.RegularExpressions.Regex.Match(users.Answer, @"""UserId"":(\d+),""Username"":""ann\.editor""").Groups[1].Value;
        Log(await prompt.RunAsync($"add-roles --id {annId} --roles Editors"), "user in role", must: true);
        var page = await prompt.RunAsync("new-page --name Products --title Products --visible true");
        Log(page, "page", must: true);
        var pageId = System.Text.RegularExpressions.Regex.Match(page.Answer, @"""TabId"":(\d+)").Groups[1].Value;
        Log(await prompt.RunAsync("new-page --name About --title \"About us\" --visible true"), "page", must: true);
        Log(await prompt.RunAsync($"add-module --name \"DNN_HTML\" --pageid {pageId} --title Welcome"), "module", must: true);

        // A child portal, through the Persona Bar's Sites API with one of its own templates.
        string? child = null;
        var templates = await prompt.GetAsync("API/PersonaBar/Sites/GetPortalTemplates");
        var template = System.Text.RegularExpressions.Regex.Match(templates, @"""Value""\s*:\s*""([^""]*Default Website[^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        TestContext.WriteLine($"templates: {(templates.Length > 400 ? templates[..400] : templates)}");
        if (template.Success)
        {
            var portal = await prompt.PostAsync("API/PersonaBar/Sites/CreatePortal", new
            {
                SiteTemplate = JsonDocument.Parse($"\"{template.Groups[1].Value}\"").RootElement.GetString(), SiteName = "Shop", SiteAlias = $"{alias}/shop",
                SiteDescription = "A second portal", SiteKeywords = "", IsChildSite = true, HomeDirectory = "Portals/[PortalID]", SiteGroupId = -1,
                UseCurrentUserAsAdmin = true, Firstname = "", Lastname = "", Username = "", Email = "", Password = "", PasswordConfirm = "", Question = "", Answer = ""
            });
            Log(portal, "child portal", must: false);
            if (portal.Ok) child = "shop";
        }
        Assert.IsNotNull(child, "The child portal wasn't made (see the log) - the upgrade would only be tested with one portal.");

        // A setting of the site's own - the upgrade must leave it.
        var webConfig = Path.Combine(site.Directory, "web.config");
        File.WriteAllText(webConfig, File.ReadAllText(webConfig).Replace("<appSettings>", "<appSettings>\r\n    <add key=\"Acme.ApiKey\" value=\"kept-by-the-upgrade\" />"));
        site.Iis.RecycleAppPool(site.Name);
        return new Content(user, child, [new DnnTestAccount(site.Host.UserName, site.Host.Password, IsHost: true), user!]);

        void Log((bool Ok, string Answer) made, string what, bool must)
        {
            TestContext.WriteLine($"{what}: {made.Answer}");
            if (must) Assert.IsTrue(made.Ok, $"The {what} wasn't made: {made.Answer}");
        }
    }
}
