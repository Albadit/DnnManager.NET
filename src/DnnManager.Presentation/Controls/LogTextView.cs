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
/// </summary>
internal sealed class LogTextView : FrameworkElement, IScrollInfo
{
    private static readonly Thickness Padding = new(10, 4, 10, 4);

    private readonly List<LogLine> _lines = new();
    private readonly DispatcherTimer _edgeScroll;
    private Typeface _typeface = new("Consolas");
    private double _fontSize = 13, _charWidth = 7, _lineHeight = 16;
    // The longest line, in characters - how far the text scrolls sideways.
    private int _longest;
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

    public LogTextView()
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
        _lineHeight = Math.Ceiling(sample.Height);
        Changed();
    }

    // ─── The lines ────────────────────────────────────────────────────────

    public void Clear()
    {
        _lines.Clear();
        _longest = 0;
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
            _longest = Math.Max(_longest, line.Text.Length);
        }
        if (_lines.Count > max)
        {
            // A tenth at once, so this isn't done for every new line.
            var drop = Math.Min(_lines.Count, _lines.Count - max + max / 10);
            _lines.RemoveRange(0, drop);
            _longest = _lines.Count == 0 ? 0 : _lines.Max(l => l.Text.Length);
            (int, int)? Shift((int Line, int Column)? at) => at is { } p ? p.Line - drop < 0 ? (0, 0) : (p.Line - drop, p.Column) : null;
            _anchor = Shift(_anchor);
            _caret = Shift(_caret);
            // The search looks again (ContentChanged).
            _matches = [];
            _currentMatch = -1;
            _offset.Y = Math.Max(0, _offset.Y - drop * _lineHeight);
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
        // Only the characters on screen are laid out - a long line costs no more than a short one.
        var firstColumn = Math.Max(0, (int)((_offset.X - Padding.Left) / _charWidth));
        var columns = (int)(_viewport.Width / _charWidth) + 2;
        var first = Math.Max(0, (int)((_offset.Y - Padding.Top) / _lineHeight));
        var last = Math.Min(_lines.Count - 1, (int)((_offset.Y - Padding.Top + _viewport.Height) / _lineHeight));
        for (var index = first; index <= last; index++)
        {
            var line = _lines[index];
            var y = Padding.Top + index * _lineHeight - _offset.Y;
            DrawMatches(dc, index, y);

            if (from is { } a && to is { } b && index >= a.Line && index <= b.Line)
            {
                var start = index == a.Line ? a.Column : 0;
                // A selection going on to the next line takes this line's end with it - shown as one more character.
                var end = index == b.Line ? b.Column : line.Text.Length + 1;
                if (end > start)
                {
                    dc.PushOpacity(0.35);
                    dc.DrawRectangle(selection, null, new Rect(X(start), y, (end - start) * _charWidth, _lineHeight));
                    dc.Pop();
                }
            }

            if (firstColumn >= line.Text.Length) continue;
            var shown = line.Text.Substring(firstColumn, Math.Min(columns, line.Text.Length - firstColumn));
            dc.DrawText(Text(shown, brushes[line.Level]), new Point(X(firstColumn), y));
        }
    }

    private double X(int column) => Padding.Left + column * _charWidth - _offset.X;

    private void DrawMatches(DrawingContext dc, int lineIndex, double y)
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
            var (_, column, length) = _matches[i];
            dc.DrawRectangle((Brush)FindResource(i == _currentMatch ? "SearchCurrentBg" : "SearchMatchBg"), null,
                new Rect(X(column), y, length * _charWidth, _lineHeight));
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

    /// <summary>The line (in the middle) and the columns on screen, if they aren't.</summary>
    private void Reveal(int line, int column, int length)
    {
        var top = Padding.Top + line * _lineHeight;
        if (top < _offset.Y || top + _lineHeight > _offset.Y + _viewport.Height)
            _offset.Y = top - (_viewport.Height - _lineHeight) / 2;
        var left = Padding.Left + column * _charWidth;
        var right = left + length * _charWidth;
        if (left < _offset.X || right > _offset.X + _viewport.Width)
            _offset.X = Math.Max(0, right - _viewport.Width + 4 * _charWidth);
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
        var line = Math.Clamp((int)Math.Floor((point.Y + _offset.Y - Padding.Top) / _lineHeight), 0, _lines.Count - 1);
        var column = (int)Math.Round((point.X + _offset.X - Padding.Left) / _charWidth);
        return (line, Math.Clamp(column, 0, _lines[line].Text.Length));
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

    // ─── Scrolling (IScrollInfo) ──────────────────────────────────────────

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = true;
    public double ExtentWidth => Math.Max(_viewport.Width, Padding.Left + _longest * _charWidth + Padding.Right);
    public double ExtentHeight => Math.Max(_viewport.Height, Padding.Top + _lines.Count * _lineHeight + Padding.Bottom);
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private double MaxOffsetY => Math.Max(0, ExtentHeight - _viewport.Height);
    private bool AtEnd => _offset.Y >= MaxOffsetY - 1;

    private void ClampOffset()
    {
        _offset.X = Math.Clamp(_offset.X, 0, Math.Max(0, ExtentWidth - _viewport.Width));
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

    public void SetHorizontalOffset(double offset)
    {
        _offset.X = double.IsNaN(offset) ? _offset.X : offset;
        Changed();
    }

    public void LineUp() => ScrollBy(-_lineHeight);
    public void LineDown() => ScrollBy(_lineHeight);
    public void LineLeft() => SetHorizontalOffset(_offset.X - 4 * _charWidth);
    public void LineRight() => SetHorizontalOffset(_offset.X + 4 * _charWidth);
    public void PageUp() => ScrollBy(-Math.Max(_lineHeight, _viewport.Height - _lineHeight));
    public void PageDown() => ScrollBy(Math.Max(_lineHeight, _viewport.Height - _lineHeight));
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
    public void MouseWheelUp() => ScrollBy(-3 * _lineHeight);
    public void MouseWheelDown() => ScrollBy(3 * _lineHeight);
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
