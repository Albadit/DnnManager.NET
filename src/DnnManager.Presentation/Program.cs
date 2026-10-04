using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Updates;
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
        // The update helper (a copy of this exe, started by an update as DNN Manager closes): it installs the update and
        // starts DNN Manager again - none of the app below.
        if (UpdateHelper.IsHelper(args)) return UpdateHelper.Run(args);

        // Restarted (Troubleshoot): the previous DNN Manager is still closing - wait for it, or the check below finds it.
        args = AppRestart.WaitForPrevious(args);

        // Only one DNN Manager at a time: a second start shows the open window instead - checked before elevating,
        // so it doesn't ask for Administrator rights first.
        if (SingleInstance.HandOffToRunning()) return 0;

        if (!AdminElevation.IsAdministrator())
        {
            if (AdminElevation.TryRelaunchElevated(args)) return 0;
            MessageBox.Show("DNN Manager needs Administrator rights to manage IIS.\n\n" +
                            "Could not elevate - please run it as Administrator.",
                "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        using var running = RunningMarker.Create(out var alreadyRunning);
        // Started twice at the same moment: both got past the check above, only one made the mutex.
        if (alreadyRunning)
        {
            SingleInstance.HandOffToRunning();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        // The keyboard's place, always visible.
        FocusRing.Install();
        using var activation = SingleInstance.Listen(app.Dispatcher);
        // Until the main window opens, closing a dialog mustn't end the app.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeManager.Initialize(null);

        // The user's settings, in Documents\DnnManager\dnnmanager.db - apart from the program, so updates and
        // reinstalls keep them. Settings that can't be used are reported here, before anything else starts.
        var paths = AppDataPaths.ForCurrentUser();
        var store = new SettingsStore(paths);
        var loaded = SettingsStartup.Load(store);
        if (loaded is null) return 1;
        var startupNotices = loaded.Notices.ToList();
        ThemeManager.Initialize(loaded.Settings.Appearance.Theme);
        ThemeManager.ApplyLayout(loaded.Settings.Appearance.UiScale, loaded.Settings.Appearance.FontSize);

        // No default configuration sources: the settings come from the store above, with only the
        // DNNMANAGER_* environment variables on top.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        var options = loaded.Settings.ToAppOptions();
        builder.Configuration.GetSection(AppOptions.SectionName).Bind(options);

        // No console in a WinExe: the app's warnings and errors go to the daily log file (logs\dnnmanager-*.log), with
        // their stack traces - what the user sees goes through the activity log and message boxes.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ILoggerProvider, DailyLogFileLoggerProvider>();

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(paths, store);

        builder.Services.AddSingleton<ActivityLog>();
        builder.Services.AddSingleton<GuiProgressReporter>();
        builder.Services.AddSingleton<IProgressReporter>(sp => sp.GetRequiredService<GuiProgressReporter>());
        builder.Services.AddSingleton<GuiUserPrompt>();
        builder.Services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<GuiUserPrompt>());
        builder.Services.AddSingleton<OperationRunner>();
        builder.Services.AddSingleton<LiveSettings>();
        builder.Services.AddSingleton<DnnReleaseCatalog>();
        builder.Services.AddSingleton<ServerStore>();
        builder.Services.AddSingleton<TerminalService>();
        builder.Services.AddSingleton<EfficiencyMode>();
        builder.Services.AddSingleton<AppUpdater>();
        builder.Services.AddSingleton<WorkspaceService>();
        builder.Services.AddSingleton<AppCommands>();
        builder.Services.AddSingleton<MainWindow>();

        using var host = builder.Build();

        App.Log = host.Services.GetRequiredService<ILogger<App>>();
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
            var exitCode = app.Run(window);
            if (AppRestart.Requested) AppRestart.StartNew();
            return exitCode;
        }
        catch (Exception ex)
        {
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Unhandled fatal error");
            MessageBox.Show($"Fatal: {ex.Message}", "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }
}
