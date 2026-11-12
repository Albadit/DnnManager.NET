using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DnnManager.Application.Deployment;

namespace DnnManager.Presentation.Controls;

/// <summary>One side of a row of the diff: a line - its number, its mark (+ or -), its text and its tokens - or nothing.</summary>
public sealed class DiffSide
{
    public static readonly DiffSide Empty = new() { Kind = "Empty" };

    /// <summary>Same, Added, Removed or Empty - what colours it.</summary>
    public string Kind { get; init; } = "Same";
    public string Number { get; init; } = "";

    /// <summary>What the row shows of the line: all of it, or its first <see cref="MaxShown"/> characters.</summary>
    public string Text { get; init; } = "";

    /// <summary>The whole line, when it is longer than <see cref="MaxShown"/> - a minified file's - else null.</summary>
    public string? Full { get; init; }

    /// <summary>Show more was clicked: the whole line, wrapped.</summary>
    public bool Expanded { get; set; }

    /// <summary>
    /// The characters of a line shown at first - as VS Code stops at a long line's start with Show more: a minified file's
    /// line of 250 KB would make the row as wide as WPF can't draw (nothing shows) and wrap where it can't.
    /// </summary>
    public const int MaxShown = 1000;

    /// <summary>Show more's text: how much the whole line is.</summary>
    public string MoreText => Full is null ? "" : $"Show more ({System.Text.Encoding.UTF8.GetByteCount(Full) / 1024.0:0.0} KB)";
    public Token[] Tokens { get; init; } = [];
    public string Mark => Kind switch { "Added" => "+", "Removed" => "-", _ => " " };

    /// <summary>What the row shows of the line now: the whole line once Show more was clicked, else <see cref="Text"/>.</summary>
    public string Visible => Expanded && Full is not null ? Full : Text;

    /// <summary>Its row's index in the diff - where a selection starts and ends (-1: none).</summary>
    public int Row { get; init; } = -1;

    /// <summary>Which side it is: 0 the unified column or the left side, 1 the right side - a selection stays on one.</summary>
    public int Pane { get; init; }
}

/// <summary>A row of the diff: a hunk's header, or a line on each side (one for the unified view).</summary>
public sealed class DiffRow
{
    public string? Hunk { get; init; }

    /// <summary>Its index in the diff's rows.</summary>
    public int Index { get; init; }
    public DiffSide Left { get; init; } = DiffSide.Empty;
    public DiffSide Right { get; init; } = DiffSide.Empty;

    /// <summary>The line the unified view shows - the removed one, the added one or the one in both.</summary>
    public DiffSide Unified { get; init; } = DiffSide.Empty;

    public Visibility HunkVisibility => Hunk is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LineVisibility => Hunk is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What a screen reader says of the row.</summary>
    public string Spoken => Hunk ?? Unified.Kind switch
    {
        "Added" => $"Added line {Right.Number}: {Unified.Text}",
        "Removed" => $"Removed line {Left.Number}: {Unified.Text}",
        _ when Left.Kind != Right.Kind => $"Line {Left.Number}: {Left.Text}; becomes {Right.Text}",
        _ => $"Line {Right.Number}: {Unified.Text}"
    };
}

/// <summary>
/// A file's two versions, line by line (<see cref="TextDiff"/>) - unified or side by side, only the changes with three
/// lines around them or the whole file - coloured by its language (<see cref="SyntaxHighlighter"/>). Binary files and
/// ones too big to show say so instead. The rows are virtualized: a file of thousands of lines scrolls as one of ten.
/// </summary>
public partial class DiffView : UserControl
{
    private IReadOnlyList<DiffLine>? _lines;
    private IReadOnlyList<Token[]> _oldTokens = [];
    private IReadOnlyList<Token[]> _newTokens = [];
    // The diff asked for last: one asked for while another is still worked out wins - the older is dropped.
    private int _version;

    public DiffView()
    {
        InitializeComponent();
        // A group of its own: radio buttons of one name are one group in the whole window - two diff views would share it.
        UnifiedMode.GroupName = SplitMode.GroupName = "DiffMode" + Guid.NewGuid().ToString("N");
        ShowMessage("Choose a file to see what changed.");
    }

