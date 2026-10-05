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
}
