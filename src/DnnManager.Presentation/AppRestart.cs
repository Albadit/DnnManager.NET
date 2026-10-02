using System.Diagnostics;
using System.Windows;

namespace DnnManager.Presentation;

/// <summary>
/// Restarting DNN Manager (Troubleshoot → Restart, and after a reset). The window closes as on any quit - asking
/// first about unsaved settings or a running operation - and once this process has ended, a new one starts. It is
/// started with <see cref="AfterArgument"/> and this process's id, and waits for it to exit before anything else:
/// otherwise it would find this one still running and only bring its window to the front.
/// </summary>
internal static class AppRestart
{
    public const string AfterArgument = "--after";

    /// <summary>Set when the window closed for a restart - <see cref="Program"/> starts the new process once the app has ended.</summary>
    public static bool Requested { get; private set; }

    /// <summary>
    /// Closes the window for a restart. False when closing was cancelled (the user chose to stay) - nothing happens then.
    /// </summary>
    public static bool Restart()
    {
        if (System.Windows.Application.Current?.MainWindow is not { } window) return false;
        Requested = true;
        window.Close();
        // Still shown: a question on closing was answered with "stay".
        if (window.IsVisible) Requested = false;
        return Requested;
    }

    /// <summary>Starts the new DNN Manager - from <see cref="Program"/>, after the app has ended. It is elevated like this one.</summary>
    public static void StartNew()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(AfterArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"DNN Manager could not start again: {ex.Message}\n\nStart it from the Start menu.",
                "DNN Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// When started by <see cref="StartNew"/>: waits (up to 30 seconds) for the previous DNN Manager to exit, and returns
    /// the arguments without <see cref="AfterArgument"/> and its id.
    /// </summary>
    public static string[] WaitForPrevious(string[] args)
    {
        var at = Array.IndexOf(args, AfterArgument);
        if (at < 0) return args;
        if (at + 1 < args.Length && int.TryParse(args[at + 1], out var id))
        {
            try
            {
                using var previous = Process.GetProcessById(id);
                previous.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        return args.Where((_, i) => i != at && i != at + 1).ToArray();
    }
}
