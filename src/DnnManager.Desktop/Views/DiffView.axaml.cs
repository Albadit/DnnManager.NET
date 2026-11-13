using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DnnManager.Application.Deployment;

namespace DnnManager.Desktop.Views;

/// <summary>One side of a row of the diff: a line - its number, its mark (+ or -), its text and its tokens - or nothing.</summary>
public sealed class DiffSide : INotifyPropertyChanged
{
    public static DiffSide Empty => new() { Kind = "Empty" };

    private bool _expanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Same, Added, Removed or Empty - what colours it.</summary>
    public string Kind { get; init; } = "Same";
    public bool IsAdded => Kind == "Added";
    public bool IsRemoved => Kind == "Removed";
    public bool IsEmpty => Kind == "Empty";
    public string Number { get; init; } = "";

    /// <summary>What the row shows of the line: all of it, or its first <see cref="MaxShown"/> characters.</summary>
    public string Text { get; init; } = "";

    /// <summary>The whole line, when it is longer than <see cref="MaxShown"/> - a minified file's - else null.</summary>
    public string? Full { get; init; }

    /// <summary>Show more was clicked: the whole line, wrapped.</summary>
    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanShowMore)));
        }
    }

    /// <summary>The characters of a line shown at first - as VS Code stops at a long line's start with Show more.</summary>
    public const int MaxShown = 1000;

    public bool CanShowMore => Full is not null && !Expanded;

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
    public bool IsHunk => Hunk is not null;
    public bool IsLine => Hunk is null;

    /// <summary>Its index in the diff's rows.</summary>
    public int Index { get; init; }
    public DiffSide Left { get; init; } = DiffSide.Empty;
    public DiffSide Right { get; init; } = DiffSide.Empty;

    /// <summary>The line the unified view shows - the removed one, the added one or the one in both.</summary>
    public DiffSide Unified { get; init; } = DiffSide.Empty;

    /// <summary>How wide a column of line numbers is - as the largest number needs.</summary>
    public double NumberWidth { get; init; } = 40;

    /// <summary>A file shown as it is has one column of numbers: the old one isn't there.</summary>
    public double OldNumberWidth { get; init; } = 40;
    public bool HasOldNumbers => OldNumberWidth > 0;

    /// <summary>What a screen reader says of the row.</summary>
    public string Spoken => Hunk ?? Unified.Kind switch
    {
        "Added" => $"Added line {Right.Number}: {Unified.Text}",
        "Removed" => $"Removed line {Left.Number}: {Unified.Text}",
        _ when Left.Kind != Right.Kind => $"Line {Left.Number}: {Left.Text}; becomes {Right.Text}",
        _ => $"Line {Right.Number}: {Unified.Text}"
    };

    public override string ToString() => Spoken;
}

/// <summary>
/// A file's two versions, line by line (<see cref="TextDiff"/>) - unified or side by side, only the changes with three
/// lines around them or the whole file - coloured by its language (<see cref="SyntaxHighlighter"/>). Binary files and
/// ones too big to show say so instead. The rows are virtualized: a file of thousands of lines scrolls as one of ten.
/// The code is selected as in an editor: drag, Shift+click, double-click a word, triple-click a line, Ctrl+A - on one
/// side at a time; Ctrl+C or the menu copies it. As the Windows app's Controls/DiffView.
/// </summary>
public partial class DiffView : UserControl
{
    private IReadOnlyList<DiffLine>? _lines;
    private IReadOnlyList<Token[]> _oldTokens = [];
    private IReadOnlyList<Token[]> _newTokens = [];
    // The diff asked for last: one asked for while another is still worked out wins - the older is dropped.
    private int _version;
    private bool _plain, _split;

