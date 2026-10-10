using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.IntegrationTests;

/// <summary>The settings as rows: what a save replaces, and what it leaves - a newer version's rows.</summary>
[TestClass]
public sealed class SettingsRowsTests
{
    [TestMethod]
    public void A_save_keeps_a_newer_versions_rows_and_drops_the_items_that_are_gone()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "rows-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            var store = new SettingsStore(paths);
            store.Load();
            store.Update(s => s.Keyboard.Shortcuts["project.start"] = "Ctrl+F5");
            using (var connection = new AppDatabase(paths).Open())
            {
                // Written by a newer DNN Manager: a value in a section this one knows, and a section of its own.
                AppDatabase.Execute(connection, "INSERT INTO settings (key, value) VALUES ('projects.newThing', 'x'), ('future.value', 'y')");
            }

            store.Update(s =>
            {
                s.Keyboard.Shortcuts.Clear();
                s.Projects.DnnReleaseSources = [s.Projects.DnnReleaseSources[0]];
            });

            var saved = store.SavedValues();
            Assert.AreEqual("x", saved["projects.newThing"], "A newer version's value stays.");
            Assert.AreEqual("y", saved["future.value"]);
            Assert.IsFalse(saved.ContainsKey("keyboard.shortcuts{project.start}"), "A shortcut put back to its default goes.");
            Assert.AreEqual("1", saved["projects.dnnReleaseSources"]);
            Assert.IsFalse(saved.ContainsKey("projects.dnnReleaseSources[1]"), "The list's items that are gone, go.");
            Assert.AreEqual(1, store.Read().Projects.DnnReleaseSources.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void Keep_running_when_closed_is_on_by_default_and_reaches_the_running_app_when_saved()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "rows-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(new AppDataPaths(root));
            var options = store.Load().Settings.ToAppOptions();
            Assert.IsTrue(options.KeepRunningWhenClosed, "Closing the window hides it, unless asked otherwise.");
            Assert.AreEqual("true", store.SavedValues()["window.keepRunningWhenClosed"]);

            store.Update(s => s.Window.KeepRunningWhenClosed = false);

            Assert.AreEqual("false", store.SavedValues()["window.keepRunningWhenClosed"]);
            options.Apply(store.Read().ToAppOptions());
            Assert.IsFalse(options.KeepRunningWhenClosed, "Saved on Settings, it applies at once.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void A_new_installation_gets_an_sa_password_of_its_own_which_a_reset_keeps()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "sa-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(new AppDataPaths(root));
            var first = store.Load().Settings.SqlServer.SaPassword;
            Assert.AreNotEqual("Admin@123", first, "The password everybody knows.");
            Assert.AreEqual(first, store.Read().SqlServer.SaPassword, "Saved at the first start, not made up again.");

            // The container was made with it: resetting the settings doesn't lock DNN Manager out of it.
            store.ResetToDefaults();
            Assert.AreEqual(first, store.Read().SqlServer.SaPassword);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void A_reset_drops_an_sa_password_another_windows_user_encrypted_so_the_next_start_works()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "sa-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            var store = new SettingsStore(paths);
            store.Load();
            using (var connection = new AppDatabase(paths).Open())
            {
                // As Documents\DnnManager copied from another account (or PC): DPAPI there, unreadable here.
                var foreign = "dpapi:" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
                AppDatabase.Execute(connection, "UPDATE settings SET value = $value WHERE key = 'sqlServer.saPassword'", ("$value", foreign));
            }
            Assert.ThrowsExactly<SettingsException>(() => store.Load());

            // Kept, it would stop the next start the same way: the start-up dialog's Reset would never get past it.
            store.ResetToDefaults();
            var settings = store.Load().Settings;
            Assert.IsFalse(string.IsNullOrEmpty(settings.SqlServer.SaPassword));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void A_made_up_sql_password_meets_sql_servers_policy_and_needs_no_quoting()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = SqlPasswords.New();
            Assert.AreEqual(24, password.Length);
            Assert.IsTrue(password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit) && password.Any(c => !char.IsLetterOrDigit(c)), password);
            Assert.IsFalse(password.Any(c => c is ';' or '=' or '\'' or '"' or '$' or ' '), password);
        }
        Assert.AreNotEqual(SqlPasswords.New(), SqlPasswords.New());
    }
}
