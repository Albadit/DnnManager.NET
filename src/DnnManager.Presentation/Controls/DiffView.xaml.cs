using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Deployment;

namespace DnnManager.Presentation.Controls;

/// <summary>One side of a row of the diff: a line - its number, its mark (+ or -), its text and its tokens - or nothing.</summary>
public sealed class DiffSide
{
    public static readonly DiffSide Empty = new() { Kind = "Empty" };

    /// <summary>Same, Added, Removed or Empty - what colours it.</summary>
    public string Kind { get; init; } = "Same";
    public string Number { get; init; } = "";
    public string Text { get; init; } = "";
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
        _lines = null;
        Rows.ItemsSource = null;
        Rows.Visibility = Visibility.Collapsed;
        Message.Text = message;
        Message.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = false;
        Titles.Text = "";
    }

    /// <summary>
    /// Shows how <paramref name="oldBytes"/> (<paramref name="oldName"/>, null: no file) became <paramref name="newBytes"/>
    /// - for <paramref name="path"/>, whose extension decides the colours.
    /// </summary>
    public void ShowDiff(string path, byte[]? oldBytes, byte[]? newBytes, string oldName, string newName)
    {
        var oldText = oldBytes is null ? TextContent.Empty : TextContent.From(oldBytes);
        var newText = newBytes is null ? TextContent.Empty : TextContent.From(newBytes);
        if (oldText is null || newText is null)
        {
            ShowMessage($"{path} isn't text (or is larger than {TextContent.MaxBytes / (1024 * 1024)} MB) - " +
                        $"{Describe(oldBytes, oldName)}, {Describe(newBytes, newName)}.");
            return;
        }
        var language = SyntaxHighlighter.For(path);
        _oldTokens = SyntaxHighlighter.Tokenize(oldText.Lines, language);
        _newTokens = SyntaxHighlighter.Tokenize(newText.Lines, language);
        _lines = TextDiff.Compute(oldText.Lines, newText.Lines);
        _oldName = oldName;
        _newName = newName;
        var added = _lines.Count(l => l.Kind == DiffKind.Added);
        var removed = _lines.Count(l => l.Kind == DiffKind.Removed);
        Titles.Text = $"{oldName} → {newName}   +{added} −{removed}" +
                      (oldText.Newline != newText.Newline && oldBytes is not null && newBytes is not null ? "   (line endings differ)" : "");
        if (added == 0 && removed == 0)
        {
            ShowMessage(oldBytes is not null && newBytes is not null && !oldBytes.AsSpan().SequenceEqual(newBytes)
                ? "The lines are the same - only the encoding, a byte order mark or the line endings differ."
                : "The two versions are the same.");
            return;
        }
        Render();
    }

    private static string Describe(byte[]? bytes, string name) => bytes is null ? $"none in {name}" : $"{bytes.Length:N0} bytes in {name}";

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && _lines is not null) Render();
    }

    private void Render()
    {
        if (_lines is null) return;
        var context = WholeFile.IsChecked == true ? int.MaxValue / 4 : 3;
        var split = SplitMode.IsChecked == true;
        var rows = new List<DiffRow>();
        foreach (var hunk in TextDiff.Hunks(_lines, context))
        {
            if (WholeFile.IsChecked != true) rows.Add(new DiffRow { Hunk = hunk.Header });
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
        Rows.ItemTemplate = (DataTemplate)Resources[split ? "SplitRow" : "UnifiedRow"];
        Rows.ItemsSource = rows;
        Rows.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = true;
    }

    private DiffSide Side(DiffLine? line, bool old)
    {
        if (line is null) return DiffSide.Empty;
        var number = old ? line.OldNumber : line.NewNumber;
        var tokens = number is { } n ? (old ? _oldTokens : _newTokens).ElementAtOrDefault(n - 1) ?? [] : [];
        return new DiffSide { Kind = line.Kind.ToString(), Number = number?.ToString() ?? "", Text = line.Text, Tokens = tokens };
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lines is null) return;
        try { Clipboard.SetText(TextDiff.Unified(_lines, _oldName, _newName)); }
        catch (System.Runtime.InteropServices.COMException) { /* the clipboard is busy - nothing copied */ }
    }
}
