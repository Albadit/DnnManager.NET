using System.Xml.Linq;
using DnnManager.Infrastructure.WebConfigs;
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
}
