using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop;

/// <summary>
/// DNN Manager on Windows, macOS and Linux (Avalonia). It starts as the Windows app does (src/DnnManager.Presentation/
/// Program.cs) - the update helper when an update started it, once per session, with administrator rights on Windows
/// (IIS needs them), the settings from Documents/DnnManager/dnnmanager.db with the DNNMANAGER_* variables on top, the same
/// services. macOS and Linux never ask for administrator rights.
/// </summary>
internal static class Program
{
    /// <summary>The app's services - made before Avalonia starts; the windows take theirs from here.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>What the start has to say - shown in Output once the window is open.</summary>
    public static IReadOnlyList<SettingsNotice> StartupNotices { get; private set; } = [];

    /// <summary>The settings as loaded - the theme and the window start from them.</summary>
    public static UserSettings Settings { get; private set; } = new();

    /// <summary>
    /// Started as the app (Main), not by the tests: only then does the window check for updates, greet a first start with
    /// the getting started guide, and put an icon in the notification area.
    /// </summary>
    public static bool IsRealStart { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Junctions made without administrator rights aren't followed (Windows) - first, so the update helper has it too.
        var redirectionTrust = RedirectionTrust.Enforce();

        // The update helper (a copy of this exe, started by an update as DNN Manager closes): it installs the update and
        // starts DNN Manager again - none of the app below.
        if (UpdateHelper.IsHelper(args)) return UpdateHelper.Run(args);

        // Restarted: the previous DNN Manager is still closing - wait for it, or the check below finds it.
        args = AppRestart.WaitForPrevious(args);

        // Once per session (Windows): a second start shows the open window instead - before asking for administrator rights.
        if (SingleInstance.HandOffToRunning()) return 0;
        if (AdminElevation.Wanted())
        {
            // Nothing of this start's command line goes along: whoever started it chose that, not the user.
            if (AdminElevation.TryRelaunchElevated([], out var declined)) return 0;
            AdminElevation.Tell(declined
                ? "DNN Manager needs administrator rights to manage IIS - and Windows' prompt for them was answered No.\n\nStart DNN Manager again and choose Yes."
                : "DNN Manager needs administrator rights to manage IIS.\n\nCould not elevate - please run it as Administrator.", error: !declined);
            return 1;
        }
        using var running = RunningMarker.Create(out var alreadyRunning);
        // Started twice at the same moment: both got past the check above, only one made the mutex.
        if (alreadyRunning)
        {
            SingleInstance.HandOffToRunning();
            return 0;
        }
        IsRealStart = true;

        // The shared presentation code (the activity log) writes on Avalonia's UI thread.
        UiThread.Post = action => Dispatcher.UIThread.Post(action);

        // Documents/DnnManager - or the folder DNNMANAGER_DATA_DIR names (a portable copy, a try-out apart from the real data).
        var paths = Environment.GetEnvironmentVariable("DNNMANAGER_DATA_DIR") is { Length: > 0 } data && Path.IsPathFullyQualified(data)
            ? new AppDataPaths(data)
            : AppDataPaths.ForCurrentUser();
        var store = new SettingsStore(paths);
        SettingsLoadResult loaded;
        try
        {
            paths.EnsureCreated();
            loaded = store.Load();
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            var problems = ex is SettingsException { Problems.Count: > 0 } settings
                ? string.Concat(settings.Problems.Select(p => $"{Environment.NewLine}  • {p}")) : "";
            AdminElevation.Tell($"DNN Manager can't use its settings in {store.Location}: {ex.Message}{problems}{Environment.NewLine}{Environment.NewLine}" +
                                "Fix what is named above, or move the database aside to start with the defaults.", error: true);
            return 1;
        }
        Settings = loaded.Settings;
        var notices = loaded.Notices.ToList();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Configuration.AddEnvironmentVariables(LiveSettings.EnvironmentPrefix);
        var options = LiveSettings.WithOverrides(loaded.Settings, builder.Configuration, out var overrideProblems);
        if (overrideProblems.Count > 0) notices.Add(new SettingsNotice(true, LiveSettings.OverridesIgnored(overrideProblems)));
        if (!Platform.IsWindows)
            notices.Add(new SettingsNotice(false,
                $"DNN Manager on {Platform.Name}: Source Control compares, pushes and pulls your projects' files as on Windows. " +
                "Making and running DNN sites needs IIS - Windows only."));
        notices.AddRange(ElevatedNotices(redirectionTrust, paths));

        // Warnings and errors go to the daily log file (logs/dnnmanager-yyyyMMdd.log), with their stack traces.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ILoggerProvider, DailyLogFileLoggerProvider>();

        AddServices(builder.Services, options, paths, store);

        using var host = builder.Build();
        Use(host.Services, loaded.Settings, notices);
        App.Log = host.Services.GetRequiredService<ILogger<App>>();
        App.LogsDirectory = paths.LogsDirectory;
        DeleteExpiredBackups(host.Services.GetRequiredService<AppDataCleaner>(), options.BackupKeepDays, host.Services.GetRequiredService<ActivityLog>());
        // Later starts bring this window to the front; Setup asks it to quit before it replaces the files.
        using var listening = SingleInstance.Listen(BringToFront, QuitForSetup);
        try
        {
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            if (AppRestart.Requested) AppRestart.StartNew();
            return exitCode;
        }
        catch (Exception ex)
        {
            App.Log.LogCritical(ex, "Unhandled fatal error");
            AdminElevation.Tell($"DNN Manager ran into an error it can't go on from: {ex.Message}\n\nWhat happened is in the log, in {paths.LogsDirectory} - " +
                                "start DNN Manager again; if it happens again, the log says why.", error: true);
            return 2;
        }
    }

