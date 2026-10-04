using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.State;
using DnnManager.Presentation;
using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>
/// What DNN Manager keeps between starts: the workspace as rows in its database (a value that can't be read keeps its
/// default), the form drafts (never a password), the window's place.
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

    /// <summary>An area of the workspace, with a list and a dictionary of its own.</summary>
    public sealed class TestState : IStateFile
    {
        public static string Area => "test";
        public string? Name { get; set; }
        public int Count { get; set; }
        public List<string> Items { get; set; } = ["default"];
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private AppDatabase Database => new(_dir);

    private Dictionary<string, string> RowsOf(string area)
    {
        using var connection = Database.Open();
        return AppDatabase.KeyValues(connection, "SELECT key, value FROM state WHERE area = $area", ("$area", area));
    }

    private void WriteRow(string area, string key, string value)
    {
        using var connection = Database.Open();
        AppDatabase.Execute(connection, "INSERT OR REPLACE INTO state (area, key, value) VALUES ($area, $key, $value)",
            ("$area", area), ("$key", key), ("$value", value));
    }

    // ─── The store ────────────────────────────────────────────────────────

    [TestMethod]
    public void A_state_is_saved_as_rows_and_read_back_by_the_next_start()
    {
        Assert.AreEqual(0, new StateStore(Database).Load<TestState>().Count, "Nothing saved yet: the defaults.");

        var store = new StateStore(Database);
        Assert.IsTrue(store.Save(new TestState { Name = "mine", Count = 3, Items = ["a", "b"], Values = { ["Key.With Dots"] = "x" } }));
        Assert.IsFalse(store.Save(new TestState { Name = "mine", Count = 3, Items = ["a", "b"], Values = { ["Key.With Dots"] = "x" } }),
            "Unchanged: not written again.");
        var rows = RowsOf("test");
        Assert.AreEqual("mine", rows["name"]);
        Assert.AreEqual("3", rows["count"]);
        Assert.AreEqual("2", rows["items"], "A list: how many, then each.");
        Assert.AreEqual("b", rows["items[1]"]);
        Assert.AreEqual("x", rows["values{Key.With%20Dots}"]);
        CollectionAssert.AreEquivalent(new[] { AppDatabase.FileName }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray(),
            "One file, nothing left beside it.");

        var next = new StateStore(Database).Load<TestState>();
        Assert.AreEqual("mine", next.Name);
        Assert.AreEqual(3, next.Count);
        CollectionAssert.AreEqual(new[] { "a", "b" }, next.Items);
        Assert.AreEqual("x", next.Values["key.with dots"], "The dictionary keeps its comparer.");
    }

    [TestMethod]
    public void A_value_that_cant_be_read_keeps_its_default_and_the_rest_is_read()
    {
        WriteRow("test", "name", "kept");
        WriteRow("test", "count", "three");

        var state = new StateStore(Database).Load<TestState>();

        Assert.AreEqual("kept", state.Name);
        Assert.AreEqual(0, state.Count);
        CollectionAssert.AreEqual(new[] { "default" }, state.Items, "Without a row: its default.");
    }

    [TestMethod]
    public void A_state_that_cant_be_written_doesnt_stop_anything()
    {
        // The folder is a file: the database can't be made in it.
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "");
        Assert.IsFalse(new StateStore(new AppDatabase(blocked)).Save(new TestState { Count = 1 }));
        Assert.AreEqual(0, new StateStore(new AppDatabase(blocked)).Load<TestState>().Count);
    }

    [TestMethod]
    public void A_factory_reset_leaves_no_state_behind()
    {
        var store = new StateStore(Database);
        store.Save(new TestState { Count = 1 });
        WriteRow("old", "x", "1");
        using (var connection = Database.Open())
            AppDatabase.Execute(connection, "INSERT INTO settings (key, value) VALUES ('appearance.theme', 'dark')");

        store.Clear();

        using (var connection = Database.Open())
        {
            Assert.AreEqual(0L, AppDatabase.Scalar<long>(connection, "SELECT COUNT(*) FROM state"));
            Assert.AreEqual(1L, AppDatabase.Scalar<long>(connection, "SELECT COUNT(*) FROM settings"), "The settings aren't the workspace's.");
        }
        Assert.IsTrue(store.Save(new TestState { Count = 1 }), "What was written before is forgotten too: the same state is written again.");
    }

    [TestMethod]
    public void The_workspace_areas_keep_what_they_hold()
    {
        var store = new StateStore(Database);
        store.Save(new WorkspaceState
        {
            Page = "Settings", SidebarPage = "Projects", SettingsCategory = "Sql", Project = "MyProject", ProjectTab = "Database",
            Projects = new ProjectsTableState { Search = "my", OnlyRunning = true, SortBy = "CpuSort", SortDescending = true, Expanded = ["MyProject"], Selected = "Other", ScrollOffset = 12.5 }
        });
        store.Save(new FormsState { Drafts = { ["Setup"] = new() { ["NameBox"] = "shop" } } });
        store.Save(new WindowLayout { Left = 10.5, Width = 1200, Maximized = true });

        var next = new StateStore(Database);
        var workspace = next.Load<WorkspaceState>();
        Assert.AreEqual("Sql", workspace.SettingsCategory);
        Assert.AreEqual("Database", workspace.ProjectTab);
        Assert.AreEqual("CpuSort", workspace.Projects.SortBy);
        Assert.IsTrue(workspace.Projects.SortDescending && workspace.Projects.OnlyRunning);
        Assert.AreEqual(12.5, workspace.Projects.ScrollOffset);
        CollectionAssert.AreEqual(new[] { "MyProject" }, workspace.Projects.Expanded);
        Assert.AreEqual("shop", next.Load<FormsState>().Drafts["setup"]["NameBox"], "The pages' drafts are found ignoring case.");
        var window = next.Load<WindowLayout>();
        Assert.AreEqual(10.5, window.Left);
        Assert.IsNull(window.Top, "Not saved: still none.");
        Assert.IsTrue(window.Maximized);
        using var connection = Database.Open();
        CollectionAssert.AreEquivalent(new[] { "forms", "window", "workspace" },
            AppDatabase.KeyValues(connection, "SELECT DISTINCT area, area FROM state").Keys.ToArray(), "One area each.");
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
