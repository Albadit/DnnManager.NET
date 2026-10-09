using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace DnnManager.IntegrationTests.Support;

/// <summary>
/// One UI thread for every test that builds WPF controls: a WPF application can be made once per process, and what it
/// holds (its styles, their brushes) belongs to the thread that made it. That thread has DNN Manager's own App, with
/// App.xaml's resources - the light theme, the tokens, icons and control styles - as the app has them.
/// </summary>
internal static class WpfUi
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    /// <summary>Runs <paramref name="body"/> on the UI thread; what it throws is thrown here.</summary>
    public static void Run(Action body)
    {
        Exception? failure = null;
        Ui.Value.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                // The app itself, as Program makes it: App.xaml's resources - the palette, the tokens, icons and styles.
                var app = new DnnManager.Presentation.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex) { failure = ex; }
            ready.Set();
            if (failure is null) Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return dispatcher!;
    }
}
