using System.Text.Json.Nodes;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.IntegrationTests;

/// <summary>The local SQL container's login is sqlServer.userName - also in a file that has the unreleased sqlServer.user.</summary>
[TestClass]
public sealed class SettingsUserNameTests
{
    [TestMethod]
    public void TheContainersUser_IsTheUserName_AndSaWhenEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(root);
            File.WriteAllText(paths.SettingsFile, """
                { "version": 3, "sqlServer": { "type": "container", "host": "localhost", "port": 1433, "saPassword": "Admin@123",
                                               "userName": "", "user": "dnnadmin" } }
                """);

            var loaded = new SettingsStore(paths).Load();
            Assert.AreEqual("dnnadmin", loaded.Settings.SqlServer.UserName, "user is taken over into the empty userName.");
            Assert.AreEqual("dnnadmin", loaded.Settings.ToAppOptions().Docker.SqlUser);
            var sql = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!["sqlServer"]!.AsObject();
            Assert.IsFalse(sql.ContainsKey("user"), "The separate key is gone from the file.");
            Assert.AreEqual("dnnadmin", (string?)sql["userName"]);

            loaded.Settings.SqlServer.UserName = "";
            Assert.AreEqual("sa", loaded.Settings.ToAppOptions().Docker.SqlUser, "Empty is sa.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
