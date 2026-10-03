using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DnnManager.Presentation.Controls;

/// <summary>An entry of the command palette: a command or a project, with what it does when chosen.</summary>
/// <param name="Detail">Shown muted on the right - a project's address, a command's area.</param>
/// <param name="Keywords">More words it is found by.</param>
public sealed record PaletteItem(string Title, string Detail, string Shortcut, Action Run, string Keywords = "")
{
    /// <summary>The group it is in (Customize Layout's "Visibility", "Panel Alignment"…) - named on its first item, a line above.</summary>
    public string Group { get; init; } = "";
    /// <summary>What is chosen now - a check mark after the title.</summary>
    public bool IsChecked { get; init; }
    /// <summary>Choosing it leaves the list open, updated (Customize Layout) - instead of closing it.</summary>
    public bool KeepOpen { get; init; }
    /// <summary>Only says something ("No matching results") - choosing it does nothing.</summary>
    public bool IsInert { get; init; }
    /// <summary>A drawing on the left, in a 16 × 16 box: its lines, and what is filled in.</summary>
    public Geometry? Icon { get; init; }
    public Geometry? IconFill { get; init; }

    /// <summary>Set while it is listed: the first of its group (its name on the right), after another group (a line above).</summary>
    public bool StartsGroup { get; set; }
    public bool ShowsSeparator { get; set; }

    /// <summary>On the right: the group's name on its first item, else the detail.</summary>
    public string Side => StartsGroup && Group.Length > 0 ? Group : Detail;

    /// <summary>What a screen reader says for it.</summary>
    public string Spoken => (IsChecked ? $"{Title}, chosen" : Title) + (Shortcut.Length > 0 ? $", {Shortcut}" : "");
}

/// <summary>
/// The command palette, like VS Code's: a box at the top of the window and what matches what is typed. Ctrl+P lists the
/// projects (typing <c>&gt;</c> switches to the commands), Ctrl+Shift+P the commands (deleting the <c>&gt;</c> switches to
/// the projects); a command for a project asks which one (<see cref="ShowPick"/>). The arrows choose, Enter runs, Esc -
/// or a click outside - closes it, and the keyboard goes back where it was.
/// </summary>
public partial class CommandPalette : UserControl
{
    private IReadOnlyList<PaletteItem> _projects = [], _commands = [], _pick = [];
    private bool _picking;
    private string _pickPrompt = "";
    private IInputElement? _returnFocus;
    // A list that stays open (Customize Layout): makes its items anew after one was chosen, and puts it all back.
    private Func<IReadOnlyList<PaletteItem>>? _refresh;
    private Action? _reset;
    // While a kept-open item runs: what it does may move the keyboard for a moment - that doesn't close the list.
    private bool _running;

    public CommandPalette()
    {
        InitializeComponent();
        // The keyboard went elsewhere (a click in the window, Alt+Tab): it is closed, without taking the keyboard back.
        IsKeyboardFocusWithinChanged += (_, e) => { if (!(bool)e.NewValue && IsOpen && !_running) Close(restoreFocus: false); };
    }

    /// <summary>
    /// Where it opens: at the top, over the title bar's search (as VS Code's) - or, centred, in the middle of the window
    /// (Customize Layout's Quick Input Position).
    /// </summary>
    public bool Centered
    {
        get => _centered;
        set
        {
            _centered = value;
            if (IsOpen) Place();
        }
    }

    private bool _centered;

    // In the middle: its top a little above the middle, so a full list is about centred - it grows downwards.
    private void Place() => Box.Margin = new Thickness(16, Centered ? Math.Max(44, ActualHeight / 2 - 240) : 1, 16, 0);

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The projects and the commands, the projects shown - or the commands with <paramref name="commands"/>.</summary>
    public void ShowQuick(IReadOnlyList<PaletteItem> projects, IReadOnlyList<PaletteItem> commandItems, bool commands)
    {
        _projects = projects;
        _commands = commandItems;
        _picking = false;
        ShowTitle(null);
        Open(commands ? ">" : "");
    }

    /// <summary>A choice to make - e.g. which project to start - with <paramref name="prompt"/> in the box.</summary>
    public void ShowPick(string prompt, IReadOnlyList<PaletteItem> items)
    {
        _pick = items;
        _pickPrompt = prompt;
        _picking = true;
        ShowTitle(null);
        Open("");
    }

    /// <summary>
    /// A list with a name (<paramref name="title"/>) that stays open while its items are chosen - each changes something
    /// at once, and the list is made anew (<paramref name="refresh"/>) to show it. <paramref name="reset"/> puts it all
    /// back (the ↺ by the name).
    /// </summary>
    public void ShowSettings(string title, string prompt, Func<IReadOnlyList<PaletteItem>> refresh, Action reset)
    {
        _pick = refresh();
        _pickPrompt = prompt;
        _picking = true;
        ShowTitle(title);
        _refresh = refresh;
        _reset = reset;
        ResetButton.ToolTip = $"Reset {title}";
        Open("");
    }

