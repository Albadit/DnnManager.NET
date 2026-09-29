using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation;

internal static class Program
{
    /// <summary>Prefix of the environment variables that override settings, e.g. <c>DNNMANAGER_DnnManager__SitePort</c>.</summary>
    public const string EnvironmentPrefix = "DNNMANAGER_";

    [STAThread]
    private static int Main(string[] args)
    {
        if (!AdminElevation.IsAdministrator())
        {
            if (AdminElevation.TryRelaunchElevated(args)) return 0;
            MessageBox.Show("DNN Manager needs Administrator rights to manage IIS.\n\n" +
                            "Could not elevate - please run it as Administrator.",
                "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        using var running = RunningMarker.Create();

        var app = new App();
        app.InitializeComponent();
        // Until the main window opens, closing a dialog mustn't end the app.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeManager.Initialize(null);

        // The user's settings, in Documents\DnnManager - apart from the program, so updates and
        // reinstalls keep them. A file that can't be used is reported here, before anything else starts.
        var paths = AppDataPaths.ForCurrentUser();
        var store = new SettingsStore(paths);
        var loaded = SettingsStartup.Load(store);
        if (loaded is null) return 1;
        var startupNotices = loaded.Notices.ToList();
        ThemeManager.Initialize(loaded.Settings.Appearance.Theme);

        // No default configuration sources: the settings come from settings.json above, with only the
        // DNNMANAGER_* environment variables on top.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        var options = loaded.Settings.ToAppOptions();
        builder.Configuration.GetSection(AppOptions.SectionName).Bind(options);

        // No console in a WinExe - errors surface in the activity log and message boxes instead.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(paths, store);

        builder.Services.AddSingleton<ActivityLog>();
        builder.Services.AddSingleton<GuiProgressReporter>();
        builder.Services.AddSingleton<IProgressReporter>(sp => sp.GetRequiredService<GuiProgressReporter>());
        builder.Services.AddSingleton<GuiUserPrompt>();
        builder.Services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<GuiUserPrompt>());
        builder.Services.AddSingleton<OperationRunner>();
        builder.Services.AddSingleton<DnnReleaseCatalog>();
        builder.Services.AddSingleton<MainWindow>();

        using var host = builder.Build();

        var log = host.Services.GetRequiredService<ActivityLog>();
        foreach (var notice in startupNotices)
        {
            if (notice.IsWarning) log.Warn(notice.Message);
            else log.Info(notice.Message);
        }

        try
        {
            var window = host.Services.GetRequiredService<MainWindow>();
            app.MainWindow = window;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            return app.Run(window);
        }
        catch (Exception ex)
        {
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Unhandled fatal error");
            MessageBox.Show($"Fatal: {ex.Message}", "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }
}
