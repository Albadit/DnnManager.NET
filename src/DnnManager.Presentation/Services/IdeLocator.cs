using System.Diagnostics;
using System.Text.Json;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Presentation.Services;

/// <param name="OpensSolution">Opens a project's <c>.sln</c> rather than its folder when there is exactly one.</param>
/// <param name="MajorVersion">The product's major version (e.g. 22 for SSMS 22), or 0 when unknown.</param>
internal sealed record Ide(string Name, string ExePath, bool OpensSolution, int MajorVersion = 0);

/// <summary>
/// Finds the code editors / IDEs installed on this PC: Visual Studio (through vswhere), VS Code and its forks,
/// JetBrains Rider and IntelliJ IDEA, Sublime Text, Zed, Vim and Neovim, from their default install folders or PATH -
/// and SQL Server Management Studio, for a project's database. Only what's found is offered.
/// </summary>
internal static class IdeLocator
{
    private const string SsmsProduct = "Microsoft.VisualStudio.Product.Ssms";

    // Looked up once per run (vswhere takes a moment). Lazy so the background warm-up and a right-click that
    // arrives before it finishes share one lookup instead of running vswhere twice.
    private static readonly Lazy<IReadOnlyList<Ide>> _installed = new(Find);
    private static readonly Lazy<IReadOnlyList<Ide>> _managementStudios = new(FindManagementStudios);

    /// <summary>The installed IDEs.</summary>
    public static IReadOnlyList<Ide> Installed => _installed.Value;

    /// <summary>The installed SQL Server Management Studio versions, newest first.</summary>
    public static IReadOnlyList<Ide> ManagementStudios => _managementStudios.Value;

