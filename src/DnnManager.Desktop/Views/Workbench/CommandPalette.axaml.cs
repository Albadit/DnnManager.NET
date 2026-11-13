using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace DnnManager.Desktop.Views.Workbench;

/// <summary>
/// A row of the command palette: a project, a command, a choice. <see cref="Detail"/> and <see cref="Keywords"/> are
/// searched too; <see cref="Group"/> lists it under a name ("recently used").
/// </summary>
public sealed record PaletteItem(string Title, string Detail, string Shortcut, Action Run, string Keywords = "")
{
    public string Group { get; init; } = "";
    /// <summary>Ticked: what is chosen now (a theme, a layout option).</summary>
    public bool IsChecked { get; init; }
    /// <summary>The list stays open when it is chosen (Customize Layout) - and is made anew.</summary>
    public bool KeepOpen { get; init; }
    /// <summary>Choosing it does nothing ("No matching results").</summary>
    public bool IsInert { get; init; }
    public Geometry? Icon { get; init; }
    public Geometry? IconFill { get; init; }

    public bool StartsGroup { get; set; }
    public bool ShowsSeparator { get; set; }

    public bool HasShortcut => Shortcut.Length > 0;
    public bool HasIcon => Icon is not null;

    /// <summary>On the right: the group's name on its first row, else the detail.</summary>
    public string Side => StartsGroup && Group.Length > 0 ? Group : Detail;
}

/// <summary>
/// The command palette (src/DnnManager.Presentation/Controls/CommandPalette.xaml.cs): Ctrl+P lists the projects (Go to
/// project), Ctrl+Shift+P - or ">" typed first - the commands; also a list to pick from (a theme, which project) and a
/// list that stays open (Customize Layout). Typing filters it; ↑ ↓ Enter Esc, as VS Code's.
/// </summary>
public partial class CommandPalette : UserControl
{
    private static readonly PaletteItem NoMatch = new("No matching results", "", "", () => { }) { IsInert = true };

    private IReadOnlyList<PaletteItem> _projects = [], _commands = [], _pick = [];
    private bool _picking, _running, _centered;
    private string _pickPrompt = "";
    private IInputElement? _returnFocus;
    private Func<IReadOnlyList<PaletteItem>>? _refresh;
    private Action? _reset;

    public CommandPalette()
    {
        InitializeComponent();
        Query.TextChanged += (_, _) => Filter();
        // The keyboard went elsewhere (a click in the window, Alt+Tab): closed, without taking the keyboard back.
        Query.LostFocus += (_, _) =>
        {
            // Opened again since (a command that opens another list): that one stays.
            var opened = _opened;
            Dispatcher.UIThread.Post(() =>
            {
                if (opened == _opened && IsOpen && !_running && !IsKeyboardFocusWithin) Close(restoreFocus: false);
            });
        };
    }

    // How many times it was opened - a close that a lost keyboard asked for is for the opening it was asked in.
    private int _opened;

    /// <summary>Customize Layout's Quick Input Position: in the middle of the window, not at its top.</summary>
    public bool Centered
    {
        get => _centered;
        set
        {
            _centered = value;
            if (IsOpen) Place();
        }
    }

    public bool IsOpen => IsVisible;

    /// <summary>What the list shows now - for the tests.</summary>
    internal IReadOnlyList<PaletteItem> Shown => Results.ItemsSource as IReadOnlyList<PaletteItem> ?? [];

    private void Place() => Box.Margin = new Thickness(16, Centered ? Math.Max(44, Bounds.Height / 2 - 240) : 1, 16, 0);

    /// <summary>Go to project (<paramref name="commands"/> false) or the commands - both a ">" apart.</summary>
    public void ShowQuick(IReadOnlyList<PaletteItem> projects, IReadOnlyList<PaletteItem> commandItems, bool commands)
    {
        _projects = projects;
        _commands = commandItems;
        _picking = false;
        ShowTitle(null);
        Open(commands ? ">" : "");
    }

    /// <summary>A list to choose one from, <paramref name="prompt"/> in the search box.</summary>
    public void ShowPick(string prompt, IReadOnlyList<PaletteItem> items)
    {
        _pick = items;
        _pickPrompt = prompt;
        _picking = true;
        ShowTitle(null);
        Open("");
    }

    /// <summary>A list of settings that stays open as they are chosen, with its name, Reset and close (Customize Layout).</summary>
    public void ShowSettings(string title, string prompt, Func<IReadOnlyList<PaletteItem>> refresh, Action reset)
    {
        _pick = refresh();
        _pickPrompt = prompt;
        _picking = true;
        ShowTitle(title);
        _refresh = refresh;
        _reset = reset;
        ToolTip.SetTip(ResetButton, $"Reset {title}");
        Open("");
    }

