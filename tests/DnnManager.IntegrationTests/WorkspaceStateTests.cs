using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using DnnManager.Infrastructure.State;
using DnnManager.Presentation;
using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>
/// What DNN Manager keeps between starts: the state files (written whole, versioned, a bad one set aside), the form
/// drafts (never a password), the window's place.
/// </summary>
[TestClass]
public sealed class WorkspaceStateTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerStateTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a shell may still hold the folder for a moment */ }
    }

    /// <summary>A state file at format 2, whose format 1 called its name "oldName".</summary>
    public sealed class TestState : IStateFile
    {
        public static string FileName => "test.json";
        public static int CurrentFormat => 2;
        public int Format { get; set; }
        public string? Name { get; set; }
        public int Count { get; set; }

        public static JsonObject Upgrade(JsonObject file, int from)
        {
            if (from < 2 && file["oldName"] is { } old) file["name"] = old.GetValue<string>();
            return file;
        }
    }

    private string FileOf(string name) => Path.Combine(_dir, name);

    // ─── The store ────────────────────────────────────────────────────────

    [TestMethod]
    public void A_state_is_saved_whole_and_read_back_by_the_next_start()
    {
        Assert.AreEqual(0, new StateStore(_dir).Load<TestState>().Count, "No file yet: the defaults.");

        var store = new StateStore(_dir);
        Assert.IsTrue(store.Save(new TestState { Name = "mine", Count = 3 }));
        var written = File.GetLastWriteTimeUtc(FileOf("test.json"));
        Assert.IsFalse(store.Save(new TestState { Name = "mine", Count = 3 }), "Unchanged: not written again.");
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(FileOf("test.json")));
        Assert.IsFalse(File.Exists(FileOf("test.json.tmp")), "Nothing is left beside it.");

        var next = new StateStore(_dir).Load<TestState>();
        Assert.AreEqual("mine", next.Name);
        Assert.AreEqual(3, next.Count);
        Assert.AreEqual(2, next.Format);
    }

    [TestMethod]
    public void A_file_that_cant_be_read_is_set_aside_and_the_defaults_are_used()
    {
        foreach (var bad in new[] { "{ not json", "[1, 2]", """{ "format": 2, "count": "three" }""" })
        {
            File.WriteAllText(FileOf("test.json"), bad);
            var state = new StateStore(_dir).Load<TestState>();
            Assert.AreEqual(0, state.Count, bad);
            Assert.IsFalse(File.Exists(FileOf("test.json")), $"{bad}: set aside");
            Assert.AreEqual(bad, File.ReadAllText(FileOf("test.json.bad")), "Kept as it was, to look at.");
        }
        // The next save starts afresh.
        Assert.IsTrue(new StateStore(_dir).Save(new TestState { Count = 1 }));
        Assert.AreEqual(1, new StateStore(_dir).Load<TestState>().Count);
    }

    [TestMethod]
    public void A_newer_versions_file_is_ignored_and_left_alone()
    {
        const string newer = """{ "format": 3, "name": "from the future", "count": 9 }""";
        File.WriteAllText(FileOf("test.json"), newer);

        var state = new StateStore(_dir).Load<TestState>();

        Assert.IsNull(state.Name);
        Assert.AreEqual(newer, File.ReadAllText(FileOf("test.json")), "Not set aside - it is that version's.");
    }

    [TestMethod]
    public void An_older_versions_file_is_upgraded_as_it_is_read()
    {
        File.WriteAllText(FileOf("test.json"), """{ "format": 1, "oldName": "renamed since", "count": 4 }""");

        var state = new StateStore(_dir).Load<TestState>();

        Assert.AreEqual("renamed since", state.Name);
        Assert.AreEqual(4, state.Count);
        Assert.AreEqual(2, state.Format);
    }

    [TestMethod]
    public void A_state_that_cant_be_written_doesnt_stop_anything()
    {
        // The folder is a file: nothing can be written in it.
        var blocked = FileOf("blocked");
        File.WriteAllText(blocked, "");
        Assert.IsFalse(new StateStore(blocked).Save(new TestState { Count = 1 }));
        Assert.AreEqual(0, new StateStore(blocked).Load<TestState>().Count);
    }

    [TestMethod]
    public void A_factory_reset_leaves_no_state_behind()
    {
        var store = new StateStore(_dir);
        store.Save(new TestState { Count = 1 });
        File.WriteAllText(FileOf("old.json.bad"), "x");

        store.Clear();

        Assert.AreEqual(0, Directory.GetFiles(_dir).Length);
        Assert.IsTrue(store.Save(new TestState { Count = 1 }), "What was written before is forgotten too: the same state is written again.");
    }

    [TestMethod]
    public void The_workspace_files_keep_what_they_hold()
    {
        var store = new StateStore(_dir);
        store.Save(new WorkspaceState
        {
            Page = "Settings", SidebarPage = "Projects", SettingsCategory = "Sql", Project = "MyProject", ProjectTab = "Database",
            Projects = new ProjectsTableState { Search = "my", OnlyRunning = true, SortBy = "CpuSort", SortDescending = true, Expanded = ["MyProject"], Selected = "Other", ScrollOffset = 12 }
        });
        store.Save(new FormsState { Drafts = { ["Setup"] = new() { ["NameBox"] = "shop" } } });
        // Left by an earlier build (terminals aren't kept any more): removed, the rest untouched.
        File.WriteAllText(FileOf("terminals.json"), "{}");
        store.DeleteFile("terminals.json");

        var next = new StateStore(_dir);
        var workspace = next.Load<WorkspaceState>();
        Assert.AreEqual("Sql", workspace.SettingsCategory);
        Assert.AreEqual("Database", workspace.ProjectTab);
        Assert.AreEqual("CpuSort", workspace.Projects.SortBy);
        Assert.IsTrue(workspace.Projects.SortDescending && workspace.Projects.OnlyRunning);
        Assert.AreEqual(12, workspace.Projects.ScrollOffset);
        Assert.AreEqual("shop", next.Load<FormsState>().Drafts["Setup"]["NameBox"]);
        CollectionAssert.AreEquivalent(new[] { "workspace.json", "forms.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray(), "One file per area.");
    }

    // ─── Forms ────────────────────────────────────────────────────────────

    private sealed record Choice(string Label);

    private static (StackPanel Root, TextBox Name, CheckBox Keep, RadioButton B, ComboBox Language, ComboBox Version, TextBox Inner) Form(bool withVersions)
    {
        var name = new TextBox { Name = "NameBox" };
        var keep = new CheckBox { Name = "Keep" };
        var a = new RadioButton { Name = "A", GroupName = "g", IsChecked = true };
        var b = new RadioButton { Name = "B", GroupName = "g" };
        var language = new ComboBox { Name = "Language" };
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en-US" });
        language.Items.Add(new ComboBoxItem { Content = "Deutsch", Tag = "de-DE" });
        language.SelectedIndex = 0;
        var version = new ComboBox { Name = "Version", DisplayMemberPath = "Label" };
        if (withVersions) foreach (var v in new[] { "10.0.1", "9.13.9" }) version.Items.Add(new Choice(v));
        var inner = new TextBox { Name = "Inner" };
        var root = new StackPanel();
        foreach (var field in new System.Windows.UIElement[]
                 {
                     name, keep, a, b, language, version, new PasswordBox { Name = "Secret" }, new TextBox { Name = "Shown", IsReadOnly = true },
                     new TextBox { Text = "no name" }, new Border { Child = new StackPanel { Children = { inner } } }
                 })
            root.Children.Add(field);
        return (root, name, keep, b, language, version, inner);
    }

    [TestMethod]
    public void A_form_is_kept_by_field_name_without_its_passwords() => Sta(() =>
    {
        var form = Form(withVersions: true);
        form.Name.Text = "shop";
        form.Keep.IsChecked = true;
        form.B.IsChecked = true;
        form.Language.SelectedIndex = 1;
        form.Version.SelectedIndex = 1;
        form.Inner.Text = "deep";
        ((PasswordBox)form.Root.Children[6]).Password = "p@ss";

        var draft = FormDraft.Capture(form.Root);

        Assert.AreEqual("shop", draft["NameBox"]);
        Assert.AreEqual("true", draft["Keep"]);
        Assert.AreEqual("true", draft["B"]);
        Assert.AreEqual("de-DE", draft["Language"], "A list's item by its tag, not its position.");
        Assert.AreEqual("9.13.9", draft["Version"]);
        Assert.AreEqual("deep", draft["Inner"]);
        Assert.IsFalse(draft.ContainsKey("Secret"), "Never a password.");
        Assert.IsFalse(draft.ContainsKey("Shown"), "Not a read-only box.");
        Assert.IsFalse(draft.Values.Contains("p@ss"));
    });

    [TestMethod]
    public void A_form_is_put_back_even_when_its_lists_fill_later() => Sta(() =>
    {
        var draft = new Dictionary<string, string>
        {
            ["NameBox"] = "shop", ["Keep"] = "true", ["B"] = "true", ["Language"] = "de-DE", ["Version"] = "9.13.9", ["Inner"] = "deep", ["Gone"] = "x"
        };
        var form = Form(withVersions: false);

        FormDraft.Restore(form.Root, draft);
        Assert.AreEqual("shop", form.Name.Text);
        Assert.IsTrue(form.Keep.IsChecked);
        Assert.IsTrue(form.B.IsChecked);
        Assert.AreEqual("Deutsch", (form.Language.SelectedItem as ComboBoxItem)?.Content);
        Assert.AreEqual("deep", form.Inner.Text);
        Assert.IsNull(form.Version.SelectedItem, "Its versions aren't there yet.");

        // The versions arrive (from GitHub): the one that was chosen is chosen again.
        form.Version.Items.Add(new Choice("10.0.1"));
        form.Version.Items.Add(new Choice("9.13.9"));
        Assert.AreEqual("9.13.9", (form.Version.SelectedItem as Choice)?.Label);
    });

    // ─── The window ───────────────────────────────────────────────────────

    [TestMethod]
    public void A_window_place_off_the_screens_isnt_used()
    {
        var left = System.Windows.SystemParameters.VirtualScreenLeft;
        var top = System.Windows.SystemParameters.VirtualScreenTop;
        Assert.IsTrue(MainWindow.OnScreen(left + 100, top + 100, 900));
        Assert.IsFalse(MainWindow.OnScreen(left - 100_000, top, 900), "A monitor that isn't there any more.");
        Assert.IsFalse(MainWindow.OnScreen(left + 100, top - 100_000, 900));
        Assert.IsFalse(MainWindow.OnScreen(left + 100, top + 100, 50), "Too small to use.");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>WPF controls need a single-threaded apartment.</summary>
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