    /// <summary>A running instance of exactly this SSMS (same exe) with a main window, or null.</summary>
    public static Process? FindRunning(Ide ssms)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ssms.ExePath)))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero &&
                    string.Equals(process.MainModule?.FileName, ssms.ExePath, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch { /* exited, or no access to its modules */ }
            process.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Starts SSMS with no connection switches, so it just shows its Connect dialog (any connection switch makes
    /// it connect at once - without a password, failing with an error first).
    /// </summary>
    public static Process? StartManagementStudio(Ide ssms)
    {
        var psi = new ProcessStartInfo(ssms.ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add("-nosplash");
        return Process.Start(psi);
    }

    /// <summary>
    /// Opens <paramref name="database"/> in SSMS: server, database and login filled in. SSMS takes no password
    /// on its command line, so it asks for one (and can remember it); no user means Windows authentication.
    /// </summary>
    /// <param name="trustServerCertificate">
    /// Trust the server's certificate without validating it - for the local container, whose certificate is
    /// self-signed. SSMS 20+ encrypts by default and refuses such a certificate otherwise.
    /// </param>
    /// <returns>The started SSMS process.</returns>
    public static Process? OpenDatabase(Ide ssms, SiteSqlConnection database, bool trustServerCertificate, string displayName)
    {
        // SSMS 21+ switches: -S -d -U -A -C -N -i -dn -nosplash -log; no user = Windows authentication.
        // SSMS 18-20 have no -C / -dn and take -E for Windows authentication.
        var modern = ssms.MajorVersion >= 21;
        var psi = new ProcessStartInfo(ssms.ExePath) { UseShellExecute = false };
        void Add(params string[] args) { foreach (var a in args) psi.ArgumentList.Add(a); }

        Add("-S", database.Server);
        if (database.Database.Length > 0) Add("-d", database.Database);
        if (database.User.Length > 0) Add("-U", database.User);
        else if (!modern) Add("-E");
        if (modern)
        {
            if (trustServerCertificate) Add("-C");
            Add("-dn", displayName);
        }
        Add("-nosplash");
        return Process.Start(psi);
    }

    /// <summary>
    /// Opens <paramref name="projectDirectory"/> (or its only solution file) in <paramref name="ide"/> - as the signed-in
    /// user, not with DNN Manager's Administrator rights: an editor has no need of them, and one installed in the user's
    /// own folders (VS Code's user installer) could have been changed by any program of theirs. Only when the desktop's
    /// shell can't start it, and only administrators can change it, it is started directly.
    /// </summary>
    public static void Open(Ide ide, string projectDirectory)
    {
        var target = ide.OpensSolution && SolutionFor(projectDirectory) is { } sln ? sln : projectDirectory;
        if (Unelevated.Start(ide.ExePath, [target], projectDirectory)) return;
        if (!TrustedPrograms.MayRun(ide.ExePath))
            throw new InvalidOperationException($"{ide.Name} couldn't be started as you, and DNN Manager doesn't start it as Administrator: " +
                                                $"programs without administrator rights could change {ide.ExePath}.");
        var psi = new ProcessStartInfo(ide.ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = projectDirectory
        };
        psi.ArgumentList.Add(target);
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
        try { found.AddRange(FromVsWhere(products: null, fallbackName: "Visual Studio", opensSolution: true)); }
        catch { /* vswhere missing or unreadable */ }

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
        AddFirst(found, "Rider", true, JetBrainsCandidates(local, programFiles, "Rider", "JetBrains Rider*", "rider64.exe").ToArray());
        AddFirst(found, "IntelliJ IDEA", false,
            JetBrainsCandidates(local, programFiles, "IntelliJ IDEA Ultimate", "IntelliJ IDEA*", "idea64.exe")
                .Concat(JetBrainsCandidates(local, programFiles, "IntelliJ IDEA Community Edition", "IntelliJ IDEA*", "idea64.exe"))
                .ToArray());
        AddFirst(found, "Sublime Text", false,
            Path.Combine(programFiles, "Sublime Text", "sublime_text.exe"),
            Path.Combine(programFiles, "Sublime Text 3", "sublime_text.exe"),
            OnPath("sublime_text.exe"));
        AddFirst(found, "Zed", false,
            Path.Combine(local, "Programs", "Zed", "Zed.exe"),
            Path.Combine(programFiles, "Zed", "Zed.exe"),
            OnPath("zed.exe"));
        // Vim and Neovim: the windowed editor when it's there; otherwise the console one, which opens in a window of
        // its own (DNN Manager has no console) on the project folder.
        AddFirst(found, "Vim", false,
            VersionedCandidates("Vim", "vim*", "gvim.exe").Append(OnPath("gvim.exe"))
                .Concat(VersionedCandidates("Vim", "vim*", "vim.exe")).Append(OnPath("vim.exe")).ToArray());
        AddFirst(found, "Neovim", false,
            Path.Combine(programFiles, "Neovim", "bin", "nvim-qt.exe"), OnPath("nvim-qt.exe"),
            Path.Combine(programFiles, "Neovim", "bin", "nvim.exe"), OnPath("nvim.exe"));
        return found;
    }

    /// <summary>
    /// The full path of <paramref name="exeName"/> in a folder on PATH, or null. Git for Windows' own tools (its vim,
    /// used for commit messages) don't count - that's not an editor anyone installed.
    /// </summary>
    private static string? OnPath(string exeName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), exeName);
                if (exe.Contains(@"\Git\usr\", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains(@"\Git\bin\", StringComparison.OrdinalIgnoreCase) ||
                    exe.Contains(@"\Git\mingw64\", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(exe)) return exe;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="exeName"/> in the versioned folders under <c>Program Files\&lt;product&gt;</c> (both Program Files),
    /// newest first - e.g. <c>Vim\vim91\gvim.exe</c>.
    /// </summary>
    private static IEnumerable<string> VersionedCandidates(string product, string versionPattern, string exeName)
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                     .Select(Environment.GetFolderPath).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(root, product);
            if (!Directory.Exists(folder)) continue;
            foreach (var dir in Directory.EnumerateDirectories(folder, versionPattern).OrderByDescending(d => d))
                yield return Path.Combine(dir, exeName);
        }
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

    private static IEnumerable<string> JetBrainsCandidates(string local, string programFiles, string toolboxFolder,
        string standalonePattern, string exeName)
    {
        // JetBrains Toolbox installs here; the standalone installer uses a versioned folder.
        yield return Path.Combine(local, "Programs", toolboxFolder, "bin", exeName);
        var jetBrains = Path.Combine(programFiles, "JetBrains");
        if (!Directory.Exists(jetBrains)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(jetBrains, standalonePattern).OrderByDescending(d => d))
            yield return Path.Combine(dir, "bin", exeName);
    }

    /// <summary>
    /// SSMS 21 and later are Visual Studio Installer products, so vswhere lists them with their real name;
    /// SSMS 18-20 are found by their default install folders.
    /// </summary>
    private static IReadOnlyList<Ide> FindManagementStudios()
    {
        var found = new List<Ide>();
        try { found.AddRange(FromVsWhere(SsmsProduct, "SQL Server Management Studio", opensSolution: false)); }
        catch { /* vswhere missing or unreadable */ }

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        foreach (var root in programFiles.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "Microsoft SQL Server Management Studio *"))
            {
                var exe = new[] { Path.Combine(dir, "Common7", "IDE", "Ssms.exe"), Path.Combine(dir, "Release", "Common7", "IDE", "SSMS.exe") }
                    .FirstOrDefault(File.Exists);
                if (exe is null || found.Any(f => f.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase))) continue;
                var name = Path.GetFileName(dir)["Microsoft ".Length..];
                found.Add(new Ide(name, exe, OpensSolution: false,
                    int.TryParse(name.Split(' ').Last(), out var major) ? major : 0));
            }
        }
        return found.OrderByDescending(f => f.MajorVersion).ToList();
    }

    /// <summary>
    /// Every install vswhere knows about, newest first: Visual Studio itself when <paramref name="products"/> is
    /// null (vswhere's default: Community, Professional, Enterprise), otherwise that product.
    /// </summary>
    private static IEnumerable<Ide> FromVsWhere(string? products, string fallbackName, bool opensSolution)
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
        if (products is not null)
        {
            psi.ArgumentList.Add("-products");
            psi.ArgumentList.Add(products);
        }

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
            var version = vs.TryGetProperty("installationVersion", out var v) && v.GetString() is { } text &&
                          int.TryParse(text.Split('.')[0], out var major) ? major : 0;
            list.Add(new Ide(name ?? fallbackName, exe, opensSolution, version));
        }
        return list;
    }
}
