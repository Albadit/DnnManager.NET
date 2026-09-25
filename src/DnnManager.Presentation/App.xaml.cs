using System.Windows.Threading;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation;

public partial class App : System.Windows.Application
{
    public App()
    {
        // Last line of defence: an exception escaping a click handler shows a message instead of
        // killing the app (use cases themselves run through OperationRunner, which logs failures).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Dialogs.Error($"Unexpected error: {e.Exception.Message}");
        e.Handled = true;
    }
}
