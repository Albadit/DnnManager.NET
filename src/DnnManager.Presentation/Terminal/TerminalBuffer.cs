using System.Text;

namespace DnnManager.Presentation.Terminal;

[Flags]
internal enum CellStyle : byte { None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16 }

/// <summary>
/// One character of the screen. Colours: 0 is the default, <see cref="TerminalBuffer.Indexed"/> | n one of the 256
/// palette colours, <see cref="TerminalBuffer.Rgb"/> | 0xRRGGBB a direct colour.
/// </summary>
internal struct Cell
{
    public char Char;
    public uint Foreground, Background;
    public CellStyle Style;
}

/// <summary>
/// The screen of a terminal: a grid of cells with the cursor, the lines that scrolled off the top (the scrollback)
/// and the alternate screen full-screen programs switch to. <see cref="Feed"/> takes what the program printed - text
/// with VT / xterm escape sequences, as a Windows pseudo console sends it - and applies it. Not thread-safe: fed and
/// read on the UI thread.
/// </summary>
internal sealed class TerminalBuffer
{
    public const uint Indexed = 0x0100_0000, Rgb = 0x0200_0000;
    private const int MaxScrollback = 5000;

    private readonly List<Cell[]> _scrollback = new();
    private Cell[][] _screen;
    // The main screen, kept while the alternate one is shown.
    private Cell[][]? _mainScreen;
    private (int X, int Y) _mainCursor;
    private (int X, int Y, Cell Pen) _saved;
    private Cell _pen;
    // The cursor sits on the last column with a character just written there: the next one wraps.
    private bool _wrapPending;
    private int _scrollTop, _scrollBottom;

    // The escape sequence being read.
    private enum State { Ground, Escape, Csi, String, StringEscape, SkipOne }
    private State _state;
    private readonly StringBuilder _sequence = new();

    public TerminalBuffer(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _screen = NewScreen(Columns, Rows);
        _scrollBottom = Rows - 1;
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    /// <summary>The program asked for cursor keys as application keys (ESC O A rather than ESC [ A).</summary>
    public bool ApplicationCursorKeys { get; private set; }
    /// <summary>The program wants a paste wrapped in markers, so it can tell it from typing.</summary>
    public bool BracketedPaste { get; private set; }
    /// <summary>Lines that scrolled off the top, oldest first.</summary>
    public IReadOnlyList<Cell[]> Scrollback => _scrollback;
    /// <summary>Scrollback plus screen - the lines a view can scroll through.</summary>
    public int TotalLines => _scrollback.Count + Rows;

    /// <summary>An answer the program asked for (e.g. the cursor position) - to be written to its input.</summary>
    public event Action<string>? Reply;

    /// <summary>Line <paramref name="index"/> of scrollback plus screen (0 = the oldest scrollback line).</summary>
    public Cell[] Line(int index) => index < _scrollback.Count ? _scrollback[index] : _screen[index - _scrollback.Count];

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows) return;

