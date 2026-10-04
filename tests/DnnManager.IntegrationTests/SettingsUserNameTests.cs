using DnnManager.Infrastructure.Settings;

namespace DnnManager.IntegrationTests;

/// <summary>The local SQL container's login is sqlServer.userName - sa when empty; its password is saved encrypted.</summary>
[TestClass]
public sealed class SettingsUserNameTests
{
    [TestMethod]
    public void TheContainersUser_IsTheUserName_AndSaWhenEmpty_ItsPasswordSavedEncrypted()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(new AppDataPaths(root));
            store.Load();
            store.Update(s =>
            {
                s.SqlServer.UserName = "dnnadmin";
                s.SqlServer.SaPassword = "Pa$$word1";
            });

            var read = new SettingsStore(new AppDataPaths(root)).Read();
            Assert.AreEqual("dnnadmin", read.ToAppOptions().Docker.SqlUser);
            Assert.AreEqual("Pa$$word1", read.SqlServer.SaPassword, "Decrypted as it is read.");
            var saved = store.SavedValues()["sqlServer.saPassword"];
            Assert.AreNotEqual("Pa$$word1", saved, "Never saved as plain text.");
            Assert.IsTrue(SecretProtector.IsProtected(saved));

            read.SqlServer.UserName = "";
            Assert.AreEqual("sa", read.ToAppOptions().Docker.SqlUser, "Empty is sa.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
