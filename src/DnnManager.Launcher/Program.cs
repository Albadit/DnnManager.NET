using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DnnManager.Infrastructure.Startup;

namespace DnnManager.Launcher;

/// <summary>
/// Starts DnnManager.exe from this exe's folder with .NET's variables taken out of the environment
/// (<see cref="LaunchEnvironment.Clean"/>), and ends. Its own command line goes nowhere: whoever started it chose that,
/// not the user. It starts DNN Manager with the rights it has itself - elevated when the sign-in task or Windows'
/// administrator prompt started it.
/// </summary>
internal static partial class Program
{
    private static int Main()
    {
        // This exe's own folder - never the working directory, which whoever started it chose.
        var folder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var app = Path.Combine(folder, LaunchEnvironment.AppFileName);
        if (!File.Exists(app))
        {
            Show($"{LaunchEnvironment.AppFileName} isn't in {folder} - reinstall DNN Manager (its Setup's Repair).");
            return 1;
        }

        var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = folder };
        start.Environment.Clear();
        foreach (var (name, value) in LaunchEnvironment.Clean(Environment.GetEnvironmentVariables()))
            start.Environment[name] = value;
        try
        {
            using var _ = Process.Start(start);
            return 0;
        }
        catch (Win32Exception ex)
        {
            Show($"DNN Manager could not be started: {ex.Message}");
            return 1;
        }
    }

    private static void Show(string text) => MessageBoxW(0, text, "DNN Manager", 0x10 /* MB_ICONERROR */);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