        _screen = Resized(_screen, columns, rows, keepBottom: _mainScreen is null, toScrollback: _mainScreen is null);
        if (_mainScreen is not null) _mainScreen = Resized(_mainScreen, columns, rows, keepBottom: true, toScrollback: false);
        Columns = columns;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorX = Math.Min(CursorX, columns - 1);
        CursorY = Math.Min(CursorY, rows - 1);
        _wrapPending = false;
    }

    // Fewer rows: the top ones go (to the scrollback on the main screen), so the cursor's line stays. The pseudo
    // console redraws its screen after a resize anyway.
    private Cell[][] Resized(Cell[][] screen, int columns, int rows, bool keepBottom, bool toScrollback)
    {
        var drop = keepBottom ? Math.Max(0, CursorY - (rows - 1)) : 0;
        if (toScrollback) for (var y = 0; y < drop; y++) PushScrollback(screen[y]);
        CursorY -= drop;
        var result = new Cell[rows][];
        for (var y = 0; y < rows; y++)
        {
            result[y] = new Cell[columns];
            var source = y + drop;
            if (source < screen.Length) Array.Copy(screen[source], result[y], Math.Min(columns, screen[source].Length));
        }
        return result;
    }

    private static Cell[][] NewScreen(int columns, int rows)
    {
        var screen = new Cell[rows][];
        for (var y = 0; y < rows; y++) screen[y] = new Cell[columns];
        return screen;
    }

    private void PushScrollback(Cell[] line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > MaxScrollback) _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
    }

    // ─── Parsing ──────────────────────────────────────────────────────────

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (_state)
            {
                case State.Ground:
                    if (c == '\x1b') _state = State.Escape;
                    else if (c < ' ') Control(c);
                    else if (c != '\x7f') Print(c);
                    break;

                case State.Escape:
                    _state = State.Ground;
                    switch (c)
                    {
                        case '[': _sequence.Clear(); _state = State.Csi; break;
                        // OSC (window title…), DCS, SOS, PM, APC: strings up to BEL or ESC \ - nothing here uses them.
                        case ']' or 'P' or 'X' or '^' or '_': _state = State.String; break;
                        // Character set choices take one more character.
                        case '(' or ')' or '*' or '+' or '#' or '%': _state = State.SkipOne; break;
                        case '7': SaveCursor(); break;
                        case '8': RestoreCursor(); break;
                        case 'D': LineFeed(); break;
                        case 'E': CursorX = 0; LineFeed(); break;
                        case 'M': ReverseLineFeed(); break;
                        case 'c': Reset(); break;
                    }
                    break;

                case State.Csi:
                    // Parameters and intermediates (0x20-0x3F) are collected; a letter (0x40-0x7E) ends the sequence.
                    if (c is >= '\x40' and <= '\x7e') { _state = State.Ground; Csi(_sequence.ToString(), c); }
                    else if (c == '\x1b') _state = State.Escape;
                    else if (c < ' ') Control(c);
                    else if (_sequence.Length < 64) _sequence.Append(c);
                    break;

                case State.String:
                    if (c == '\a') _state = State.Ground;
                    else if (c == '\x1b') _state = State.StringEscape;
                    break;

                case State.StringEscape:
                    _state = c == '\\' ? State.Ground : State.String;
                    break;

                case State.SkipOne:
                    _state = State.Ground;
                    break;
            }
        }
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\r': CursorX = 0; _wrapPending = false; break;
            case '\n' or '\v' or '\f': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
            case '\t': CursorX = Math.Min(Columns - 1, (CursorX / 8 + 1) * 8); _wrapPending = false; break;
        }
    }

    private void Print(char c)
    {
        if (_wrapPending)
        {
            CursorX = 0;
            LineFeed();
        }
        ref var cell = ref _screen[CursorY][CursorX];
        cell = _pen;
        cell.Char = c;
        if (CursorX == Columns - 1) _wrapPending = true;
        else CursorX++;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _scrollBottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseLineFeed()
    {
        _wrapPending = false;
        if (CursorY == _scrollTop) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    /// <summary>The scroll region moves up <paramref name="count"/> lines; on the main screen, lines leaving the top are kept.</summary>
    private void ScrollUp(int count)
    {
        for (var i = 0; i < Math.Min(count, _scrollBottom - _scrollTop + 1); i++)
        {
            var top = _screen[_scrollTop];
            if (_scrollTop == 0 && _mainScreen is null) PushScrollback(top);
            Array.Copy(_screen, _scrollTop + 1, _screen, _scrollTop, _scrollBottom - _scrollTop);
            _screen[_scrollBottom] = BlankLine();
        }
    }

    private void ScrollDown(int count)
    {
        for (var i = 0; i < Math.Min(count, _scrollBottom - _scrollTop + 1); i++)
        {
            Array.Copy(_screen, _scrollTop, _screen, _scrollTop + 1, _scrollBottom - _scrollTop);
            _screen[_scrollTop] = BlankLine();
        }
    }

    // Erased cells take the current background, as terminals do.
    private Cell Blank => new() { Background = _pen.Background };

    private Cell[] BlankLine()
    {
        var line = new Cell[Columns];
        if (_pen.Background != 0) Array.Fill(line, Blank);
        return line;
    }

    private void SaveCursor() => _saved = (CursorX, CursorY, _pen);

    private void RestoreCursor()
    {
        CursorX = Math.Min(_saved.X, Columns - 1);
        CursorY = Math.Min(_saved.Y, Rows - 1);
        _pen = _saved.Pen;
        _wrapPending = false;
    }

    private void Reset()
    {
        _mainScreen = null;
        _screen = NewScreen(Columns, Rows);
        _pen = default;
        CursorX = CursorY = 0;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorVisible = true;
        ApplicationCursorKeys = BracketedPaste = _wrapPending = false;
    }

    // ─── CSI sequences ────────────────────────────────────────────────────

    private void Csi(string body, char final)
    {
        // "?" (and > = !) mark private sequences; a trailing space, $ or " is an intermediate of ones not handled here.
        var isPrivate = body.Length > 0 && body[0] is '?' or '>' or '=' or '!';
        if (body.Length > 0 && body[^1] is ' ' or '$' or '"' or '\'') return;
        var numbers = Numbers(isPrivate ? body.AsSpan(1) : body);
        int Arg(int index, int fallback) => index < numbers.Count && numbers[index] > 0 ? numbers[index] : fallback;

        if (isPrivate)
        {
            if (body[0] == '?' && final is 'h' or 'l') foreach (var mode in numbers) SetMode(mode, final == 'h');
            return;
        }

        switch (final)
        {
            case 'A': MoveTo(CursorX, CursorY - Arg(0, 1), withinRegion: true); break;
            case 'B' or 'e': MoveTo(CursorX, CursorY + Arg(0, 1), withinRegion: true); break;
            case 'C' or 'a': MoveTo(CursorX + Arg(0, 1), CursorY); break;
            case 'D': MoveTo(CursorX - Arg(0, 1), CursorY); break;
            case 'E': MoveTo(0, CursorY + Arg(0, 1)); break;
            case 'F': MoveTo(0, CursorY - Arg(0, 1)); break;
            case 'G' or '`': MoveTo(Arg(0, 1) - 1, CursorY); break;
            case 'd': MoveTo(CursorX, Arg(0, 1) - 1); break;
            case 'H' or 'f': MoveTo(Arg(1, 1) - 1, Arg(0, 1) - 1); break;
            case 'J': EraseDisplay(Arg(0, 0)); break;
            case 'K': EraseLine(Arg(0, 0)); break;
            case 'X': Fill(CursorY, CursorX, Math.Min(Columns, CursorX + Arg(0, 1))); break;
            case 'P': DeleteChars(Arg(0, 1)); break;
            case '@': InsertChars(Arg(0, 1)); break;
            case 'L': InsertLines(Arg(0, 1)); break;
            case 'M': DeleteLines(Arg(0, 1)); break;
            case 'S': ScrollUp(Arg(0, 1)); break;
            case 'T': ScrollDown(Arg(0, 1)); break;
            case 'm': Sgr(numbers); break;
            case 'r':
                var top = Arg(0, 1) - 1;
                var bottom = Arg(1, Rows) - 1;
                if (top < bottom && bottom < Rows) { _scrollTop = top; _scrollBottom = bottom; }
                else { _scrollTop = 0; _scrollBottom = Rows - 1; }
                MoveTo(0, 0);
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            // The program asks where the cursor is / what kind of terminal this is.
            case 'n' when Arg(0, 0) == 6: Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R"); break;
            case 'n' when Arg(0, 0) == 5: Reply?.Invoke("\x1b[0n"); break;
            case 'c': Reply?.Invoke("\x1b[?1;0c"); break;
        }
    }

    private static List<int> Numbers(ReadOnlySpan<char> body)
    {
        var numbers = new List<int>();
        var value = 0;
        var any = false;
        foreach (var c in body)
        {
            if (c is >= '0' and <= '9') { value = Math.Min(value * 10 + (c - '0'), 99999); any = true; }
            else if (c is ';' or ':') { numbers.Add(value); value = 0; any = true; }
        }
        if (any) numbers.Add(value);
        return numbers;
    }

    private void SetMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47 or 1047 or 1049:
                if (on && _mainScreen is null)
                {
                    _mainScreen = _screen;
                    _mainCursor = (CursorX, CursorY);
                    _screen = NewScreen(Columns, Rows);
                    CursorX = CursorY = 0;
                }
                else if (!on && _mainScreen is not null)
                {
                    _screen = _mainScreen;
                    _mainScreen = null;
                    (CursorX, CursorY) = _mainCursor;
                }
                _scrollTop = 0;
                _scrollBottom = Rows - 1;
                _wrapPending = false;
                break;
        }
    }

    private void MoveTo(int x, int y, bool withinRegion = false)
    {
        CursorX = Math.Clamp(x, 0, Columns - 1);
        // Moving up or down stops at the scroll region's edge when the cursor is inside it.
        var (min, max) = withinRegion && CursorY >= _scrollTop && CursorY <= _scrollBottom ? (_scrollTop, _scrollBottom) : (0, Rows - 1);
        CursorY = Math.Clamp(y, min, max);
        _wrapPending = false;
    }

    private void Fill(int y, int from, int to)
    {
        var blank = Blank;
        for (var x = Math.Max(0, from); x < Math.Min(Columns, to); x++) _screen[y][x] = blank;
    }

    private void EraseLine(int mode)
    {
        _wrapPending = false;
        switch (mode)
        {
            case 0: Fill(CursorY, CursorX, Columns); break;
            case 1: Fill(CursorY, 0, CursorX + 1); break;
            case 2: Fill(CursorY, 0, Columns); break;
        }
    }

    private void EraseDisplay(int mode)
    {
        _wrapPending = false;
        switch (mode)
        {
            case 0:
                Fill(CursorY, CursorX, Columns);
                for (var y = CursorY + 1; y < Rows; y++) Fill(y, 0, Columns);
                break;
            case 1:
                for (var y = 0; y < CursorY; y++) Fill(y, 0, Columns);
                Fill(CursorY, 0, CursorX + 1);
                break;
            case 2:
                for (var y = 0; y < Rows; y++) Fill(y, 0, Columns);
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void DeleteChars(int count)
    {
        var line = _screen[CursorY];
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(line, CursorX + count, line, CursorX, Columns - CursorX - count);
        Fill(CursorY, Columns - count, Columns);
    }

    private void InsertChars(int count)
    {
        var line = _screen[CursorY];
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(line, CursorX, line, CursorX + count, Columns - CursorX - count);
        Fill(CursorY, CursorX, CursorX + count);
    }

    // Inserting / deleting lines shifts the rest of the scroll region, from the cursor's line down.
    private void InsertLines(int count)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        var top = _scrollTop;
        _scrollTop = CursorY;
        ScrollDown(count);
        _scrollTop = top;
        CursorX = 0;
    }

    private void DeleteLines(int count)
    {
        if (CursorY < _scrollTop || CursorY > _scrollBottom) return;
        var (top, main) = (_scrollTop, _mainScreen);
        _scrollTop = CursorY;
        // Deleted lines aren't history: keep them out of the scrollback.
        _mainScreen ??= _screen;
        ScrollUp(count);
        _mainScreen = main;
        _scrollTop = top;
        CursorX = 0;
    }

    /// <summary>Colours and styles for what is printed next (SGR).</summary>
    private void Sgr(List<int> codes)
    {
        if (codes.Count == 0) { _pen = default; return; }
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            switch (code)
            {
                case 0: _pen = default; break;
                case 1: _pen.Style |= CellStyle.Bold; break;
                case 2: _pen.Style |= CellStyle.Dim; break;
                case 3: _pen.Style |= CellStyle.Italic; break;
                case 4: _pen.Style |= CellStyle.Underline; break;
                case 7: _pen.Style |= CellStyle.Inverse; break;
                case 22: _pen.Style &= ~(CellStyle.Bold | CellStyle.Dim); break;
                case 23: _pen.Style &= ~CellStyle.Italic; break;
                case 24: _pen.Style &= ~CellStyle.Underline; break;
                case 27: _pen.Style &= ~CellStyle.Inverse; break;
                case >= 30 and <= 37: _pen.Foreground = Indexed | (uint)(code - 30); break;
                case 39: _pen.Foreground = 0; break;
                case >= 40 and <= 47: _pen.Background = Indexed | (uint)(code - 40); break;
                case 49: _pen.Background = 0; break;
                case >= 90 and <= 97: _pen.Foreground = Indexed | (uint)(code - 90 + 8); break;
                case >= 100 and <= 107: _pen.Background = Indexed | (uint)(code - 100 + 8); break;
                case 38 or 48:
                    // 38;5;n - palette colour n; 38;2;r;g;b - direct colour. 48 the same for the background.
                    uint colour = 0;
                    if (i + 2 < codes.Count && codes[i + 1] == 5)
                    {
                        colour = Indexed | (uint)Math.Clamp(codes[i + 2], 0, 255);
                        i += 2;
                    }
                    else if (i + 4 < codes.Count && codes[i + 1] == 2)
                    {
                        colour = Rgb | (uint)(Math.Clamp(codes[i + 2], 0, 255) << 16 | Math.Clamp(codes[i + 3], 0, 255) << 8 | Math.Clamp(codes[i + 4], 0, 255));
                        i += 4;
                    }
                    else
                    {
                        i = codes.Count; // malformed - ignore the rest
                    }
                    if (code == 38) _pen.Foreground = colour;
                    else _pen.Background = colour;
                    break;
            }
        }
    }
}
