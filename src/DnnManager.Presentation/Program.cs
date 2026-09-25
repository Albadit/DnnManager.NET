using System.Text;
using System.Windows;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation;

internal static class Program
{
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

        // Write the default config and compose file next to the exe if either is missing (first run,
        // cleaned publish folder…). Reported in the activity log once the window is up.
        var startupNotices = new List<(bool Ok, string Message)>();
        foreach (var file in new[] { BundledFiles.AppSettings, BundledFiles.DockerCompose })
        {
            try
            {
                if (BundledFiles.EnsureExists(file))
                    startupNotices.Add((true, $"{file} was missing - created the default one next to the app."));
            }
            catch (Exception ex)
            {
                startupNotices.Add((false, $"{file} is missing and could not be recreated: {ex.Message}"));
            }
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Settings come from appsettings.json next to the exe; if it couldn't be written (e.g. a read-only
        // folder), the built-in default is used as-is so the app still starts.
        builder.Configuration.SetBasePath(AppContext.BaseDirectory);
        if (File.Exists(BundledFiles.PathOf(BundledFiles.AppSettings)))
            builder.Configuration.AddJsonFile(BundledFiles.AppSettings, optional: false, reloadOnChange: false);
        else
            builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(BundledFiles.DefaultContent(BundledFiles.AppSettings))));
        builder.Configuration.AddEnvironmentVariables("DNNMGR_");

        // No console in a WinExe - errors surface in the activity log and message boxes instead.
        builder.Logging.ClearProviders();

        builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();

        // GUI services
        builder.Services.AddSingleton<ActivityLog>();
        builder.Services.AddSingleton<GuiProgressReporter>();
        builder.Services.AddSingleton<IProgressReporter>(sp => sp.GetRequiredService<GuiProgressReporter>());
        builder.Services.AddSingleton<GuiUserPrompt>();
        builder.Services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<GuiUserPrompt>());
        builder.Services.AddSingleton<OperationRunner>();
        builder.Services.AddSingleton<MainWindow>();

        using var host = builder.Build();

        var app = new App();
        app.InitializeComponent();
        ThemeManager.Initialize(host.Services.GetRequiredService<IOptions<AppOptions>>().Value.Theme);

        var log = host.Services.GetRequiredService<ActivityLog>();
        foreach (var (ok, message) in startupNotices)
        {
            if (ok) log.Info(message);
            else log.Fail(message);
        }

        try
        {
            return app.Run(host.Services.GetRequiredService<MainWindow>());
        }
        catch (Exception ex)
        {
            host.Services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Unhandled fatal error");
            MessageBox.Show($"Fatal: {ex.Message}", "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }
}
