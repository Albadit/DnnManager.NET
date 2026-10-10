namespace DnnManager.Infrastructure.Startup;

/// <summary>
/// What the launcher (<c>DnnManager-launcher.exe</c>, src/DnnManager.Launcher) starts DNN Manager with. DNN Manager runs
/// with administrator rights, and .NET reads some variables before any of its code runs: a profiler (CORECLR_PROFILER*,
/// COR_PROFILER*), a diagnostic port or EventPipe file (DOTNET_DiagnosticPorts, DOTNET_EnableEventPipe and
/// DOTNET_EventPipeOutputPath), a startup hook, another runtime. They come from the user's environment
/// (HKCU\Environment), which every program of the user's can change - and the sign-in task and Windows' administrator
/// prompt both start DNN Manager with it. The launcher is compiled ahead of time (Native AOT, no CoreCLR to read them),
/// drops every such variable and only then starts DNN Manager.
/// This file is part of the app (the tests reach it) and linked into the launcher, which has nothing else of the app's -
/// so it uses nothing but the base library.
/// </summary>
public static class LaunchEnvironment
{
    /// <summary>The launcher's file name, beside <see cref="AppFileName"/> in an installed DNN Manager.</summary>
    public const string LauncherFileName = "DnnManager-launcher.exe";

    /// <summary>The program the launcher starts.</summary>
    public const string AppFileName = "DnnManager.exe";

    // Every variable starting so is .NET's: the runtime's (CoreCLR's COMPlus_ and DOTNET_), the host's, and the profiling
    // API's (COR_ and CORECLR_). None of them is DNN Manager's to need: dropping them all leaves nothing to be missed.
    private static readonly string[] DroppedPrefixes = ["DOTNET_", "COMPlus_", "COR_", "CORECLR_"];

    /// <summary>Whether the launcher leaves <paramref name="name"/> out.</summary>
    public static bool IsDropped(string name)
    {
        foreach (var prefix in DroppedPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// <paramref name="environment"/> without .NET's variables, and with its diagnostics switched off
    /// (<c>DOTNET_EnableDiagnostics=0</c>: no debugger, profiler, diagnostic port or EventPipe file) - set, not only left
    /// out, so nothing later turns them on. Names compare without case, as Windows' do.
    /// </summary>
    public static Dictionary<string, string> Clean(System.Collections.IDictionary environment)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name || name.Length == 0 || entry.Value is not string value) continue;
            if (!IsDropped(name)) clean[name] = value;
        }
        clean["DOTNET_EnableDiagnostics"] = "0";
        return clean;
    }

    /// <summary>
    /// The launcher beside <paramref name="appExe"/> (DNN Manager's exe), or null when there is none - a portable
    /// DNN Manager, or a development build, has none and is started as before.
    /// </summary>
    public static string? LauncherBeside(string? appExe)
    {
        if (string.IsNullOrEmpty(appExe) || !string.Equals(Path.GetFileName(appExe), AppFileName, StringComparison.OrdinalIgnoreCase))
            return null;
        var launcher = Path.Combine(Path.GetDirectoryName(appExe) ?? "", LauncherFileName);
        return File.Exists(launcher) ? launcher : null;
    }
}
