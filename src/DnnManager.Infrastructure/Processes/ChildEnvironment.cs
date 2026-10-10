using System.Text.RegularExpressions;
using DnnManager.Infrastructure.Files;
using Microsoft.Win32;

namespace DnnManager.Infrastructure.Processes;

/// <summary>
/// The environment of a program DNN Manager starts with its administrator rights. It comes from the user's own
/// environment, which any program of theirs can change: variables that make a program load other code (.NET startup
/// hooks, profilers and diagnostics, another .NET runtime, NuGet's folders and plugins, another Docker or Docker
/// configuration) are dropped; PowerShell only finds the modules of Windows and Program Files, not those in the user's
/// Documents; docker reads a configuration of DNN Manager's own, not the user's <c>.docker</c> (its context, plugins and
/// credential helpers); and PATH is the computer's PATH only - a folder on the user's PATH is one they can write to.
/// </summary>
public static class ChildEnvironment
{
    // Every variable starting so: .NET's runtime, host and SDK settings (startup hooks, profilers, diagnostic ports,
    // EventPipe output files, another runtime), NuGet's (package folders, plugins), docker's and sqlcmd's.
    private static readonly string[] DroppedPrefixes = ["DOTNET_", "COMPlus_", "COR_", "CORECLR_", "NUGET_", "DOCKER_", "SQLCMD"];

    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>
    /// The computer's PATH, from the registry: its %variables% filled in with the folders of Windows and Program Files
    /// and the computer's own variables (which only administrators change) - never the user's, who could otherwise point
    /// %SystemRoot% or %JAVA_HOME% in it at a folder of theirs. A folder with a variable it can't fill in so is left out.
    /// What a program DNN Manager starts with administrator rights looks programs up in.
    /// </summary>
    public static string MachinePath =>
        string.Join(';', (MachineValue("Path") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => Expand(entry, 0)).OfType<string>());

    /// <summary>The computer's PATHEXT.</summary>
    public static string MachinePathExt =>
        MachineValue("PATHEXT") is { Length: > 0 } ext && Expand(ext, 0) is { } expanded ? expanded : ".COM;.EXE;.BAT;.CMD";

    /// <summary>Makes <paramref name="environment"/> (a ProcessStartInfo's) safe to start a program with administrator rights in.</summary>
    public static void Apply(IDictionary<string, string?> environment)
    {
        if (!TrustedPrograms.IsElevated) return;
        Harden(environment);
    }

    /// <summary>
    /// Only drops the variables that make a program load other code, and keeps PowerShell to the modules of Windows -
    /// for a terminal's shell, which keeps the user's PATH: the user types its commands.
    /// </summary>
    public static void Drop(IDictionary<string, string?> environment)
    {
        if (!TrustedPrograms.IsElevated) return;
        DropCodeLoading(environment);
        SetSystemFolders(environment);
    }

    /// <summary>
    /// For DNN Manager itself, started again (a restart, the update helper, the new version, Setup): the variables that
    /// make it load other code dropped, its diagnostics off. Its PATH stays - its terminals' shells keep the user's.
    /// </summary>
    public static void ForSelf(IDictionary<string, string?> environment)
    {
        if (!TrustedPrograms.IsElevated) return;
        DropCodeLoading(environment);
        SetSystemFolders(environment);
        environment["DOTNET_EnableDiagnostics"] = "0";
    }

    // What makes .NET load other code into a process, or write files where it is told, as it starts - read by the
    // runtime before DNN Manager's own code runs, so DNN Manager can only say it was there.
    private static readonly string[] LoadingSuffixes =
    [
        "ENABLE_PROFILING", "PROFILER", "PROFILER_PATH", "PROFILER_PATH_32", "PROFILER_PATH_64", "STARTUP_HOOKS", "ADDITIONAL_DEPS",
        "DiagnosticPorts", "EnableEventPipe", "EventPipeOutputPath", "EventPipeConfig", "DbgEnableMiniDump", "DbgMiniDumpName", "JitStdOutFile"
    ];

