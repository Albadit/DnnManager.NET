using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DnnManager.Application.Deployment;

/// <summary>What to look for in a project's files - VS Code's search box and its toggles: match case, whole word, regular expression.</summary>
public sealed record FindQuery(string Text, bool MatchCase = false, bool WholeWord = false, bool UseRegex = false)
{
    // A pattern that takes this long on one line ("(a+)+b"…) gives up on that line, not on the search.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    /// <summary>The pattern - null when the text is empty, or (with <paramref name="problem"/>) not a valid regular expression.</summary>
    public Regex? ToRegex(out string? problem)
    {
        problem = null;
        if (Text.Length == 0) return null;
        var pattern = UseRegex ? Text : Regex.Escape(Text);
        // A word: not right after or before a letter, digit or _ - as VS Code's Match Whole Word.
        if (WholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        try
        {
            return new Regex(pattern, (MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, Timeout);
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}

/// <summary>
/// A match on a line (1-based) at <paramref name="Start"/> (a character of the line, 0-based) - and what the results
/// show of the line: a little before it, the match, the rest (cut, for a minified file's line).
/// </summary>
public sealed record FindMatch(int Line, int Start, int Length, string Before, string Text, string After);

/// <summary>A file's matches, its path relative to the project with <c>/</c>.</summary>
public sealed record FoundFile(string Path, IReadOnlyList<FindMatch> Matches);

/// <summary>The files with a match, by path; how many files were read; whether it stopped at <see cref="ProjectFiles.MaxMatches"/>.</summary>
public sealed record FindResult(IReadOnlyList<FoundFile> Files, int MatchCount, int Searched, bool Truncated);

/// <summary>What Replace All did: files written, matches replaced, and the files it couldn't write, with why.</summary>
public sealed record ReplaceResult(int Files, int Replacements, IReadOnlyList<string> Failed);

/// <summary>A file or folder of a project's folder, as the Explorer lists it.</summary>
public sealed record ProjectEntry(string Path, string Name, bool IsFolder);

/// <summary>
/// A project's files as Source Control's Explorer and Search see them: those its server's ignore rules don't ignore
/// (what a push would send) - no links or junctions, no .git, none of DNN Manager's own. Search reads each text file
/// (binary ones and ones over <see cref="TextContent.MaxBytes"/> are passed over) line by line, as VS Code does;
/// Replace All writes a file back in its own encoding, byte order mark and line endings.
/// </summary>
public static class ProjectFiles
{
    /// <summary>Search stops after this many matches - more is no help in a list, and it would take long.</summary>
    public const int MaxMatches = 20_000;

    // What the results show of a line around its match.
    private const int BeforeChars = 40, AfterChars = 200;

    // Never text: not read at all, which would take long for a site's bin/.
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".bmp", ".tif", ".tiff", ".zip", ".7z", ".gz",
        ".rar", ".nupkg", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".webm", ".avi", ".mov", ".pdf", ".mdf", ".ldf",
        ".bak", ".bacpac", ".psd"
    };

    private static readonly EnumerationOptions OneFolder = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>What <paramref name="folder"/> (relative, "" the project's) holds: its folders, then its files, each by name.</summary>
    public static IReadOnlyList<ProjectEntry> List(string root, string folder, IgnoreRules rules)
    {
        var directory = new DirectoryInfo(Full(root, folder));
        if (!directory.Exists) return [];
        var entries = new List<ProjectEntry>();
        foreach (var info in directory.EnumerateFileSystemInfos("*", OneFolder))
        {
            var isFolder = info is DirectoryInfo;
            var path = folder.Length == 0 ? info.Name : $"{folder}/{info.Name}";
            if (Skipped(path, info.Name, isFolder, rules)) continue;
            entries.Add(new ProjectEntry(path, info.Name, isFolder));
        }
        return [.. entries.OrderBy(e => e.IsFolder ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every file of the project the rules don't ignore, relative with <c>/</c>.</summary>
    public static IEnumerable<string> All(string root, IgnoreRules rules, CancellationToken ct)
    {
        var folders = new Stack<string>([""]);
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            IEnumerable<ProjectEntry> entries;
            try { entries = List(root, folder, rules); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry.IsFolder) folders.Push(entry.Path);
                else yield return entry.Path;
            }
        }
    }

    /// <summary>Every match of <paramref name="query"/> in the project's text files - the files by path, the matches in order.</summary>
    public static FindResult Find(string root, IgnoreRules rules, FindQuery query, CancellationToken ct)
    {
        if (query.ToRegex(out _) is not { } regex) return new FindResult([], 0, 0, false);
        var found = new ConcurrentBag<FoundFile>();
        var count = 0;
        var searched = 0;
        var truncated = false;
        Parallel.ForEach(All(root, rules, ct), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            (path, state) =>
            {
                if (Volatile.Read(ref count) >= MaxMatches)
                {
                    truncated = true;
                    state.Stop();
                    return;
                }
                if (Read(root, path) is not { } text) return;
                Interlocked.Increment(ref searched);
                var matches = new List<FindMatch>();
                var line = 0;
                foreach (var content in Lines(text))
                {
                    line++;
                    foreach (var (start, length) in Matches(regex, content))
                    {
                        matches.Add(Describe(line, content, start, length));
                        if (Interlocked.Increment(ref count) >= MaxMatches) break;
                    }
                    if (Volatile.Read(ref count) >= MaxMatches) break;
                }
                if (matches.Count > 0) found.Add(new FoundFile(path, matches));
            });
        if (count >= MaxMatches) truncated = true;
        return new FindResult([.. found.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)], Math.Min(count, MaxMatches), searched, truncated);
    }

    /// <summary>
    /// Replaces every match of <paramref name="query"/> in <paramref name="paths"/> with <paramref name="replacement"/> -
    /// <c>$1</c> and the like a group's text with a regular expression, as written otherwise. Each file is read again
    /// (what it holds now is replaced) and written whole, in its own encoding, byte order mark and line endings, through
    /// a file beside it, so a failure never leaves half a file.
    /// </summary>
    public static ReplaceResult Replace(string root, IEnumerable<string> paths, FindQuery query, string replacement, CancellationToken ct)
    {
        if (query.ToRegex(out var problem) is not { } regex) return new ReplaceResult(0, 0, problem is null ? [] : [problem]);
        var files = 0;
        var replaced = 0;
        var failed = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var full = Full(root, path);
                if (TextContent.Decode(File.ReadAllBytes(full)) is not { } decoded) continue;
                var (text, count) = ReplaceIn(decoded.Text, regex, query.UseRegex, replacement);
                if (count == 0) continue;
                if (decoded.Encoding is { CodePage: 28591 } && text.Any(c => c > 'ÿ'))
                {
                    failed.Add($"{path}: the replacement has characters its encoding (Latin-1) can't hold");
                    continue;
                }
                byte[] bytes = [.. decoded.HasBom ? decoded.Encoding.GetPreamble() : [], .. decoded.Encoding.GetBytes(text)];
                var temp = full + ".dnnmgr-replace";
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    File.Move(temp, full, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                files++;
                replaced += count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{path}: {ex.Message}");
            }
        }
        return new ReplaceResult(files, replaced, failed);
    }

    /// <summary><paramref name="text"/> with each line's matches replaced - line by line, as they were found - and how many.</summary>
    internal static (string Text, int Count) ReplaceIn(string text, Regex regex, bool useRegex, string replacement)
    {
        var result = new StringBuilder(text.Length);
        var count = 0;
        var at = 0;
        while (at <= text.Length)
        {
            var end = text.IndexOf('\n', at);
            var next = end < 0 ? text.Length + 1 : end + 1;
            var lineEnd = end < 0 ? text.Length : end > at && text[end - 1] == '\r' ? end - 1 : end;
            var line = text[at..lineEnd];
            result.Append(regex.Replace(line, m =>
            {
                if (m.Length == 0) return m.Value;
                count++;
                return useRegex ? m.Result(replacement) : replacement;
            }));
            result.Append(text, lineEnd, Math.Min(next, text.Length) - lineEnd);
            at = next;
        }
        return (result.ToString(), count);
    }

    // The matches on a line - none of no length (a pattern as "a*" matches everywhere); a line that takes too long, none.
    private static IEnumerable<(int Start, int Length)> Matches(Regex regex, string line)
    {
        var list = new List<(int, int)>();
        try
        {
            for (var m = regex.Match(line); m.Success; m = m.NextMatch())
                if (m.Length > 0) list.Add((m.Index, m.Length));
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return list;
    }

    private static FindMatch Describe(int line, string content, int start, int length)
    {
        var from = Math.Max(0, start - BeforeChars);
        var before = content[from..start].TrimStart();
        if (from > 0) before = "…" + before;
        var shown = Math.Min(length, AfterChars);
        var afterEnd = Math.Min(content.Length, start + length + AfterChars);
        return new FindMatch(line, start, length, before, content.Substring(start, shown), content[(start + shown)..Math.Max(start + shown, afterEnd)]);
    }

    // The lines of a text, without their line endings.
    private static IEnumerable<string> Lines(string text)
    {
        var at = 0;
        while (at <= text.Length)
        {
            var end = text.IndexOf('\n', at);
            if (end < 0)
            {
                if (at < text.Length) yield return text[at..];
                yield break;
            }
            yield return text[at..(end > at && text[end - 1] == '\r' ? end - 1 : end)];
            at = end + 1;
        }
    }

    // A file's text - null when it is binary, too big, or can't be read.
    private static string? Read(string root, string path)
    {
        if (BinaryExtensions.Contains(Path.GetExtension(path))) return null;
        try
        {
            var full = Full(root, path);
            if (new FileInfo(full).Length > TextContent.MaxBytes) return null;
            return TextContent.Decode(File.ReadAllBytes(full))?.Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Skipped(string path, string name, bool isFolder, IgnoreRules rules) =>
        (isFolder && name.Equals(".git", StringComparison.OrdinalIgnoreCase)) || DeployPath.IsOwnFile(path) || rules.IsIgnored(path, isFolder);

    /// <summary>
    /// <paramref name="relative"/> under <paramref name="root"/> - never outside it (a path with <c>..</c> from a list
    /// that was tampered with).
    /// </summary>
    public static string Full(string root, string relative)
    {
        var top = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(top, relative.Replace('/', Path.DirectorySeparatorChar)));
        var inside = top.EndsWith(Path.DirectorySeparatorChar) ? top : top + Path.DirectorySeparatorChar;
        if (!full.Equals(top, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"{relative} is outside the project's folder.");
        return full;
    }
}
