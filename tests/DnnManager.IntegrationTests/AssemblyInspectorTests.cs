using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DnnManager.Infrastructure.Diagnostics;

namespace DnnManager.IntegrationTests;

/// <summary>
/// A site's Assemblies check: what web.config's runtime section says (binding redirects, codeBase, the probing path) and
/// what is found with it - on a site of tiny strong-named assemblies written here, which only have metadata.
/// </summary>
[TestClass]
public sealed class AssemblyInspectorTests
{
    private const string Asm = "urn:schemas-microsoft-com:asm.v1";
    private static readonly byte[] PublicKey = Enumerable.Range(0, 160).Select(i => (byte)i).ToArray();
    private static readonly byte[] Token = [1, 2, 3, 4, 5, 6, 7, 8];
    private string _site = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_site = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "assemblies-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_site, recursive: true); }
        catch (IOException) { /* still open */ }
    }

    // Like 2sxc and Imageflow: App references Lib 2.0 and Extra 1.0, which are in bin\Imageflow (not on the probing
    // path) - web.config's codeBase entries load them from there, next to Lib 1.0 in bin.
    private const string Runtime =
        "<runtime>" +
        $"<assemblyBinding xmlns=\"{Asm}\"><probing privatePath=\"bin;bin\\Providers\" /></assemblyBinding>" +
        $"<assemblyBinding xmlns=\"{Asm}\">" +
        "<dependentAssembly><assemblyIdentity name=\"Lib\" publicKeyToken=\"0102030405060708\" />" +
        "<bindingRedirect oldVersion=\"0.0.0.0-1.0.0.0\" newVersion=\"1.0.0.0\" />" +
        "<bindingRedirect oldVersion=\"2.0.0.0-32767.32767.32767.32767\" newVersion=\"2.0.0.0\" />" +
        "<codeBase version=\"2.0.0.0\" href=\"bin\\Imageflow\\Lib.dll\" /></dependentAssembly>" +
        "<dependentAssembly><assemblyIdentity name=\"Extra\" publicKeyToken=\"0102030405060708\" />" +
        "<codeBase version=\"1.0.0.0\" href=\"bin\\Imageflow\\Extra.dll\" /></dependentAssembly>" +
        "</assemblyBinding></runtime>";

    private void Site(string runtime)
    {
        File.WriteAllText(Path.Combine(_site, "web.config"), $"<configuration>{runtime}</configuration>");
        Directory.CreateDirectory(Path.Combine(_site, @"bin\Imageflow"));
        WriteAssembly(@"bin\App.dll", "App", new(1, 0, 0, 0), ("Lib", new(2, 0, 0, 0)), ("Extra", new(1, 0, 0, 0)));
        WriteAssembly(@"bin\Lib.dll", "Lib", new(1, 0, 0, 0));
        WriteAssembly(@"bin\Imageflow\Lib.dll", "Lib", new(2, 0, 0, 0));
        WriteAssembly(@"bin\Imageflow\Extra.dll", "Extra", new(1, 0, 0, 0));
    }

    private AssemblyInspection Inspect()
    {
        var config = WebConfigInspector.Inspect(_site);
        return AssemblyInspector.Inspect(_site, config.BindingRedirects, config.CodeBases, config.ProbingPath);
    }

    [TestMethod]
    public void Web_config_runtime_is_read_from_every_assemblyBinding_with_every_redirect_and_codeBase()
    {
        Site(Runtime);
        var config = WebConfigInspector.Inspect(_site);
        Assert.AreEqual(@"bin;bin\Providers", config.ProbingPath);
        CollectionAssert.AreEqual(new[] { "0.0.0.0-1.0.0.0", "2.0.0.0-32767.32767.32767.32767" },
            config.BindingRedirects.Where(r => r.Name == "Lib").Select(r => r.OldVersion).ToList());
        CollectionAssert.AreEqual(new[] { @"Lib 2.0.0.0 bin\Imageflow\Lib.dll", @"Extra 1.0.0.0 bin\Imageflow\Extra.dll" },
            config.CodeBases.Select(c => $"{c.Name} {c.Version} {c.Href}").ToList());
    }

    [TestMethod]
    public void Assemblies_web_config_loads_from_another_folder_are_neither_missing_nor_conflicts()
    {
        Site(Runtime);
        var inspection = Inspect();
        Assert.AreEqual(0, inspection.Problems.Count, string.Join(Environment.NewLine, inspection.Problems.Select(p => p.Text)));
        Assert.AreEqual(4, inspection.Assemblies.Count);
    }

    [TestMethod]
    public void Without_the_codeBase_entries_they_are_missing_and_where_they_are_is_said()
    {
        Site(Runtime.Replace("codeBase", "ignored"));
        var problems = Inspect().Problems;
        var missing = problems.Single(p => p is { Kind: "missing", Assembly: "Extra" });
        StringAssert.EndsWith(missing.Text, @"bin\Imageflow has 1.0.0.0, but web.config has no codeBase for it");
        Assert.IsTrue(problems.Any(p => p is { Kind: "redirect", Assembly: "Lib" }));
    }

    [TestMethod]
    public void Bin_folders_of_their_own_are_read_and_their_assemblies_find_what_is_next_to_them()
    {
        Site(Runtime);
        // A module's folder: its assembly references the one next to it, and has its own copy of Lib.
        Directory.CreateDirectory(Path.Combine(_site, @"bin\Module"));
        WriteAssembly(@"bin\Module\Module.dll", "Module", new(1, 0, 0, 0), ("Helper", new(3, 0, 0, 0)), ("Gone", new(1, 0, 0, 0)));
        WriteAssembly(@"bin\Module\Helper.dll", "Helper", new(3, 0, 0, 0));
        WriteAssembly(@"bin\Module\Lib.dll", "Lib", new(1, 0, 0, 0));
        // A program's folder, like bin\roslyn: what it references loads in that program.
        Directory.CreateDirectory(Path.Combine(_site, @"bin\roslyn"));
        File.WriteAllText(Path.Combine(_site, @"bin\roslyn\csc.exe"), "");
        WriteAssembly(@"bin\roslyn\Compiler.dll", "Compiler", new(4, 0, 0, 0), ("Microsoft.Build.Tasks.Core", new(15, 1, 0, 0)));

        var inspection = Inspect();
        CollectionAssert.AreEquivalent(new[] { @"bin\Module\Module.dll", @"bin\Module\Helper.dll", @"bin\Module\Lib.dll", @"bin\roslyn\Compiler.dll" },
            inspection.Assemblies.Where(a => !a.Loaded).Select(a => a.File).ToList());
        Assert.AreEqual(8, inspection.Assemblies.Count);
        // Only what is really missing - not the module's Helper, not the compiler's references, no duplicate Lib.
        Assert.AreEqual("Gone", inspection.Problems.Single().Assembly, string.Join(Environment.NewLine, inspection.Problems.Select(p => p.Text)));
    }

    [TestMethod]
    public void A_codeBase_whose_file_is_not_there_is_reported()
    {
        Site(Runtime);
        File.Delete(Path.Combine(_site, @"bin\Imageflow\Extra.dll"));
        var problem = Inspect().Problems.Single(p => p.Kind == "codebase");
        Assert.AreEqual("Extra", problem.Assembly);
        StringAssert.Contains(problem.Text, @"bin\Imageflow\Extra.dll");
    }

    /// <summary>An assembly with only metadata: its name, version and public key, and references to others.</summary>
    private void WriteAssembly(string file, string name, Version version, params (string Name, Version Version)[] references)
    {
        var md = new MetadataBuilder();
        md.AddModule(0, md.GetOrAddString(name + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        md.AddAssembly(md.GetOrAddString(name), version, default, md.GetOrAddBlob(PublicKey), AssemblyFlags.PublicKey, AssemblyHashAlgorithm.Sha1);
        foreach (var reference in references)
            md.AddAssemblyReference(md.GetOrAddString(reference.Name), reference.Version, default, md.GetOrAddBlob(Token), default, default);
        md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(md), new BlobBuilder()).Serialize(image);
        File.WriteAllBytes(Path.Combine(_site, file), image.ToArray());
    }
}
