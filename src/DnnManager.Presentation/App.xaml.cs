using System.Windows;
using System.Windows.Threading;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation;

public partial class App : System.Windows.Application
{
    /// <summary>Where unexpected errors are written (the daily log file) - set once the host is built.</summary>
    internal static ILogger? Log { get; set; }

    public App()
    {
        // Last line of defence: an exception escaping a click handler shows a message instead of killing the app (use
        // cases themselves run through OperationRunner, which reports failures). Each is written to the log with its
        // stack trace; the user sees only the message.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Off the UI thread nothing can be saved - but what killed the app is in the log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception on a background thread");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log?.LogWarning(e.Exception, "A background task failed and nobody observed it");
            e.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log?.LogError(e.Exception, "Unhandled exception on the UI thread");
        Dialogs.Error($"Unexpected error: {e.Exception.Message}");
        e.Handled = true;
    }
}