    /// <summary>
    /// The variables in <paramref name="environment"/> (this process's, when null) that made .NET load other code or
    /// write a diagnostics file as it started - with administrator rights, from the user's environment, which any of
    /// their programs can change.
    /// </summary>
    public static IReadOnlyList<string> CodeLoadingVariables(System.Collections.IDictionary? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariables()).Keys.OfType<string>()
        .Where(k => new[] { "DOTNET_", "COMPlus_", "COR_", "CORECLR_" }.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                    LoadingSuffixes.Any(s => k.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase)))
        .Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary><see cref="Apply"/> without the check for administrator rights - what the tests look at.</summary>
    internal static void Harden(IDictionary<string, string?> environment)
    {
        DropCodeLoading(environment);
        SetSystemFolders(environment);
        environment["PATH"] = MachinePath;
        environment["PATHEXT"] = MachinePathExt;
        // .NET programs (SqlPackage, dotnet): no debugger, profiler, diagnostic port or EventPipe file - set, not just
        // left out, so nothing else turns them on.
        environment["DOTNET_EnableDiagnostics"] = "0";
        environment["DOCKER_CONFIG"] = DockerConfig();
    }

    internal static void DropCodeLoading(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(k => DroppedPrefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList())
            environment.Remove(name);
    }

    /// <summary>
    /// The variables naming Windows' own folders, from Windows itself rather than the user's environment; and
    /// PSModulePath to the modules of Windows and Program Files only: Windows PowerShell looks in the user's Documents
    /// first otherwise, and loads a module it finds there (PSReadLine, Dism) by itself, with administrator rights.
    /// </summary>
    private static void SetSystemFolders(IDictionary<string, string?> environment)
    {
        foreach (var (name, folder) in SystemFolders())
            if (folder.Length > 0) environment[name] = folder;
        environment["PSModulePath"] = string.Join(';',
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"));
    }

    private static IEnumerable<(string Name, string Folder)> SystemFolders()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return ("SystemRoot", windows);
        yield return ("windir", windows);
        yield return ("SystemDrive", Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "");
        yield return ("ComSpec", Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        yield return ("ProgramFiles", programFiles);
        yield return ("ProgramW6432", programFiles);
        yield return ("ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        yield return ("CommonProgramFiles", common);
        yield return ("CommonProgramW6432", common);
        yield return ("CommonProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        yield return ("ProgramData", programData);
        yield return ("ALLUSERSPROFILE", programData);
    }

    /// <summary>
    /// <paramref name="value"/> with its %variables% filled in from <see cref="SystemFolders"/> or the computer's own
    /// variables; null when one is neither.
    /// </summary>
    private static string? Expand(string value, int depth)
    {
        var folders = SystemFolders().Where(f => f.Folder.Length > 0).ToDictionary(f => f.Name, f => f.Folder, StringComparer.OrdinalIgnoreCase);
        var unknown = false;
        var expanded = Regex.Replace(value, "%([^%;]+)%", m =>
        {
            if (folders.TryGetValue(m.Groups[1].Value, out var folder)) return folder;
            if (depth < 3 && MachineValue(m.Groups[1].Value) is { } machine && Expand(machine, depth + 1) is { } inner) return inner;
            unknown = true;
            return m.Value;
        });
        return unknown ? null : expanded;
    }

    /// <summary>A variable of the computer's (HKLM), as written - its %variables% not filled in from anyone's environment.</summary>
    private static string? MachineValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineKey);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>
    /// docker's configuration folder: DNN Manager's own, which only administrators can change - an empty one, so docker
    /// talks to Docker Desktop's engine (no context of the user's), and finds its compose plugin in Docker's folders
    /// only. Should that folder not be there to make, a folder that isn't there and that only administrators could
    /// make - never the user's.
    /// </summary>
    private static string DockerConfig()
    {
        try
        {
            return PrivateTemp.DockerConfigPath;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Path.Combine(Environment.SystemDirectory, "config", "DnnManager-docker");
        }
    }
}
