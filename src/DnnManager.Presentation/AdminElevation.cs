using System.Diagnostics;
using System.Security.Principal;
using DnnManager.Infrastructure.Startup;

namespace DnnManager.Presentation;

internal static class AdminElevation
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Re-launches the current executable elevated (triggers a UAC prompt) in a new
    /// console window. Returns true if the elevated process was started; the caller
    /// should then exit so the new elevated instance can take over.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args) => TryRelaunchElevated(args, out _);

    /// <summary>
    /// The same; <paramref name="declined"/> says the user answered No to Windows' administrator prompt. An installed DNN
    /// Manager asks for the rights for its launcher (<see cref="LaunchEnvironment.LauncherFileName"/>), which starts
    /// DnnManager.exe elevated without the .NET variables of the user's environment - the elevated process gets that
    /// environment, and CoreCLR would read them (a profiler, a diagnostic port) before DNN Manager's code runs. The
    /// launcher takes no arguments: <paramref name="args"/> go along only without it (a portable DNN Manager).
    /// </summary>
    public static bool TryRelaunchElevated(string[] args, out bool declined)
    {
        declined = false;
        if (!OperatingSystem.IsWindows()) return false;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var launcher = LaunchEnvironment.LauncherBeside(exePath);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = launcher ?? exePath,
                UseShellExecute = true,
                Verb            = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (launcher is null)
                foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            declined = true;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
