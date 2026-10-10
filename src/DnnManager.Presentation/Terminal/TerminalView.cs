using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Terminal;

/// <summary>
/// Draws a <see cref="TerminalSession"/>'s screen as a grid of characters and sends what is typed to its shell.
/// Mouse wheel scrolls back through the output; drag selects text; Ctrl+C copies a selection (and interrupts the
/// program when nothing is selected); Ctrl+V or a right-click pastes (a right-click copies when text is selected).
/// </summary>
internal sealed class TerminalView : FrameworkElement, Controls.ISearchTarget
{
    private static readonly Thickness Padding = new(8, 6, 4, 6);

    // The 16 ANSI colours: Windows Terminal's Campbell on the dark theme, One Half Light on the light one (the
    // bright colours of a dark scheme can't be read on white).
    private static readonly uint[] DarkPalette =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2
    ];
    private static readonly uint[] LightPalette =
    [
        0x383A42, 0xE45649, 0x50A14F, 0xC18301, 0x0184BC, 0xA626A4, 0x0997B3, 0x7A7F87,
        0x4F525D, 0xDF6C75, 0x3E8E3D, 0xA67101, 0x0B76C5, 0x9B2C9A, 0x0A8B9E, 0x23262B
    ];

    private readonly TerminalSession _session;
    private readonly Dictionary<uint, Brush> _brushes = [];
    private Typeface _typeface = new("Consolas");
    private double _fontSize = 13, _cellWidth = 7, _cellHeight = 16;
    // The first line shown, in scrollback + screen lines; null while following the output.
    private int? _viewTop;
    // A selection from where the mouse went down to where it is: (line, column) in scrollback + screen lines.
    private (int Line, int Column)? _selectionStart, _selectionEnd;
    // A search's matches (line, column, length) in scrollback + screen lines, in reading order, and the current one.
    private IReadOnlyList<(int Line, int Column, int Length)> _matches = [];
    private int _currentMatch = -1;
    // The screen changed while the window was minimized (EfficiencyMode) and wasn't drawn - it is once restored.
    private bool _changedUnseen;

    public TerminalView(TerminalSession session)
    {
        _session = session;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        // Tab and the arrow keys are for the shell, not for moving the focus.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);

        // The output is always read and kept in the buffer (a shell whose output isn't read stalls); only drawing it
        // waits while nobody can see it.
        session.Changed += (_, _) =>
        {
            if (EfficiencyMode.GetIsSaving(this)) _changedUnseen = true;
            else ShowChanges();
        };
        // The ANSI colours differ per theme. Followed only while shown, so a closed terminal's view can be collected.
        Loaded += (_, _) => { OnThemeChanged(null, EventArgs.Empty); ThemeManager.Changed += OnThemeChanged; };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    private TerminalBuffer Buffer => _session.Buffer;

    /// <summary>Draws the screen as it is now, and tells the scrollbar and a search that it changed.</summary>
    private void ShowChanges()
    {
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty); // more scrollback, or a new size
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    // The window is back from minimized: what the shell printed meanwhile is drawn, once.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != EfficiencyMode.IsSavingProperty || (bool)e.NewValue || !_changedUnseen) return;
        _changedUnseen = false;
        ShowChanges();
    }

    /// <summary>The font, from the settings. The grid is measured again with it.</summary>
    public void SetFont(FontFamily family, double size)
    {
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fontSize = size;
        var sample = Text("MMMMMMMMMM", Brushes.Black, _typeface);
        _cellWidth = sample.WidthIncludingTrailingWhitespace / 10;
        _cellHeight = Math.Ceiling(sample.Height);
        FitGrid();
        InvalidateVisual();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _brushes.Clear();
        InvalidateVisual();
    }

    // ─── Size ─────────────────────────────────────────────────────────────

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        FitGrid();
    }

    // As many columns and rows as fit - the shell is told, and wraps and redraws for the new size.
    private void FitGrid()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var columns = (int)((ActualWidth - Padding.Left - Padding.Right) / _cellWidth);
        var rows = (int)((ActualHeight - Padding.Top - Padding.Bottom) / _cellHeight);
        if (columns >= 2 && rows >= 1) _session.Resize(columns, rows);
    }

    // ─── Drawing ──────────────────────────────────────────────────────────

    private int ViewTop => Math.Clamp(_viewTop ?? Buffer.Scrollback.Count, 0, Buffer.Scrollback.Count);

    // A screen reader finds a document named Terminal, its value the rows on screen - read only when asked (TextViewPeer).
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new Controls.TextViewPeer(this, "Terminal", VisibleText);

    /// <summary>The rows on screen, without their trailing blanks, one per line.</summary>
    private string VisibleText()
    {
        var text = new StringBuilder();
        var top = ViewTop;
        for (var index = top; index < Math.Min(top + Buffer.Rows, Buffer.TotalLines); index++)
        {
            var row = new StringBuilder();
            foreach (var cell in Buffer.Line(index)) row.Append(cell.Char == '\0' ? ' ' : cell.Char);
            text.AppendLine(row.ToString().TrimEnd());
        }
        return text.ToString();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var background = (Brush)FindResource("LogBg");
        var foreground = (Brush)FindResource("LogFg");
        dc.DrawRectangle(background, null, new Rect(RenderSize));

        var top = ViewTop;
        var (selFrom, selTo) = OrderedSelection();
        var run = new StringBuilder();
        for (var row = 0; row < Buffer.Rows; row++)
        {
            var lineIndex = top + row;
            var line = Buffer.Line(lineIndex);
            var y = Padding.Top + row * _cellHeight;

            // Runs of cells that look the same are drawn in one go - in three layers: the cells' backgrounds, then the search's
            // matches on them, then the text over both (a match drawn over the text would wash it out).
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 1) DrawMatches(dc, lineIndex, y);
                var start = 0;
                while (start < line.Length)
                {
                    var first = line[start];
                    var end = start + 1;
                    while (end < line.Length && line[end].Foreground == first.Foreground && line[end].Background == first.Background &&
                           line[end].Style == first.Style)
                        end++;

                    var inverse = (first.Style & CellStyle.Inverse) != 0;
                    var fg = first.Foreground == 0 ? foreground : BrushFor(first.Foreground);
                    var bg = first.Background == 0 ? null : BrushFor(first.Background);
                    if (inverse) (fg, bg) = (bg ?? background, fg);

                    var x = Padding.Left + start * _cellWidth;
                    if (pass == 0)
                    {
                        if (bg is not null) dc.DrawRectangle(bg, null, new Rect(x, y, (end - start) * _cellWidth, _cellHeight));
                        start = end;
                        continue;
                    }

                    run.Clear();
                    var blank = true;
                    for (var i = start; i < end; i++)
                    {
                        var c = line[i].Char;
                        if (c != '\0' && c != ' ') blank = false;
                        run.Append(c == '\0' ? ' ' : c);
                    }
                    if (!blank)
                    {
                        var typeface = (first.Style & (CellStyle.Bold | CellStyle.Italic)) == 0 ? _typeface
                            : new Typeface(_typeface.FontFamily,
                                (first.Style & CellStyle.Italic) != 0 ? FontStyles.Italic : FontStyles.Normal,
                                (first.Style & CellStyle.Bold) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                        var text = Text(run.ToString(), fg, typeface);
                        if ((first.Style & CellStyle.Underline) != 0) text.SetTextDecorations(TextDecorations.Underline);
                        if ((first.Style & CellStyle.Dim) != 0) dc.PushOpacity(0.6);
                        dc.DrawText(text, new Point(x, y));
                        if ((first.Style & CellStyle.Dim) != 0) dc.Pop();
                    }
                    start = end;
                }
            }

            if (selFrom is { } from && selTo is { } to && lineIndex >= from.Line && lineIndex <= to.Line)
            {
                var a = lineIndex == from.Line ? from.Column : 0;
                var b = lineIndex == to.Line ? to.Column + 1 : Buffer.Columns;
                if (b > a)
                {
                    dc.PushOpacity(0.35);
                    dc.DrawRectangle((Brush)FindResource("Accent"), null,
                        new Rect(Padding.Left + a * _cellWidth, y, (b - a) * _cellWidth, _cellHeight));
                    dc.Pop();
                }
            }
        }

        // The cursor: a block while the view has the keyboard, an outline when it doesn't.
        var cursorRow = Buffer.Scrollback.Count + Buffer.CursorY - top;
        if (Buffer.CursorVisible && cursorRow >= 0 && cursorRow < Buffer.Rows)
        {
            var rect = new Rect(Padding.Left + Buffer.CursorX * _cellWidth, Padding.Top + cursorRow * _cellHeight, _cellWidth, _cellHeight);
            if (IsKeyboardFocused)
            {
                dc.PushOpacity(0.55);
                dc.DrawRectangle(foreground, null, rect);
                dc.Pop();
            }
            else
            {
                rect.Inflate(-0.5, -0.5);
                dc.DrawRectangle(null, new Pen(foreground, 1), rect);
            }
        }
    }

    // The search's matches on this line, between the cells' backgrounds and the text - the text keeps its full contrast
    // on them: the current one stronger.
    private void DrawMatches(DrawingContext dc, int lineIndex, double y)
    {
        if (_matches.Count == 0) return;
        // The first match on this line or after it (they are in reading order).
        int lo = 0, hi = _matches.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_matches[mid].Line < lineIndex) lo = mid + 1; else hi = mid;
        }
        for (var i = lo; i < _matches.Count && _matches[i].Line == lineIndex; i++)
        {
            var (_, column, length) = _matches[i];
            // The current one outlined too: it stands out from the others by more than its colour (WCAG 1.4.1).
            var current = i == _currentMatch;
            dc.DrawRectangle((Brush)FindResource(current ? "SearchCurrentBg" : "SearchMatchBg"),
                current ? new Pen((Brush)FindResource("SearchCurrentBorder"), 1) : null,
                new Rect(Padding.Left + column * _cellWidth, y, length * _cellWidth, _cellHeight));
        }
    }

    // ─── Search ───────────────────────────────────────────────────────────

    /// <summary>Every match of <paramref name="query"/> in the output and its scrollback, highlighted; their number.</summary>
    public int Find(Controls.SearchQuery query)
    {
        var matches = new List<(int, int, int)>();
        if (!query.IsEmpty)
        {
            var text = new StringBuilder();
            for (var index = 0; index < Buffer.TotalLines; index++)
            {
                var line = Buffer.Line(index);
                text.Clear();
                foreach (var cell in line) text.Append(cell.Char == '\0' ? ' ' : cell.Char);
                foreach (var (at, length) in query.Matches(text.ToString()))
                    matches.Add((index, at, length));
            }
        }
        _matches = matches;
        _currentMatch = -1;
        InvalidateVisual();
        return matches.Count;
    }

    /// <summary>The output changed - a search looks again.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>Marks match <paramref name="index"/>, and scrolls it into view (in the middle) when it is out of view.</summary>
    public void ShowMatch(int index, bool reveal = true)
    {
        if (index < 0 || index >= _matches.Count) return;
        _currentMatch = index;
        var line = _matches[index].Line;
        if (reveal && (line < ViewTop || line >= ViewTop + Buffer.Rows)) ScrollTo(line - Buffer.Rows / 2);
        InvalidateVisual();
    }

    public void ClearSearch()
    {
        _matches = [];
        _currentMatch = -1;
        InvalidateVisual();
    }

    private FormattedText Text(string text, Brush brush, Typeface typeface) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, typeface, _fontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush BrushFor(uint colour)
    {
        if (_brushes.TryGetValue(colour, out var brush)) return brush;
        var index = (int)(colour & 0xFF_FFFF);
        uint rgb;
        if ((colour & TerminalBuffer.Rgb) != 0) rgb = (uint)index;
        else if (index < 16) rgb = (ThemeManager.Current == AppTheme.Dark ? DarkPalette : LightPalette)[index];
        else if (index < 232)
        {
            // The 6 x 6 x 6 colour cube.
            static uint Level(int n) => n == 0 ? 0u : (uint)(55 + n * 40);
            var n = index - 16;
            rgb = Level(n / 36) << 16 | Level(n / 6 % 6) << 8 | Level(n % 6);
        }
        else
        {
            var grey = (uint)(8 + (index - 232) * 10);
            rgb = grey << 16 | grey << 8 | grey;
        }
        brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return _brushes[colour] = brush;
    }

    // ─── Keyboard ─────────────────────────────────────────────────────────

    private void Send(string text)
    {
        // Typing goes to the prompt at the bottom - show it.
        ScrollToBottom();
        ClearSelection();
        _session.Write(text);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        // Enter, Backspace, Escape and Ctrl combinations are sent from OnKeyDown.
        if (e.Text.Length == 0 || e.Text[0] < ' ' || e.Text[0] == '\x7f') return;
        Send(e.Text);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        var alt = (modifiers & ModifierKeys.Alt) != 0;

        if (ctrl && !alt)
        {
            if (key == Key.C && (shift || HasSelection)) { Copy(); e.Handled = true; return; }
            if (key == Key.V) { Paste(); e.Handled = true; return; }
            if (shift && key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
            {
                ScrollBy(key switch { Key.Up => -1, Key.Down => 1, Key.PageUp => -Buffer.Rows, _ => Buffer.Rows });
                e.Handled = true;
                return;
            }
            // Ctrl+A … Ctrl+Z are the control characters 1 … 26 (Ctrl+C interrupts, Ctrl+D ends input…).
            if (key is >= Key.A and <= Key.Z) { Send(((char)(key - Key.A + 1)).ToString()); e.Handled = true; return; }
            if (key == Key.Space) { Send("\0"); e.Handled = true; return; }
        }
        if (shift && key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(key == Key.PageUp ? -Buffer.Rows : Buffer.Rows);
            e.Handled = true;
            return;
        }

        // Cursor keys: ESC [ A, or ESC O A when the program switched to application keys.
        var arrow = Buffer.ApplicationCursorKeys ? "\x1bO" : "\x1b[";
        var sequence = key switch
        {
            Key.Enter => "\r",
            Key.Back => ctrl ? "\x17" : "\x7f",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            Key.Up => arrow + "A",
            Key.Down => arrow + "B",
            Key.Right => ctrl ? "\x1b[1;5C" : arrow + "C",
            Key.Left => ctrl ? "\x1b[1;5D" : arrow + "D",
            Key.Home => arrow + "H",
            Key.End => arrow + "F",
            Key.Insert => "\x1b[2~",
            Key.Delete => "\x1b[3~",
            Key.PageUp => "\x1b[5~",
            Key.PageDown => "\x1b[6~",
            Key.F1 => "\x1bOP",
            Key.F2 => "\x1bOQ",
            Key.F3 => "\x1bOR",
            Key.F4 => "\x1bOS",
            Key.F5 => "\x1b[15~",
            Key.F6 => "\x1b[17~",
            Key.F7 => "\x1b[18~",
            Key.F8 => "\x1b[19~",
            Key.F9 => "\x1b[20~",
            Key.F10 => "\x1b[21~",
            Key.F11 => "\x1b[23~",
            Key.F12 => "\x1b[24~",
            _ => null
        };
        if (sequence is null) return;
        Send(sequence);
        e.Handled = true;
    }

    // ─── Clipboard ────────────────────────────────────────────────────────

    private bool HasSelection => _selectionStart is not null && _selectionEnd is not null && _selectionStart != _selectionEnd;

    private ((int Line, int Column)? From, (int Line, int Column)? To) OrderedSelection()
    {
        if (!HasSelection) return (null, null);
        var (a, b) = (_selectionStart!.Value, _selectionEnd!.Value);
        return a.Line < b.Line || (a.Line == b.Line && a.Column <= b.Column) ? (a, b) : (b, a);
    }

    private void ClearSelection()
    {
        if (_selectionStart is null) return;
        _selectionStart = _selectionEnd = null;
        InvalidateVisual();
    }

    /// <summary>Copies the selected text - each line without its trailing blanks.</summary>
    public void Copy()
    {
        var (from, to) = OrderedSelection();
        if (from is null || to is null) return;
        var text = new StringBuilder();
        for (var index = from.Value.Line; index <= Math.Min(to.Value.Line, Buffer.TotalLines - 1); index++)
        {
            var line = Buffer.Line(index);
            var a = index == from.Value.Line ? from.Value.Column : 0;
            var b = index == to.Value.Line ? Math.Min(to.Value.Column + 1, line.Length) : line.Length;
            var part = new StringBuilder();
            for (var i = a; i < b; i++) part.Append(line[i].Char == '\0' ? ' ' : line[i].Char);
            if (text.Length > 0) text.Append("\r\n");
            text.Append(part.ToString().TrimEnd());
        }
        try { Clipboard.SetText(text.ToString()); }
        catch (System.Runtime.InteropServices.ExternalException) { /* another program has the clipboard open */ }
        ClearSelection();
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }
        if (text.Length == 0) return;
        // A shell takes Enter as a carriage return; the markers tell a program that asked for them it is a paste.
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        Send(Buffer.BracketedPaste ? $"\x1b[200~{text}\x1b[201~" : text);
    }

    // ─── Mouse ────────────────────────────────────────────────────────────

    private (int Line, int Column) CellAt(Point point) => (
        ViewTop + Math.Clamp((int)((point.Y - Padding.Top) / _cellHeight), 0, Buffer.Rows - 1),
        Math.Clamp((int)((point.X - Padding.Left) / _cellWidth), 0, Buffer.Columns - 1));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _selectionStart = _selectionEnd = CellAt(e.GetPosition(this));
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || _selectionStart is null) return;
        var cell = CellAt(e.GetPosition(this));
        if (cell == _selectionEnd) return;
        _selectionEnd = cell;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!HasSelection) ClearSelection();
    }

    // Like the Windows console: a right-click copies the selection, or pastes when there is none.
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Focus();
        if (HasSelection) Copy();
        else Paste();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-Math.Sign(e.Delta) * 3);
        e.Handled = true;
    }

    private void ScrollBy(int lines) => ScrollTo(ViewTop + lines);

    // ─── Scrolling (for the scrollbar next to the view) ───────────────────

    /// <summary>The scroll position or its range changed.</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>How far the view can be scrolled: the number of lines that scrolled off the top.</summary>
    public int ScrollMaximum => Buffer.Scrollback.Count;

    /// <summary>The first line shown, from 0 (the oldest line) to <see cref="ScrollMaximum"/> (the bottom).</summary>
    public int ScrollPosition => ViewTop;

    /// <summary>The number of lines shown at once.</summary>
    public int VisibleRows => Buffer.Rows;

    public bool IsAtBottom => _viewTop is null;

    public void ScrollTo(int position)
    {
        var top = Math.Clamp(position, 0, Buffer.Scrollback.Count);
        // At the bottom again: follow the output.
        int? viewTop = top >= Buffer.Scrollback.Count ? null : top;
        if (viewTop == _viewTop) return;
        _viewTop = viewTop;
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollToBottom() => ScrollTo(int.MaxValue);
}