    private void ShowTitle(string? title)
    {
        _refresh = null;
        _reset = null;
        TitleText.Text = title ?? "";
        TitleRow.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Open(string text)
    {
        if (!IsOpen) _returnFocus = Keyboard.FocusedElement;
        Place();
        Visibility = Visibility.Visible;
        Query.Text = text;
        Query.CaretIndex = text.Length;
        Filter();
        Dispatcher.BeginInvoke(() => Query.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Closes it - by default giving the keyboard back to what had it before.</summary>
    public void Close(bool restoreFocus = true)
    {
        if (!IsOpen) return;
        Visibility = Visibility.Collapsed;
        Results.ItemsSource = null;
        if (restoreFocus && _returnFocus is UIElement { IsVisible: true } back) back.Focus();
        _returnFocus = null;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Filter();

    private void Filter()
    {
        var text = Query.Text;
        IReadOnlyList<PaletteItem> source;
        string name;
        if (_picking)
        {
            (source, name) = (_pick, _pickPrompt);
        }
        else if (text.StartsWith('>'))
        {
            (source, name) = (_commands, "Commands");
            text = text[1..];
        }
        else
        {
            (source, name) = (_projects, "Go to project - type > for the commands");
        }
        Query.Tag = name;
        var shown = PaletteFilter.Rank(source, text);
        // Nothing matches: a row that says so, as VS Code's - choosing it does nothing.
        if (shown.Count == 0) shown = [NoMatch];
        // Groups as listed: each one's name on its first item, a line between them.
        string? group = null;
        foreach (var item in shown)
        {
            item.StartsGroup = item.Group.Length > 0 && item.Group != group;
            item.ShowsSeparator = item.StartsGroup && group is not null;
            group = item.Group;
        }
        Results.ItemsSource = shown;
        Results.SelectedIndex = 0;
        Results.ScrollIntoView(shown[0]);
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(10); break;
            case Key.PageUp: Move(-10); break;
            case Key.Enter: Run(Results.SelectedItem as PaletteItem); break;
            case Key.Escape: Close(); break;
            // Tab would take the keyboard out of the palette: it stays here, as in VS Code.
            case Key.Tab: break;
            default: return;
        }
        e.Handled = true;

        void Move(int by)
        {
            if (count == 0) return;
            var index = Math.Clamp(Results.SelectedIndex + by, 0, count - 1);
            // Down on the last, up on the first: round to the other end.
            if (by is 1 or -1 && Results.SelectedIndex + by is var wrapped && (wrapped < 0 || wrapped >= count)) index = (wrapped + count) % count;
            Results.SelectedIndex = index;
            Results.ScrollIntoView(Results.SelectedItem);
        }
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteItem item }) Run(item);
    }

    private static readonly PaletteItem NoMatch = new("No matching results", "", "", () => { }) { IsInert = true };

    private void Run(PaletteItem? item)
    {
        if (item is null || item.IsInert) return;
        if (item.KeepOpen && _refresh is not null)
        {
            RunKeepingOpen(item.Run);
            return;
        }
        // The keyboard back first: what the item opens can take it from there.
        Close();
        item.Run();
    }

    /// <summary>Does <paramref name="change"/> and shows the list as it is now, on the same row.</summary>
    private void RunKeepingOpen(Action change)
    {
        var index = Results.SelectedIndex;
        _running = true;
        try
        {
            change();
        }
        finally
        {
            _running = false;
        }
        if (!IsOpen || _refresh is null) return;
        _pick = _refresh();
        Filter();
        if (Results.Items.Count > 0) Results.SelectedIndex = Math.Clamp(index, 0, Results.Items.Count - 1);
        Query.Focus();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_reset is { } reset) RunKeepingOpen(reset);
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Outside_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    // A click in the box isn't a click outside it.
    private void Box_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}

/// <summary>Which palette entries match what is typed, best first.</summary>
public static class PaletteFilter
{
    /// <summary>
    /// The entries with every typed word in their title, detail or keywords - those whose title starts with the text
    /// first, then those with a word starting with it, then the rest, each in their own order.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Rank(IReadOnlyList<PaletteItem> items, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return items;
        var text = query.Trim();
        return items
            .Select((item, index) => (item, index, all: $"{item.Title} {item.Detail} {item.Keywords}"))
            .Where(x => words.All(w => x.all.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.item.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0
                : x.item.Title.Split(' ', ':', '-', '.').Any(w => w.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) ? 1 : 2)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }
}
