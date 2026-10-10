using System.IO.Compression;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Sql;
using DnnManager.Infrastructure.WebConfigs;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>web.config made ready for the live server (Export for deployment): what DNN Manager switched off for local use goes back on.</summary>
[TestClass]
public sealed class WebConfigDeploymentTests
{
    private const string WebConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <connectionStrings>
            <add name="SiteSqlServer" connectionString="Data Source=.;Initial Catalog=local;Integrated Security=True" providerName="System.Data.SqlClient" />
          </connectionStrings>
          <appSettings>
            <add key="SiteSqlServer" value="Data Source=.;Initial Catalog=local;Integrated Security=True" />
          </appSettings>
          <system.web>
            <compilation debug="true" targetFramework="4.8" />
          </system.web>
          <system.webServer>
            <rewrite>
              <rules>
                <rule name="Redirect to HTTPS" stopProcessing="true">
                  <match url="(.*)" />
                  <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
                </rule>
              </rules>
            </rewrite>
          </system.webServer>
        </configuration>
        """;

    private string _dir = "";
    private readonly WebConfigService _service = new(NullLogger<WebConfigService>.Instance);

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerWebConfigTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string Write()
    {
        var path = Path.Combine(_dir, "web.config");
        File.WriteAllText(path, WebConfig);
        return path;
    }

    [TestMethod]
    public void TheHttpsRules_SwitchedOffLocally_GoBackOn_AsTheyWere()
    {
        var path = Write();
        var original = XDocument.Load(path).ToString();
        Assert.AreEqual("Redirect to HTTPS", _service.DisableHttpsRedirectRules(path).Value!.SwitchedOff.Single());

        var enabled = _service.EnableHttpsRedirectRules(path);

        Assert.IsTrue(enabled.Success, enabled.Error);
        CollectionAssert.AreEqual(new[] { "Redirect to HTTPS" }, enabled.Value!.ToArray());
        Assert.AreEqual(original, XDocument.Load(path).ToString(), "No enabled=\"false\", no comment - the rule as it was.");
    }

    [TestMethod]
    public void ARuleTheSiteSwitchedOffItself_StaysOff()
    {
        var path = Write();
        File.WriteAllText(path, File.ReadAllText(path).Replace("<rule name=\"Redirect to HTTPS\"", "<rule name=\"Redirect to HTTPS\" enabled=\"false\""));

        var enabled = _service.EnableHttpsRedirectRules(path);

        Assert.AreEqual(0, enabled.Value!.Count);
        Assert.AreEqual("false", (string?)XDocument.Load(path).Descendants("rule").Single().Attribute("enabled"));
    }

    [TestMethod]
    public void TheLiveConnectionString_AndDebugOff_AreWritten()
    {
        var path = Write();
        const string live = "Data Source=sql.example.com;Initial Catalog=live;User ID=dnn;Password=p;w=d";

        Assert.IsTrue(_service.WriteConnectionString(path, live).Success);
        Assert.IsTrue(_service.SetDebug(path, false).Success);

        var doc = XDocument.Load(path);
        Assert.AreEqual(live, (string?)doc.Descendants("connectionStrings").Elements("add").Single().Attribute("connectionString"));
        Assert.AreEqual(live, (string?)doc.Descendants("appSettings").Elements("add").Single().Attribute("value"));
        Assert.AreEqual("false", (string?)doc.Descendants("compilation").Single().Attribute("debug"));
    }

    [TestMethod]
    public void A_site_whose_file_is_Web_config_gets_one_web_config_in_the_package_the_prepared_one()
    {
        var zipPath = Path.Combine(_dir, "shop.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("Web.config").Open())) writer.Write(WebConfig);
            zip.CreateEntry("bin/DotNetNuke.dll");
        }
        const string live = "Data Source=sql.example.com;Initial Catalog=live;User ID=dnn;Password=p";
        var export = new ExportForDeploymentUseCase(null!, null!, null!, null!, null!, _service,
            NullLogger<ExportForDeploymentUseCase>.Instance, new OperationUndo(), new TempIn(_dir));

        var result = export.PrepareWebConfig(zipPath,
            new ExportForDeploymentRequest { ProjectName = "shop", ProjectDirectory = _dir, OutputFolder = _dir, ConnectionString = live },
            new RecordingReporter());

        Assert.IsTrue(result.Success, result.Error);
        using var package = ZipFile.OpenRead(zipPath);
        // Two would leave the local connection string (and debug on) on the server, whichever unzips last.
        var webConfigs = package.Entries.Where(e => e.FullName.Equals("web.config", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.AreEqual(1, webConfigs.Count, string.Join(", ", webConfigs.Select(e => e.FullName)));
        Assert.AreEqual("Web.config", webConfigs[0].FullName, "The site's own capitals are kept.");
        using var reader = new StreamReader(webConfigs[0].Open());
        var doc = XDocument.Parse(reader.ReadToEnd());
        Assert.AreEqual(live, (string?)doc.Descendants("connectionStrings").Elements("add").Single().Attribute("connectionString"));
        Assert.IsNotNull(package.GetEntry("bin/DotNetNuke.dll"));
    }

    [TestMethod]
    public void A_site_reaches_the_container_at_127_0_0_1_not_localhost()
    {
        // .NET Framework's SqlClient (a DNN site's) goes to localhost by this computer's name and network address - the
        // container, published on the loopback only, isn't there: "The remote computer refused the network connection".
        var written = ConnectionStrings.ForSite(new DatabaseConnection(DatabaseKind.Container, "localhost,1433", "shop", SqlAuthentication.Sql, "dnn_shop", "p"));
        StringAssert.Contains(written, "Data Source=127.0.0.1,1433");
        Assert.AreEqual(@".\SQLEXPRESS", new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            ConnectionStrings.ForSite(new DatabaseConnection(DatabaseKind.SqlServer, @".\SQLEXPRESS", "shop", SqlAuthentication.Windows))).DataSource);
    }

    [TestMethod]
    public void An_existing_site_on_localhost_is_pointed_at_127_0_0_1_the_rest_as_it_was()
    {
        var path = Path.Combine(_dir, "web.config");
        File.WriteAllText(path, WebConfig.Replace("Data Source=.;Initial Catalog=local;Integrated Security=True",
            "Data Source=localhost,1433;Initial Catalog=shop;User ID=dnn_shop;Password=Pw-1;Connect Timeout=20"));

        var changed = _service.UseLoopbackAddress(path);

        Assert.IsTrue(changed is { Success: true, Value: true }, changed.Error);
        var doc = XDocument.Load(path);
        var site = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            (string?)doc.Descendants("connectionStrings").Elements("add").Single().Attribute("connectionString"));
        Assert.AreEqual("127.0.0.1,1433", site.DataSource);
        Assert.AreEqual("shop", site.InitialCatalog);
        Assert.AreEqual("dnn_shop", site.UserID);
        Assert.AreEqual("Pw-1", site.Password);
        Assert.AreEqual(20, site.ConnectTimeout);
        // The appSettings copy DNN's upgrade wizard reads, too.
        StringAssert.Contains((string?)doc.Descendants("appSettings").Elements("add").Single().Attribute("value"), "127.0.0.1,1433");
        // Once is enough: nothing to change the second time.
        Assert.IsTrue(_service.UseLoopbackAddress(path) is { Success: true, Value: false });
    }

    private sealed class TempIn(string folder) : IPrivateTemp
    {
        public string Folder => folder;
    }
}
