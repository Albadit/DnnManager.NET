using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The window and its pages, made as the app makes them - their XAML read, every binding, resource and name in it -
/// offscreen, over a stand-in IIS and settings of their own: what is wrong in them fails here, not in front of the user.
/// Nothing is started, shown or saved where the user's DNN Manager keeps its own.
/// </summary>
[TestClass]
public sealed class PagesLoadTests
{
    private static readonly List<ServiceProvider> Kept = [];
    private string _run = "";

    [TestInitialize]
    public void Initialize() => _run = TestEnvironment.NewRunDirectory();

    [TestCleanup]
    public void Cleanup() => TestEnvironment.DeleteDirectory(_run);

    [TestMethod]
    public void The_window_and_every_page_load() => WpfUi.Run(() =>
    {
        // Kept: the window queues work on the UI thread that still uses them after the test is done.
        var services = Services();
        Kept.Add(services);
        var failed = new List<string>();
        foreach (var type in new[] { typeof(MainWindow), typeof(SetupPage), typeof(SettingsPage), typeof(ProjectsPage), typeof(TroubleshootPage), typeof(DeployPage) })
        {
            try
            {
                var element = (FrameworkElement)ActivatorUtilities.CreateInstance(services, type);
                element.Measure(new Size(1200, 800));
                element.Arrange(new Rect(0, 0, 1200, 800));
                element.UpdateLayout();
                if (element is Window window) window.Close();
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException is not null) inner = inner.InnerException;
                failed.Add($"{type.Name}: {ex.Message} -> {inner.Message}");
            }
        }
        Assert.AreEqual(0, failed.Count, string.Join(Environment.NewLine, failed));
    });

    [TestMethod]
    public void Settings_has_a_deploy_category_with_its_defaults() => WpfUi.Run(() =>
    {
        var services = Services();
        Kept.Add(services);
        var page = ActivatorUtilities.CreateInstance<SettingsPage>(services);
        page.Measure(new Size(1200, 800));
        page.Arrange(new Rect(0, 0, 1200, 800));
        page.UpdateLayout();
        page.ShowCategory("Deploy");
        page.UpdateLayout();
        Assert.AreEqual(Visibility.Visible, page.DeployPanel.Visibility);
        Assert.AreEqual("4", page.DeployParallel.Text);
        Assert.IsTrue(page.DeployCompareOnOpen.IsChecked);
        Assert.AreEqual(90, (page.DeployKeepVersions.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag);
        StringAssert.Contains(page.DeployIgnoreRules.Text, "/web.config");
        Assert.IsFalse(page.HasUnsavedChanges, "Shown as saved - nothing to save yet.");
        if (Environment.GetEnvironmentVariable("DEPLOY_TEST_SCREENSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            foreach (var dark in new[] { false, true })
            {
                page.Resources.MergedDictionaries.Clear();
                page.Resources.MergedDictionaries.Add(dark ? new DnnManager.Presentation.Themes.DarkTheme() : new DnnManager.Presentation.Themes.LightTheme());
                var host = new System.Windows.Controls.Border { Child = null, Width = 1200, Height = 800 };
                page.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var back = new System.Windows.Media.DrawingVisual();
                using (var dc = back.RenderOpen()) dc.DrawRectangle((System.Windows.Media.Brush)page.FindResource("WindowBg"), null, new Rect(0, 0, 1200, 800));
                bitmap.Render(back);
                bitmap.Render(page);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, dark ? "settings-deploy-dark.png" : "settings-deploy.png"));
                encoder.Save(file);
            }
            page.Resources.MergedDictionaries.Clear();
        }
        page.DeployParallel.Text = "2";
        Assert.IsTrue(page.HasUnsavedChanges, "A change is noticed.");
        page.DeployIgnoreRules.Text = "[broken";
        Assert.AreEqual(Visibility.Visible, page.DeployRulesProblem.Visibility, "A line that isn't a pattern is said as it is typed.");
    });

    [TestMethod]
    public void The_upgrade_path_dialog_loads_and_checks_what_is_typed() => WpfUi.Run(() =>
    {
        // 99: a minimum saved by hand, not one of the choices - it must not be lost by opening the dialog.
        var dialog = new DnnUpgradePathDialog(
        [
            new DnnUpgradeStopSetting { Version = "10.4.2", SqlServerMajor = 15, Note = "A note." },
            new DnnUpgradeStopSetting { Version = "11.0.0", SqlServerMajor = 99 }
        ]);
        dialog.Measure(new Size(800, 600));
        dialog.Arrange(new Rect(0, 0, 800, 600));
        dialog.UpdateLayout();
        Assert.IsFalse(dialog.OkButton.IsEnabled, "Nothing to save yet.");
        // The whole path, newest first - DNN's own versions and the ones added - each with what it needs.
        Assert.AreEqual(DnnUpgradePath.Versions.Count + 2, dialog.RowsPanel.Children.Count);
        System.Windows.Controls.UIElementCollection Cells(int row) => ((System.Windows.Controls.Panel)dialog.RowsPanel.Children[row]).Children;
        string Text(int row, int cell) => Cells(row)[cell] switch
        {
            System.Windows.Controls.TextBox box => box.Text,
            System.Windows.Controls.ComboBox combo => (string)((System.Windows.Controls.ComboBoxItem)combo.SelectedItem).Content,
            var other => other.ToString()!
        };
        void Click(System.Windows.UIElement button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.AreEqual("11.0.0", Text(0, 0));
        Assert.AreEqual("SQL Server (version 99)", Text(0, 2));
        CollectionAssert.AreEqual(new[] { "10.03.03", ".NET Framework 4.8", "SQL Server 2017", "" }, Enumerable.Range(0, 4).Select(c => Text(2, c)).ToArray());
        // One of DNN's own keeps its number: it is changed by what it needs, or taken off.
        Assert.IsTrue(((System.Windows.Controls.TextBox)Cells(2)[0]).IsReadOnly);
        var oldest = DnnUpgradePath.Versions.Count + 1;
        Assert.AreEqual("02.00.04", Text(oldest, 0));
        StringAssert.Contains((string)((System.Windows.Controls.TextBox)Cells(oldest)[0]).ToolTip, "Done by hand");
        Assert.AreEqual(Visibility.Collapsed, dialog.Problem.Visibility, dialog.Problem.Text);

        // 10.02.05 taken off: said under the path, and it can be saved.
        Assert.AreEqual("10.02.05", Text(3, 0));
        Click(Cells(3)[4]);
        Assert.AreEqual(Visibility.Visible, dialog.Removed.Visibility);
        StringAssert.Contains(dialog.Removed.Text, "10.02.05");
        Assert.IsTrue(dialog.OkButton.IsEnabled);

        Click(dialog.AddButton);
        var version = (System.Windows.Controls.TextBox)Cells(0)[0];
        Assert.AreEqual("", version.Text, "A new version goes at the top, with the newest.");
        Assert.IsFalse(dialog.OkButton.IsEnabled, "An empty row can't be saved.");
        Assert.AreEqual(Visibility.Visible, dialog.Problem.Visibility);

        version.Text = "abc";
        StringAssert.Contains(dialog.Problem.Text, "three numbers");
        Assert.IsFalse(dialog.OkButton.IsEnabled);
        version.Text = "10.04.02";
        StringAssert.Contains(dialog.Problem.Text, "twice");
        Assert.IsFalse(dialog.OkButton.IsEnabled);
        version.Text = "12.0.0";
        Assert.AreEqual(Visibility.Collapsed, dialog.Problem.Visibility, dialog.Problem.Text);
        Assert.IsTrue(dialog.OkButton.IsEnabled);

        // Reset: DNN's own path again, every change undone - which differs from what was saved, so it can be saved.
        Click(dialog.ResetButton);
        Assert.AreEqual(DnnUpgradePath.Versions.Count, dialog.RowsPanel.Children.Count);
        Assert.AreEqual(Visibility.Collapsed, dialog.Removed.Visibility);
        Assert.IsTrue(dialog.OkButton.IsEnabled);
        // Never shown, so not closed: as the first window of the tests' one App, closing it would end that App for the tests after it.
    });

    private ServiceProvider Services()
    {
        var paths = new AppDataPaths(Path.Combine(_run, "data"));
        var store = new SettingsStore(paths);
        var options = store.Load().Settings.ToAppOptions();
        options.BaseDirectory = Path.Combine(_run, "projects");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(options));
        services.AddApplication();
        services.AddInfrastructure(paths, store);
        services.RemoveAll<IIisManager>();
        services.AddSingleton<IIisManager>(new UntouchedIis());
        services.RemoveAll<IPrerequisiteChecker>();
        services.AddSingleton<IPrerequisiteChecker, NoIisFeatures>();
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
        return services.BuildServiceProvider();
    }
}
