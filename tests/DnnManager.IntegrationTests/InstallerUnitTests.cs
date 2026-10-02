using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Dnn;
using DnnManager.Presentation.Controls;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;

using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>The automatic install's parts that need no web server or database.</summary>
[TestClass]
public sealed class InstallerUnitTests
{
    // What DNN 10.3.3's Install.aspx streamed in a real install (shortened), and the end of one with a too-short host password.
    private const string Succeeded =
        "<html><head><title>DotNetNuke</title></head><body><img src=\"../images/branding/DNN_logo.png\"><br>\r\n" +
        "<!-- tags excluded on purpose\r\n</body>\r\n</html>\r\n-->\r\n<h1>Installing DNN</h1>" +
        "00:00:00.025 -&nbsp;Checking File and Folder permissions <font color='green'>Success</font><br>" +
        "00:00:00.083 -&nbsp;&nbsp;&nbsp;Executing Script:DotNetNuke.Schema.SqlDataProvider&nbsp;<font color='green'>Success</font><br>" +
        "00:00:09.121 -&nbsp;&nbsp;&nbsp;Installing Package File DNNCE_HTML_10.03.03_Install: &nbsp;<font color='green'>Success</font><br>" +
        "00:00:16.559 -&nbsp;&nbsp;&nbsp;Creating Site: My &amp; Site<br>" +
        "00:00:18.904 -&nbsp;&nbsp;&nbsp;Creating Site Alias: localhost:8081<br>" +
        "00:00:18.910 -&nbsp;&nbsp;&nbsp;<font color='green'>Successfully Installed Site 0:</font><br>" +
        "<h2>Installation Complete</h2><br><br><h2><a href='../Default.aspx'>Click Here To Access Your Site</a></h2><br><br></body></html>";

    private const string ShortPassword =
        "<h1>Installing DNN</h1>" +
        "00:00:16.559 -&nbsp;&nbsp;&nbsp;Creating Site: ShortPw<br>" +
        "00:00:18.904 -&nbsp;&nbsp;&nbsp;Creating Site Alias: localhost:8085<br>" +
        "00:00:18.950 -&nbsp;Object reference not set to an instance of an object. at DotNetNuke.Services.Upgrade.Upgrade.AddPortal <font color='red'>Error!</font><br>" +
        "Site failed to install:<font color='red'>Error!</font><br>" +
        "<h2>Installation Complete</h2><br>";

    private const string PackageFailed =
        "<h1>Installing DNN</h1>" +
        "00:00:12.256 -&nbsp;&nbsp;&nbsp;Installing Package File AspNetClientCapabilityProvider_10.03.03_Install: &nbsp;<font color='red'>Error!</font><br>" +
        "00:00:18.910 -&nbsp;&nbsp;&nbsp;<font color='green'>Successfully Installed Site 0:</font><br>" +
        "<h2>Installation Complete</h2><br>";

