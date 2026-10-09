using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation;
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
        foreach (var type in new[] { typeof(MainWindow), typeof(SetupPage), typeof(SettingsPage), typeof(ProjectsPage), typeof(TroubleshootPage),
                                     typeof(ExistingFolderPage) })
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