    private void ShowTitle(string? title)
    {
        _refresh = null;
        _reset = null;
        TitleText.Text = title ?? "";
        TitleRow.IsVisible = title is not null;
    }

    private void Open(string text)
    {
        if (!IsOpen) _returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        IsVisible = true;
        Place();
        Query.Text = text;
        Query.CaretIndex = text.Length;
        Filter();
        _opened++;
        Query.Focus();
        Dispatcher.UIThread.Post(() => Query.Focus(), DispatcherPriority.Input);
    }

    public void Close(bool restoreFocus = true)
    {
        if (!IsOpen) return;
        IsVisible = false;
        Results.ItemsSource = null;
        if (restoreFocus && _returnFocus is InputElement { IsVisible: true } back) back.Focus();
        _returnFocus = null;
    }

    private void Filter()
    {
        if (!IsOpen) return;
        var text = Query.Text ?? "";
        IReadOnlyList<PaletteItem> source;
        string name;
        if (_picking) (source, name) = (_pick, _pickPrompt);
        else if (text.StartsWith('>'))
        {
            (source, name) = (_commands, "Commands");
            text = text[1..];
        }
        else (source, name) = (_projects, "Go to project - type > for the commands");
        Query.PlaceholderText = name;
        AutomationProperties.SetName(Query, name);
        var shown = Rank(source, text);
        if (shown.Count == 0) shown = [NoMatch];
        string? group = null;
        foreach (var item in shown)
        {
            item.StartsGroup = item.Group.Length > 0 && item.Group != group;
            item.ShowsSeparator = item.StartsGroup && group is not null;
            group = item.Group;
        }
        Results.ItemsSource = shown;
        Results.SelectedIndex = 0;
    }

    /// <summary>
    /// The items with every word of <paramref name="query"/> in their title, detail or keywords - groups kept in order, and
    /// in each a title that starts with it first, then one with a word that does.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Rank(IReadOnlyList<PaletteItem> items, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return items;
        var text = query.Trim();
        var groups = items.Select(i => i.Group).Distinct().ToList();
        return items
            .Select((item, index) => (item, index, all: $"{item.Title} {item.Detail} {item.Keywords}"))
            .Where(x => words.All(w => x.all.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => groups.IndexOf(x.item.Group))
            .ThenBy(x => x.item.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0
                : x.item.Title.Split(' ', ':', '-', '.').Any(w => w.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) ? 1 : 2)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    private void Query_KeyDown(object? sender, KeyEventArgs e)
    {
        var count = Results.ItemCount;
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(10); break;
            case Key.PageUp: Move(-10); break;
            case Key.Enter: Run(Results.SelectedItem as PaletteItem); break;
            case Key.Escape: Close(); break;
            case Key.Tab: break;
            default: return;
        }
        e.Handled = true;

        void Move(int by)
        {
            if (count == 0) return;
            var index = Math.Clamp(Results.SelectedIndex + by, 0, count - 1);
            if (by is 1 or -1 && Results.SelectedIndex + by is var wrapped && (wrapped < 0 || wrapped >= count)) index = (wrapped + count) % count;
            Results.SelectedIndex = index;
            if (Results.SelectedItem is { } item) Results.ScrollIntoView(item);
        }
    }

    private void Item_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: PaletteItem item }) Run(item);
    }

    /// <summary>Runs <paramref name="item"/> - for the tests too.</summary>
    internal void Run(PaletteItem? item)
    {
        if (item is null || item.IsInert) return;
        if (item.KeepOpen && _refresh is not null)
        {
            RunKeepingOpen(item.Run);
            return;
        }
        Close();
        item.Run();
    }

    private void RunKeepingOpen(Action change)
    {
        var index = Results.SelectedIndex;
        _running = true;
        try { change(); }
        finally { _running = false; }
        if (!IsOpen || _refresh is null) return;
        _pick = _refresh();
        Filter();
        if (Results.ItemCount > 0) Results.SelectedIndex = Math.Clamp(index, 0, Results.ItemCount - 1);
        Query.Focus();
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (_reset is { } reset) RunKeepingOpen(reset);
    }

    private void TitleClose_Click(object? sender, RoutedEventArgs e) => Close();

    private void Outside_PointerPressed(object? sender, PointerPressedEventArgs e) => Close();

    // A click in the box isn't one outside it.
    private void Box_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
