using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The Logs tab's text: a log's lines in its level's colour, drawn only where they are on screen (a log keeps up to
/// 50,000). Text is selected like in an editor - drag (across lines, scrolling at the edges), Shift+click to extend,
/// double-click a word, triple-click a line, Ctrl+A everything - and copied with Ctrl+C or the right-click menu. The
/// search's matches are highlighted. It scrolls itself inside a ScrollViewer (<see cref="IScrollInfo"/>), and follows
/// the newest line while it is at the end.
/// <para>
/// Lines wrap at the view's width, after the last space that fits (a word longer than a row is broken), so there is
/// nothing to scroll sideways. The font is fixed-width: a row holds a known number of characters, and where each of a
/// line's rows starts is worked out when the width or the font changes - for new lines only when lines are added.
/// Copying gives the lines as they are, without the breaks.
/// </para>
/// </summary>
internal sealed class LogView : FrameworkElement, IScrollInfo
{
    // Room around the text - above the first line and below the last, as in the terminal; part of the scrolled extent.
    private static readonly Thickness Padding = new(10, 8, 10, 8);

    private readonly List<LogLine> _lines = [];
    private readonly DispatcherTimer _edgeScroll;
    private Typeface _typeface = new("Consolas");
    // A row is 1.6 × the font size, as the Output tab's lines; the text is drawn in its middle (_textTop down).
    private double _fontSize = 13, _charWidth = 7, _lineHeight = 21, _textTop = 2;
    // Wrapping: the characters a row holds, where each line's rows start (null for a line of one row), and the row each
    // line starts on - with the rows of all lines after the last.
    private int _rowWidth = int.MaxValue;
    private readonly List<int[]?> _rowStarts = [];
    private readonly List<int> _firstRow = [];
    private int _rows;
    private Vector _offset;
    private Size _viewport;
    // Whether new lines scroll into view: while the view is at its end.
    private bool _following = true;
    // A selection from where it began (the mouse went down) to where it ends: (line, position between characters).
    private (int Line, int Column)? _anchor, _caret;
    private Point _mouse;
    // A search's matches (line, column, length), in reading order, and the current one.
    private IReadOnlyList<(int Line, int Column, int Length)> _matches = [];
    private int _currentMatch = -1;

