using System.Diagnostics;
using System.Text.Json;

namespace DnnManager.Presentation.Services;

/// <param name="OpensSolution">Opens a project's <c>.sln</c> rather than its folder when there is exactly one.</param>
internal sealed record Ide(string Name, string ExePath, bool OpensSolution);

/// <summary>
/// Finds the code editors / IDEs installed on this PC: Visual Studio (through vswhere), VS Code and its
/// forks, JetBrains Rider and Sublime Text, from their default install folders.
/// </summary>
internal static class IdeLocator
{
    private static IReadOnlyList<Ide>? _cache;

    /// <summary>The installed IDEs. Looked up once per run - vswhere takes a moment.</summary>
    public static IReadOnlyList<Ide> Installed => _cache ??= Find();

    /// <summary>Opens <paramref name="projectDirectory"/> (or its only solution file) in <paramref name="ide"/>.</summary>
    public static void Open(Ide ide, string projectDirectory)
    {
        var psi = new ProcessStartInfo(ide.ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = projectDirectory
        };
        psi.ArgumentList.Add(ide.OpensSolution && SolutionFor(projectDirectory) is { } sln ? sln : projectDirectory);
        Process.Start(psi);
    }

    /// <summary>The project's solution file when its folder holds exactly one, otherwise null.</summary>
    public static string? SolutionFor(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory)) return null;
        var solutions = Directory.EnumerateFiles(projectDirectory, "*.sln")
            .Concat(Directory.EnumerateFiles(projectDirectory, "*.slnx"))
            .Take(2)
            .ToList();
        return solutions.Count == 1 ? solutions[0] : null;
    }

    private static IReadOnlyList<Ide> Find()
    {
        var found = new List<Ide>();
        try { found.AddRange(FindVisualStudio()); } catch { /* vswhere missing or unreadable */ }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        AddFirst(found, "Visual Studio Code", false,
            Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
            FromPath("code.cmd", "Code.exe"));
        AddFirst(found, "VS Code Insiders", false,
            Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"),
            Path.Combine(programFiles, "Microsoft VS Code Insiders", "Code - Insiders.exe"));
        AddFirst(found, "Cursor", false,
            Path.Combine(local, "Programs", "cursor", "Cursor.exe"));
        AddFirst(found, "Windsurf", false,
            Path.Combine(local, "Programs", "Windsurf", "Windsurf.exe"));
        AddFirst(found, "Rider", true, RiderCandidates(local, programFiles).ToArray());
        AddFirst(found, "Sublime Text", false,
            Path.Combine(programFiles, "Sublime Text", "sublime_text.exe"),
            Path.Combine(programFiles, "Sublime Text 3", "sublime_text.exe"));
        return found;
    }

    private static void AddFirst(List<Ide> found, string name, bool opensSolution, params string?[] candidates)
    {
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is not null) found.Add(new Ide(name, exe, opensSolution));
    }

    /// <summary>
    /// The editor exe next to a launcher script on PATH, e.g. <c>…\Microsoft VS Code\bin\code.cmd</c> ->
    /// <c>…\Microsoft VS Code\Code.exe</c> - catches installs outside the default folders.
    /// </summary>
    private static string? FromPath(string launcher, string exeName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!File.Exists(Path.Combine(dir, launcher))) continue;
                var exe = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? "", exeName);
                if (File.Exists(exe)) return exe;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    private static IEnumerable<string> RiderCandidates(string local, string programFiles)
    {
        // JetBrains Toolbox installs here; the standalone installer uses a versioned folder.
        yield return Path.Combine(local, "Programs", "Rider", "bin", "rider64.exe");
        var jetBrains = Path.Combine(programFiles, "JetBrains");
        if (!Directory.Exists(jetBrains)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(jetBrains, "JetBrains Rider*").OrderByDescending(d => d))
            yield return Path.Combine(dir, "bin", "rider64.exe");
    }

    /// <summary>Every Visual Studio install vswhere knows about, newest first.</summary>
    private static IEnumerable<Ide> FindVisualStudio()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return Array.Empty<Ide>();

        var psi = new ProcessStartInfo(vswhere)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-all", "-prerelease", "-sort", "-format", "json", "-utf8" }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return Array.Empty<Ide>();
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        using var doc = JsonDocument.Parse(json);
        var list = new List<Ide>();
        foreach (var vs in doc.RootElement.EnumerateArray())
        {
            if (!vs.TryGetProperty("productPath", out var path) || path.GetString() is not { } exe || !File.Exists(exe)) continue;
            var name = vs.TryGetProperty("displayName", out var display) ? display.GetString() : null;
            list.Add(new Ide(name ?? "Visual Studio", exe, OpensSolution: true));
        }
        return list;
    }
}