    [TestMethod]
    public void Output_CleanInstall_Succeeds_InReadableSteps()
    {
        var output = new DnnInstallOutput();
        // In small pieces, as it streams in.
        var steps = new List<DnnInstallStep>();
        for (var at = 0; at < Succeeded.Length; at += 37) steps.AddRange(output.Add(Succeeded.Substring(at, Math.Min(37, Succeeded.Length - at))));
        steps.AddRange(output.Finish());

        Assert.IsTrue(output.Outcome().Success, output.Outcome().Error);
        CollectionAssert.Contains(steps.Select(s => s.Text).ToList(), "Installing Package File DNNCE_HTML_10.03.03_Install");
        CollectionAssert.Contains(steps.Select(s => s.Text).ToList(), "Creating Site: My & Site");
        Assert.IsTrue(steps.First(s => s.Text.StartsWith("Checking File", StringComparison.Ordinal)).Succeeded);
        Assert.IsFalse(steps.Any(s => s.Text.Contains("DotNetNuke</title", StringComparison.Ordinal) || s.Text.Contains("Click Here", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Output_ShortPassword_FailsAlthoughInstallationComplete()
    {
        var output = new DnnInstallOutput();
        output.Add(ShortPassword);
        output.Finish();
        var outcome = output.Outcome();
        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error!, "Object reference not set");
    }

    [TestMethod]
    public void Output_FailedPackage_FailsAlthoughTheSiteWasInstalled()
    {
        var output = new DnnInstallOutput();
        output.Add(PackageFailed);
        output.Finish();
        var outcome = output.Outcome();
        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Error!, "AspNetClientCapabilityProvider");
    }

    [TestMethod]
    public void Output_AlreadyInstalled_SaysSo()
    {
        var output = new DnnInstallOutput();
        output.Add("<h1>Upgrade Status Report</h1>Nothing To Install At This Time<br>");
        output.Finish();
        StringAssert.Contains(output.Outcome().Error!, "installed already");
    }

    [TestMethod]
    public void Template_HasTheAccountTheAliasAndNoConnection()
    {
        var site = Path.Combine(Path.GetTempPath(), "dnnit-template-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(site, "Install"));
        try
        {
            File.WriteAllText(Path.Combine(site, "Install", DnnInstallTemplate.ShippedFileName),
                "<dotnetnuke><version>10.00.00</version><installCulture>en-US</installCulture>" +
                "<connection><server>(local)</server><database>x</database></connection>" +
                "<superuser><username>host</username><password>dnnhost</password><email>host@changeme.invalid</email></superuser>" +
                "<settings><HostEmail></HostEmail></settings>" +
                "<portals><portal><portalname>My Website</portalname><administrator><username>admin</username><password>dnnadmin</password></administrator>" +
                "<templatefile>Default Website.template</templatefile><portalaliases><portalalias></portalalias></portalaliases><ischild>false</ischild></portal></portals></dotnetnuke>");
            var account = new DnnAccount("sitehost", "pa<ss>&'\"word", "me@example.com", "Shop & Co <test>", "en-US", "Blank Website");

            var written = XDocument.Load(DnnInstallTemplate.Write(site, account, "shop.dnndev.me"));

            var root = written.Root!;
            Assert.IsNull(root.Element("connection"), "<connection> would make DNN rewrite web.config.");
            Assert.AreEqual("sitehost", root.Element("superuser")!.Element("username")!.Value);
            Assert.AreEqual(account.Password, root.Element("superuser")!.Element("password")!.Value);
            Assert.AreEqual("false", root.Element("superuser")!.Element("updatepassword")!.Value);
            Assert.AreEqual("me@example.com", root.Element("settings")!.Element("HostEmail")!.Value);
            var portal = root.Element("portals")!.Element("portal")!;
            Assert.AreEqual("Shop & Co <test>", portal.Element("portalname")!.Value);
            Assert.AreEqual("Blank Website.template", portal.Element("templatefile")!.Value);
            Assert.AreEqual("shop.dnndev.me", portal.Element("portalaliases")!.Element("portalalias")!.Value);
            Assert.AreNotEqual("dnnadmin", portal.Element("administrator")!.Element("password")!.Value);

            DnnInstallTemplate.Delete(site);
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(site, "Install")).Any(), "The templates are still there.");
        }
        finally
        {
            Directory.Delete(site, recursive: true);
        }
    }

    [TestMethod]
    public void ConnectionStrings_QuoteAPasswordWithSpecialCharacters()
    {
        var connection = new DatabaseConnection(DatabaseKind.SqlServer, "localhost,1433", "shop", SqlAuthentication.Sql, "shopuser", "a;b=c'd\"e{f}");
        var site = ConnectionStrings.ForSite(connection);
        var parsed = new SqlConnectionStringBuilder(site);
        Assert.AreEqual("a;b=c'd\"e{f}", parsed.Password);
        Assert.AreEqual("shop", parsed.InitialCatalog);
        Assert.IsFalse(site.Contains("Trust Server Certificate", StringComparison.OrdinalIgnoreCase), "DNN's System.Data.SqlClient doesn't know that keyword.");

        var roundTrip = ConnectionStrings.Parse(site)!;
        Assert.AreEqual(connection with { Kind = DatabaseKind.SqlServer }, roundTrip);
    }

    [TestMethod]
    public void ConnectionStrings_WindowsAuthenticationAndLocalDbFile()
    {
        var windows = ConnectionStrings.ForSite(new DatabaseConnection(DatabaseKind.SqlServer, @".\SQLEXPRESS", "shop", SqlAuthentication.Windows));
        Assert.IsTrue(new SqlConnectionStringBuilder(windows).IntegratedSecurity);
        Assert.IsFalse(windows.Contains("Password", StringComparison.OrdinalIgnoreCase));

        var file = ConnectionStrings.ForSite(new DatabaseConnection(DatabaseKind.LocalDbFile, DatabaseConnection.LocalDbServer, "Database.mdf", SqlAuthentication.Windows));
        var builder = new SqlConnectionStringBuilder(file);
        Assert.AreEqual(@"|DataDirectory|Database.mdf", builder.AttachDBFilename);
        Assert.IsFalse(file.Contains("User Instance", StringComparison.OrdinalIgnoreCase), "LocalDB refuses User Instance.");
        Assert.AreEqual(DatabaseKind.LocalDbFile, ConnectionStrings.Parse(file)!.Kind);
    }

    [TestMethod]
    public void Connection_NeverPrintsItsPassword()
    {
        var connection = new DatabaseConnection(DatabaseKind.SqlServer, "db", "shop", SqlAuthentication.Sql, "u", "Secret-123");
        Assert.IsFalse($"{connection}".Contains("Secret-123", StringComparison.Ordinal));
        Assert.IsFalse(new DnnAccount("host", "Secret-123", "a@b.c", "x", "en-US", "Default Website").ToString().Contains("Secret-123", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AccountRules_RefuseWhatDnnWouldFailOnSilently()
    {
        Assert.IsNotNull(DnnAccountRules.PasswordProblem("abc"), "DNN finishes without a host for a 3-character password.");
        Assert.IsNull(DnnAccountRules.PasswordProblem("abcdefg"));
        Assert.IsNotNull(DnnAccountRules.UserNameProblem("host,admin"));
        Assert.IsNotNull(DnnAccountRules.UserNameProblem(""));
        Assert.IsNull(DnnAccountRules.UserNameProblem("host"));
        Assert.IsNotNull(DnnAccountRules.EmailProblem("host"));
        Assert.IsNull(DnnAccountRules.EmailProblem("host@dnndev.me"));
        Assert.AreEqual(0, DnnAccountRules.Problems(new DnnAccount("host", "abcdefg", "h@x.io", "Site", "en-US", "Default Website")).Count);
        Assert.AreEqual(2, DnnAccountRules.Problems(new DnnAccount("host", "abcdefg", "h@x.io", "Site", "xx-XX", "Other")).Count);
    }

    [TestMethod]
    public void MembershipHash_KeyedAlgorithm_UsesTheSaltAsTheKey()
    {
        var salt = Convert.ToBase64String(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        // SqlMembershipProvider repeats the 16-byte salt to fill HMACSHA256's 64-byte key.
        var key = Enumerable.Repeat(Convert.FromBase64String(salt), 4).SelectMany(b => b).ToArray();
        var expected = Convert.ToBase64String(new HMACSHA256(key).ComputeHash(Encoding.Unicode.GetBytes("pässword")));
        Assert.AreEqual(expected, MembershipPasswords.Hash("pässword", salt, "HMACSHA256"));

        var plain = Convert.ToBase64String(SHA1.HashData(Convert.FromBase64String(salt).Concat(Encoding.Unicode.GetBytes("pässword")).ToArray()));
        Assert.AreEqual(plain, MembershipPasswords.Hash("pässword", salt, "SHA1"));
        Assert.IsFalse(MembershipPasswords.IsSupported("Whirlpool"));
    }

    [TestMethod]
    public void CredentialStore_KeepsAndForgetsASecret()
    {
        var store = new WindowsCredentialStore();
        var name = "tests/" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.IsNull(store.Read(name));
            Assert.IsTrue(store.Write(name, "Sécret;=\"'").Success);
            Assert.AreEqual("Sécret;=\"'", store.Read(name));
            Assert.IsTrue(store.Write(name, "changed").Success);
            Assert.AreEqual("changed", store.Read(name));
        }
        finally
        {
            Assert.IsTrue(store.Delete(name).Success);
        }
        Assert.IsNull(store.Read(name));
        Assert.IsTrue(store.Delete(name).Success, "Deleting what isn't there is fine.");
    }

    [TestMethod]
    public void Settings_ValidateDnnDefaultsAndDatabaseServer()
    {
        var settings = new UserSettings();
        Assert.AreEqual(0, settings.Validate().Count, string.Join("; ", settings.Validate()));

        settings.Projects.DnnDefaults.HostUsername = "a,b";
        settings.Projects.DnnDefaults.Language = "xx-XX";
        settings.SqlServer.Type = "sqlServer";
        settings.SqlServer.Authentication = "sql";
        var keys = settings.Validate().Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "projects.dnnDefaults.hostUsername");
        CollectionAssert.Contains(keys, "projects.dnnDefaults.language");
        CollectionAssert.Contains(keys, "sqlServer.userName"); // SQL Server authentication without a user name

        settings.SqlServer.Type = "oracle";
        settings.SqlServer.Server = "";
        keys = settings.Validate().Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "sqlServer.type");
        CollectionAssert.Contains(keys, "sqlServer.server");
    }

    [TestMethod]
    public void SettingsMigration_V2ProfileBecomesTheDatabaseServer()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {
              "version": 2,
              "projects": {
                "baseDirectory": "C:\\DNN",
                "databaseProfiles": [
                  { "id": "a1", "name": "Express", "type": "sqlServer", "server": ".\\SQLEXPRESS", "authentication": "sql", "userName": "dnn" },
                  { "id": "b2", "name": "File", "type": "localDbFile", "server": "(LocalDB)\\MSSQLLocalDB", "authentication": "windows", "userName": "" }
                ],
                "defaultDatabaseProfile": "a1"
              },
              "sqlServer": { "host": "localhost", "port": 1433, "saPassword": "x" },
              "appearance": { "projectColumns": [ "dnn", "cpu" ] }
            }
            """)!;
        var secrets = new MemorySecrets();
        secrets.Write(SecretNames.LegacyDatabaseProfilePassword("a1"), "Pa$$word");
        secrets.Write(SecretNames.LegacyDatabaseProfilePassword("b2"), "unused");

        SettingsMigrations.Apply(root, 2, secrets);

        Assert.AreEqual(3, (int)root["version"]!);
        var sql = root["sqlServer"]!;
        Assert.AreEqual("sqlServer", (string)sql["type"]!);
        Assert.AreEqual(@".\SQLEXPRESS", (string)sql["server"]!);
        Assert.AreEqual("sql", (string)sql["authentication"]!);
        Assert.AreEqual("dnn", (string)sql["userName"]!);
        Assert.IsNull(root["projects"]!["databaseProfiles"]);
        Assert.IsNull(root["projects"]!["defaultDatabaseProfile"]);
        CollectionAssert.AreEqual(new[] { "url", "dnn", "cpu" },
            root["appearance"]!["projectColumns"]!.AsArray().Select(c => (string)c!).ToArray(), "The Site column comes first.");
        Assert.AreEqual("Pa$$word", secrets.Read(SecretNames.DatabaseServerPassword));
        Assert.IsNull(secrets.Read(SecretNames.LegacyDatabaseProfilePassword("a1")));
        Assert.IsNull(secrets.Read(SecretNames.LegacyDatabaseProfilePassword("b2")));

        // Without a chosen profile the server stays the local container.
        var plain = (JsonObject)JsonNode.Parse("""{ "version": 2, "projects": { "defaultDatabaseProfile": "container" } }""")!;
        SettingsMigrations.Apply(plain, 2, secrets: null);
        Assert.AreEqual(SqlServerSettings.ContainerType, (string)plain["sqlServer"]!["type"]!);
    }

    [TestMethod]
    public void AppDataCleaner_DeletesOnlyWhatIsChosen()
    {
        var root = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        void Write(string relative, int bytes)
        {
            var file = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[bytes]);
        }
        try
        {
            Write("settings.json", 10);
            Write(@"logs\dnnmanager-2026-09-30.log", 100);
            Write(@"packages\dnnsoftware.Dnn.Platform\DNN_10.zip", 1000);
            Write(@"backups\settings.v2.json", 20);
            Write(@"backups\shop\shop_20261001_120000\shop.zip", 5000);
            Write(@"projects\shop.json", 30);
            var cleaner = new AppDataCleaner(paths);

            Assert.AreEqual(1000, cleaner.Measure(AppDataKind.DnnPackages));
            Assert.AreEqual(20, cleaner.Measure(AppDataKind.SettingsCopies), "Only the settings copies, not the project backups next to them.");
            Assert.AreEqual(5000, cleaner.Measure(AppDataKind.ProjectBackups), "Only the projects' folders, not the settings copies.");

            Assert.AreEqual(new CleanupResult(1000, 0), cleaner.Clean(AppDataKind.DnnPackages));
            Assert.IsFalse(Directory.Exists(paths.PackagesDirectory), "The emptied folder goes too.");
            Assert.AreEqual(new CleanupResult(20, 0), cleaner.Clean(AppDataKind.SettingsCopies));
            Assert.IsTrue(File.Exists(Path.Combine(root, @"backups\shop\shop_20261001_120000\shop.zip")));
            Assert.AreEqual(new CleanupResult(5000, 0), cleaner.Clean(AppDataKind.ProjectBackups));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(paths.BackupsDirectory).Length);

            Assert.IsTrue(File.Exists(paths.SettingsFile), "settings.json is never cleaned up.");
            Assert.IsTrue(File.Exists(Path.Combine(root, @"projects\shop.json")), "Nor what is known about the projects.");
            Assert.AreEqual(100, cleaner.Measure(AppDataKind.Logs), "Logs only when chosen.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OperationUndo_TakesBackWhatWasDone_LastFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var existing = Path.Combine(root, "web.config");
            File.WriteAllText(existing, "before");
            var newFolder = Path.Combine(root, "site");
            var newFile = Path.Combine(root, "new.txt");
            var order = new List<string>();
            var undo = new OperationUndo();
            Assert.IsTrue(undo.IsEmpty);

            // As an operation notes them: before each change.
            undo.DeleteFolderOnUndo(newFolder);
            Directory.CreateDirectory(Path.Combine(newFolder, "bin"));
            File.WriteAllText(Path.Combine(newFolder, "bin", "x.dll"), "x");
            File.SetAttributes(Path.Combine(newFolder, "bin", "x.dll"), FileAttributes.ReadOnly);
            undo.RestoreFileOnUndo(existing);
            File.WriteAllText(existing, "changed");
            undo.RestoreFileOnUndo(existing); // only the first counts - that is how it was
            File.WriteAllText(existing, "changed again");
            undo.RestoreFileOnUndo(newFile);
            File.WriteAllText(newFile, "new");
            undo.Add("first", () => { order.Add("first"); return Result.Ok(); });
            undo.Add("second", () => { order.Add("second"); return Result.Fail("in use"); });
            undo.CannotUndo("database [shop] was replaced.");
            undo.DeleteFolderOnUndo(root); // already there - not this operation's to delete
            Assert.IsFalse(undo.IsEmpty);

            var reporter = new RecordingReporter();
            Assert.IsFalse(await undo.RunAsync(reporter), "A failed step makes the undo incomplete.");

            CollectionAssert.AreEqual(new[] { "second", "first" }, order, "The last first.");
            Assert.AreEqual("before", File.ReadAllText(existing));
            Assert.IsFalse(File.Exists(newFile));
            Assert.IsFalse(Directory.Exists(newFolder), "Read-only files are deleted too.");
            Assert.IsTrue(Directory.Exists(root));
            StringAssert.Contains(reporter.Text, "FAIL second: in use");
            StringAssert.Contains(reporter.Text, "WARN Can't be put back: database [shop] was replaced.");
            Assert.IsTrue(undo.IsEmpty, "Done once.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LogView_WrapsAfterTheLastSpaceThatFits()
    {
        Assert.IsNull(LogView.RowStarts("short line", 20), "A line that fits has one row.");
        Assert.IsNull(LogView.RowStarts("exactly ten", 11));

        // "aaa bbb ccc ddd" at 8 a row: "aaa bbb " | "ccc ddd" - the space stays at the end of its row.
        CollectionAssert.AreEqual(new[] { 0, 8 }, LogView.RowStarts("aaa bbb ccc ddd", 8));
        // A space right at the edge: the row is full without it, the next starts after it.
        CollectionAssert.AreEqual(new[] { 0, 4 }, LogView.RowStarts("abc defg", 4));
        // A word longer than a row is broken where the row is full.
        CollectionAssert.AreEqual(new[] { 0, 4, 8 }, LogView.RowStarts("abcdefghij", 4));
        CollectionAssert.AreEqual(new[] { 0, 3, 7 }, LogView.RowStarts("ab cdefghij", 4));

        // Every row fits, and together they are the whole line.
        var text = @"2026-10-01 12:00:00 ERROR An exception was thrown while loading C:\DNN\shop\Portals\0\Skins\Xcillion\Home.ascx";
        foreach (var width in new[] { 1, 5, 17, 40 })
        {
            var starts = LogView.RowStarts(text, width)!;
            Assert.AreEqual(0, starts[0]);
            for (var i = 0; i < starts.Length; i++)
            {
                var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
                Assert.IsTrue(end - starts[i] is > 0 and var length && length <= width, $"Row {i} at width {width}.");
            }
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new();

        public string? Read(string name) => _secrets.GetValueOrDefault(name);

        public Result Write(string name, string secret)
        {
            _secrets[name] = secret;
            return Result.Ok();
        }

        public Result Delete(string name)
        {
            _secrets.Remove(name);
            return Result.Ok();
        }
    }
}