    /// <summary>Shows <paramref name="message"/> in place of a diff - nothing chosen, a binary file, reading it failed.</summary>
    public void ShowMessage(string message)
    {
        _version++;
        _lines = null;
        Rows.ItemsSource = null;
        Rows.Visibility = Visibility.Collapsed;
        SplitScroll.Visibility = Visibility.Collapsed;
        Message.Text = message;
        Message.Visibility = Visibility.Visible;
        ClearSelection([]);
        Titles.Text = "";
    }

    // What the diff of two versions is - worked out off the UI thread.
    private sealed record Computed(IReadOnlyList<DiffLine> Lines, IReadOnlyList<Token[]> OldTokens, IReadOnlyList<Token[]> NewTokens,
        int Added, int Removed, bool NewlinesDiffer, string? Problem);

    /// <summary>
    /// Shows how <paramref name="oldBytes"/> (<paramref name="oldName"/>, null: no file) became <paramref name="newBytes"/>
    /// - for <paramref name="path"/>, whose extension decides the colours. The decoding, the colours and the diff are worked
    /// out off the UI thread, so a big file doesn't hold the window up; asked again meanwhile, only the last one shows.
    /// </summary>
    public async Task ShowDiffAsync(string path, byte[]? oldBytes, byte[]? newBytes, string oldName, string newName)
    {
        var version = ++_version;
        var computed = await Task.Run(() => Compute(path, oldBytes, newBytes, oldName, newName));
        if (version != _version) return;
        Plain = false;
        if (computed.Problem is { } problem)
        {
            ShowMessage(problem);
            return;
        }
        _lines = computed.Lines;
        _oldTokens = computed.OldTokens;
        _newTokens = computed.NewTokens;
        Titles.Text = $"{oldName} → {newName}   +{computed.Added} −{computed.Removed}" + (computed.NewlinesDiffer ? "   (line endings differ)" : "");
        if (computed.Added == 0 && computed.Removed == 0)
        {
            ShowMessage(oldBytes is not null && newBytes is not null && !oldBytes.AsSpan().SequenceEqual(newBytes)
                ? "The lines are the same - only the encoding, a byte order mark or the line endings differ."
                : "The two versions are the same.");
            return;
        }
        await RenderAsync();
    }

    /// <summary>
    /// Shows <paramref name="bytes"/> - a project's file opened from the Explorer or a search result - as it is: every
    /// line, one column of numbers, coloured by its language; no marks, no modes.
    /// </summary>
    public async Task ShowFileAsync(string path, byte[] bytes, string name)
    {
        var version = ++_version;
        var computed = await Task.Run(() => Compute(path, bytes, bytes, name, name));
        if (version != _version) return;
        Plain = true;
        if (computed.Problem is not null)
        {
            ShowMessage($"{path} isn't text (or is larger than {TextContent.MaxBytes / (1024 * 1024)} MB) - it can't be shown.");
            return;
        }
        if (computed.Lines.Count == 0)
        {
            ShowMessage($"{path} is empty.");
            return;
        }
        _lines = computed.Lines;
        _oldTokens = computed.OldTokens;
        _newTokens = computed.NewTokens;
        Titles.Text = $"{name}   {computed.Lines.Count:N0} lines";
        await RenderAsync();
    }

    // A file shown as it is (ShowFileAsync), not a diff: the whole file, unified, one column of numbers, no modes.
    private bool _plain;

