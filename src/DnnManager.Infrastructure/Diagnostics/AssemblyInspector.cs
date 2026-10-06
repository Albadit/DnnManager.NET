using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DnnManager.Infrastructure.Diagnostics;

/// <param name="File">Relative to the site: <c>bin\DotNetNuke.dll</c>.</param>
/// <param name="Loaded">
/// ASP.NET finds it: in bin, the probing path or a codeBase. False in a folder of its own under bin that .NET doesn't
/// look in (bin\Imageflow without a codeBase, bin\roslyn) - only what uses that folder loads it.
/// </param>
public sealed record BinAssembly(string File, string Name, Version Version, string? FileVersion, string? PublicKeyToken, DateTime Modified,
    bool Loaded = true)
{
    /// <summary>The folder it is in, relative to the site: <c>bin</c>, <c>bin\Imageflow</c>.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(File) ?? "";
}

/// <summary>Something wrong between the site's assemblies - what was found, in words.</summary>
/// <param name="Kind">"duplicate", "redirect", "codebase", "version" or "missing".</param>
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
/// Reads the site's assemblies - every DLL under bin, the folders web.config's probing path adds and the files its
/// codeBase entries point at - without loading them (their metadata only): versions, duplicates, binding redirects
/// that don't match what is there, codeBase files that aren't there, and references to assemblies at a version the
/// site doesn't have, or that are nowhere (the site, the GAC, the .NET Framework). What ASP.NET loads is checked
/// against what ASP.NET finds; an assembly in a folder of its own under bin (a module's) also finds what is next to it.
/// </summary>
public static class AssemblyInspector
{
    public static AssemblyInspection Inspect(string root, IReadOnlyList<BindingRedirect> redirects, IReadOnlyList<CodeBase> codeBases, string? probingPath)
    {
        var folders = new List<string> { "bin" };
        foreach (var extra in (probingPath ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!folders.Contains(extra, StringComparer.OrdinalIgnoreCase)) folders.Add(extra.TrimEnd('\\', '/'));

        var assemblies = new List<BinAssembly>();
        var references = new List<(BinAssembly From, string Name, Version Version, bool StrongNamed)>();
        var native = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string file, bool loaded)
        {
            if (!seen.Add(System.IO.Path.GetFullPath(file))) return;
            if (Read(file, System.IO.Path.GetRelativePath(root, file)) is not { } read)
            {
                native++;
                return;
            }
            var assembly = read.Assembly with { Loaded = loaded };
            assemblies.Add(assembly);
            references.AddRange(read.References.Select(r => (assembly, r.Name, r.Version, r.StrongNamed)));
        }

        var problems = new List<AssemblyProblem>();
        // A codeBase loads that version from its own file - one outside the probing path (bin\2sxc, bin\Imageflow) too.
        var codeBaseFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var codeBase in codeBases)
        {
            var file = CodeBasePath(root, codeBase.Href);
            if (file is not null && File.Exists(file))
            {
                Add(file, loaded: true);
                codeBaseFiles.Add(System.IO.Path.GetRelativePath(root, file));
            }
            else
                problems.Add(new("codebase", codeBase.Name,
                    $"web.config loads {codeBase.Name} {codeBase.Version} from {codeBase.Href}, which isn't there"));
        }

        foreach (var folder in folders)
        {
            var dir = System.IO.Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.dll")) Add(file, loaded: true);
        }

        // The rest of bin: folders of their own - a module's (bin\2sxc, bin\Imageflow, bin\DnnSharp), the compiler's
        // (bin\roslyn), native ones (bin\runtimes, bin\x64).
        var bin = System.IO.Path.Combine(root, "bin");
        if (Directory.Exists(bin))
            foreach (var file in Directory.EnumerateFiles(bin, "*.dll", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                Add(file, loaded: false);

        var byName = assemblies.Where(a => a.Loaded).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // The same assembly twice: which one loads depends on the probing order - unless web.config's codeBase entries
        // say which version comes from where.
        foreach (var (name, copies) in byName.Where(p => p.Value.Count(c => !codeBaseFiles.Contains(c.File)) > 1))
            problems.Add(new("duplicate", name, $"{name} is there {copies.Count} times: " +
                                                string.Join(", ", copies.Select(c => $"{c.File} ({c.Version})"))));

        // A redirect to a version bin doesn't have: that assembly can't load.
        foreach (var redirect in redirects)
        {
            if (!byName.TryGetValue(redirect.Name, out var copies) || !Version.TryParse(redirect.NewVersion, out var target)) continue;
            if (copies.Any(c => c.Version == target)) continue;
            problems.Add(new("redirect", redirect.Name,
                $"web.config redirects {redirect.Name} {redirect.OldVersion} to {redirect.NewVersion}, but the site has {Versions(copies)}"));
        }

        // A folder .NET doesn't look in - bin\Imageflow without a codeBase for it, say - that has the version a reference
        // wants: what web.config lacks, in words.
        string? Unreachable(string name, Version version) =>
            assemblies.FirstOrDefault(a => !a.Loaded && a.Version == version && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } other
                ? $"{other.Folder} has {version}, but web.config has no codeBase for it"
                : null;

        // An assembly in a folder of its own finds what is next to it: whatever loads it from there - the module, the
        // compiler - loads that too.
        var besides = assemblies.Where(a => !a.Loaded).Select(a => (a.Folder.ToLowerInvariant(), a.Name.ToLowerInvariant(), a.Version)).ToHashSet();
        references.RemoveAll(r => !r.From.Loaded && besides.Contains((r.From.Folder.ToLowerInvariant(), r.Name.ToLowerInvariant(), r.Version)));
        // A program's folder - bin\roslyn has csc.exe, the compiler ASP.NET starts: what its assemblies reference loads in
        // that program, never in the site.
        var programs = assemblies.Where(a => !a.Loaded).Select(a => a.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => Directory.EnumerateFiles(System.IO.Path.Combine(root, f), "*.exe", new EnumerationOptions { IgnoreInaccessible = true }).Any())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        references.RemoveAll(r => !r.From.Loaded && programs.Contains(r.From.Folder));

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
                    $"{by} reference {name} {version}, the site has {Versions(copies)} - " +
                    (Unreachable(name, version) ?? "no binding redirect between them")));
            }
            else if (!InFramework(name))
            {
                problems.Add(new("missing", name,
                    $"{by} reference {name} {version} - {Unreachable(name, version) ?? "not in bin, the GAC or the .NET Framework"}"));
            }
        }

        var newest = assemblies.MaxBy(a => a.Modified);
        return new AssemblyInspection(assemblies.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(), native, problems, newest);
    }

    /// <summary>"8.0.0.0 in bin, 9.0.0.0 in bin\2sxc".</summary>
    private static string Versions(IEnumerable<BinAssembly> copies) =>
        string.Join(", ", copies.Select(c => $"{c.Version} in {c.Folder}"));

    /// <summary>The file a codeBase's href names - relative to the site, or a file: URI; null when it is neither.</summary>
    private static string? CodeBasePath(string root, string href)
    {
        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
                return uri.IsFile ? uri.LocalPath : null;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, href.Replace('/', '\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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
