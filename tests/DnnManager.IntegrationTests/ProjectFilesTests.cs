using System.Text;
using DnnManager.Application.Deployment;
using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Source Control's Explorer and Search over a project's folder: what they list (the server's ignore rules apply, as to
/// a push), what Search finds with each toggle, and Replace All - which writes a file back as it was written.
/// </summary>
[TestClass]
public sealed class ProjectFilesTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = TestEnvironment.NewRunDirectory();
        Write("Portals/_default/Skins/Site/home.ascx", "<div class=\"Logo\">\r\n  <%= logo %>\r\n</div>\r\n");
        Write("js/site.js", "var logo = 1;\nvar logos = 2;\nlogo++;\n");
        Write("App_Data/Search/index.txt", "logo");
        Write("bin/x.dll", "logo");
        Write(".git/config", "logo");
        Write("readme.md", "Logo\tlogo LOGO");
    }

    [TestCleanup]
    public void Cleanup() => TestEnvironment.DeleteDirectory(_root);

    private void Write(string path, string text, Encoding? encoding = null)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, encoding ?? new UTF8Encoding(false));
    }

    private static IgnoreRules Rules => IgnoreRules.Parse("App_Data/Search/");

    [TestMethod]
    public void The_explorer_lists_folders_first_and_leaves_out_what_is_ignored()
    {
        var top = ProjectFiles.List(_root, "", Rules).Select(e => e.Name).ToList();
        CollectionAssert.AreEqual(new[] { "App_Data", "bin", "js", "Portals", "readme.md" }, top, ".git is never listed.");
        Assert.AreEqual(0, ProjectFiles.List(_root, "App_Data", Rules).Count, "Its only folder is ignored.");
        Assert.AreEqual("js/site.js", ProjectFiles.List(_root, "js", Rules).Single().Path);
        CollectionAssert.AreEquivalent(new[] { "Portals/_default/Skins/Site/home.ascx", "js/site.js", "bin/x.dll", "readme.md" },
            ProjectFiles.All(_root, Rules, default).ToList());
    }

    [TestMethod]
    public void Search_finds_each_match_with_its_line_and_the_toggles_narrow_it()
    {
        var any = ProjectFiles.Find(_root, Rules, new FindQuery("logo"), default);
        // Not in the ignored index, the .dll or .git; three in readme.md, three in site.js, two in home.ascx.
        Assert.AreEqual(8, any.MatchCount);
        CollectionAssert.AreEqual(new[] { "js/site.js", "Portals/_default/Skins/Site/home.ascx", "readme.md" }, any.Files.Select(f => f.Path).ToList());
        var ascx = any.Files[1].Matches;
        Assert.AreEqual((1, 12), (ascx[0].Line, ascx[0].Start));
        Assert.AreEqual((2, 6), (ascx[1].Line, ascx[1].Start));
        Assert.AreEqual("<%=", ascx[1].Before.Trim());
        Assert.AreEqual("logo", ascx[1].Text);

        Assert.AreEqual(5, ProjectFiles.Find(_root, Rules, new FindQuery("logo", MatchCase: true), default).MatchCount);
        // Not "logos"; "Logo" before a quote is a word.
        Assert.AreEqual(7, ProjectFiles.Find(_root, Rules, new FindQuery("logo", WholeWord: true), default).MatchCount);
        // Each line on its own: ^ is a line's start.
        Assert.AreEqual(3, ProjectFiles.Find(_root, Rules, new FindQuery(@"^logo|logos\b", UseRegex: true), default).MatchCount);

        Assert.IsNull(new FindQuery("(", UseRegex: true).ToRegex(out var problem));
        Assert.IsNotNull(problem, "A broken pattern says why.");
    }

    [TestMethod]
    public void Replace_all_keeps_each_files_encoding_bom_and_line_endings()
    {
        Write("bom.css", ".logo {}\r\n.logo2 {}\n", new UTF8Encoding(true));
        Write("latin.txt", "café logo\r\n", Encoding.Latin1);
        var query = new FindQuery("logo", MatchCase: true);
        var found = ProjectFiles.Find(_root, Rules, query, default);

        var done = ProjectFiles.Replace(_root, found.Files.Select(f => f.Path), query, "brand", default);

        Assert.AreEqual(0, done.Failed.Count, string.Join("; ", done.Failed));
        Assert.AreEqual(found.MatchCount, done.Replacements);
        var bom = File.ReadAllBytes(Path.Combine(_root, "bom.css"));
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bom[..3], "The byte order mark stays.");
        Assert.AreEqual(".brand {}\r\n.brand2 {}\n", Encoding.UTF8.GetString(bom[3..]), "Mixed line endings stay as they were.");
        Assert.AreEqual("café brand\r\n", File.ReadAllText(Path.Combine(_root, "latin.txt"), Encoding.Latin1), "Still Latin-1, é one byte.");
        Assert.AreEqual("var brand = 1;\nvar brands = 2;\nbrand++;\n", File.ReadAllText(Path.Combine(_root, "js/site.js")));
        Assert.IsFalse(Directory.EnumerateFiles(_root, "*.dnnmgr-replace", SearchOption.AllDirectories).Any(), "No file left beside them.");
    }

    [TestMethod]
    public void Replace_with_a_regular_expression_uses_its_groups_and_never_leaves_the_project()
    {
        var query = new FindQuery(@"var (\w+) =", UseRegex: true);
        ProjectFiles.Replace(_root, ["js/site.js"], query, "let $1 =", default);
        Assert.AreEqual("let logo = 1;\nlet logos = 2;\nlogo++;\n", File.ReadAllText(Path.Combine(_root, "js/site.js")));

        var outside = ProjectFiles.Replace(_root, ["../escape.txt"], new FindQuery("x"), "y", default);
        Assert.AreEqual(1, outside.Failed.Count, "A path outside the project's folder isn't written.");
    }
}
