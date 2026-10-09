using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// What the search bar looks for: its text, and how - <b>Match Case</b>, <b>Match Whole Word</b> (not inside a longer
/// word), <b>Use Regular Expression</b>. Every tab finds its matches with it, line by line.
/// </summary>
public sealed class SearchQuery
{
    // A pattern that takes this long on one line ("(a+)+b"…) stops that line's search rather than the window.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    // A whole search - every line of a 50 000-line log - stops after this long: it runs on the UI thread, which mustn't
    // freeze, whatever the pattern.
    private static readonly TimeSpan PassBudget = TimeSpan.FromMilliseconds(250);
    private static readonly IReadOnlyList<(int Start, int Length)> None = [];
    private readonly Regex? _regex;
    private readonly Stopwatch _pass = new();

    public SearchQuery(string text, bool matchCase = false, bool wholeWord = false, bool useRegex = false)
    {
        if (text.Length == 0) return;
        var pattern = useRegex ? text : Regex.Escape(text);
        // A word: letters, digits and _ - the match has none of them right before or after it.
        if (wholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        try
        {
            _regex = new Regex(pattern, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, Timeout);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Why the regular expression can't be used; null when it can.</summary>
    public string? Error { get; }

    /// <summary>Nothing to look for: no text, or a regular expression that isn't one.</summary>
    public bool IsEmpty => _regex is null;

    /// <summary>The last search took too long and stopped before the end: the matches after where it stopped aren't counted.</summary>
    public bool Stopped { get; private set; }

    /// <summary>A search through every line starts: its time is counted from now (see <see cref="Stopped"/>).</summary>
    public void BeginPass()
    {
        Stopped = false;
        _pass.Restart();
    }

    /// <summary>
    /// Where it is in <paramref name="line"/>: (start, length) of each match, empty ones left out. Nothing once the search
    /// has taken its time (<see cref="Stopped"/>).
    /// </summary>
    public IReadOnlyList<(int Start, int Length)> Matches(string line)
    {
        if (_regex is null) return None;
        if (_pass.IsRunning && _pass.Elapsed > PassBudget)
        {
            Stopped = true;
            return None;
        }
        // Most lines have no match: no list for them.
        List<(int, int)>? found = null;
        try
        {
            for (var match = _regex.Match(line); match.Success; match = match.NextMatch())
                if (match.Length > 0) (found ??= []).Add((match.Index, match.Length));
        }
        catch (RegexMatchTimeoutException)
        {
            // What was found so far on this line stays.
        }
        return found ?? None;
    }
}

/// <summary>
/// What the bottom panel's search bar (Ctrl+F) searches: the Output tab, a log in Logs, or a terminal's output.
/// Each finds every match, highlights them, and shows one of them - the bar keeps "3 / 18".
/// </summary>
internal interface ISearchTarget
{
    /// <summary>Highlights every match of <paramref name="query"/> and returns how many there are.</summary>
    int Find(SearchQuery query);

    /// <summary>
    /// Marks match <paramref name="index"/> (0-based, in reading order) as the current one - and brings it into view
    /// when <paramref name="reveal"/> (not when the text only grew: a followed log isn't pulled away from its end).
    /// </summary>
    void ShowMatch(int index, bool reveal = true);

    /// <summary>Takes the highlights away - the text itself stays as it was.</summary>
    void ClearSearch();

    /// <summary>The text changed (new output, a new log line) - the bar searches again.</summary>
    event EventHandler? ContentChanged;
}