    private bool Plain
    {
        get => _plain;
        set
        {
            _plain = value;
            Modes.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// Brings line <paramref name="line"/> (1-based) into view, a third down from the top, and selects
    /// <paramref name="length"/> characters of it from <paramref name="start"/> - a search result, as VS Code opens one.
    /// </summary>
    public void Reveal(int line, int start, int length)
    {
        var number = line.ToString();
        var index = _rows.FindIndex(r => r.Hunk is null && r.Unified.Number == number);
        if (index < 0) return;
        var row = _rows[index];
        var text = row.Unified.Visible;
        int Col(int i) => HighlightedText.Columns(text[..Math.Clamp(i, 0, text.Length)]);
        Select(0, index, Col(start), index, Col(start + length));
        Rows.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() =>
        {
            if (Rows.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement item
                || Descendants<ScrollViewer>(Rows).FirstOrDefault() is not { } scroller) return;
            var top = item.TranslatePoint(new Point(0, 0), Rows).Y;
            scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top - Rows.ActualHeight / 3));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The line selected first, 1-based - for a test - or 0.</summary>
    internal int SelectedLine => HasSelection && Ordered.Start.Row < _rows.Count && int.TryParse(SideOf(_rows[Ordered.Start.Row]).Number, out var n) ? n : 0;

    private static Computed Compute(string path, byte[]? oldBytes, byte[]? newBytes, string oldName, string newName)
    {
        var oldText = oldBytes is null ? TextContent.Empty : TextContent.From(oldBytes);
        var newText = newBytes is null ? TextContent.Empty : TextContent.From(newBytes);
        if (oldText is null || newText is null)
            return new Computed([], [], [], 0, 0, false,
                $"{path} isn't text (or is larger than {TextContent.MaxBytes / (1024 * 1024)} MB) - " +
                $"{Describe(oldBytes, oldName)}, {Describe(newBytes, newName)}.");
        var language = SyntaxHighlighter.For(path);
        var lines = TextDiff.Compute(oldText.Lines, newText.Lines);
        return new Computed(lines, SyntaxHighlighter.Tokenize(oldText.Lines, language), SyntaxHighlighter.Tokenize(newText.Lines, language),
            lines.Count(l => l.Kind == DiffKind.Added), lines.Count(l => l.Kind == DiffKind.Removed),
            oldText.Newline != newText.Newline && oldBytes is not null && newBytes is not null, null);
    }

    private static string Describe(byte[]? bytes, string name) => bytes is null ? $"none in {name}" : $"{bytes.Length:N0} bytes in {name}";

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && _lines is not null) _ = RenderAsync();
    }

