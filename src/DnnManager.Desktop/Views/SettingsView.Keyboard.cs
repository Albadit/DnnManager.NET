using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DnnManager.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views;

// Settings - Keyboard shortcuts: every command, its shortcut (the default or the one chosen), changing and resetting
// them, and a warning when two commands share one or one takes a text box's editing key. Unlike the other categories
// it doesn't wait for Save: AppCommands saves each change at once (keyboard.shortcuts in the settings). The Windows app's
// Pages/SettingsPage.Keyboard.cs.
public partial class SettingsView
{
    /// <summary>A command on Keyboard shortcuts: its shortcut, its default, and what is wrong with it.</summary>
    private sealed class ShortcutRow(AppCommand command)
    {
        public AppCommand Command { get; } = command;
        public Shortcut? Shortcut { get; set; }
        public Shortcut? Default { get; set; }
        public bool IsCustom => Shortcut != Default;
        public string Warning { get; set; } = "";
        public bool IsRecording { get; set; }

        /// <summary>"Project · default F5" - where it belongs, and its default when it was changed.</summary>
        public string Detail => IsCustom ? $"{Command.Area} · default {Default?.ToString() ?? "none"}" : Command.Area;

        public bool Matches(string search) =>
            search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
                $"{Command.Title} {Command.Area} {Shortcut} {Command.Keywords}".Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A shortcut is being recorded on Keyboard shortcuts: every key goes to it - the window's shortcuts must not run
    /// (MainWindow checks this before matching a key to a command).
    /// </summary>
    public static bool IsRecordingShortcut { get; private set; }

    private List<ShortcutRow> _shortcutRows = [];
    private bool _followingShortcuts;

    /// <summary>The app's commands - null until the main window registers them (then the list is read only, and says so).</summary>
    private AppCommands? Commands => _services.GetService<AppCommands>();

