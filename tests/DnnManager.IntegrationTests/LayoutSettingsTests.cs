using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.IntegrationTests;

/// <summary>Customize Layout's choices: what they mean for the window, what is allowed, and that they are kept in the settings.</summary>
[TestClass]
public sealed class LayoutSettingsTests
{
    [TestMethod]
    [DataRow("center", "left", false)]
    [DataRow("center", "right", false)]
    [DataRow("justify", "left", true)]
    [DataRow("justify", "right", true)]
    [DataRow("left", "left", true)]
    [DataRow("left", "right", false)]
    [DataRow("right", "left", false)]
    [DataRow("right", "right", true)]
    public void ThePanelReachesUnderTheSidebar_OnlyTowardsItsSide_OrJustified(string alignment, string sidebar, bool under)
    {
        var layout = new LayoutSettings { PanelAlignment = alignment, SidebarPosition = sidebar };
        Assert.AreEqual(under, layout.PanelUnderSidebar);
    }

    [TestMethod]
    public void ValuesItDoesNotKnow_AreNotAllowed()
    {
        var settings = new UserSettings();
        Assert.IsEmpty(settings.Validate(), "The defaults are allowed.");

        settings.Layout = new LayoutSettings { SidebarPosition = "top", PanelAlignment = "middle", QuickInputPosition = "bottom", Density = "tiny" };
        CollectionAssert.AreEquivalent(
            new[] { "layout.sidebarPosition", "layout.panelAlignment", "layout.quickInputPosition", "layout.density" },
            settings.Validate().Select(p => p.Key).ToArray());
    }

    [TestMethod]
    public void TheLayout_IsSavedAsRows_AndSettingsWithoutIt_GetTheDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            var store = new SettingsStore(paths);
            store.Load();
            store.Update(s => s.Appearance.Theme = "dark");
            // Saved by a DNN Manager before Customize Layout: no layout rows.
            using (var connection = new AppDatabase(paths).Open())
                AppDatabase.Execute(connection, "DELETE FROM settings WHERE key LIKE 'layout.%'");

            var loaded = new SettingsStore(paths).Load().Settings;
            Assert.AreEqual("left", loaded.Layout.SidebarPosition);
            Assert.AreEqual("center", loaded.Layout.PanelAlignment);
            Assert.IsTrue(loaded.Layout.StatusBarVisible);
            Assert.IsTrue(loaded.Layout.MenuBarVisible);
            Assert.AreEqual("left", store.SavedValues()["layout.sidebarPosition"], "The missing values are saved with their defaults.");

            store.Update(s => s.Layout = new LayoutSettings
            {
                SidebarPosition = "right", PanelAlignment = "justify", MenuBarVisible = false, StatusBarVisible = false,
                QuickInputPosition = "center", Density = "compact"
            });

            var saved = store.SavedValues();
            Assert.AreEqual("right", saved["layout.sidebarPosition"]);
            Assert.AreEqual("justify", saved["layout.panelAlignment"]);
            Assert.AreEqual("false", saved["layout.menuBarVisible"]);
            Assert.AreEqual("false", saved["layout.statusBarVisible"]);
            var read = store.Read().ToAppOptions().Layout;
            Assert.IsTrue(read.SidebarRight && read.PanelUnderSidebar && read.QuickInputCentered && read.Compact);
            Assert.AreEqual("dark", store.Read().Appearance.Theme, "The rest is left as it was.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}