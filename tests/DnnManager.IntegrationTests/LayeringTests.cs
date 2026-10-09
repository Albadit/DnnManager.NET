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
    [DataRow("DnnManager.Domain", "Application", "Infrastructure", "Presentation")]
    [DataRow("DnnManager.Application", "Infrastructure", "Presentation")]
    [DataRow("DnnManager.Infrastructure", "Presentation")]
    public void A_layer_uses_none_above_it(string layer, params string[] above)
    {
        var uses = new Regex($@"\bDnnManager\.({string.Join('|', above)})\b");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Source, layer), "*.cs", SearchOption.AllDirectories))
        {
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

    // Above the test's binaries (built in the repository) - or above this file, as the compiler saw it (built elsewhere).
    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? "" })
            for (var dir = start.Length > 0 && Directory.Exists(start) ? new DirectoryInfo(start) : null; dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "DnnManager.csproj"))) return dir.FullName;
        throw new InvalidOperationException($"DnnManager.csproj not found above {AppContext.BaseDirectory} or {thisFile}.");
    }
}
