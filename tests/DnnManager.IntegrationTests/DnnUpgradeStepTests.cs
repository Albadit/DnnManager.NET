using System.Xml.Linq;
using DnnManager.Application.Upgrades;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Dnn;
using DnnManager.Infrastructure.Github;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The parts of an upgrade step that need no site: DNN's binding-redirect merge (what its local upgrade does for each
/// assembly), and reading a failure for its likely cause.
/// </summary>
[TestClass]
public sealed class DnnUpgradeStepTests
{
    private const string Asm = "urn:schemas-microsoft-com:asm.v1";

    private static XDocument Config(string bindings) => XDocument.Parse(
        $"<configuration><appSettings /><runtime><assemblyBinding xmlns=\"{Asm}\">{bindings}</assemblyBinding></runtime></configuration>");

    [TestMethod]
    public void A_binding_redirect_is_pointed_at_the_new_version_as_DNNs_merge_does()
    {
        var config = Config(
            "<dependentAssembly><assemblyIdentity name=\"AngleSharp\" publicKeyToken=\"E83494DCDC6D31EA\" culture=\"neutral\" />" +
            "<bindingRedirect oldVersion=\"0.0.0.0-0.17.1.0\" newVersion=\"0.17.1.0\" /></dependentAssembly>");
        Assert.IsTrue(DnnPackageInstaller.SetBindingRedirect(config, "AngleSharp", "e83494dcdc6d31ea", new Version(1, 3, 0, 0)));
        XNamespace ab = Asm;
        var redirect = config.Descendants(ab + "dependentAssembly").Single().Element(ab + "bindingRedirect")!;
        Assert.AreEqual("0.0.0.0-32767.32767.32767.32767", (string?)redirect.Attribute("oldVersion"));
        Assert.AreEqual("1.3.0.0", (string?)redirect.Attribute("newVersion"));
        // Already so: nothing to change.
        Assert.IsFalse(DnnPackageInstaller.SetBindingRedirect(config, "AngleSharp", "e83494dcdc6d31ea", new Version(1, 3, 0, 0)));
    }

    [TestMethod]
    public void A_missing_binding_redirect_is_added_and_others_are_left_alone()
    {
        var config = Config(
            "<dependentAssembly><assemblyIdentity name=\"Newtonsoft.Json\" publicKeyToken=\"30ad4fe6b2a6aeed\" />" +
            "<bindingRedirect oldVersion=\"0.0.0.0-32767.32767.32767.32767\" newVersion=\"13.0.0.0\" /></dependentAssembly>");
        Assert.IsTrue(DnnPackageInstaller.SetBindingRedirect(config, "MailKit", "4e064fe7c44a8f1b", new Version(4, 8, 0, 0)));
        XNamespace ab = Asm;
        var names = config.Descendants(ab + "assemblyIdentity").Select(a => (string?)a.Attribute("name")).ToArray();
        CollectionAssert.AreEqual(new[] { "Newtonsoft.Json", "MailKit" }, names);
        Assert.AreEqual("13.0.0.0", (string?)config.Descendants(ab + "bindingRedirect").First().Attribute("newVersion"));
    }

    [TestMethod]
    public void A_codeBase_and_the_culture_stay_when_the_redirect_is_set()
    {
        // Side by side: an older version loaded from its own folder - the redirect changes, the codeBase is still needed.
        var config = Config(
            "<dependentAssembly><assemblyIdentity name=\"Imageflow.Net\" publicKeyToken=\"b4b1e5a3b9d5e5a1\" culture=\"neutral\" />" +
            "<codeBase version=\"0.10.0.0\" href=\"bin/Imageflow/0.10/Imageflow.Net.dll\" /></dependentAssembly>");
        Assert.IsTrue(DnnPackageInstaller.SetBindingRedirect(config, "Imageflow.Net", "b4b1e5a3b9d5e5a1", new Version(0, 13, 0, 0)));
        XNamespace ab = Asm;
        var dependent = config.Descendants(ab + "dependentAssembly").Single();
        Assert.AreEqual("neutral", (string?)dependent.Element(ab + "assemblyIdentity")!.Attribute("culture"));
        Assert.IsNotNull(dependent.Element(ab + "codeBase"), "The codeBase went.");
        Assert.AreEqual("0.13.0.0", (string?)dependent.Element(ab + "bindingRedirect")!.Attribute("newVersion"));
    }

