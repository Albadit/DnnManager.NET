using DnnManager.Application;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure;
using DnnManager.Presentation;
using DnnManager.Presentation.Tui;
using DnnManager.Presentation.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!AdminElevation.IsAdministrator())
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Elevation required - relaunching as Administrator…");
    Console.ResetColor();
    if (AdminElevation.TryRelaunchElevated(args)) return 0;
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Could not elevate. Please re-run from an elevated terminal.");
    Console.ResetColor();
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "DNN Project Manager";
Ansi.EnableVirtualTerminalProcessing();

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables("DNNMGR_");

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
    o.IncludeScopes = false;
});

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// TUI services
builder.Services.AddSingleton<ConsoleScreen>();
builder.Services.AddSingleton<ConfirmDialog>();
builder.Services.AddSingleton<TextPrompt>();
builder.Services.AddSingleton<StatusWriter>();
builder.Services.AddSingleton<TuiProgressReporter>();
builder.Services.AddSingleton<DnnManager.Application.Abstractions.IProgressReporter>(sp => sp.GetRequiredService<TuiProgressReporter>());
builder.Services.AddSingleton<TuiUserPrompt>();
builder.Services.AddSingleton<DnnManager.Application.Abstractions.IUserPrompt>(sp => sp.GetRequiredService<TuiUserPrompt>());

builder.Services.AddSingleton<MainMenuView>();
builder.Services.AddScoped<SetupView>();
builder.Services.AddScoped<ExistingProjectView>();
builder.Services.AddScoped<RemoveView>();
builder.Services.AddScoped<PrerequisitesView>();
builder.Services.AddScoped<ProjectsListView>();
builder.Services.AddScoped<ExportView>();
builder.Services.AddScoped<ImportView>();
builder.Services.AddScoped<CloneView>();
builder.Services.AddScoped<ConnectionsView>();

using var host = builder.Build();
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Resize the console window to the configured size (Windows / classic conhost only).
try
{
    var opts = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppOptions>>().Value.Console;
    if (OperatingSystem.IsWindows() && opts.WindowWidth > 0 && opts.WindowHeight > 0)
    {
        int w = Math.Min(opts.WindowWidth,  Console.LargestWindowWidth);
        int h = Math.Min(opts.WindowHeight, Console.LargestWindowHeight);
        Console.SetWindowSize(w, h);
        Console.SetBufferSize(w, h);
    }
}
catch { /* terminal may not support resize (e.g. Windows Terminal) - ignore */ }

try
{
    var menu = host.Services.GetRequiredService<MainMenuView>();
    await menu.RunAsync(cts.Token);
    return 0;
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogCritical(ex, "Unhandled fatal error");
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Fatal: {ex.Message}");
    Console.ResetColor();
    return 2;
}
finally
{
    try { Console.ResetColor(); Console.CursorVisible = true; } catch { }
}