    public DiffView()
    {
        InitializeComponent();
        Rows.AddHandler(PointerPressedEvent, Rows_PointerPressed, RoutingStrategies.Tunnel);
        Rows.AddHandler(PointerMovedEvent, Rows_PointerMoved, RoutingStrategies.Tunnel);
        Rows.AddHandler(PointerReleasedEvent, Rows_PointerReleased, RoutingStrategies.Tunnel);
        Rows.AddHandler(PointerWheelChangedEvent, Rows_PointerWheel, RoutingStrategies.Tunnel);
        Rows.AddHandler(KeyDownEvent, Rows_KeyDown, RoutingStrategies.Tunnel);
        Rows.PointerCaptureLost += (_, _) => _selecting = false;
        Rows.SizeChanged += (_, _) => FitSplitScroll();
        RowsMenu.Opening += (_, _) => CopyItem.IsEnabled = HasSelection;
        SplitScroll.ValueChanged += (_, e) =>
        {
            SplitOffset = e.NewValue;
            Redraw();
        };
        ShowMessage("Choose a file to see what changed.");
    }

    // ─── What HighlightedText asks ─────────────────────────────────────────

    /// <summary>Side by side: how far both sides' code is moved left.</summary>
    internal double SplitOffset { get; private set; }

    /// <summary>The code's width, in the unified view - every row as wide as the longest line, so they scroll as one.</summary>
    internal double CodeWidth { get; private set; }

    /// <summary>How wide a whole line shown with Show more wraps: the code's width, or a side's.</summary>
    internal double WrapWidth { get; private set; } = 400;

    private Typeface? _typeface;
    private (double Size, double Width, double Height) _metrics;

    internal Typeface CodeTypeface => _typeface ??= new Typeface(
        this.TryFindResource("CodeFont", out var font) && font is FontFamily family ? family : FontFamily.Default);

    internal double CodeSize => FontSize;

    /// <summary>The width of a character of the code's font - the font has them all as wide - and a line's height.</summary>
    internal (double Width, double Height) CharSize()
    {
        if (_metrics.Size != FontSize)
        {
            var text = new FormattedText("0000000000", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, CodeTypeface, FontSize, Brushes.Black);
            _metrics = (FontSize, text.WidthIncludingTrailingWhitespace / 10, Math.Ceiling(text.Height));
        }
        return (_metrics.Width, _metrics.Height);
    }

    // ─── Showing ───────────────────────────────────────────────────────────

    /// <summary>Shows <paramref name="message"/> in place of a diff - nothing chosen, a binary file, reading it failed.</summary>
    public void ShowMessage(string message)
    {
        _version++;
        _lines = null;
        Rows.ItemsSource = null;
        Rows.IsVisible = false;
        SplitScroll.IsVisible = false;
        Message.Text = message;
        Message.IsVisible = true;
        ClearSelection([]);
        Titles.Text = "";
    }

    /// <summary>The message shown in place of a diff - empty while a diff is.</summary>
    internal string MessageText => Message.IsVisible ? Message.Text ?? "" : "";

    /// <summary>The rows shown - for a test.</summary>
    internal IReadOnlyList<DiffRow> ShownRows => _rows;

    /// <summary>Side by side now.</summary>
    internal bool IsSplit => _split;

    /// <summary>What is compared with what - "In the project → On the server   +1 −0".</summary>
    internal string TitleText => Titles.Text ?? "";

    // What the diff of two versions is - worked out off the UI thread.
    private sealed record Computed(IReadOnlyList<DiffLine> Lines, IReadOnlyList<Token[]> OldTokens, IReadOnlyList<Token[]> NewTokens,
        int Added, int Removed, bool NewlinesDiffer, string? Problem);

    /// <summary>
    /// Shows how <paramref name="oldBytes"/> (<paramref name="oldName"/>, null: no file) became <paramref name="newBytes"/>
    /// - for <paramref name="path"/>, whose extension decides the colours. Worked out off the UI thread; asked again
    /// meanwhile, only the last one shows.
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
        var titles = $"{oldName} → {newName}   +{computed.Added} −{computed.Removed}" + (computed.NewlinesDiffer ? "   (line endings differ)" : "");
        if (computed.Added == 0 && computed.Removed == 0)
        {
            ShowMessage(oldBytes is not null && newBytes is not null && !oldBytes.AsSpan().SequenceEqual(newBytes)
                ? "The lines are the same - only the encoding, a byte order mark or the line endings differ."
                : "The two versions are the same.");
            Titles.Text = titles;
            return;
        }
        Titles.Text = titles;
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
    private bool Plain
    {
        get => _plain;
        set
        {
            _plain = value;
            Modes.IsVisible = !value;
        }
    }

