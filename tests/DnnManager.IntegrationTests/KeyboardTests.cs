using System.Text.Json.Nodes;
using System.Windows.Input;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>The keyboard: shortcuts as they are written and allowed, kept in settings.json, and the palette's search.</summary>
[TestClass]
public sealed class KeyboardTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerKeyboardTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* nothing holds it - best effort */ }
    }

    // ─── Shortcuts ────────────────────────────────────────────────────────

    [TestMethod]
    public void Shortcuts_are_written_as_VS_Code_writes_them()
    {
        foreach (var text in new[] { "Ctrl+Shift+P", "Ctrl+,", "Ctrl+`", "Ctrl+Shift+`", "Shift+F5", "F5", "Ctrl+1", "Ctrl+PageDown", "Alt+Enter", "Ctrl+Shift+Tab" })
            Assert.AreEqual(text, Shortcut.Parse(text)?.ToString(), text);

        Assert.AreEqual(new Shortcut(ModifierKeys.Control, Key.OemComma), Shortcut.Parse("ctrl+,"));
        Assert.AreEqual(new Shortcut(ModifierKeys.Control | ModifierKeys.Shift, Key.P), Shortcut.Parse(" Shift + Ctrl + p "));
        Assert.AreEqual(new Shortcut(ModifierKeys.Control, Key.OemPlus), Shortcut.Parse("Ctrl+="));
        foreach (var bad in new[] { "", "Ctrl+", "Ctrl+NoSuchKey", "Hyper+P", "Ctrl+Shift" })
            Assert.IsNull(Shortcut.Parse(bad), bad);
    }

    [TestMethod]
    public void Plain_keys_are_left_for_typing_and_moving_around()
    {
        foreach (var plain in new[] { "P", "Shift+P", "Enter", "Space", "Tab", "Down" })
            Assert.IsNotNull(Shortcut.Parse(plain)?.Problem, plain);
        foreach (var allowed in new[] { "Ctrl+P", "Alt+P", "F5", "Shift+F12", "Ctrl+Enter" })
            Assert.IsNull(Shortcut.Parse(allowed)?.Problem, allowed);

        Assert.IsTrue(Shortcut.Parse("Ctrl+C")!.Value.IsEditingKey);
        Assert.IsFalse(Shortcut.Parse("Ctrl+Shift+C")!.Value.IsEditingKey);
    }

    // ─── Kept in settings.json ────────────────────────────────────────────

    private (AppCommands Commands, SettingsStore Store) Make()
    {
        var store = new SettingsStore(new AppDataPaths(_dir));
        var commands = new AppCommands(store, Options.Create(new AppOptions()), NullLogger<AppCommands>.Instance);
        commands.Add(new AppCommand { Id = "a.first", Title = "First", Area = "Test", DefaultShortcut = "Ctrl+1" });
        commands.Add(new AppCommand { Id = "a.second", Title = "Second", Area = "Test", DefaultShortcut = "Ctrl+2" });
        commands.Add(new AppCommand { Id = "a.none", Title = "None", Area = "Test" });
        return (commands, store);
    }

    private JsonObject? SavedShortcuts() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "settings.json")))?["keyboard"]?["shortcuts"] as JsonObject;

    [TestMethod]
    public void A_changed_shortcut_is_saved_and_a_reset_one_leaves_the_file()
    {
        var (commands, _) = Make();
        var second = commands.Find("a.second")!;

        Assert.IsNull(commands.Set(second, Shortcut.Parse("Ctrl+Shift+9")));
        Assert.AreEqual("Ctrl+Shift+9", commands.ShortcutOf(second).ToString());
        Assert.IsTrue(commands.IsCustom(second));
        Assert.AreEqual("Ctrl+Shift+9", (string?)SavedShortcuts()?["a.second"]);
        Assert.AreEqual(second, commands.Match(Shortcut.Parse("Ctrl+Shift+9")!.Value));
        Assert.IsNull(commands.Match(Shortcut.Parse("Ctrl+2")!.Value), "Its old shortcut does nothing now.");

        Assert.IsNull(commands.Reset(second));
        Assert.AreEqual("Ctrl+2", commands.ShortcutOf(second).ToString());
        Assert.IsFalse(SavedShortcuts()!.ContainsKey("a.second"), "Only what differs from the defaults is in the file.");
    }

    [TestMethod]
    public void A_buttons_tooltip_names_the_shortcut_as_it_is_now()
    {
        var (commands, _) = Make();
        var before = CommandTip.Commands;
        CommandTip.Commands = commands;
        try
        {
            Assert.AreEqual("Do the first (Ctrl+1)", CommandTip.With("Do the first", "a.first"));
            commands.Set(commands.Find("a.first")!, Shortcut.Parse("Ctrl+Shift+9"));
            Assert.AreEqual("Do the first (Ctrl+Shift+9)", CommandTip.With("Do the first", "a.first"), "A changed shortcut shows at once.");
            commands.Set(commands.Find("a.first")!, null);
            Assert.AreEqual("Do the first", CommandTip.With("Do the first", "a.first"), "Taken away: no shortcut named.");
            Assert.AreEqual("Nothing", CommandTip.With("Nothing", "a.none"));
            Assert.AreEqual("Unknown", CommandTip.With("Unknown", "no.such.command"));
        }
        finally
        {
            CommandTip.Commands = before;
        }
    }

    [TestMethod]
    public void A_shortcut_can_be_taken_away_and_a_default_chosen_again_isnt_kept()
    {
        var (commands, _) = Make();
        var first = commands.Find("a.first")!;

        Assert.IsNull(commands.Set(first, null));
        Assert.IsNull(commands.ShortcutOf(first));
        Assert.AreEqual("", (string?)SavedShortcuts()?["a.first"], "None is kept as an empty one.");

        Assert.IsNull(commands.Set(first, Shortcut.Parse("Ctrl+1")));
        Assert.IsFalse(commands.IsCustom(first), "Its default again - nothing to keep.");
        Assert.IsFalse(SavedShortcuts()!.ContainsKey("a.first"));
    }

    [TestMethod]
    public void A_plain_key_is_refused_and_nothing_is_saved()
    {
        var (commands, _) = Make();
        var problem = commands.Set(commands.Find("a.first")!, Shortcut.Parse("P"));
        Assert.IsNotNull(problem);
        Assert.AreEqual("Ctrl+1", commands.ShortcutOf(commands.Find("a.first")!).ToString());
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "settings.json")));
    }

    [TestMethod]
    public void Two_commands_on_one_shortcut_are_a_conflict_and_the_first_listed_runs()
    {
        var (commands, _) = Make();
        var first = commands.Find("a.first")!;
        var second = commands.Find("a.second")!;
        commands.Set(second, Shortcut.Parse("Ctrl+1"));

        CollectionAssert.AreEqual(new[] { second }, commands.ConflictsOf(first).ToList());
        CollectionAssert.AreEqual(new[] { first }, commands.ConflictsOf(second).ToList());
        Assert.AreEqual(first, commands.Match(Shortcut.Parse("Ctrl+1")!.Value));
        Assert.AreEqual(0, commands.ConflictsOf(commands.Find("a.none")!).Count);

        commands.ResetAll();
        Assert.AreEqual(0, commands.ConflictsOf(first).Count);
        Assert.AreEqual(0, SavedShortcuts()!.Count);
    }

    [TestMethod]
    public void Shortcuts_from_settings_json_are_what_the_next_start_uses()
    {
        var (commands, store) = Make();
        commands.Set(commands.Find("a.first")!, Shortcut.Parse("Alt+F1"));

        // The next start: AppOptions from the file.
        var options = store.Read().ToAppOptions();
        var next = new AppCommands(store, Options.Create(options), NullLogger<AppCommands>.Instance);
        next.Add(new AppCommand { Id = "a.first", Title = "First", Area = "Test", DefaultShortcut = "Ctrl+1" });
        Assert.AreEqual("Alt+F1", next.ShortcutOf(next.Find("a.first")!).ToString());
    }

    // ─── The palette's search ─────────────────────────────────────────────

    [TestMethod]
    public void The_palette_finds_by_every_word_best_first()
    {
        static PaletteItem Item(string title, string detail = "", string keywords = "") => new(title, detail, "", () => { }, keywords);
        var items = new[]
        {
            Item("Panel: Show Output", keywords: "activity"), Item("Project: Start project", "shop"), Item("Project: Stop project", "shop"),
            Item("Pages: Open Settings", keywords: "preferences"), Item("Project: Restart project", "blog")
        };

        CollectionAssert.AreEqual(items, PaletteFilter.Rank(items, "  ").ToList(), "Nothing typed: all, in order.");
        CollectionAssert.AreEqual(new[] { items[1], items[2] }, PaletteFilter.Rank(items, "project shop").ToList(), "Every word, in the title or the detail.");
        Assert.AreEqual(items[3], PaletteFilter.Rank(items, "preferences").Single(), "By keyword too.");
        Assert.AreEqual(items[1], PaletteFilter.Rank(items, "start").First(), "A word that starts with it before one that only contains it (Restart).");
        Assert.AreEqual(0, PaletteFilter.Rank(items, "nothing like this").Count);
    }
}
