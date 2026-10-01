using System.Text.RegularExpressions;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// What the search bar looks for: its text, and how - <b>Match Case</b>, <b>Match Whole Word</b> (not inside a longer
/// word), <b>Use Regular Expression</b>. Every tab finds its matches with it, line by line.
/// </summary>
public sealed class SearchQuery
{
    // A pattern that takes this long on one line ("(a+)+b"…) stops that line's search rather than the window.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private readonly Regex? _regex;

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

    /// <summary>Where it is in <paramref name="line"/>: (start, length) of each match, empty ones left out.</summary>
    public List<(int Start, int Length)> Matches(string line)
    {
        var found = new List<(int, int)>();
        if (_regex is null) return found;
        try
        {
            for (var match = _regex.Match(line); match.Success; match = match.NextMatch())
                if (match.Length > 0) found.Add((match.Index, match.Length));
        }
        catch (RegexMatchTimeoutException)
        {
            // What was found so far on this line stays.
        }
        return found;
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
