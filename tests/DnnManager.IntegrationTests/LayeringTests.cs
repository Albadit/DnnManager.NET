using System.Text.RegularExpressions;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The layers' rule (.docs/architecture.md): Domain depends on nothing, Application on Domain alone, Infrastructure on
/// both - never on Presentation. The four layers are folders of one assembly, so nothing but this test keeps them
/// apart: it reads the source's usings and namespaces.
/// </summary>
[TestClass]
public sealed class LayeringTests
{
    private static readonly string Source = Path.Combine(RepositoryRoot(), "src");

    [TestMethod]
    [DataRow("DnnManager.Domain", "Application", "Infrastructure", "Presentation", "Desktop")]
    [DataRow("DnnManager.Application", "Infrastructure", "Presentation", "Desktop")]
    [DataRow("DnnManager.Infrastructure", "Presentation", "Desktop")]
    // What both interfaces share uses neither of them (its namespace is Presentation.Services: the WPF app's own is "Pages", "Controls"…).
    [DataRow("DnnManager.Ui", "Desktop", @"Presentation\.(?!Services\b)\w+")]
    public void A_layer_uses_none_above_it(string layer, params string[] above)
    {
        var uses = new Regex($@"\bDnnManager\.({string.Join('|', above)})\b");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Source, layer), "*.cs", SearchOption.AllDirectories))
        {
            // What a build generated there (obj, bin) isn't the layers' code.
            if (IsBuildOutput(file)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                // What comments say isn't a dependency.
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (uses.IsMatch(line)) found.Add($"{Path.GetRelativePath(Source, file)}:{i + 1}: {line}");
            }
        }
        Assert.AreEqual(0, found.Count, string.Join(Environment.NewLine, found));
    }

    /// <summary>
    /// What of Infrastructure Presentation uses directly - today's, so it doesn't grow: a new need goes through an
    /// Application abstraction, or is added here on purpose.
    /// </summary>
    private static readonly string[] InfrastructureForPresentation =
    [
        "DnnManager.Infrastructure", "DnnManager.Infrastructure.Data", "DnnManager.Infrastructure.Diagnostics", "DnnManager.Infrastructure.Files",
        "DnnManager.Infrastructure.Hosts", "DnnManager.Infrastructure.KeepWarm", "DnnManager.Infrastructure.Monitoring",
        "DnnManager.Infrastructure.Processes", "DnnManager.Infrastructure.Settings", "DnnManager.Infrastructure.SiteLogs",
        "DnnManager.Infrastructure.Sql", "DnnManager.Infrastructure.Startup", "DnnManager.Infrastructure.State",
        "DnnManager.Infrastructure.Terminal", "DnnManager.Infrastructure.Updates"
    ];

    [TestMethod]
    [DataRow("DnnManager.Presentation")]
    [DataRow("DnnManager.Ui")]
    [DataRow("DnnManager.Desktop")]
    public void Presentation_uses_only_the_Infrastructure_it_uses_today(string layer)
    {
        var uses = new Regex(@"\bDnnManager\.Infrastructure(\.[A-Za-z]+)?\b");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Source, layer), "*.cs", SearchOption.AllDirectories))
        {
            // What a build generated there (obj, bin) isn't the layers' code.
            if (IsBuildOutput(file)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match match in uses.Matches(line))
                    if (!InfrastructureForPresentation.Contains(match.Value))
                        found.Add($"{Path.GetRelativePath(Source, file)}:{i + 1}: {match.Value}");
            }
        }
        Assert.AreEqual(0, found.Count, string.Join(Environment.NewLine, found));
    }

    /// <summary>
    /// The files that may start a process themselves. Everything else starts one through them: as Administrator only a
    /// program nobody else can change, with the environment cleaned (ProcessRunner, ElevatedStart) - or as the user.
    /// </summary>
    private static readonly string[] MayStartProcesses =
    [
        @"DnnManager.Infrastructure\Processes\ProcessRunner.cs",
        @"DnnManager.Infrastructure\Processes\ElevatedStart.cs",
        // The update helper and DNN Manager's own restarts: its own exe, checked by its SHA-256 or its install folder.
        @"DnnManager.Infrastructure\Updates\UpdateHelper.cs",
        @"DnnManager.Presentation\AdminElevation.cs",
        @"DnnManager.Presentation\AppRestart.cs",
        @"DnnManager.Presentation\Services\AppUpdater.cs",
        // The same in the Avalonia app: its elevation and restarts (its own exe), and its update helper (checked by SHA-256).
        @"DnnManager.Desktop\Services\AppProcess.cs",
        @"DnnManager.Desktop\Services\AppUpdater.cs",
        // An editor started directly only while DNN Manager isn't elevated (as the user) - elevated, through ElevatedStart.
        @"DnnManager.Desktop\Views\Projects\IdeLocator.cs",
        // open / xdg-open: macOS and Linux only, where DNN Manager never runs elevated - Windows goes through ElevatedStart.Explorer.
        @"DnnManager.Desktop\Views\Projects\ProjectSupport.cs"
    ];

    [TestMethod]
    public void Processes_are_started_in_the_allowed_files_only()
    {
        var starts = new Regex(@"\bProcess\.Start\(|\bnew\s+ProcessStartInfo\b");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Source, "*.cs", SearchOption.AllDirectories))
        {
            // What a build generated there (obj, bin) isn't the layers' code.
            if (IsBuildOutput(file)) continue;
            var relative = Path.GetRelativePath(Source, file);
            if (relative.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || relative.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("DnnManager.Launcher", StringComparison.OrdinalIgnoreCase) ||
                MayStartProcesses.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal)) continue;
                if (starts.IsMatch(line)) found.Add($"{relative}:{i + 1}: {line}");
            }
        }
        Assert.AreEqual(0, found.Count, "Start it through ProcessRunner or ElevatedStart (as Administrator), or as the user:" +
                                        Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static bool IsBuildOutput(string file) =>
        Path.GetRelativePath(Source, file).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin");

    // Above the test's binaries (built in the repository) - or above this file, as the compiler saw it (built elsewhere).
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? "" })
            for (var dir = start.Length > 0 && Directory.Exists(start) ? new DirectoryInfo(start) : null; dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "DnnManager.csproj"))) return dir.FullName;
        throw new InvalidOperationException($"DnnManager.csproj not found above {AppContext.BaseDirectory} or {thisFile}.");
    }
}
