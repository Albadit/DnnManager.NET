using Avalonia;
using Avalonia.Threading;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop;

/// <summary>
/// DNN Manager on Windows, macOS and Linux (Avalonia). It starts as the Windows app does - the settings from
/// Documents/DnnManager/dnnmanager.db, the DNNMANAGER_* variables on top, the same services - but without what only
/// Windows has: no administrator rights to ask for, no IIS to watch.
/// </summary>
internal static class Program
{
    /// <summary>The app's services - made before Avalonia starts; the windows take theirs from here.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>What the start has to say - shown in Output once the window is open.</summary>
    public static IReadOnlyList<SettingsNotice> StartupNotices { get; private set; } = [];

    /// <summary>The settings as loaded - the theme and the window start from them.</summary>
    public static UserSettings Settings { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
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
            // No window to say it in yet: the terminal it was started from, and the exit code.
            Console.Error.WriteLine($"DNN Manager can't use its settings in {store.Location}: {ex.Message}");
            if (ex is SettingsException { Problems.Count: > 0 } settings)
                foreach (var problem in settings.Problems) Console.Error.WriteLine("  • " + problem);
            Console.Error.WriteLine("Fix what is named above, or move the database aside to start with the defaults.");
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

        // Warnings and errors go to the daily log file (logs/dnnmanager-yyyyMMdd.log), with their stack traces.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ILoggerProvider, DailyLogFileLoggerProvider>();

        AddServices(builder.Services, options, paths, store);

        using var host = builder.Build();
        Use(host.Services, loaded.Settings, notices);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Unhandled fatal error");
            Console.Error.WriteLine($"DNN Manager ran into an error it can't go on from: {ex.Message} - the log in {paths.LogsDirectory} says more.");
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
}