    /// <summary>
    /// Brings line <paramref name="line"/> (1-based) into view, a third down from the top, and selects
    /// <paramref name="length"/> characters of it from <paramref name="start"/> - a search result, as VS Code opens one.
    /// </summary>
    public void Reveal(int line, int start, int length)
    {
        var number = line.ToString(CultureInfo.InvariantCulture);
        var index = _rows.FindIndex(r => r.Hunk is null && r.Unified.Number == number);
        if (index < 0) return;
        var text = _rows[index].Unified.Visible;
        int Col(int i) => HighlightedText.Columns(text[..Math.Clamp(i, 0, text.Length)]);
        Select(0, index, Col(start), index, Col(start + length));
        Rows.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() =>
        {
            if (Rows.ContainerFromIndex(index) is not Control item || Scroller is not { } scroller
                || item.TranslatePoint(new Point(0, 0), Rows) is not { } top) return;
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Offset.Y + top.Y - Rows.Bounds.Height / 3));
        }, DispatcherPriority.Loaded);
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

    // Unified and Side by side are one choice of two; Whole file is on or off.
    private void Mode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender == UnifiedMode) SplitMode.IsChecked = UnifiedMode.IsChecked != true;
        else if (sender == SplitMode) UnifiedMode.IsChecked = SplitMode.IsChecked != true;
        if (_lines is not null) _ = RenderAsync();
    }

    /// <summary>Unified or side by side - as a click on its icon chooses it.</summary>
    internal void SetMode(bool split, bool? wholeFile = null)
    {
        SplitMode.IsChecked = split;
        UnifiedMode.IsChecked = !split;
        if (wholeFile is { } whole) WholeFile.IsChecked = whole;
        if (_lines is not null) _ = RenderAsync();
    }

    /// <summary>The rows as chosen - unified or side by side, the changes or the whole file - built off the UI thread.</summary>
    internal async Task RenderAsync()
    {
        if (_lines is not { } lines) return;
        var version = _version;
        var whole = _plain || WholeFile.IsChecked == true;
        var split = !_plain && SplitMode.IsChecked == true;
        var (oldTokens, newTokens) = (_oldTokens, _newTokens);
        var digit = CharSize().Width;
        var plain = _plain;
        var (rows, longest) = await Task.Run(() => BuildRows(lines, oldTokens, newTokens, whole, split, digit, plain));
        if (version != _version) return;
        // The mark (14), the code's padding (4 + 8) and the longest line.
        CodeWidth = Math.Ceiling(14 + HighlightedText.PadLeft + HighlightedText.PadRight + longest * digit);
        ScrollViewer.SetHorizontalScrollBarVisibility(Rows, split ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        _split = split;
        SplitScroll.Value = 0;
        SplitOffset = 0;
        ClearSelection(rows);
        Rows.ItemTemplate = (Avalonia.Controls.Templates.IDataTemplate)Resources[split ? "SplitRow" : "UnifiedRow"]!;
        Rows.ItemsSource = rows;
        Rows.IsVisible = true;
        Message.IsVisible = false;
        FitSplitScroll();
    }

    private static (List<DiffRow> Rows, int Longest) BuildRows(IReadOnlyList<DiffLine> lines, IReadOnlyList<Token[]> oldTokens,
        IReadOnlyList<Token[]> newTokens, bool whole, bool split, double digit, bool plain)
    {
        var rows = new List<DiffRow>();
        var longest = 0;
        var lastNumber = 0;
        foreach (var line in lines)
        {
            longest = Math.Max(longest, line.Text.Length > DiffSide.MaxShown
                ? HighlightedText.Columns(line.Text[..DiffSide.MaxShown]) + 22
                : HighlightedText.Columns(line.Text));
            lastNumber = Math.Max(lastNumber, Math.Max(line.OldNumber ?? 0, line.NewNumber ?? 0));
        }
        // As wide as the largest line number.
        var numberWidth = Math.Ceiling(Math.Max(2, lastNumber.ToString(CultureInfo.InvariantCulture).Length) * digit + 12);
        var oldNumberWidth = plain ? 0 : numberWidth;

        // A side of the next row (rows.Count): its index and pane are where a selection of it starts and ends.
        DiffSide Side(DiffLine? line, bool old, int pane)
        {
            if (line is null) return new DiffSide { Kind = "Empty", Row = rows.Count, Pane = pane };
            var number = old ? line.OldNumber : line.NewNumber;
            var tokens = number is { } n ? (old ? oldTokens : newTokens).ElementAtOrDefault(n - 1) ?? [] : [];
            var text = line.Text;
            return new DiffSide
            {
                Kind = plain ? "Same" : line.Kind.ToString(), Number = number?.ToString(CultureInfo.InvariantCulture) ?? "", Tokens = tokens,
                Row = rows.Count, Pane = pane,
                Text = text.Length > DiffSide.MaxShown ? text[..DiffSide.MaxShown] : text, Full = text.Length > DiffSide.MaxShown ? text : null
            };
        }

        // The whole file: every line, as one hunk - also a file with no change at all (one shown as it is), which has none.
        IReadOnlyList<DiffHunk> hunks = whole
            ? [new DiffHunk(1, lines.Count(l => l.Kind != DiffKind.Added), 1, lines.Count(l => l.Kind != DiffKind.Removed), lines)]
            : TextDiff.Hunks(lines, 3);
        foreach (var hunk in hunks)
        {
            if (!whole) rows.Add(new DiffRow { Hunk = hunk.Header, Index = rows.Count, NumberWidth = numberWidth, OldNumberWidth = oldNumberWidth });
            if (split)
            {
                foreach (var row in TextDiff.SideBySide(hunk.Lines))
                    rows.Add(new DiffRow
                    {
                        Left = Side(row.Left, old: true, pane: 0), Right = Side(row.Right, old: false, pane: 1), Index = rows.Count,
                        NumberWidth = numberWidth, OldNumberWidth = oldNumberWidth
                    });
                continue;
            }
            foreach (var line in hunk.Lines)
            {
                var side = line.Kind == DiffKind.Added ? Side(line, old: false, pane: 0) : Side(line, old: true, pane: 0);
                rows.Add(new DiffRow
                {
                    Left = line.OldNumber is null ? DiffSide.Empty : side,
                    Right = line.NewNumber is null ? DiffSide.Empty : Side(line, old: false, pane: 1),
                    Unified = side, Index = rows.Count, NumberWidth = numberWidth, OldNumberWidth = oldNumberWidth
                });
            }
        }
        return (rows, longest);
    }

    /// <summary>
    /// Side by side: the one scroll bar under both sides as far as the longest line goes beyond a side's width - and the
    /// width a whole line shown with Show more wraps at (a side's; the code's in the unified view).
    /// </summary>
    private void FitSplitScroll()
    {
        var numbers = _rows.FirstOrDefault()?.NumberWidth ?? 40;
        const double scrollBar = 14;
        var side = Math.Max(0, (Rows.Bounds.Width - 2 * numbers - scrollBar) / 2);
        var code = CodeWidth;
        var beyond = Math.Max(0, code - side);
        SplitScroll.IsVisible = _split && beyond > 1 && Rows.IsVisible;
        SplitScroll.Maximum = beyond;
        SplitScroll.ViewportSize = side;
        SplitScroll.SmallChange = 40;
        SplitScroll.LargeChange = Math.Max(40, side - 40);
        var wrap = _split ? Math.Max(40, side - 26) : Math.Max(40, Math.Max(code, Rows.Bounds.Width - 2 * numbers - scrollBar) - 26);
        if (Math.Abs(wrap - WrapWidth) > 0.5)
        {
            WrapWidth = wrap;
            foreach (var text in Codes()) text.InvalidateMeasure();
        }
    }

    // Side by side, Shift and the wheel move both sides sideways - as in VS Code.
    private void Rows_PointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_split || !SplitScroll.IsVisible || !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        SplitScroll.Value = Math.Clamp(SplitScroll.Value - delta * 50, 0, SplitScroll.Maximum);
        e.Handled = true;
    }

    private void ShowMore_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DiffSide side) side.Expanded = true;
        e.Handled = true;
    }

    private ScrollViewer? Scroller => Rows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private IEnumerable<HighlightedText> Codes() => Rows.GetVisualDescendants().OfType<HighlightedText>();

    // Draws the code on the screen again - the selection and the sideways shift are drawn by each line's HighlightedText.
    private void Redraw()
    {
        foreach (var code in Codes()) code.InvalidateVisual();
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
    }

    // The side of a row a selection is on.
    private DiffSide SideOf(DiffRow row) => !_split ? row.Unified : _pane == 0 ? row.Left : row.Right;

    /// <summary>The columns of <paramref name="side"/> selected - drawn under its code by <see cref="HighlightedText"/> - or null.</summary>
    internal (double From, double To)? SelectedColumns(DiffSide side)
    {
        if (!HasSelection || side.Row < 0 || side.Pane != _pane || side.IsEmpty) return null;
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
            if (side.IsEmpty) continue;
            var whole = side.Full ?? side.Text;
            var from = r == start.Row ? IndexAt(side.Visible, start.Col) : 0;
            var to = r != end.Row || end.Col == int.MaxValue ? whole.Length : IndexAt(side.Visible, end.Col);
            lines.Add(whole[Math.Min(from, whole.Length)..Math.Max(Math.Min(from, whole.Length), Math.Min(to, whole.Length))]);
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

    private void Rows_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Rows).Properties;
        if (!props.IsLeftButtonPressed || _rows.Count == 0) return;
        // Show more and the scroll bars keep their own clicks.
        if (e.Source is Visual source && (source.FindAncestorOfType<Button>(true) is not null || source.FindAncestorOfType<ScrollBar>(true) is not null)) return;
        var point = e.GetPosition(Rows);
        if (ItemAt(point) is not { } item) return;
        var pane = _split && point.X >= Rows.Bounds.Width / 2 ? 1 : 0;
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && pane == _pane;
        _pane = pane;
        var spot = SpotAt(point, item);
        if (extend) _caret = spot;
        else if (e.ClickCount == 2) SelectWord(spot);
        else if (e.ClickCount >= 3) SelectLine(spot.Row);
        else _anchor = _caret = spot;
        _selecting = e.ClickCount == 1;
        if (_selecting) e.Pointer.Capture(Rows);
        // The keyboard on the rows: Ctrl+C, Ctrl+A work from here.
        Rows.Focus();
        Redraw();
        e.Handled = true;
    }

    private void Rows_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_selecting) return;
        var point = e.GetPosition(Rows);
        // Dragged above or below the rows: they scroll, as an editor's do.
        if (Scroller is { } scroller)
        {
            if (point.Y < 0) scroller.LineUp();
            else if (point.Y > Rows.Bounds.Height) scroller.LineDown();
        }
        var spot = ItemAt(point) is { } item ? SpotAt(point, item)
            : point.Y > Rows.Bounds.Height / 2 ? new Spot(_rows.Count - 1, int.MaxValue) : new Spot(0, 0);
        if (spot == _caret) return;
        _caret = spot;
        Redraw();
    }

    private void Rows_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
    }

    private async void Rows_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key is Key.C or Key.Insert)
        {
            if (!HasSelection) return;
            e.Handled = true;
            await CopyAsync();
        }
        else if (command && e.Key == Key.A && _rows.Count > 0)
        {
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && HasSelection)
        {
            _anchor = _caret;
            Redraw();
            e.Handled = true;
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e) => await CopyAsync();

    private void SelectAll_Click(object? sender, RoutedEventArgs e) => SelectAll();

    /// <summary>Selects from a row's column to another's on one side (0 unified or left, 1 right) - as a drag does.</summary>
    internal void Select(int pane, int fromRow, int fromCol, int toRow, int toCol)
    {
        _pane = pane;
        _anchor = new Spot(fromRow, fromCol);
        _caret = new Spot(toRow, toCol);
        Redraw();
    }

    /// <summary>The text Ctrl+C copies now - empty when nothing is selected.</summary>
    internal string Selection => HasSelection ? SelectedText() : "";

    // Copies the selection to the clipboard.
    private async Task CopyAsync()
    {
        if (!HasSelection || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(SelectedText()); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy - nothing copied.
        }
    }

    /// <summary>Every line of the side the last click was on - Ctrl+A.</summary>
    internal void SelectAll()
    {
        if (_rows.Count == 0) return;
        _anchor = new Spot(0, 0);
        _caret = new Spot(_rows.Count - 1, int.MaxValue);
        Redraw();
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
        var x = Math.Clamp(point.X, 1, Math.Max(1, Rows.Bounds.Width - 16));
        var y = Math.Clamp(point.Y, 1, Math.Max(1, Rows.Bounds.Height - 2));
        return (Rows.InputHitTest(new Point(x, y)) as Visual)?.FindAncestorOfType<ListBoxItem>(true);
    }

    // The spot under a point on a row: the column of the selection's side under it, at the nearest character's edge.
    private Spot SpotAt(Point point, ListBoxItem item)
    {
        if (item.DataContext is not DiffRow row) return new Spot(_rows.Count - 1, int.MaxValue);
        // Unified: the one line; side by side: the left one, then the right one.
        var codes = item.GetVisualDescendants().OfType<HighlightedText>().Where(c => c.IsEffectivelyVisible).ToList();
        var code = codes.Count > _pane ? codes[_pane] : codes.FirstOrDefault();
        if (row.Hunk is not null || code is null || Rows.TranslatePoint(point, code) is not { } at) return new Spot(row.Index, 0);
        var text = SideOf(row).Visible;
        var col = code.ColumnAt(at.X);
        return new Spot(row.Index, HighlightedText.Columns(text[..IndexAt(text, col)]));
    }
}

