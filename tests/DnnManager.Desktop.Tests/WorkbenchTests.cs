using Avalonia.Input;
using DnnManager.Desktop.Services;
using DnnManager.Desktop.Views.Workbench;

namespace DnnManager.Desktop.Tests;

/// <summary>The window's parts that draw nothing: shortcuts, the toasts' queue, the command palette's ranking.</summary>
[TestClass]
public sealed class WorkbenchTests
{
    [TestMethod]
    public void Shortcuts_read_and_write_as_VS_Code_writes_them()
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.AreEqual(new Shortcut(command | KeyModifiers.Shift, Key.P), Shortcut.Parse("Ctrl+Shift+P"));
        Assert.AreEqual(new Shortcut(command, Key.Oem3), Shortcut.Parse("Ctrl+`"));
        Assert.AreEqual(new Shortcut(command, Key.OemComma), Shortcut.Parse("ctrl+,"));
        Assert.AreEqual(new Shortcut(command, Key.D1), Shortcut.Parse("Ctrl+1"));
        Assert.AreEqual(new Shortcut(KeyModifiers.None, Key.F1), Shortcut.Parse("F1"));
        Assert.AreEqual(new Shortcut(command | KeyModifiers.Alt, Key.B), Shortcut.Parse("Ctrl+Alt+B"));
        Assert.IsNull(Shortcut.Parse("Hyper+B"), "Not a modifier.");
        Assert.IsNull(Shortcut.Parse(""));
        if (!OperatingSystem.IsMacOS())
        {
            Assert.AreEqual("Ctrl+Shift+P", Shortcut.Parse("Ctrl+Shift+P")!.Value.ToString());
            Assert.AreEqual("Ctrl+`", Shortcut.Parse("Ctrl+`")!.Value.ToString());
            Assert.AreEqual("Ctrl+PageUp", Shortcut.Parse("Ctrl+PageUp")!.Value.ToString());
        }
        Assert.IsNotNull(new Shortcut(KeyModifiers.Shift, Key.A).Problem, "A plain key is for typing.");
        Assert.IsNull(new Shortcut(KeyModifiers.None, Key.F5).Problem);
    }

    [TestMethod]
    public void A_toast_that_asks_for_something_waits_and_a_passing_one_shows_at_once()
    {
        var queue = new ToastQueue();
        var error = new ToastMessage("Push failed", ToastKind.Error);
        var saved = new ToastMessage("Settings saved", ToastKind.Success);
        var warning = new ToastMessage("Disk almost full", ToastKind.Warning);
        Assert.AreSame(error, queue.Add(error));
        Assert.AreSame(saved, queue.Add(saved), "A passing message shows at once - the error comes back after it.");
        Assert.AreSame(warning, queue.Add(warning), "Over a passing message, the next one shows at once.");
        Assert.IsNull(queue.Add(new ToastMessage("Disk almost full", ToastKind.Warning)), "The same one twice shows once.");
        var other = new ToastMessage("Pull failed", ToastKind.Error);
        Assert.IsNull(queue.Add(other), "Over one that asks for something, it waits its turn.");
        Assert.AreEqual(2, queue.Waiting);
        Assert.AreSame(error, queue.Next());
        Assert.AreSame(other, queue.Next());
        Assert.IsNull(queue.Next());
    }

    [TestMethod]
    public void A_long_toast_says_its_first_sentence()
    {
        Assert.AreEqual("The update failed.", Toast.Summary("The update failed. GitHub answered 500 for DnnManager_Setup-2.0.0-x64.exe."));
        Assert.AreEqual("Short", Toast.Summary("  Short "));
        var longOne = string.Join(' ', Enumerable.Repeat("word", 60));
        Assert.IsTrue(Toast.Summary(longOne).EndsWith('…'));
        Assert.IsTrue(Toast.Summary(longOne).Length <= 141);
    }

    [TestMethod]
    public void The_palette_finds_every_word_and_puts_a_title_that_starts_with_it_first()
    {
        static PaletteItem Item(string title, string keywords = "", string group = "") => new(title, "", "", () => { }, keywords) { Group = group };
        var items = new[]
        {
            Item("Layout: Show or hide the panel", "output logs bottom"),
            Item("Panel: Show Output", "activity operations"),
            Item("Application: Check for updates", "version release github"),
        };
        var found = CommandPalette.Rank(items, "output");
        CollectionAssert.AreEqual(new[] { "Panel: Show Output", "Layout: Show or hide the panel" }, found.Select(i => i.Title).ToArray(),
            "A word of the title that starts with it first, then one that has it in its keywords.");
        Assert.AreEqual("Panel: Show Output", CommandPalette.Rank(items, "panel: show").First().Title, "A title that starts with it first.");
        Assert.AreEqual(0, CommandPalette.Rank(items, "nothing like it").Count);
        Assert.AreEqual(3, CommandPalette.Rank(items, "  ").Count, "Nothing typed: all of them, as listed.");

        var grouped = new[] { Item("b", group: "recently used"), Item("a", group: "other commands"), Item("ab", group: "recently used") };
        CollectionAssert.AreEqual(new[] { "b", "a", "ab" }, CommandPalette.Rank(grouped, "").Select(i => i.Title).ToArray(), "Nothing typed: as listed.");
        CollectionAssert.AreEqual(new[] { "ab", "a" }, CommandPalette.Rank(grouped, "a").Select(i => i.Title).ToArray(), "Groups stay in their order.");
    }
}
