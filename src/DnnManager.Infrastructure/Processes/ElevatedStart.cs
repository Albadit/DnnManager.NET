using System.ComponentModel;
using System.Diagnostics;

namespace DnnManager.Infrastructure.Processes;

/// <summary>
/// The one way - besides <see cref="ProcessRunner"/>, for a program whose output is read as it runs - to start a program
/// with DNN Manager's administrator rights: resolved through <see cref="TrustedPrograms.Resolve"/> (only a copy nobody but
/// administrators can change, started by its own path) and given <see cref="ChildEnvironment.Apply"/>'s environment
/// (none of the user's variables that make a program load other code). A program for the user - an editor, a browser -
/// isn't started here: it starts as the user (the desktop's shell, or <see cref="Explorer"/>).
/// </summary>
public static class ElevatedStart
{
    /// <summary>
    /// Starts <paramref name="program"/> with <paramref name="arguments"/> - each passed as it is, never a command line put
    /// together. Throws <see cref="InvalidOperationException"/> saying why when it may not be started (or isn't there), and
    /// <see cref="Win32Exception"/> when Windows can't start it.
    /// </summary>
    public static Process Start(string program, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var resolved = TrustedPrograms.Resolve(program);
        if (resolved.Path is not { } path) throw new InvalidOperationException(resolved.Problem(program));
        var psi = new ProcessStartInfo(path) { UseShellExecute = false };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        ChildEnvironment.Apply(psi.Environment);
        return Process.Start(psi) ?? throw new InvalidOperationException($"{program} didn't start.");
    }

    /// <summary>
    /// Runs <paramref name="program"/> and returns what it wrote to its standard output - or null when it may not be
    /// started, failed to, or didn't finish within <paramref name="timeout"/> (it is ended then, with what it started).
    /// For a quick question to a tool (vswhere): reading its output never waits longer than that.
    /// </summary>
    public static string? Output(string program, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var resolved = TrustedPrograms.Resolve(program);
        if (resolved.Path is not { } path) return null;
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        ChildEnvironment.Apply(psi.Environment);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            // Read alongside: a full pipe would stop it, and ReadToEnd alone would wait for it however long it takes.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                return null;
            }
            // The streams end with the process; a child it left holding them doesn't keep this waiting.
            return output.Wait(TimeSpan.FromSeconds(2)) ? output.Result : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hands <paramref name="target"/> (a folder, a file, a web address, a program the user starts) to Windows' own
    /// Explorer - which runs as the signed-in user, so what it opens doesn't get DNN Manager's rights. Explorer by its full
    /// path: a bare name would be looked up in DNN Manager's own folder first. Throws <see cref="Win32Exception"/> or
    /// <see cref="InvalidOperationException"/> when it can't be started.
    /// </summary>
    public static void Explorer(string target)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using var _ = Start(explorer, [target]);
    }
}
