using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DnnManager.Infrastructure.Diagnostics;

/// <param name="File">Relative to the site: <c>bin\DotNetNuke.dll</c>.</param>
public sealed record BinAssembly(string File, string Name, Version Version, string? FileVersion, string? PublicKeyToken, DateTime Modified);

/// <summary>Something wrong between the site's assemblies - what was found, in words.</summary>
/// <param name="Kind">"duplicate", "redirect", "version" or "missing".</param>
public sealed record AssemblyProblem(string Kind, string Assembly, string Text);

public sealed record AssemblyInspection(
    IReadOnlyList<BinAssembly> Assemblies,
    int NativeFiles,
    IReadOnlyList<AssemblyProblem> Problems,
    BinAssembly? Newest)
{
    public static readonly AssemblyInspection None = new([], 0, [], null);
}

/// <summary>
/// Reads the assemblies ASP.NET loads for the site - bin and the folders web.config's probing path adds - without
/// loading them (their metadata only): versions, duplicates, binding redirects that don't match what is there, and
/// references to assemblies at a version bin doesn't have, or that are nowhere (bin, the GAC, the .NET Framework).
/// </summary>
public static class AssemblyInspector
{
    public static AssemblyInspection Inspect(string root, IReadOnlyList<BindingRedirect> redirects, string? probingPath)
    {
        var folders = new List<string> { "bin" };
        foreach (var extra in (probingPath ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!folders.Contains(extra, StringComparer.OrdinalIgnoreCase)) folders.Add(extra.TrimEnd('\\', '/'));

        var assemblies = new List<BinAssembly>();
        var references = new List<(BinAssembly From, string Name, Version Version, bool StrongNamed)>();
        var native = 0;
        foreach (var folder in folders)
        {
            var dir = System.IO.Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var read = Read(file, System.IO.Path.GetRelativePath(root, file));
                if (read is null)
                {
                    native++;
                    continue;
                }
                assemblies.Add(read.Value.Assembly);
                references.AddRange(read.Value.References.Select(r => (read.Value.Assembly, r.Name, r.Version, r.StrongNamed)));
            }
        }

        var problems = new List<AssemblyProblem>();
        var byName = assemblies.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // The same assembly twice: which one loads depends on the probing order.
        foreach (var (name, copies) in byName.Where(p => p.Value.Count > 1))
            problems.Add(new("duplicate", name, $"{name} is there {copies.Count} times: " +
                                                string.Join(", ", copies.Select(c => $"{c.File} ({c.Version})"))));

        // A redirect to a version bin doesn't have: that assembly can't load.
        foreach (var redirect in redirects)
        {
            if (!byName.TryGetValue(redirect.Name, out var copies) || !Version.TryParse(redirect.NewVersion, out var target)) continue;
            if (copies.Any(c => c.Version == target)) continue;
            problems.Add(new("redirect", redirect.Name,
                $"web.config redirects {redirect.Name} {redirect.OldVersion} to {redirect.NewVersion}, but bin has {string.Join(", ", copies.Select(c => c.Version))}"));
        }

        // References: to another version of a strong-named assembly with no redirect bridging them, or to one nowhere.
        foreach (var group in references.GroupBy(r => (r.Name.ToLowerInvariant(), r.Version)))
        {
            var (name, version) = (group.First().Name, group.Key.Version);
            var by = string.Join(", ", group.Select(r => r.From.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)) +
                     (group.Select(r => r.From.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 4 ? "…" : "");
            if (byName.TryGetValue(name, out var copies))
            {
                if (!group.First().StrongNamed || copies.Any(c => c.Version == version)) continue;
                // A redirect covers it: right, or reported above as pointing at a version bin doesn't have.
                if (redirects.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && InRange(r.OldVersion, version))) continue;
                problems.Add(new("version", name,
                    $"{by} reference {name} {version}, bin has {string.Join(", ", copies.Select(c => c.Version))} - no binding redirect between them"));
            }
            else if (!InFramework(name))
            {
                problems.Add(new("missing", name, $"{by} reference {name} {version} - not in bin, the GAC or the .NET Framework"));
            }
        }

        var newest = assemblies.MaxBy(a => a.Modified);
        return new AssemblyInspection(assemblies.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(), native, problems, newest);
    }

    private static bool InRange(string range, Version version)
    {
        var parts = range.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!Version.TryParse(parts[0], out var low)) return false;
        if (parts.Length == 1) return version == low;
        return Version.TryParse(parts[1], out var high) && version >= low && version <= high;
    }

    private static (BinAssembly Assembly, List<(string Name, Version Version, bool StrongNamed)> References)? Read(string path, string relative)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            var definition = md.GetAssemblyDefinition();
            var key = definition.PublicKey.IsNil ? null : Token(md.GetBlobBytes(definition.PublicKey));
            string? fileVersion = null;
            try { fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { /* none */ }
            var assembly = new BinAssembly(relative, md.GetString(definition.Name), definition.Version, fileVersion, key, File.GetLastWriteTime(path));
            var references = md.AssemblyReferences.Select(h => md.GetAssemblyReference(h))
                .Select(r => (md.GetString(r.Name), r.Version, !r.PublicKeyOrToken.IsNil))
                .ToList();
            return (assembly, references);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The public key token of a public key: the last 8 bytes of its SHA-1, reversed.</summary>
    private static string Token(byte[] publicKey)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(publicKey);
        return Convert.ToHexString(hash[^8..].Reverse().ToArray()).ToLowerInvariant();
    }

    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // Where .NET Framework finds what isn't in bin: the GAC (both of them), and the framework's own folder.
    private static readonly string[] FrameworkFolders =
    [
        System.IO.Path.Combine(Windows, @"Microsoft.NET\assembly\GAC_MSIL"),
        System.IO.Path.Combine(Windows, @"Microsoft.NET\assembly\GAC_64"),
        System.IO.Path.Combine(Windows, @"Microsoft.NET\assembly\GAC_32"),
        System.IO.Path.Combine(Windows, @"assembly\GAC_MSIL"),
        System.IO.Path.Combine(Windows, @"assembly\GAC"),
    ];

    private static readonly string[] FrameworkDirectories =
    [
        System.IO.Path.Combine(Windows, @"Microsoft.NET\Framework64\v4.0.30319"),
        System.IO.Path.Combine(Windows, @"Microsoft.NET\Framework\v4.0.30319"),
    ];

    private static bool InFramework(string name) =>
        FrameworkFolders.Any(f => Directory.Exists(System.IO.Path.Combine(f, name))) ||
        FrameworkDirectories.Any(d => File.Exists(System.IO.Path.Combine(d, name + ".dll")) ||
                                      File.Exists(System.IO.Path.Combine(d, "WPF", name + ".dll")));
}