    /// <summary>The list, as the commands' shortcuts are now - when the category is shown, and whenever one changes.</summary>
    private void ShowShortcuts()
    {
        if (Commands is not { } commands)
        {
            KeyboardIntro.Text = "The commands and their shortcuts come with the command palette - not here yet.";
            ResetAllShortcutsButton.IsEnabled = false;
            NoShortcut.IsVisible = true;
            return;
        }
        if (!_followingShortcuts)
        {
            _followingShortcuts = true;
            EventHandler changed = (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (KeyboardPanel.IsVisible) ShowShortcuts(); });
            commands.Changed += changed;
            DetachedFromVisualTree += (_, _) =>
            {
                commands.Changed -= changed;
                _followingShortcuts = false;
                IsRecordingShortcut = false;
            };
        }
        var kept = _shortcutRows.ToDictionary(r => r.Command);
        var rows = commands.All.Select(c => kept.GetValueOrDefault(c) ?? new ShortcutRow(c)).ToList();
        foreach (var row in rows)
        {
            row.Shortcut = commands.ShortcutOf(row.Command);
            row.Default = commands.DefaultOf(row.Command);
        }
        // Who shares a shortcut, in one pass - not every command against every other.
        var sharing = rows.Where(r => r.Shortcut is not null).GroupBy(r => r.Shortcut!.Value).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var row in rows) row.Warning = WarningOf(row, row.Shortcut is { } s ? sharing.GetValueOrDefault(s) : null);
        _shortcutRows = rows;
        FilterShortcuts();
    }

    /// <param name="sharing">The rows with <paramref name="row"/>'s shortcut, in the commands' order - this one too; null: none shares it.</param>
    private static string WarningOf(ShortcutRow row, List<ShortcutRow>? sharing)
    {
        if (sharing is not null)
        {
            var conflicts = sharing.Where(r => r != row).Select(r => r.Command).ToList();
            // The first listed runs (AppCommands.Match).
            var runs = sharing[0] == row;
            return $"{row.Shortcut} is also the shortcut of {string.Join(", ", conflicts.Select(c => c.Label))}" +
                   (runs ? " - this one runs." : " - that one runs, not this.");
        }
        return row.Shortcut is { } shortcut && IsEditingKey(shortcut)
            ? $"{shortcut} copies, pastes or undoes in text boxes - it won't do that there any more." : "";
    }

    /// <summary>Ctrl (⌘) with C, V, X, Z, Y or A, nothing else: what a text box does with it.</summary>
    private static bool IsEditingKey(Shortcut shortcut)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        return shortcut.Modifiers == command && shortcut.Key is Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.A;
    }

    private void ShortcutSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_categories is null) return; // raised during InitializeComponent
        FilterShortcuts();
    }

    private void FilterShortcuts()
    {
        var search = (ShortcutSearch.Text ?? "").Trim();
        var shown = _shortcutRows.Where(r => r.Matches(search)).ToList();
        ShortcutList.Children.Clear();
        foreach (var row in shown) ShortcutList.Children.Add(RowView(row));
        NoShortcut.IsVisible = shown.Count == 0;
        ResetAllShortcutsButton.IsEnabled = _shortcutRows.Any(r => r.IsCustom);
    }

    private Control RowView(ShortcutRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200,36") };
        var what = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        what.Children.Add(new TextBlock { Text = row.Command.Title, TextWrapping = TextWrapping.Wrap });
        what.Children.Add(Caption(row.Detail, "TextMuted"));
        if (row.Warning.Length > 0) what.Children.Add(Caption(row.Warning, "LogWarn"));

        // Pressed: it records the next keys.
        var shortcut = new Button
        {
            Content = row.IsRecording ? "Press the keys…" : row.Shortcut?.ToString() ?? "-",
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(shortcut, row.IsRecording ? "Press the new shortcut - Esc cancels, Backspace removes it" : "Change the shortcut");
        Avalonia.Automation.AutomationProperties.SetName(shortcut,
            $"Shortcut for {row.Command.Title}: {row.Shortcut?.ToString() ?? "none"}. Press Enter to change it.");
        shortcut.Click += (_, _) => StartRecording(shortcut, row);
        shortcut.AddHandler(KeyDownEvent, (_, e) => Shortcut_KeyDown(shortcut, row, e), RoutingStrategies.Tunnel);
        shortcut.LostFocus += (_, _) => { if (row.IsRecording) StopRecording(shortcut, row); };
        Grid.SetColumn(shortcut, 1);

        var resetTip = row.IsCustom ? $"Back to the default: {row.Default?.ToString() ?? "none"}" : "It has its default";
        var reset = new Button
        {
            Classes = { "icon" }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = row.IsCustom,
            Content = MainWindow.HasIconFont
                ? new TextBlock { Classes = { "glyph" }, Text = (string)Avalonia.Application.Current!.FindResource("GlyphReset")!, FontSize = 13 }
                : new TextBlock { Text = "↺" }
        };
        ToolTip.SetTip(reset, resetTip);
        ToolTip.SetShowOnDisabled(reset, true);
        Avalonia.Automation.AutomationProperties.SetName(reset, resetTip);
        reset.Click += (_, _) =>
        {
            if (Commands is { } commands) ShowShortcutStatus(commands.Set(row.Command, row.Default));
        };
        Grid.SetColumn(reset, 2);

        grid.Children.Add(what);
        grid.Children.Add(shortcut);
        grid.Children.Add(reset);
        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7), Child = grid };
        line.Bind(Border.BorderBrushProperty, line.GetResourceObservable("CardBorder"));
        return line;
    }

    private static TextBlock Caption(string text, string brush)
    {
        var caption = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable(brush));
        caption.Bind(TextBlock.FontSizeProperty, caption.GetResourceObservable("TextCaption"));
        return caption;
    }

    private void ShowShortcutStatus(string? text)
    {
        ShortcutStatus.Text = text ?? "";
        ShortcutStatus.IsVisible = text is not null;
    }

    // ─── Recording a shortcut ─────────────────────────────────────────────

    private void StartRecording(Button button, ShortcutRow row)
    {
        foreach (var other in _shortcutRows.Where(r => r.IsRecording && r != row)) other.IsRecording = false;
        row.IsRecording = true;
        IsRecordingShortcut = true;
        button.Content = "Press the keys…";
        ToolTip.SetTip(button, "Press the new shortcut - Esc cancels, Backspace removes it");
        button.Focus();
        ShowShortcutStatus(null);
    }

    private void StopRecording(Button button, ShortcutRow row)
    {
        row.IsRecording = false;
        IsRecordingShortcut = _shortcutRows.Any(r => r.IsRecording);
        button.Content = row.Shortcut?.ToString() ?? "-";
        ToolTip.SetTip(button, "Change the shortcut");
    }

    private void Shortcut_KeyDown(Button button, ShortcutRow row, KeyEventArgs e)
    {
        if (!row.IsRecording || Commands is not { } commands) return;
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            StopRecording(button, row);
            return;
        }
        Shortcut? chosen;
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None) chosen = null;
        else if (Shortcut.Of(e) is { } pressed) chosen = pressed;
        else return; // a modifier on its own: the key comes next

        if (chosen is { Problem: { } problem })
        {
            ShowShortcutStatus($"{chosen}: {problem}");
            return;
        }
        StopRecording(button, row);
        // Saved at once; AppCommands.Changed redraws the list.
        ShowShortcutStatus(commands.Set(row.Command, chosen));
    }

    private async void ResetAllShortcuts_Click(object? sender, RoutedEventArgs e)
    {
        if (Commands is not { } commands) return;
        if (!await Dialogs.ConfirmAsync("Put every keyboard shortcut back to its default?", "Reset all", "Cancel")) return;
        ShowShortcutStatus(commands.ResetAll());
    }
}