    /// <summary>The app's services - the same layers as the Windows app's, and this app's answers to the use cases' questions.</summary>
    internal static void AddServices(IServiceCollection services, AppOptions options, AppDataPaths paths, SettingsStore store)
    {
        services.AddSingleton(Options.Create(options));
        services.AddApplication();
        services.AddInfrastructure(paths, store);
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<GuiProgressReporter>();
        services.AddSingleton<IProgressReporter>(sp => sp.GetRequiredService<GuiProgressReporter>());
        services.AddSingleton<IUserPrompt, AvaloniaUserPrompt>();
        services.AddSingleton<OperationRunner>();
        services.AddSingleton<LiveSettings>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<AppUpdater>();
        services.AddSingleton<AppCommands>();
        // The sites in IIS, kept up to date - one for the Projects page, the status bar's IIS cell and New project.
        services.AddSingleton<Views.Projects.ServerStore>();
    }

    /// <summary>The services, settings and start's notices the windows use - the start's, or a test's.</summary>
    internal static void Use(IServiceProvider services, UserSettings settings, IReadOnlyList<SettingsNotice> notices)
    {
        Services = services;
        Settings = settings;
        StartupNotices = notices;
    }

    /// <summary>Avalonia's set-up - also what the previewer and the tests start.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    /// <summary>
    /// What running with administrator rights needs said (Windows, as the Windows app says it): no redirection trust,
    /// Developer Mode's links, .NET variables that load other code - and, anywhere, a data folder that a cloud syncs.
    /// </summary>
    private static IEnumerable<SettingsNotice> ElevatedNotices(bool redirectionTrust, AppDataPaths paths)
    {
        if (paths.SyncNotice() is { } synced) yield return new SettingsNotice(true, synced);
        if (!OperatingSystem.IsWindows() || !AdminElevation.IsAdministrator()) yield break;
        if (!redirectionTrust)
            yield return new SettingsNotice(true,
                "This Windows doesn't have the protection that keeps DNN Manager from following junctions other programs made " +
                "(redirection trust). DNN Manager checks for them itself where it deletes and writes, but with its Administrator " +
                "rights a junction it misses could let a program without those rights have files elsewhere deleted or changed. " +
                "Run Windows Update (Settings → Windows Update) and install the latest updates, then restart DNN Manager.");
        if (RedirectionTrust.DeveloperModeAllowsLinks)
            yield return new SettingsNotice(true,
                "Windows' Developer Mode is on: programs without administrator rights can make symbolic links, which a program " +
                "could put where DNN Manager - with its administrator rights - deletes or writes, to reach files elsewhere. DNN " +
                "Manager doesn't follow links where it deletes and writes, but unless you need Developer Mode, turn it off " +
                "(Settings → System → For developers → Developer Mode).");
        if (ChildEnvironment.CodeLoadingVariables() is { Count: > 0 } loading)
            yield return new SettingsNotice(true,
                $"DNN Manager started with environment variables that make .NET load other code or write diagnostics files: " +
                $"{string.Join(", ", loading)}. With its administrator rights, that code runs as Administrator. Unless you set them " +
                "yourself, remove them (Windows Settings → System → About → Advanced system settings → Environment Variables) " +
                "and check this computer for malware.");
    }

    /// <summary>
    /// Setting backups.keepDays: the project backups and deployment packages older than that are deleted - off the UI
    /// thread, a moment into the start - and Output says which. Nothing when it is 0 (keep them for good).
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

    private static Views.MainWindow? Shown =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as Views.MainWindow;

    // A later start: the window - or the dialog over it - to the front.
    private static void BringToFront()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        Shown?.ShowFromBackground();
        var dialog = desktop.Windows.LastOrDefault(w => w.IsVisible && w != desktop.MainWindow);
        dialog?.Activate();
    }

    // Setup is about to replace or remove the files: dialogs closed (as cancelled), the window quits without asking.
    private static void QuitForSetup()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Shown is not { } main) return;
        foreach (var dialog in desktop.Windows.Where(w => w != main).ToList()) dialog.Close();
        Dispatcher.UIThread.Post(main.QuitForSetup, DispatcherPriority.Background);
    }
}