    /// <summary>The rows as chosen - unified or side by side, the changes or the whole file - built off the UI thread.</summary>
    private async Task RenderAsync()
    {
        if (_lines is not { } lines) return;
        var version = _version;
        var whole = _plain || WholeFile.IsChecked == true;
        var split = !_plain && SplitMode.IsChecked == true;
        var (oldTokens, newTokens) = (_oldTokens, _newTokens);
        var (rows, longest, lastNumber) = await Task.Run(() => BuildRows(lines, oldTokens, newTokens, whole, split));
        if (version != _version) return;
        // As wide as the largest line number and the longest line - the code scrolls sideways, as in VS Code.
        var digit = DigitWidth();
        Resources["NumberWidth"] = new GridLength(Math.Ceiling(Math.Max(2, lastNumber.ToString().Length) * digit + 12));
        Resources["OldNumberWidth"] = _plain ? new GridLength(0) : Resources["NumberWidth"];
        // The mark (14), the code's padding (4 + 8) and the longest line.
        Resources["CodeWidth"] = Math.Ceiling(14 + 12 + longest * digit);
        // Unified: the rows scroll sideways. Side by side: each side half the width, both moved by SplitScroll.
        ScrollViewer.SetHorizontalScrollBarVisibility(Rows, split ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        _split = split;
        SplitScroll.Value = 0;
        FitSplitScroll();
        ClearSelection(rows);
        Rows.ItemTemplate = (DataTemplate)Resources[split ? "SplitRow" : "UnifiedRow"];
        Rows.ItemsSource = rows;
        Rows.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        FitSplitScroll();
    }

    private static (List<DiffRow> Rows, int Longest, int LastNumber) BuildRows(IReadOnlyList<DiffLine> lines, IReadOnlyList<Token[]> oldTokens,
        IReadOnlyList<Token[]> newTokens, bool whole, bool split)
    {
        var rows = new List<DiffRow>();

        // A side of the next row (rows.Count): its index and pane are where a selection of it starts and ends.
        DiffSide Side(DiffLine? line, bool old, int pane)
        {
            if (line is null) return DiffSide.Empty;
            var number = old ? line.OldNumber : line.NewNumber;
            var tokens = number is { } n ? (old ? oldTokens : newTokens).ElementAtOrDefault(n - 1) ?? [] : [];
            var text = line.Text;
            return new DiffSide
            {
                Kind = line.Kind.ToString(), Number = number?.ToString() ?? "", Tokens = tokens, Row = rows.Count, Pane = pane,
                Text = text.Length > DiffSide.MaxShown ? text[..DiffSide.MaxShown] : text, Full = text.Length > DiffSide.MaxShown ? text : null
            };
        }

        var longest = 0;
        var lastNumber = 0;
        // The whole file: every line, as one hunk - also a file with no change at all (one shown as it is), which has none.
        IReadOnlyList<DiffHunk> hunks = whole
            ? [new DiffHunk(1, lines.Count(l => l.Kind != DiffKind.Added), 1, lines.Count(l => l.Kind != DiffKind.Removed), lines)]
            : TextDiff.Hunks(lines, 3);
        foreach (var hunk in hunks)
        {
            if (!whole) rows.Add(new DiffRow { Hunk = hunk.Header, Index = rows.Count });
            foreach (var line in hunk.Lines)
            {
                longest = Math.Max(longest, line.Text.Length > DiffSide.MaxShown
                    ? HighlightedText.Columns(line.Text[..DiffSide.MaxShown]) + 22
                    : HighlightedText.Columns(line.Text));
                lastNumber = Math.Max(lastNumber, Math.Max(line.OldNumber ?? 0, line.NewNumber ?? 0));
            }
            if (split)
            {
                foreach (var row in TextDiff.SideBySide(hunk.Lines))
                    rows.Add(new DiffRow { Left = Side(row.Left, old: true, pane: 0), Right = Side(row.Right, old: false, pane: 1), Index = rows.Count });
            }
            else
            {
                foreach (var line in hunk.Lines)
                {
                    var side = line.Kind == DiffKind.Added ? Side(line, old: false, pane: 0) : Side(line, old: true, pane: 0);
                    rows.Add(new DiffRow
                    {
                        Left = line.OldNumber is null ? DiffSide.Empty : side, Right = line.NewNumber is null ? DiffSide.Empty : Side(line, old: false, pane: 1),
                        Unified = side, Index = rows.Count
                    });
                }
            }
        }
        return (rows, longest, lastNumber);
    }

    private bool _split;

    /// <summary>
    /// Side by side: the one scroll bar under both sides as far as the longest line goes beyond a side's width - and the
    /// width a whole line shown with Show more wraps at (a side's; the code's in the unified view).
    /// </summary>
    private void FitSplitScroll()
    {
        var code = (double)Resources["CodeWidth"];
        var numbers = ((GridLength)Resources["NumberWidth"]).Value;
        // A side: half of what is left beside the numbers and the vertical scroll bar; its code after the mark and padding.
        var side = Math.Max(0, (Rows.ActualWidth - 2 * numbers - SystemParameters.VerticalScrollBarWidth) / 2);
        var beyond = Math.Max(0, code - side);
        SplitScroll.Visibility = _split && beyond > 1 && Rows.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        SplitScroll.Maximum = beyond;
        SplitScroll.ViewportSize = side;
        SplitScroll.SmallChange = 40;
        SplitScroll.LargeChange = Math.Max(40, side - 40);
        Resources["WrapWidth"] = _split ? Math.Max(40, side - 26) : Math.Max(40, code - 26);
    }

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e) => FitSplitScroll();

    private void SplitScroll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ((TranslateTransform)Resources["CodeShift"]).X = -e.NewValue;

