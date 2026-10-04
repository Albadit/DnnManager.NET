using System.Text.Json.Nodes;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.IntegrationTests;

/// <summary>Customize Layout's choices: what they mean for the window, what is allowed, and that they are kept in settings.json.</summary>
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
    public void TheLayout_IsSavedInSettingsJson_AndAFileWithoutIt_GetsTheDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(root);
            // Written by a DNN Manager before Customize Layout.
            File.WriteAllText(paths.SettingsFile, """{ "version": 3, "appearance": { "theme": "dark" } }""");
            var store = new SettingsStore(paths);
            var loaded = store.Load().Settings;
            Assert.AreEqual("left", loaded.Layout.SidebarPosition);
            Assert.AreEqual("center", loaded.Layout.PanelAlignment);
            Assert.IsTrue(loaded.Layout.StatusBarVisible);
            Assert.IsTrue(loaded.Layout.MenuBarVisible);

            store.Update(s => s.Layout = new LayoutSettings
            {
                SidebarPosition = "right", PanelAlignment = "justify", MenuBarVisible = false, StatusBarVisible = false,
                QuickInputPosition = "center", Density = "compact"
            });

            var layout = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!["layout"]!.AsObject();
            Assert.AreEqual("right", (string?)layout["sidebarPosition"]);
            Assert.AreEqual("justify", (string?)layout["panelAlignment"]);
            Assert.IsFalse((bool)layout["menuBarVisible"]!);
            Assert.IsFalse((bool)layout["statusBarVisible"]!);
            var read = store.Read().ToAppOptions().Layout;
            Assert.IsTrue(read.SidebarRight && read.PanelUnderSidebar && read.QuickInputCentered && read.Compact);
            Assert.AreEqual("dark", store.Read().Appearance.Theme, "The rest of the file is left as it was.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