    [TestMethod]
    public void A_redirect_in_a_later_assemblyBinding_is_set_there_not_added_again()
    {
        var config = XDocument.Parse(
            $"<configuration><runtime><assemblyBinding xmlns=\"{Asm}\" /><assemblyBinding xmlns=\"{Asm}\">" +
            "<dependentAssembly><assemblyIdentity name=\"MailKit\" publicKeyToken=\"4e064fe7c44a8f1b\" />" +
            "<bindingRedirect oldVersion=\"0.0.0.0-4.0.0.0\" newVersion=\"4.0.0.0\" /></dependentAssembly></assemblyBinding></runtime></configuration>");
        Assert.IsTrue(DnnPackageInstaller.SetBindingRedirect(config, "MailKit", "4e064fe7c44a8f1b", new Version(4, 8, 0, 0)));
        XNamespace ab = Asm;
        var redirects = config.Descendants(ab + "bindingRedirect").ToList();
        Assert.AreEqual(1, redirects.Count, config.ToString());
        Assert.AreEqual("4.8.0.0", (string?)redirects[0].Attribute("newVersion"));
    }

    [TestMethod]
    public void GitHubs_next_page_is_read_from_its_Link_header()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add("Link", "<https://api.github.com/repositories/1/releases?per_page=100&page=2>; rel=\"next\", " +
                                     "<https://api.github.com/repositories/1/releases?per_page=100&page=3>; rel=\"last\"");
        Assert.AreEqual("https://api.github.com/repositories/1/releases?per_page=100&page=2", GitHubDnnReleaseService.NextPage(response));
        using var last = new HttpResponseMessage();
        last.Headers.Add("Link", "<https://api.github.com/repositories/1/releases?per_page=100&page=1>; rel=\"prev\"");
        Assert.IsNull(GitHubDnnReleaseService.NextPage(last));
    }

    [TestMethod]
    public void A_web_config_without_runtime_gets_one()
    {
        var config = XDocument.Parse("<configuration><appSettings /></configuration>");
        Assert.IsTrue(DnnPackageInstaller.SetBindingRedirect(config, "Dnn.Sample", "0123456789abcdef", new Version(1, 0, 0, 0)));
        XNamespace ab = Asm;
        Assert.AreEqual(1, config.Root!.Element("runtime")!.Element(ab + "assemblyBinding")!.Elements(ab + "dependentAssembly").Count());
    }

    [TestMethod]
    public void DNNs_log_errors_are_read_in_DNN_9s_and_DNN_10s_formats_with_their_message()
    {
        var site = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(site, "Portals", "_default", "Logs");
        Directory.CreateDirectory(logs);
        try
        {
            var since = new DateTime(2026, 10, 4, 23, 30, 0, DateTimeKind.Local).ToUniversalTime();
            File.WriteAllLines(Path.Combine(logs, "2026.10.04.log.resources"),
            [
                "2026-10-04 23:20:00,123 [PC][Thread:9][ERROR] DotNetNuke.Old - before the step",
                "2026-10-04 23:37:54,504 [PC][Thread:9][ERROR] DotNetNuke.Nine - DNN 9's format",
                // DNN 10's own lines, as the failing-step test logged them: the SQL error's text on the next line.
                "2026-10-04 23:37:54.504+02:00 [Albadit][D:2][T:28][ERROR] DotNetNuke.Data.PetaPoco.PetaPocoHelper - [1] Error executing SQL: ;Exec dbo.UpdateDatabaseVersionAndName @0, @1, @2, @3",
                "Acme audit: version change refused",
                "2026-10-04 23:38:00.000+02:00 [Albadit][D:2][T:28][INFO] DotNetNuke.Fine - not an error",
                "2026-10-04 23:38:55.008+02:00 [Albadit][D:2][T:28][FATAL] DotNetNuke.Web - System.IO.IOException: …installBlocker.lock",
                "   at System.IO.__Error.WinIOError(Int32 errorCode, String maybeFullPath)"
            ]);
            var errors = DnnUpgradeChecks.DnnLogErrors(site, since).ToList();
            Assert.AreEqual(3, errors.Count, string.Join(Environment.NewLine, errors));
            StringAssert.Contains(errors[0], "DNN 9's format");
            StringAssert.Contains(errors[1], "UpdateDatabaseVersionAndName @0, @1, @2, @3 → Acme audit: version change refused");
            Assert.IsFalse(errors[2].Contains("WinIOError"), "A stack trace line isn't the message.");
        }
        finally
        {
            Directory.Delete(site, recursive: true);
        }
    }

    [TestMethod]
    public void Content_is_compared_one_by_one_and_what_DNN_removed_with_its_extension_isnt_lost()
    {
        // DNN 10 removes the Digital Assets Manager: its module and the page that held only it go - expected.
        var before = new DnnSiteState(null, ["DigitalAssetsManagement", "DNN_HTML"], [],
        [
            new("page", 1, "//Home", Permissions: 3), new("page", 2, "//Admin//FileManagement", Permissions: 2), new("page", 3, "//Products", Permissions: 4),
            new("module", 10, "'Files'", "DigitalAssetsManagement", 2), new("module", 11, "'Welcome'", "DNN_HTML", 1, Permissions: 2),
            new("module", 12, "'Specials'", "DNN_HTML", 3), new("job", 20, "DotNetNuke.Old.Job")
        ]);
        var after = new List<DnnContentItem> { new("page", 1, "//Home", Permissions: 3), new("page", 3, "//Products", Permissions: 1), new("module", 11, "'Welcome'", "DNN_HTML", 1, Permissions: 2) };
        var outcome = DnnContentComparison.Compare(before, after, ["DNN_HTML", "Dnn.ResourceManager"]);
        CollectionAssert.AreEquivalent(new[] { "'Files'", "//Admin//FileManagement" }, outcome.RemovedWithExtension.Select(i => i.Name).ToArray());
        Assert.AreEqual("'Specials'", outcome.Lost.Single().Name, "A module whose extension is still there was lost.");
        StringAssert.Contains(outcome.FewerPermissions.Single(), "//Products: 4 → 1");
        Assert.AreEqual("DotNetNuke.Old.Job", outcome.JobsGone.Single().Name);
    }

    [TestMethod]
    public void DNNs_own_Admin_and_Host_pages_gone_with_an_upgrade_arent_lost_content()
    {
        // As 7.4.2 → 8.0.4 did: some of DNN's Admin and Host pages go, with the modules on them; a page of the site's own stays content.
        var before = new DnnSiteState(null, ["DotNetNuke.SQL"], [],
        [
            new("page", 40, "//Host//SQL", Permissions: 1), new("page", 41, "//Admin//LogViewer"), new("page", 42, "//Administration", Permissions: 1),
            new("module", 50, "'SQL'", "DotNetNuke.SQL", 40), new("module", 51, "'Notes'", "DNN_HTML", 42),
            // 8.0.4 → 9.1.1: the console module on //Admin goes, the page stays.
            new("page", 43, "//Admin"), new("module", 52, "'Basic Features'", "DotNetNuke.Console", 43)
        ]);
        var outcome = DnnContentComparison.Compare(before, [new("page", 43, "//Admin")], ["DotNetNuke.SQL", "DNN_HTML", "DotNetNuke.Console"]);
        CollectionAssert.AreEquivalent(new[] { "//Host//SQL", "//Admin//LogViewer", "'SQL'", "'Basic Features'" }, outcome.RemovedWithExtension.Select(i => i.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "//Administration", "'Notes'" }, outcome.Lost.Select(i => i.Name).ToArray());
    }

    [TestMethod]
    public void A_module_whose_module_type_the_upgrade_removed_isnt_lost_content()
    {
        // 8.0.4 → 9.1.1: DNN 8's 'Navigation' modules have no package, and DNN 9 removes their module type.
        var before = new DnnSiteState(null, ["DNN_HTML"], [],
        [
            new("type", 1, "Navigation"), new("type", 2, "DNN_HTML"),
            new("module", 60, "'Navigation' (#60)", "Navigation", 10), new("module", 61, "'Text' (#61)", "DNN_HTML", 10),
            // 8.0.4 → 9.1.1, as tested: the Console module on //ActivityFeed goes, its extension stays.
            new("module", 62, "'Navigation' (#62)", "DotNetNuke.Console", 11)
        ]);
        var outcome = DnnContentComparison.Compare(before, [new("type", 2, "DNN_HTML")], ["DNN_HTML", "DotNetNuke.Console"]);
        CollectionAssert.AreEquivalent(new[] { "'Navigation' (#60)", "'Navigation' (#62)" }, outcome.RemovedWithExtension.Select(i => i.Name).ToArray());
        Assert.AreEqual("'Text' (#61)", outcome.Lost.Single().Name, "A module whose type is still there was lost.");
    }

    [TestMethod]
    [DataRow("Could not load file or assembly 'AngleSharp' or one of its dependencies. The located assembly's manifest definition does not match", "binding redirect")]
    [DataRow("Another installation or upgrade of this site is running (DNN's installBlocker.lock).", "installBlocker.lock")]
    [DataRow("DNN couldn't connect to its database - check the site's connection string", "can't reach its database")]
    [DataRow("Content was lost (Users 12 → 11).", "content")]
    [DataRow("The host 'dnnhost' can't sign in (auth cookie not set)", "signing in")]
    [DataRow("DNN's installer answered HTTP 503 Service Unavailable", "HTTP 503")]
    [DataRow("Expected DNN 10.3.3 - the files are at 10.3.3, the database at 10.2.5", "database isn't at the version")]
    public void A_failure_is_read_for_its_likely_cause(string error, string cause) =>
        StringAssert.Contains(DnnUpgradeDiagnosis.Explain(error, []).Cause, cause);

    [TestMethod]
    public void A_time_out_with_DNNs_lock_in_its_log_is_read_as_the_lock()
    {
        // As on IIS (DNN Manager 1.7.5's first run): the site didn't answer because DNN's lock was left - the logs say so.
        var evidence = new[]
        {
            @"Kept Providers\DataProviders\SqlDataProvider\10.03.00.log.resources",
            "DNN log: 2026-10-05 00:15:10.231+02:00 [Albadit][D:2][T:9][ERROR] DotNetNuke.Services.Exceptions.Exceptions - Resource Not Found: - / → " +
            "System.Web.HttpException (0x80004005): The site was accessed while an installation/upgrade was in progress"
        };
        StringAssert.Contains(DnnUpgradeDiagnosis.Explain("The site didn't answer within 3 minutes.", evidence).Cause, "installBlocker.lock");
        // With nothing more telling, a time-out is a time-out.
        StringAssert.Contains(DnnUpgradeDiagnosis.Explain("The site didn't answer within 3 minutes.", [evidence[0]]).Cause, "took too long");
    }

    [TestMethod]
    public void DNNs_output_is_read_without_the_error_page_it_broke_off_into()
    {
        const string html = "<!-- GET /Install/Install.aspx?mode=upgrade → HTTP 200 OK  -->" +
            "<h2>Upgrade Status Report</h2>00:00:01.000 - Upgrading DNN<br>00:00:59.000 - Upgrade Complete<br>" +
            "<!DOCTYPE html><html><head><title>The process cannot access the file</title><meta name=\"viewport\" />" +
            "<style>body {font-family:\"Verdana\";} .error {margin-bottom: 10px;} @media screen and (max-width: 639px) { pre { width: 440px; } }</style></head>" +
            "<body bgcolor=\"white\"><span><H1>Server Error in '/' Application.<hr></H1>";
        var lines = UpgradeDnnUseCase.LastLines(html, 8).ToList();
        CollectionAssert.AreEqual(new[] { "Upgrade Status Report", "00:00:01.000 - Upgrading DNN", "00:00:59.000 - Upgrade Complete" }, lines);
    }

    [TestMethod]
    public void A_cause_is_also_found_in_the_logs_and_an_unknown_one_says_so()
    {
        var fromLogs = DnnUpgradeDiagnosis.Explain("Step failed.", ["Database script 10.03.00.SqlDataProvider: Error in UpdateDatabaseVersion"]);
        StringAssert.Contains(fromLogs.Cause, "database scripts failed");
        Assert.IsTrue(fromLogs.Fixes.Count > 0);
        StringAssert.StartsWith(DnnUpgradeDiagnosis.Explain("Something new.", []).Cause, "Unknown");
    }
}