    public LogView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        _edgeScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => DragTo(_mouse), Dispatcher);
        _edgeScroll.Stop();

        var copy = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => Copy();
        var selectAll = new MenuItem { Header = "Select all", InputGestureText = "Ctrl+A" };
        selectAll.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { Items = { copy, selectAll } };
        ContextMenuOpening += (_, _) => copy.IsEnabled = HasSelection;

        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    public int Count => _lines.Count;

    public LogLine this[int index] => _lines[index];

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    public void SetFont(FontFamily family, double size)
    {
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fontSize = size;
        var sample = Text("MMMMMMMMMM", Brushes.Black);
        _charWidth = sample.WidthIncludingTrailingWhitespace / 10;
        _lineHeight = Math.Ceiling(Math.Max(sample.Height, size * 1.6));
        _textTop = Math.Floor((_lineHeight - sample.Height) / 2);
        // Another character width: another number of them to a row.
        _rowWidth = RowWidthFor(_viewport.Width);
        Rewrap();
        Changed();
    }

    // ─── The lines ────────────────────────────────────────────────────────

    public void Clear()
    {
        _lines.Clear();
        _rowStarts.Clear();
        _firstRow.Clear();
        _rows = 0;
        _anchor = _caret = null;
        _matches = [];
        _currentMatch = -1;
        _offset = default;
        _following = true;
        Changed();
    }

    /// <summary>Adds <paramref name="lines"/> at the end - the oldest go beyond <paramref name="max"/> - and shows them while at the end.</summary>
    public void Append(IEnumerable<LogLine> lines, int max)
    {
        foreach (var line in lines)
        {
            _lines.Add(line);
            AddRows(line.Text);
        }
        if (_lines.Count > max)
        {
            // A tenth at once, so this isn't done for every new line.
            var drop = Math.Min(_lines.Count, _lines.Count - max + max / 10);
            var droppedRows = drop < _lines.Count ? _firstRow[drop] : _rows;
            _lines.RemoveRange(0, drop);
            Rewrap();
            (int, int)? Shift((int Line, int Column)? at) => at is { } p ? p.Line - drop < 0 ? (0, 0) : (p.Line - drop, p.Column) : null;
            _anchor = Shift(_anchor);
            _caret = Shift(_caret);
            // The search looks again (ContentChanged).
            _matches = [];
            _currentMatch = -1;
            _offset.Y = Math.Max(0, _offset.Y - droppedRows * _lineHeight);
        }
        if (_following) _offset.Y = MaxOffsetY;
        Changed();
    }

    /// <summary>Back at the newest line, following the new ones again.</summary>
    public void ScrollToEnd()
    {
        _following = true;
        _offset.Y = MaxOffsetY;
        Changed();
    }

    private void Changed()
    {
        ClampOffset();
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    // ─── Drawing ──────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Another width: the lines wrap again - keeping the line at the top of the view there.
        var rowWidth = RowWidthFor(finalSize.Width);
        if (rowWidth != _rowWidth)
        {
            var top = LineOfRow(RowAt(_offset.Y));
            _rowWidth = rowWidth;
            Rewrap();
            if (!_following && top < _lines.Count) _offset.Y = Padding.Top + _firstRow[top] * _lineHeight;
        }
        _viewport = finalSize;
        // Resized (the panel maximized, the splitter dragged): still at the end if it was.
        if (_following) _offset.Y = MaxOffsetY;
        ClampOffset();
        ScrollOwner?.InvalidateScrollInfo();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent but there, so the mouse finds it everywhere.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_lines.Count == 0) return;

        var brushes = new Dictionary<LogLineLevel, Brush>
        {
            [LogLineLevel.Normal] = (Brush)FindResource("LogFg"),
            [LogLineLevel.Error] = (Brush)FindResource("LogFail"),
            [LogLineLevel.Warning] = (Brush)FindResource("LogWarn"),
            [LogLineLevel.Note] = (Brush)FindResource("LogMuted"),
        };
        var selection = (Brush)FindResource("Accent");
        var (from, to) = OrderedSelection();
        // Only the rows on screen are laid out.
        var firstRow = Math.Max(0, RowAt(_offset.Y));
        var lastRow = Math.Min(_rows - 1, RowAt(_offset.Y + _viewport.Height));
        var index = LineOfRow(firstRow);
        for (var row = firstRow; row <= lastRow && index < _lines.Count; row++)
        {
            while (index + 1 < _lines.Count && _firstRow[index + 1] <= row) index++;
            var line = _lines[index];
            var (start, end) = RowSpan(index, row - _firstRow[index]);
            var lastOfLine = end == line.Text.Length;
            var y = Padding.Top + row * _lineHeight - _offset.Y;
            DrawMatches(dc, index, start, end, y);

            if (from is { } a && to is { } b && index >= a.Line && index <= b.Line)
            {
                var selStart = Math.Max(start, index == a.Line ? a.Column : 0);
                // A selection going on to the next line takes this line's end with it - shown as one more character.
                var selEnd = Math.Min(lastOfLine ? end + 1 : end, index == b.Line ? b.Column : line.Text.Length + 1);
                if (selEnd > selStart)
                {
                    dc.PushOpacity(0.35);
                    dc.DrawRectangle(selection, null, new Rect(X(selStart, start), y, (selEnd - selStart) * _charWidth, _lineHeight));
                    dc.Pop();
                }
            }

            if (end > start) dc.DrawText(Text(line.Text.Substring(start, end - start), brushes[line.Level]), new Point(Padding.Left, y + _textTop));
        }
    }

    /// <summary>Where a column of a row is drawn: <paramref name="rowStart"/> is the column the row starts with.</summary>
    private double X(int column, int rowStart) => Padding.Left + (column - rowStart) * _charWidth;

    private void DrawMatches(DrawingContext dc, int lineIndex, int rowStart, int rowEnd, double y)
    {
        if (_matches.Count == 0) return;
        int lo = 0, hi = _matches.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_matches[mid].Line < lineIndex) lo = mid + 1; else hi = mid;
        }
        for (var i = lo; i < _matches.Count && _matches[i].Line == lineIndex; i++)
        {
            // The part of the match on this row - a match can go on to the next one.
            var (_, column, length) = _matches[i];
            var start = Math.Max(column, rowStart);
            var end = Math.Min(column + length, rowEnd);
            if (end <= start) continue;
            dc.DrawRectangle((Brush)FindResource(i == _currentMatch ? "SearchCurrentBg" : "SearchMatchBg"), null,
                new Rect(X(start, rowStart), y, (end - start) * _charWidth, _lineHeight));
        }
    }

    private FormattedText Text(string text, Brush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _typeface, _fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ─── Search ───────────────────────────────────────────────────────────

    public int Find(SearchQuery query)
    {
        var matches = new List<(int, int, int)>();
        if (!query.IsEmpty)
            for (var index = 0; index < _lines.Count; index++)
                foreach (var (start, length) in query.Matches(_lines[index].Text))
                    matches.Add((index, start, length));
        _matches = matches;
        _currentMatch = -1;
        InvalidateVisual();
        return matches.Count;
    }

    /// <summary>Marks match <paramref name="index"/> - and, with <paramref name="reveal"/>, scrolls to it when it is out of view.</summary>
    public void ShowMatch(int index, bool reveal = true)
    {
        if (index < 0 || index >= _matches.Count) return;
        _currentMatch = index;
        if (reveal) Reveal(_matches[index].Line, _matches[index].Column, _matches[index].Length);
        InvalidateVisual();
    }

    public void ClearSearch()
    {
        _matches = [];
        _currentMatch = -1;
        InvalidateVisual();
    }

    /// <summary>The row with <paramref name="column"/> of <paramref name="line"/> on screen (in the middle), if it isn't.</summary>
    private void Reveal(int line, int column, int length)
    {
        var top = Padding.Top + (_firstRow[line] + RowInLine(line, column)) * _lineHeight;
        if (top < _offset.Y || top + _lineHeight > _offset.Y + _viewport.Height)
            _offset.Y = top - (_viewport.Height - _lineHeight) / 2;
        ClampOffset();
        _following = AtEnd;
        Changed();
    }

    // ─── Selecting and copying ────────────────────────────────────────────

    public bool HasSelection => _anchor is { } a && _caret is { } c && a != c;

    private ((int Line, int Column)? From, (int Line, int Column)? To) OrderedSelection()
    {
        if (!HasSelection) return (null, null);
        var (a, c) = (_anchor!.Value, _caret!.Value);
        return a.Line < c.Line || (a.Line == c.Line && a.Column <= c.Column) ? (a, c) : (c, a);
    }

    /// <summary>The selected text, lines separated by line breaks; empty without a selection.</summary>
    public string SelectedText
    {
        get
        {
            if (OrderedSelection() is not ({ } from, { } to)) return "";
            var text = new StringBuilder();
            for (var index = from.Line; index <= to.Line; index++)
            {
                var line = _lines[index].Text;
                var start = Math.Min(index == from.Line ? from.Column : 0, line.Length);
                var end = Math.Min(index == to.Line ? to.Column : line.Length, line.Length);
                if (index > from.Line) text.Append("\r\n");
                if (end > start) text.Append(line, start, end - start);
            }
            return text.ToString();
        }
    }

    public void Copy()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { } // the clipboard is busy - another program has it
    }

    public void SelectAll()
    {
        if (_lines.Count == 0) return;
        _anchor = (0, 0);
        _caret = (_lines.Count - 1, _lines[^1].Text.Length);
        InvalidateVisual();
    }

    /// <summary>The position between characters nearest to <paramref name="point"/> - clamped to the text.</summary>
    private (int Line, int Column) PositionAt(Point point)
    {
        if (_lines.Count == 0) return (0, 0);
        var row = Math.Clamp(RowAt(point.Y + _offset.Y), 0, _rows - 1);
        var line = LineOfRow(row);
        var (start, end) = RowSpan(line, row - _firstRow[line]);
        var column = start + (int)Math.Round((point.X - Padding.Left) / _charWidth);
        return (line, Math.Clamp(column, start, end));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_lines.Count == 0) return;
        var at = PositionAt(e.GetPosition(this));
        switch (e.ClickCount)
        {
            case 2:
                // The word: letters, digits and _ . - / : around the click (a URL or path stays whole).
                var text = _lines[at.Line].Text;
                static bool Word(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or ':' or '\\';
                int start = at.Column, end = at.Column;
                while (start > 0 && Word(text[start - 1])) start--;
                while (end < text.Length && Word(text[end])) end++;
                _anchor = (at.Line, start);
                _caret = (at.Line, end);
                break;
            case 3:
                _anchor = (at.Line, 0);
                _caret = at.Line + 1 < _lines.Count ? (at.Line + 1, 0) : (at.Line, _lines[at.Line].Text.Length);
                break;
            default:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor is not null) _caret = at;
                else _anchor = _caret = at;
                CaptureMouse();
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        _mouse = e.GetPosition(this);
        DragTo(_mouse);
        // Past the top or bottom edge: keeps scrolling while the mouse stays there.
        if (_mouse.Y < 0 || _mouse.Y > _viewport.Height) _edgeScroll.Start(); else _edgeScroll.Stop();
    }

    private void DragTo(Point point)
    {
        if (!IsMouseCaptured) { _edgeScroll.Stop(); return; }
        if (point.Y < 0) ScrollBy(-_lineHeight);
        else if (point.Y > _viewport.Height) ScrollBy(_lineHeight);
        _caret = PositionAt(point);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _edgeScroll.Stop();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.C) Copy();
        else if (e.Key == Key.A) SelectAll();
        else return;
        e.Handled = true;
    }

    // ─── Wrapping ─────────────────────────────────────────────────────────

    /// <summary>The characters a row holds in a view <paramref name="width"/> wide - all of a line while there is no width yet.</summary>
    private int RowWidthFor(double width)
    {
        var room = width - Padding.Left - Padding.Right;
        return room <= 0 || double.IsNaN(room) || double.IsInfinity(room) ? int.MaxValue : Math.Max(1, (int)(room / _charWidth));
    }

    /// <summary>Works out every line's rows again - for another width or font, or after lines were dropped.</summary>
    private void Rewrap()
    {
        _rowStarts.Clear();
        _firstRow.Clear();
        _rows = 0;
        foreach (var line in _lines) AddRows(line.Text);
    }

    /// <summary>The rows of a line added at the end.</summary>
    private void AddRows(string text)
    {
        var starts = RowStarts(text, _rowWidth);
        _rowStarts.Add(starts);
        _firstRow.Add(_rows);
        _rows += starts?.Length ?? 1;
    }

    /// <summary>
    /// Where <paramref name="text"/>'s rows start when a row holds <paramref name="width"/> characters: after the last
    /// space that fits, or - for a word longer than a row - where the row is full. Null when it fits on one row.
    /// </summary>
    internal static int[]? RowStarts(string text, int width)
    {
        if (text.Length <= width) return null;
        var starts = new List<int> { 0 };
        var start = 0;
        while (text.Length - start > width)
        {
            var limit = start + width;
            // The space stays at the end of its row; a row never starts with the one it was broken at.
            var space = text.LastIndexOf(' ', limit - 1, limit - start);
            start = space > start ? space + 1 : limit;
            starts.Add(start);
        }
        return starts.ToArray();
    }

    /// <summary>The columns of row <paramref name="row"/> of line <paramref name="line"/>: from its start to the next row's.</summary>
    private (int Start, int End) RowSpan(int line, int row)
    {
        var text = _lines[line].Text;
        if (_rowStarts[line] is not { } starts) return (0, text.Length);
        return (starts[row], row + 1 < starts.Length ? starts[row + 1] : text.Length);
    }

    /// <summary>Which of a line's rows <paramref name="column"/> is on.</summary>
    private int RowInLine(int line, int column)
    {
        if (_rowStarts[line] is not { } starts) return 0;
        var row = 0;
        while (row + 1 < starts.Length && starts[row + 1] <= column) row++;
        return row;
    }

    /// <summary>The row at <paramref name="y"/> (from the top of all the text).</summary>
    private int RowAt(double y) => (int)Math.Floor((y - Padding.Top) / _lineHeight);

    /// <summary>The line row <paramref name="row"/> belongs to.</summary>
    private int LineOfRow(int row)
    {
        if (_firstRow.Count == 0) return 0;
        // The last line starting at or before the row.
        int lo = 0, hi = _firstRow.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_firstRow[mid] <= row) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ─── Scrolling (IScrollInfo) ──────────────────────────────────────────

    public bool CanVerticallyScroll { get; set; } = true;
    // The lines wrap: there is nothing beside the view to scroll to.
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _viewport.Width;
    public double ExtentHeight => Math.Max(_viewport.Height, Padding.Top + _rows * _lineHeight + Padding.Bottom);
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private double MaxOffsetY => Math.Max(0, ExtentHeight - _viewport.Height);
    private bool AtEnd => _offset.Y >= MaxOffsetY - 1;

    private void ClampOffset()
    {
        _offset.X = 0;
        _offset.Y = Math.Clamp(_offset.Y, 0, MaxOffsetY);
    }

    private void ScrollBy(double y) => SetVerticalOffset(_offset.Y + y);

    // Scrolled by the user (wheel, scrollbar, keys): following the new lines while at the end, not otherwise.
    public void SetVerticalOffset(double offset)
    {
        _offset.Y = double.IsNaN(offset) ? _offset.Y : Math.Clamp(offset, 0, MaxOffsetY);
        _following = AtEnd;
        Changed();
    }

    public void SetHorizontalOffset(double offset) { }

    public void LineUp() => ScrollBy(-_lineHeight);
    public void LineDown() => ScrollBy(_lineHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => ScrollBy(-Math.Max(_lineHeight, _viewport.Height - _lineHeight));
    public void PageDown() => ScrollBy(Math.Max(_lineHeight, _viewport.Height - _lineHeight));
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => ScrollBy(-3 * _lineHeight);
    public void MouseWheelDown() => ScrollBy(3 * _lineHeight);
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