    /// <summary>Side by side, Shift and the wheel move both sides sideways - as in VS Code.</summary>
    private void Rows_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!_split || SplitScroll.Visibility != Visibility.Visible || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
            return;
        SplitScroll.Value = Math.Clamp(SplitScroll.Value - e.Delta, 0, SplitScroll.Maximum);
        e.Handled = true;
    }

    // The width of a character of the code's font - the font has them all as wide.
    private double DigitWidth()
    {
        var text = new FormattedText("0000000000", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)Resources["Mono"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace / 10;
    }

    // ─── Selecting the code, as in an editor ──────────────────────────────

    // A place in the code: a row and a column of it as shown (a tab is HighlightedText.TabSize columns).
    private readonly record struct Spot(int Row, int Col) : IComparable<Spot>
    {
        public int CompareTo(Spot other) => Row != other.Row ? Row.CompareTo(other.Row) : Col.CompareTo(other.Col);
    }

    private List<DiffRow> _rows = [];
    // Where the selection started and where it ends now - on one side (_pane) of the rows.
    private Spot _anchor, _caret;
    private int _pane;
    // The mouse button held down on the code: moving it moves _caret.
    private bool _selecting;

    private bool HasSelection => _anchor != _caret;
    private (Spot Start, Spot End) Ordered => _anchor.CompareTo(_caret) <= 0 ? (_anchor, _caret) : (_caret, _anchor);

    private void ClearSelection(List<DiffRow> rows)
    {
        _rows = rows;
        _anchor = _caret = default;
        _pane = 0;
        _selecting = false;
        if (Rows.IsMouseCaptured) Rows.ReleaseMouseCapture();
    }

    // The side of a row a selection is on.
    private DiffSide SideOf(DiffRow row) => !_split ? row.Unified : _pane == 0 ? row.Left : row.Right;

    /// <summary>The columns of <paramref name="side"/> selected - drawn under its code by <see cref="HighlightedText"/> - or null.</summary>
    internal (double From, double To)? SelectedColumns(DiffSide side)
    {
        if (!HasSelection || side.Row < 0 || side.Pane != _pane || side.Kind == "Empty") return null;
        var (start, end) = Ordered;
        if (side.Row < start.Row || side.Row > end.Row) return null;
        var length = HighlightedText.Columns(side.Visible);
        double from = side.Row == start.Row ? Math.Min(start.Col, length) : 0;
        // A line selected past its end: half a column more, the line break, as VS Code marks it.
        var to = side.Row == end.Row ? Math.Min(end.Col, length) : length + 0.5;
        return to > from ? (from, to) : null;
    }

    /// <summary>The text selected - each line whole as it is in the file (tabs, and past Show more), one per line.</summary>
    private string SelectedText()
    {
        var (start, end) = Ordered;
        var lines = new List<string>();
        for (var r = start.Row; r <= end.Row && r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (row.Hunk is not null) continue;
            var side = SideOf(row);
            if (side.Kind == "Empty") continue;
            var whole = side.Full ?? side.Text;
            var from = r == start.Row ? IndexAt(side.Visible, start.Col) : 0;
            var to = r != end.Row || end.Col == int.MaxValue ? whole.Length : IndexAt(side.Visible, end.Col);
            lines.Add(whole[from..Math.Max(from, to)]);
        }
        return string.Join(Environment.NewLine, lines);
    }

    // The character of text at a column as shown - a column inside a tab goes to the nearer side of it.
    private static int IndexAt(string text, int col)
    {
        var at = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var width = text[i] == '\t' ? HighlightedText.TabSize : 1;
            if (col < at + width) return col - at <= width / 2 ? i : i + 1;
            at += width;
        }
        return text.Length;
    }

    // Redraws the rows on the screen - the selection is drawn by each line's HighlightedText.
    private void RefreshSelection()
    {
        foreach (var code in Descendants<HighlightedText>(Rows)) code.UpdateSelection();
    }

    private void Rows_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Show more and the scroll bars keep their own clicks.
        if (_rows.Count == 0 || Ancestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource) is not null
            || Ancestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource) is not null) return;
        var point = e.GetPosition(Rows);
        if (ItemAt(point) is not { } item) return;
        var pane = _split && point.X >= (Rows.ActualWidth - SystemParameters.VerticalScrollBarWidth) / 2 ? 1 : 0;
        var extend = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 && pane == _pane;
        _pane = pane;
        var spot = SpotAt(point, item);
        if (extend) _caret = spot;
        else if (e.ClickCount == 2) SelectWord(spot);
        else if (e.ClickCount >= 3) SelectLine(spot.Row);
        else _anchor = _caret = spot;
        _selecting = e.ClickCount == 1;
        if (_selecting) Rows.CaptureMouse();
        // The keyboard on the row: Ctrl+C, Ctrl+A and the arrow keys work from here.
        item.Focus();
        RefreshSelection();
        e.Handled = true;
    }

    private void Rows_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_selecting) return;
        var point = e.GetPosition(Rows);
        // Dragged above or below the rows: they scroll, as an editor's do.
        if (Descendants<ScrollViewer>(Rows).FirstOrDefault() is { } scroller)
        {
            if (point.Y < 0) scroller.LineUp();
            else if (point.Y > Rows.ActualHeight) scroller.LineDown();
        }
        var spot = ItemAt(point) is { } item ? SpotAt(point, item)
            : point.Y > Rows.ActualHeight / 2 ? new Spot(_rows.Count - 1, int.MaxValue) : new Spot(0, 0);
        if (spot == _caret) return;
        _caret = spot;
        RefreshSelection();
    }

    private void Rows_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        Rows.ReleaseMouseCapture();
    }

    private void Rows_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => _selecting = false;

    private void Rows_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (ctrl && (e.Key == System.Windows.Input.Key.C || e.Key == System.Windows.Input.Key.Insert))
        {
            e.Handled = Copy();
        }
        else if (ctrl && e.Key == System.Windows.Input.Key.A && _rows.Count > 0)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && HasSelection)
        {
            _anchor = _caret;
            RefreshSelection();
            e.Handled = true;
        }
    }

    private void Rows_ContextMenuOpening(object sender, ContextMenuEventArgs e) => CopyItem.IsEnabled = HasSelection;

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();

    /// <summary>Selects from a row's column to another's on one side (0 unified or left, 1 right) - as a drag does.</summary>
    internal void Select(int pane, int fromRow, int fromCol, int toRow, int toCol)
    {
        _pane = pane;
        _anchor = new Spot(fromRow, fromCol);
        _caret = new Spot(toRow, toCol);
        RefreshSelection();
    }

    /// <summary>The text Ctrl+C copies now - empty when nothing is selected.</summary>
    internal string Selection => HasSelection ? SelectedText() : "";

    // Copies the selection; false when nothing is selected.
    private bool Copy()
    {
        if (!HasSelection) return false;
        try { Clipboard.SetText(SelectedText()); }
        catch (System.Runtime.InteropServices.COMException) { /* the clipboard is busy - nothing copied */ }
        return true;
    }

    /// <summary>Every line of the side the last click was on - Ctrl+A.</summary>
    internal void SelectAll()
    {
        if (_rows.Count == 0) return;
        _anchor = new Spot(0, 0);
        _caret = new Spot(_rows.Count - 1, int.MaxValue);
        RefreshSelection();
    }

    // The word (letters, digits, _) or the run of spaces at a spot - or the one other character there.
    private void SelectWord(Spot spot)
    {
        var text = HighlightedText.Shown(SideOf(_rows[spot.Row]).Visible);
        var i = Math.Min(spot.Col, text.Length - 1);
        if (i < 0)
        {
            _anchor = _caret = spot;
            return;
        }
        static int Class(char c) => char.IsLetterOrDigit(c) || c == '_' ? 0 : char.IsWhiteSpace(c) ? 1 : 2;
        var kind = Class(text[i]);
        var (from, to) = (i, i + 1);
        if (kind != 2)
        {
            while (from > 0 && Class(text[from - 1]) == kind) from--;
            while (to < text.Length && Class(text[to]) == kind) to++;
        }
        _anchor = new Spot(spot.Row, from);
        _caret = new Spot(spot.Row, to);
    }

    // A whole line and its line break, as a triple-click in VS Code.
    private void SelectLine(int row)
    {
        _anchor = new Spot(row, 0);
        _caret = row + 1 < _rows.Count ? new Spot(row + 1, 0) : new Spot(row, int.MaxValue);
    }

    // The row under a point of the list - kept inside it, so a drag past its edges still finds one.
    private ListBoxItem? ItemAt(Point point)
    {
        var x = Math.Clamp(point.X, 1, Math.Max(1, Rows.ActualWidth - SystemParameters.VerticalScrollBarWidth - 2));
        var y = Math.Clamp(point.Y, 1, Math.Max(1, Rows.ActualHeight - 2));
        return Ancestor<ListBoxItem>(Rows.InputHitTest(new Point(x, y)));
    }

    // The spot under a point on a row: the column of the selection's side under it, at the nearest character's edge.
    private Spot SpotAt(Point point, ListBoxItem item)
    {
        if (item.DataContext is not DiffRow row) return new Spot(_rows.Count - 1, int.MaxValue);
        // Unified: the one line; side by side: the left one, then the right one.
        var codes = Descendants<HighlightedText>(item).ToList();
        var code = codes.Count > _pane ? codes[_pane] : codes.FirstOrDefault();
        if (row.Hunk is not null || code is null) return new Spot(row.Index, 0);
        var text = SideOf(row).Visible;
        var col = code.ColumnAt(Rows.TranslatePoint(point, code).X);
        return new Spot(row.Index, HighlightedText.Columns(text[..IndexAt(text, col)]));
    }

    private static T? Ancestor<T>(object? source) where T : DependencyObject
    {
        var node = source as DependencyObject;
        while (node is not null and not T)
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }
}