/// <summary>
/// A line of code, coloured by its tokens (<see cref="SyntaxHighlighter"/>), for a row of <see cref="DiffView"/> - drawn
/// character cell by character cell in the code's monospace font, with the part of it selected under the text, as an
/// editor marks it. Side by side (<see cref="Shifted"/>) it is cut off at its side's edge and moved sideways with the
/// other side by the view's one scroll bar.
/// </summary>
public sealed class HighlightedText : Control
{
    public static readonly StyledProperty<DiffSide?> LineProperty = AvaloniaProperty.Register<HighlightedText, DiffSide?>(nameof(Line));

    public static readonly StyledProperty<bool> ShiftedProperty = AvaloniaProperty.Register<HighlightedText, bool>(nameof(Shifted));

    /// <summary>The code's padding, as WPF's: 4 before it, 8 after it.</summary>
    public const double PadLeft = 4, PadRight = 8;

    /// <summary>How many columns a tab takes - as the diff measures a line's width (<see cref="Columns"/>).</summary>
    public const int TabSize = 4;

    private DiffView? _owner;

    static HighlightedText()
    {
        AffectsMeasure<HighlightedText>(LineProperty, ShiftedProperty);
        AffectsRender<HighlightedText>(LineProperty);
    }

    // Light or dark chosen: the code's colours are the theme's.
    public HighlightedText() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public DiffSide? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    /// <summary>Side by side: as wide as its side, the code moved by the view's sideways scroll.</summary>
    public bool Shifted
    {
        get => GetValue(ShiftedProperty);
        set => SetValue(ShiftedProperty, value);
    }

