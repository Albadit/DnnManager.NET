using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DnnManager.Desktop.Services;
using DnnManager.Desktop.Views;
using Microsoft.Extensions.Logging;

namespace DnnManager.Desktop;

public partial class App : Avalonia.Application
{
    /// <summary>Where unexpected errors are written (the daily log file) - set once the host is built.</summary>
    internal static ILogger? Log { get; set; }

    /// <summary>Where the log is written - said in an error message, so what happened can be found.</summary>
    internal static string? LogsDirectory { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyTheme(Program.Settings.Appearance.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window closes for good only when it quits (it may hide in the notification area instead).
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            AppearanceLayout.Apply(window, Program.Settings.Appearance);
            desktop.MainWindow = window;
        }
        if (Program.IsRealStart) CatchUnhandled();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The last line of defence (as the Windows app's App): an exception escaping a click handler is written to the log
    /// with its stack trace and shown as a message - the app goes on. Off the UI thread, it is at least in the log.
    /// </summary>
    private static void CatchUnhandled()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log?.LogError(e.Exception, "Unhandled exception on the UI thread");
            _ = Dialogs.MessageAsync($"Something went wrong that DNN Manager didn't expect: {e.Exception.Message}\n\n" +
                                     $"It goes on as before. What happened is in the log{(LogsDirectory is { } logs ? $", in {logs}" : "")}.");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception on a background thread");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log?.LogWarning(e.Exception, "A background task failed and nobody observed it");
            e.SetObserved();
        };
    }

    /// <summary>The setting appearance.theme: system (follows the OS), light or dark - at once, in every window.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
