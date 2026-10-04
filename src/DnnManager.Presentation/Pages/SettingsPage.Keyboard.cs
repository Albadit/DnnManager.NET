using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Presentation.Pages;

/// <summary>A command on Settings - Keyboard shortcuts: its shortcut, its default, and what is wrong with it.</summary>
public sealed class ShortcutRow(AppCommand command) : INotifyPropertyChanged
{
    private bool _recording;

    public AppCommand Command { get; } = command;
    public string Title => Command.Title;
    public Shortcut? Shortcut { get; set; }
    public Shortcut? Default { get; set; }
    public bool IsCustom { get; set; }
    public string Warning { get; set; } = "";
    public bool HasWarning => Warning.Length > 0;

    /// <summary>"Project · default F5" - where it belongs, and its default when it was changed.</summary>
    public string Detail => IsCustom ? $"{Command.Area} · default {Default?.ToString() ?? "none"}" : Command.Area;

    public bool IsRecording
    {
        get => _recording;
        set { _recording = value; Changed(); }
    }

    public string ShortcutText => _recording ? "Press the keys…" : Shortcut?.ToString() ?? "-";
    public string ShortcutTip => _recording ? "Press the new shortcut - Esc cancels, Backspace removes it" : "Change the shortcut";
    public string SpokenShortcut => $"Shortcut for {Title}: {Shortcut?.ToString() ?? "none"}. Press Enter to change it.";
    public string ResetTip => IsCustom ? $"Back to the default: {Default?.ToString() ?? "none"}" : "It has its default";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Everything shown has changed.</summary>
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    /// <summary>Whether every word of <paramref name="search"/> is in its title, area, shortcut or keywords.</summary>
    public bool Matches(string search) =>
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            $"{Title} {Command.Area} {Shortcut} {Command.Keywords}".Contains(word, StringComparison.OrdinalIgnoreCase));
}

// Settings - Keyboard shortcuts: every command, its shortcut (the default or the one chosen), changing and resetting
// them, and a warning when two commands share one or one takes a text box's editing key. Unlike the other categories
// it doesn't wait for Save: AppCommands saves each change at once (keyboard.shortcuts in the settings).
public partial class SettingsPage
{
    private List<ShortcutRow> _shortcutRows = [];
    private bool _followingShortcuts;

    private AppCommands Commands => _services.GetRequiredService<AppCommands>();

    /// <summary>The list, as the commands' shortcuts are now - when the category is shown, and whenever one changes.</summary>
    private void ShowShortcuts()
    {
        if (!_followingShortcuts)
        {
            _followingShortcuts = true;
            EventHandler changed = (_, _) => ShowShortcuts();
            Commands.Changed += changed;
            Unloaded += (_, _) => Commands.Changed -= changed;
        }
        var commands = Commands;
        // Kept rows keep their buttons - and the keyboard on the one that was just changed.
        var rows = commands.All.Select(c => _shortcutRows.FirstOrDefault(r => r.Command == c) ?? new ShortcutRow(c)).ToList();
        foreach (var row in rows)
        {
            row.Shortcut = commands.ShortcutOf(row.Command);
            row.Default = commands.DefaultOf(row.Command);
            row.IsCustom = commands.IsCustom(row.Command);
            row.Warning = WarningOf(commands, row);
            row.Changed();
        }
        _shortcutRows = rows;
        FilterShortcuts();
    }

    private static string WarningOf(AppCommands commands, ShortcutRow row)
    {
        var conflicts = commands.ConflictsOf(row.Command);
        if (conflicts.Count > 0)
        {
            // The first listed runs (AppCommands.Match).
            var runs = commands.All.First(c => c == row.Command || conflicts.Contains(c)) == row.Command;
            return $"{row.Shortcut} is also the shortcut of {string.Join(", ", conflicts.Select(c => c.Label))}" +
                   (runs ? " - this one runs." : " - that one runs, not this.");
        }
        return row.Shortcut is { IsEditingKey: true } editing ? $"{editing} copies, pastes or undoes in text boxes - it won't do that there any more." : "";
    }

    private void ShortcutSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterShortcuts();

    private void FilterShortcuts()
    {
        var search = ShortcutSearch.Text.Trim();
        var shown = _shortcutRows.Where(r => r.Matches(search)).ToList();
        ShortcutList.ItemsSource = shown;
        NoShortcut.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetAllShortcutsButton.IsEnabled = _shortcutRows.Any(r => r.IsCustom);
    }

    private void ShowShortcutStatus(string? text)
    {
        ShortcutStatus.Text = text ?? "";
        ShortcutStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        ShortcutStatus.SetResourceReference(TextBlock.ForegroundProperty, "LogWarn");
    }

    // ─── Recording a shortcut ─────────────────────────────────────────────

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ShortcutRow row } button) StartRecording(button, row);
    }

    private void StartRecording(Button button, ShortcutRow row)
    {
        foreach (var other in _shortcutRows.Where(r => r.IsRecording)) other.IsRecording = false;
        row.IsRecording = true;
        // Every key goes to the button now - not to the window's shortcuts (MainWindow.OnPreviewKeyDown).
        button.Tag = MainWindow.ShortcutRecording;
        button.Focus();
        ShowShortcutStatus(null);
    }

    private static void StopRecording(Button button, ShortcutRow row)
    {
        row.IsRecording = false;
        button.Tag = null;
    }

    private void Shortcut_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button { DataContext: ShortcutRow row } button || !row.IsRecording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            StopRecording(button, row);
            return;
        }
        Shortcut? chosen;
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) chosen = null;
        else if (Shortcut.Of(e) is { } pressed) chosen = pressed;
        else return; // a modifier on its own: the key comes next

        if (chosen is { Problem: { } problem })
        {
            ShowShortcutStatus($"{chosen}: {problem}");
            return;
        }
        StopRecording(button, row);
        ShowShortcutStatus(Commands.Set(row.Command, chosen));
    }

    // Clicked elsewhere, or Tab'd away before pressing anything: nothing changes.
    private void Shortcut_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: ShortcutRow { IsRecording: true } row } button) StopRecording(button, row);
    }

    private void ResetShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ShortcutRow row }) ShowShortcutStatus(Commands.Reset(row.Command));
    }

    private void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Put every keyboard shortcut back to its default?", "Reset all", "Cancel")) return;
        ShowShortcutStatus(Commands.ResetAll());
    }
}
