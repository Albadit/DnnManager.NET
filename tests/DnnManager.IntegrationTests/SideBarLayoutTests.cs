using System.Reflection;
using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Deployment;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The window's layout as VS Code's: the activity bar only icons, the primary side bar with the page's own view
/// (Source Control's changes), the secondary side bar on the other side - made as the app makes the window, not shown.
/// </summary>
[TestClass]
public class SideBarLayoutTests
{
    private static readonly List<IDisposable> Kept = [];

    [TestMethod]
    public void The_primary_side_bar_shows_the_pages_view_and_the_secondary_opens_on_the_other_side() => WpfUi.Run(() =>
    {
        var run = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "sidebars-" + Guid.NewGuid().ToString("N"));
        try
        {
            Motion.Apply(false);
            var window = Window(run);
            Assert.IsTrue(window.IsCompact, "The activity bar shows only its icons, whatever the window's width.");

            // Projects has nothing for it: the primary side bar is hidden, its button has nothing to do.
            window.NavProjects.IsChecked = true;
            Assert.AreEqual(Visibility.Collapsed, window.PrimarySideBar.Visibility);
            Assert.IsFalse(window.SidebarToggle.IsEnabled);

            // Source Control: its servers, changes and history in the primary side bar, beside the activity bar.
            window.NavDeploy.IsChecked = true;
            var deploy = (DnnManager.Presentation.Pages.SourceControlPage)window.PageHost.Content;
            Assert.AreSame(deploy.SideBar, window.PrimaryHost.Content);
            Assert.AreEqual(Visibility.Visible, window.PrimarySideBar.Visibility);
            Assert.AreEqual(2, Grid(window.PrimarySideBar));
            Assert.AreEqual(300, window.WorkbenchLeft.Width.Value);
            Assert.IsTrue(window.SidebarToggle.IsEnabled);

            // Ctrl+B hides it, and again shows it.
            Invoke(window, "TogglePrimarySideBar");
            Assert.AreEqual(Visibility.Collapsed, window.PrimarySideBar.Visibility);
            Assert.AreEqual(0, window.WorkbenchLeft.Width.Value);
            Invoke(window, "TogglePrimarySideBar");
            Assert.AreEqual(Visibility.Visible, window.PrimarySideBar.Visibility);

            // Ctrl+Alt+B: the secondary, on the page's other side - empty for now.
            Assert.AreEqual(Visibility.Collapsed, window.SecondarySideBar.Visibility, "Hidden at first.");
            Invoke(window, "ToggleSecondarySideBar");
            Assert.AreEqual(Visibility.Visible, window.SecondarySideBar.Visibility);
            Assert.AreEqual(6, Grid(window.SecondarySideBar));
            Assert.AreEqual(300, window.WorkbenchRight.Width.Value);

            // Panel Alignment, as VS Code's: Center, the panel between the two side bars, full height beside it; Justify, under both;
            // Left, under the one on the left only. The activity bar beside it all, whatever it is.
            int Span(UIElement part) => System.Windows.Controls.Grid.GetColumnSpan(part);
            int Rows(UIElement part) => System.Windows.Controls.Grid.GetRowSpan(part);
            Assert.AreEqual((4, 1), (Grid(window.PanelCard), Span(window.PanelCard)), "Center: under the page only.");
            Assert.AreEqual((3, 3), (Rows(window.PrimarySideBar), Rows(window.SecondarySideBar)));
            Invoke(window, "ChangeLayout", (Action<DnnManager.Application.Configuration.LayoutSettings>)(l => l.PanelAlignment = "justify"));
            Assert.AreEqual((2, 5), (Grid(window.PanelCard), Span(window.PanelCard)), "Justify: under both side bars.");
            Assert.AreEqual((1, 1), (Rows(window.PrimarySideBar), Rows(window.SecondarySideBar)));
            Invoke(window, "ChangeLayout", (Action<DnnManager.Application.Configuration.LayoutSettings>)(l => l.PanelAlignment = "left"));
            Assert.AreEqual((2, 3), (Grid(window.PanelCard), Span(window.PanelCard)), "Left: under the left one.");
            Assert.AreEqual((1, 3), (Rows(window.PrimarySideBar), Rows(window.SecondarySideBar)));
            Assert.AreEqual(3, Rows(window.Sidebar), "The activity bar full height, always.");
            Invoke(window, "ChangeLayout", (Action<DnnManager.Application.Configuration.LayoutSettings>)(l => l.PanelAlignment = "center"));

            // The side bar's side on the right: the two change places.
            Invoke(window, "ChangeLayout", (Action<DnnManager.Application.Configuration.LayoutSettings>)(l => l.SidebarPosition = "right"));
            Assert.AreEqual(6, Grid(window.PrimarySideBar));
            Assert.AreEqual(2, Grid(window.SecondarySideBar));
            Assert.AreEqual(8, Grid(window.Sidebar));

            // Kept for the next start.
            var layout = (WindowLayout)window.GetType().GetMethod("CaptureLayout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [])!;
            Assert.IsTrue(layout.SecondarySideBarOpen);
            Assert.IsFalse(layout.PrimarySideBarHidden);

            // Leaving the page takes its view out of the side bar.
            window.NavProjects.IsChecked = true;
            Assert.IsNull(window.PrimaryHost.Content);
            Assert.IsNull(deploy.SideBar.Parent);
        }
        finally
        {
            Motion.Apply(true);
            TestEnvironment.DeleteDirectory(run);
        }
    });

    private static int Grid(UIElement part) => System.Windows.Controls.Grid.GetColumn(part);

    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static MainWindow Window(string run)
    {
        var paths = new AppDataPaths(Path.Combine(run, "data"));
        var store = new SettingsStore(paths);
        store.Load();
        store.Update(s => s.Updates.CheckAtStart = false);
        var options = store.Read().ToAppOptions();
        options.BaseDirectory = Path.Combine(run, "projects");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(options));
        services.AddApplication();
        services.AddInfrastructure(paths, store);
        services.RemoveAll<IIisManager>();
        services.AddSingleton<IIisManager>(new UntouchedIis());
        services.RemoveAll<IRemoteFileSystemFactory>();
        services.AddSingleton(new FolderServer(Path.Combine(run, "server")).Factory);
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<GuiProgressReporter>();
        services.AddSingleton<IProgressReporter>(sp => sp.GetRequiredService<GuiProgressReporter>());
        services.AddSingleton<GuiUserPrompt>();
        services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<GuiUserPrompt>());
        services.AddSingleton<OperationRunner>();
        services.AddSingleton<LiveSettings>();
        services.AddSingleton<DnnReleaseCatalog>();
        services.AddSingleton<ServerStore>();
        services.AddSingleton<TerminalService>();
        services.AddSingleton<EfficiencyMode>();
        services.AddSingleton<AppUpdater>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<AppCommands>();
        services.AddSingleton<MainWindow>();
        var provider = services.BuildServiceProvider();
        Kept.Add(provider);
        return provider.GetRequiredService<MainWindow>();
    }
}
