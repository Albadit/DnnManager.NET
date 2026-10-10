using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;
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
        // Junctions made without administrator rights (in a site folder, %TEMP%, Documents) aren't followed - with
        // administrator rights a delete or a write through one would reach wherever another program pointed it. First,
        // so the update helper below has it too.
        var redirectionTrust = RedirectionTrust.Enforce();

        // The update helper (a copy of this exe, started by an update as DNN Manager closes): it installs the update and
        // starts DNN Manager again - none of the app below.
        if (UpdateHelper.IsHelper(args)) return UpdateHelper.Run(args);

        // Restarted (Troubleshoot): the previous DNN Manager is still closing - wait for it, or the check below finds it.
        args = AppRestart.WaitForPrevious(args);

        // Only one DNN Manager at a time: a second start shows the open window instead - checked before elevating,
        // so it doesn't ask for administrator rights first.
        if (SingleInstance.HandOffToRunning()) return 0;

        if (!AdminElevation.IsAdministrator())
        {
            // Nothing of this start's command line goes along: whoever started it chose that, not the user.
            if (AdminElevation.TryRelaunchElevated([], out var declined)) return 0;
            MessageBox.Show(declined
                    ? "DNN Manager needs administrator rights to manage IIS - and Windows' prompt for them was answered No.\n\n" +
                      "Start DNN Manager again and choose Yes."
                    : "DNN Manager needs administrator rights to manage IIS.\n\n" +
                      "Could not elevate - please run it as Administrator.",
                "DNN Manager", MessageBoxButton.OK, declined ? MessageBoxImage.Information : MessageBoxImage.Error);
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
        Motion.Apply(loaded.Settings.Appearance.Animations);

        // No default configuration sources (and no command line): the settings come from the store above, with only the
        // DNNMANAGER_* environment variables on top - held to the same rules as the saved settings, since any program of
        // the user can set them.
        // Saving on the Settings page does the same (LiveSettings): the variables stay on top while they are allowed.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);
        var options = LiveSettings.WithOverrides(loaded.Settings, builder.Configuration, out var overrideProblems);
        if (overrideProblems.Count > 0) startupNotices.Add(new SettingsNotice(true, LiveSettings.OverridesIgnored(overrideProblems)));
        if (!redirectionTrust)
            startupNotices.Add(new SettingsNotice(true,
                "This Windows doesn't have the protection that keeps DNN Manager from following junctions other programs made " +
                "(redirection trust). DNN Manager checks for them itself where it deletes and writes, but with its Administrator " +
                "rights a junction it misses could let a program without those rights have files elsewhere deleted or changed. " +
                "Run Windows Update (Settings → Windows Update) and install the latest updates, then restart DNN Manager."));
        if (RedirectionTrust.DeveloperModeAllowsLinks)
            startupNotices.Add(new SettingsNotice(true,
                "Windows' Developer Mode is on: programs without administrator rights can make symbolic links, which a program " +
                "could put where DNN Manager - with its administrator rights - deletes or writes, to reach files elsewhere. DNN " +
                "Manager doesn't follow links where it deletes and writes, but unless you need Developer Mode, turn it off " +
                "(Settings → System → For developers → Developer Mode)."));
        if (paths.SyncNotice() is { } synced) startupNotices.Add(new SettingsNotice(true, synced));
        if (ChildEnvironment.CodeLoadingVariables() is { Count: > 0 } loading)
            startupNotices.Add(new SettingsNotice(true,
                $"DNN Manager started with environment variables that make .NET load other code or write diagnostics files: " +
                $"{string.Join(", ", loading)}. With its administrator rights, that code runs as Administrator. Unless you set them " +
                "yourself, remove them (Windows Settings → System → About → Advanced system settings → Environment Variables) " +
                "and check this computer for malware."));

        // No console in a WinExe: the app's warnings and errors go to the daily log file (logs\dnnmanager-yyyyMMdd.log), with
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
        App.LogsDirectory = host.Services.GetRequiredService<AppDataPaths>().LogsDirectory;
        var log = host.Services.GetRequiredService<ActivityLog>();
        foreach (var notice in startupNotices)
        {
            if (notice.IsWarning) log.Warn(notice.Message);
            else log.Info(notice.Message);
        }
        DeleteExpiredBackups(host.Services.GetRequiredService<AppDataCleaner>(), options.BackupKeepDays, log);

        try
        {
            var window = host.Services.GetRequiredService<MainWindow>();
            app.MainWindow = window;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            ToastWarnings(window, startupNotices.Where(n => n.IsWarning).ToList(), host.Services.GetRequiredService<AppCommands>());
            var exitCode = app.Run(window);
            // A password copied in the last minute would stay on the clipboard after DNN Manager is gone.
            SecretClipboard.ClearIfStillOurs();
            if (AppRestart.Requested) AppRestart.StartNew();
            return exitCode;
        }
        catch (Exception ex)
        {
            SecretClipboard.ClearIfStillOurs();
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Unhandled fatal error");
            MessageBox.Show($"DNN Manager ran into an error it can't go on from: {ex.Message}\n\n" +
                            $"What happened is in the log, in {host.Services.GetRequiredService<AppDataPaths>().LogsDirectory} - " +
                            "start DNN Manager again; if it happens again, the log says why.",
                "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }

    /// <summary>
    /// The start's warnings where they are seen: the Output tab has them, which is closed at first - so a toast says so
    /// too once the window is shown (one warning in full, several counted), with a button to the Output tab.
    /// </summary>
    private static void ToastWarnings(Window window, IReadOnlyList<SettingsNotice> warnings, AppCommands commands)
    {
        if (warnings.Count == 0) return;
        var message = warnings.Count == 1
            ? warnings[0].Message
            : $"DNN Manager started with {warnings.Count} warnings that need your attention - the Output tab says what they are and what to do.";
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            Toast.Show(message, ToastKind.Warning, "Show output", () => commands.Find("panel.output")?.Run?.Invoke()));
    }

    /// <summary>
    /// Setting <c>backups.keepDays</c>: the project backups and deployment packages older than that are deleted - off the UI
    /// thread, a moment into the start - and the Output tab says which. Nothing when it is 0 (keep them for good).
    /// </summary>
    private static void DeleteExpiredBackups(AppDataCleaner cleaner, int keepDays, ActivityLog log)
    {
        if (keepDays <= 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var result = cleaner.DeleteExpired(keepDays, DateTime.Now);
                if (result.Deleted.Count > 0)
                    log.Info($"Deleted {result.Deleted.Count} backup(s) and deployment package(s) older than {keepDays} days " +
                             $"({ByteSize.Format(result.FreedBytes)}, Settings → Projects → Backups): " +
                             string.Join(", ", result.Deleted.Select(Path.GetFileName)) + ".");
                if (result.Skipped > 0)
                    log.Warn($"{result.Skipped} file(s) of backups older than {keepDays} days couldn't be deleted (in use) - they are tried again at the next start.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log?.LogWarning(ex, "Could not delete the old backups");
            }
        });
    }
}