    /// <summary>The columns <paramref name="text"/> takes, its tabs <see cref="TabSize"/> each.</summary>
    public static int Columns(string text)
    {
        var tabs = 0;
        foreach (var c in text)
            if (c == '\t') tabs++;
        return text.Length + tabs * (TabSize - 1);
    }

    /// <summary>A line as it is drawn: each tab <see cref="TabSize"/> spaces.</summary>
    internal static string Shown(string text) => text.Contains('\t') ? text.Replace("\t", new string(' ', TabSize)) : text;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty)
        {
            if (change.OldValue is DiffSide old) old.PropertyChanged -= Line_PropertyChanged;
            if (change.NewValue is DiffSide now) now.PropertyChanged += Line_PropertyChanged;
        }
    }

    // Show more clicked: the whole line, wrapped.
    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = this.FindAncestorOfType<DiffView>();
        ClipToBounds = Shifted;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _owner = null;
    }

    private (double Width, double Height) Cell => _owner?.CharSize() ?? (7.2, 16);

    private bool IsExpanded => Line is { Expanded: true, Full: not null };

    private TextLayout? WrappedLayout(double width) => Line is { } line && _owner is { } owner
        ? new TextLayout(Shown(line.Visible), owner.CodeTypeface, owner.CodeSize, Res.Brush("TextPrimary"), textWrapping: TextWrapping.Wrap,
            maxWidth: Math.Max(40, width))
        : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var (width, height) = Cell;
        if (Line is not { } line) return new Size(0, height);
        if (IsExpanded)
        {
            var layout = WrappedLayout(_owner?.WrapWidth ?? 400);
            return new Size(PadLeft + (layout?.Width ?? 0) + PadRight, Math.Max(height, layout?.Height ?? height));
        }
        if (Shifted) return new Size(0, height);
        var content = PadLeft + Columns(line.Visible) * width + PadRight;
        // Every row as wide as the longest line: the rows scroll sideways as one.
        return new Size(Math.Max(content, (_owner?.CodeWidth ?? 0) - 14), height);
    }

    /// <summary>
    /// The column nearest to <paramref name="x"/> (in this element) - between 0 and the line's end. A whole line wrapped
    /// by Show more: its start or its end.
    /// </summary>
    internal int ColumnAt(double x)
    {
        if (Line is not { } line) return 0;
        var length = Columns(line.Visible);
        if (IsExpanded) return x < Bounds.Width / 2 ? 0 : length;
        var shift = Shifted ? _owner?.SplitOffset ?? 0 : 0;
        return (int)Math.Clamp(Math.Round((x + shift - PadLeft) / Cell.Width), 0, length);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // Transparent, not nothing: a click past the end of a line still lands on it.
        context.FillRectangle(Brushes.Transparent, bounds);
        if (Line is not { } line || _owner is not { } owner) return;
        var (width, height) = Cell;
        var shift = Shifted ? owner.SplitOffset : 0;
        var selection = owner.SelectedColumns(line);
        var selectionBrush = Res.Brush("DiffSelectionBg");
        if (IsExpanded)
        {
            if (selection is not null) context.FillRectangle(selectionBrush, bounds);
            WrappedLayout(owner.WrapWidth)?.Draw(context, new Point(PadLeft, 0));
            return;
        }
        if (selection is var (from, to))
            context.FillRectangle(selectionBrush, new Rect(PadLeft + from * width - shift, 0, (to - from) * width, height));
        var text = line.Visible;
        var foreground = Res.Brush("TextPrimary");
        var at = 0;
        var col = 0;
        void Draw(int start, int length, IBrush brush)
        {
            var piece = Shown(text.Substring(start, length));
            var x = PadLeft + col * width - shift;
            col += piece.Length;
            // Only what can be seen: past the right edge, or wholly left of it, isn't drawn.
            if (x > bounds.Width || x + piece.Length * width < 0) return;
            context.DrawText(new FormattedText(piece, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, owner.CodeTypeface, owner.CodeSize, brush),
                new Point(x, 0));
        }
        foreach (var token in line.Tokens)
        {
            if (token.Start < at || token.Start + token.Length > text.Length) continue;
            if (token.Start > at) Draw(at, token.Start - at, foreground);
            Draw(token.Start, token.Length, SyntaxHighlighter.Brush(token.Kind) is { } key ? Res.Brush(key) : foreground);
            at = token.Start + token.Length;
        }
        if (at < text.Length) Draw(at, text.Length - at, foreground);
    }
}
