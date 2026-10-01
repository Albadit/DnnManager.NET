using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Dnn;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Sql;
using Microsoft.Data.SqlClient;

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
    public void Settings_ValidateDnnDefaultsAndProfiles()
    {
        var settings = new UserSettings();
        Assert.AreEqual(0, settings.Validate().Count, string.Join("; ", settings.Validate()));

        settings.Projects.DnnDefaults.HostUsername = "a,b";
        settings.Projects.DnnDefaults.Language = "xx-XX";
        settings.Projects.DatabaseProfiles.Add(new DatabaseProfileSettings { Id = "p1", Name = "Express", Server = @".\SQLEXPRESS", Authentication = "sql" });
        settings.Projects.DefaultDatabaseProfile = "nope";
        var keys = settings.Validate().Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "projects.dnnDefaults.hostUsername");
        CollectionAssert.Contains(keys, "projects.dnnDefaults.language");
        CollectionAssert.Contains(keys, "projects.databaseProfiles"); // SQL authentication without a user name
        CollectionAssert.Contains(keys, "projects.defaultDatabaseProfile");
    }
}