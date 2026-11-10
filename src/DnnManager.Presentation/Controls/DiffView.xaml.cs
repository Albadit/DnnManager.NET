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
}

/// <summary>A row of the diff: a hunk's header, or a line on each side (one for the unified view).</summary>
public sealed class DiffRow
{
    public string? Hunk { get; init; }
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
    private string _oldName = "", _newName = "";
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
        CopyButton.IsEnabled = false;
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
        if (computed.Problem is { } problem)
        {
            ShowMessage(problem);
            return;
        }
        _lines = computed.Lines;
        _oldTokens = computed.OldTokens;
        _newTokens = computed.NewTokens;
        _oldName = oldName;
        _newName = newName;
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
        var whole = WholeFile.IsChecked == true;
        var split = SplitMode.IsChecked == true;
        var (oldTokens, newTokens) = (_oldTokens, _newTokens);
        var (rows, longest, lastNumber) = await Task.Run(() => BuildRows(lines, oldTokens, newTokens, whole, split));
        if (version != _version) return;
        // As wide as the largest line number and the longest line - the code scrolls sideways, as in VS Code.
        var digit = DigitWidth();
        Resources["NumberWidth"] = new GridLength(Math.Ceiling(Math.Max(2, lastNumber.ToString().Length) * digit + 12));
        // The mark (14), the code's padding (4 + 8) and the longest line.
        Resources["CodeWidth"] = Math.Ceiling(14 + 12 + longest * digit);
        // Unified: the rows scroll sideways. Side by side: each side half the width, both moved by SplitScroll.
        ScrollViewer.SetHorizontalScrollBarVisibility(Rows, split ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        _split = split;
        SplitScroll.Value = 0;
        FitSplitScroll();
        Rows.ItemTemplate = (DataTemplate)Resources[split ? "SplitRow" : "UnifiedRow"];
        Rows.ItemsSource = rows;
        Rows.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = true;
        FitSplitScroll();
    }

    private static (List<DiffRow> Rows, int Longest, int LastNumber) BuildRows(IReadOnlyList<DiffLine> lines, IReadOnlyList<Token[]> oldTokens,
        IReadOnlyList<Token[]> newTokens, bool whole, bool split)
    {
        DiffSide Side(DiffLine? line, bool old)
        {
            if (line is null) return DiffSide.Empty;
            var number = old ? line.OldNumber : line.NewNumber;
            var tokens = number is { } n ? (old ? oldTokens : newTokens).ElementAtOrDefault(n - 1) ?? [] : [];
            var text = line.Text;
            return new DiffSide
            {
                Kind = line.Kind.ToString(), Number = number?.ToString() ?? "", Tokens = tokens,
                Text = text.Length > DiffSide.MaxShown ? text[..DiffSide.MaxShown] : text, Full = text.Length > DiffSide.MaxShown ? text : null
            };
        }

        var rows = new List<DiffRow>();
        var longest = 0;
        var lastNumber = 0;
        foreach (var hunk in TextDiff.Hunks(lines, whole ? int.MaxValue / 4 : 3))
        {
            if (!whole) rows.Add(new DiffRow { Hunk = hunk.Header });
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
                    rows.Add(new DiffRow { Left = Side(row.Left, old: true), Right = Side(row.Right, old: false) });
            }
            else
            {
                foreach (var line in hunk.Lines)
                {
                    var side = line.Kind == DiffKind.Added ? Side(line, old: false) : Side(line, old: true);
                    rows.Add(new DiffRow
                    {
                        Left = line.OldNumber is null ? DiffSide.Empty : side, Right = line.NewNumber is null ? DiffSide.Empty : Side(line, old: false),
                        Unified = side
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

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lines is null) return;
        try { Clipboard.SetText(TextDiff.Unified(_lines, _oldName, _newName)); }
        catch (System.Runtime.InteropServices.COMException) { /* the clipboard is busy - nothing copied */ }
    }
}
