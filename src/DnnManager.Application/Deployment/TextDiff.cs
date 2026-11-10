using System.Text;

namespace DnnManager.Application.Deployment;

/// <summary>What a line of a diff is: in both, only in the new text, or only in the old one.</summary>
public enum DiffKind { Same, Added, Removed }

/// <summary>A line of a diff, with its number in the old and in the new text (1-based; null where it isn't in that one).</summary>
public sealed record DiffLine(DiffKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>A part of a unified diff: the lines around a change, and where they are (<c>@@ -12,7 +12,9 @@</c>).</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines)
{
    public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@";
}

/// <summary>A row of a side-by-side diff: the old line on the left, the new one on the right - either may be missing.</summary>
public sealed record SideBySideRow(DiffLine? Left, DiffLine? Right);

/// <summary>
/// A line-by-line diff of two texts (Myers' algorithm, as git's): the shortest list of lines added and removed that turns
/// the old text into the new one. Lines are compared exactly - a changed line ending or trailing space is a change.
/// </summary>
public static class TextDiff
{
    // Beyond so many differing lines the edit script isn't searched for further: the rest is one block replaced. The
    // search keeps each round's points (d² of them), so this bounds its memory too - some 36 MB.
    private const int MaxEdits = 3_000;

    /// <summary>The diff of <paramref name="oldLines"/> to <paramref name="newLines"/>, every line of both in order.</summary>
    public static IReadOnlyList<DiffLine> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        // What both start and end with is the same - only what is between is searched.
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix &&
               oldLines[oldLines.Count - 1 - suffix] == newLines[newLines.Count - 1 - suffix]) suffix++;

        var result = new List<DiffLine>(Math.Max(oldLines.Count, newLines.Count) + 8);
        for (var i = 0; i < prefix; i++) result.Add(new DiffLine(DiffKind.Same, i + 1, i + 1, oldLines[i]));

        var a = new ArraySegment<string>([.. oldLines.Skip(prefix).Take(oldLines.Count - prefix - suffix)]);
        var b = new ArraySegment<string>([.. newLines.Skip(prefix).Take(newLines.Count - prefix - suffix)]);
        foreach (var (kind, ai, bi) in Middle(a, b))
            result.Add(kind switch
            {
                DiffKind.Same => new DiffLine(DiffKind.Same, prefix + ai + 1, prefix + bi + 1, a[ai]),
                DiffKind.Removed => new DiffLine(DiffKind.Removed, prefix + ai + 1, null, a[ai]),
                _ => new DiffLine(DiffKind.Added, null, prefix + bi + 1, b[bi])
            });

        for (var i = suffix; i > 0; i--)
            result.Add(new DiffLine(DiffKind.Same, oldLines.Count - i + 1, newLines.Count - i + 1, oldLines[oldLines.Count - i]));
        return result;
    }

    /// <summary>The pairs of line numbers (0-based) that are the same in both - the longest common subsequence.</summary>
    public static IReadOnlyList<(int Old, int New)> Matches(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines) =>
        [.. Compute(oldLines, newLines).Where(l => l.Kind == DiffKind.Same).Select(l => (l.OldNumber!.Value - 1, l.NewNumber!.Value - 1))];

    // Myers' O(ND) greedy search, keeping each round's furthest points to walk the path back.
    private static IEnumerable<(DiffKind Kind, int A, int B)> Middle(ArraySegment<string> a, ArraySegment<string> b)
    {
        int n = a.Count, m = b.Count;
        if (n == 0)
        {
            for (var j = 0; j < m; j++) yield return (DiffKind.Added, 0, j);
            yield break;
        }
        if (m == 0)
        {
            for (var i = 0; i < n; i++) yield return (DiffKind.Removed, i, 0);
            yield break;
        }
        // Lines compared by number, not by string, in the loop.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var ai = a.Select(s => Id(ids, s)).ToArray();
        var bi = b.Select(s => Id(ids, s)).ToArray();

        var max = Math.Min(n + m, MaxEdits);
        var offset = max + 1;
        var v = new int[2 * max + 3];
        // Round d's points before it ran - only the diagonals -d-1 … d+1 it can read.
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add(v[(offset - d - 1)..(offset + d + 2)]);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && ai[x] == bi[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }
        if (!found)
        {
            // Too different to search further: all of the old out, all of the new in.
            for (var i = 0; i < n; i++) yield return (DiffKind.Removed, i, 0);
            for (var j = 0; j < m; j++) yield return (DiffKind.Added, 0, j);
            yield break;
        }

        // Walk back from the end through each round's furthest points.
        var steps = new List<(DiffKind, int, int)>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            int At(int diagonal) => vd[diagonal + d + 1];
            var k = cx - cy;
            int prevK;
            if (d == 0) prevK = 0;
            else if (k == -d || (k != d && At(k - 1) < At(k + 1))) prevK = k + 1;
            else prevK = k - 1;
            var prevX = d == 0 ? 0 : At(prevK);
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--; cy--;
                steps.Add((DiffKind.Same, cx, cy));
            }
            if (d > 0)
            {
                if (cx == prevX) steps.Add((DiffKind.Added, cx, cy = prevY));
                else steps.Add((DiffKind.Removed, cx = prevX, cy));
            }
            cx = prevX; cy = prevY;
        }
        for (var i = steps.Count - 1; i >= 0; i--) yield return steps[i];
    }

    private static int Id(Dictionary<string, int> ids, string line)
    {
        if (!ids.TryGetValue(line, out var id)) ids[line] = id = ids.Count;
        return id;
    }

    /// <summary>The changes with <paramref name="context"/> unchanged lines around each - a unified diff's hunks.</summary>
    public static IReadOnlyList<DiffHunk> Hunks(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var hunks = new List<DiffHunk>();
        var i = 0;
        while (i < lines.Count)
        {
            while (i < lines.Count && lines[i].Kind == DiffKind.Same) i++;
            if (i >= lines.Count) break;
            var start = Math.Max(0, i - context);
            var end = i;
            // Grow while the next change is within twice the context - one hunk, as git does.
            while (true)
            {
                while (end < lines.Count && lines[end].Kind != DiffKind.Same) end++;
                var same = 0;
                while (end + same < lines.Count && lines[end + same].Kind == DiffKind.Same) same++;
                if (end + same < lines.Count && same <= 2 * context) { end += same; continue; }
                end = Math.Min(lines.Count, end + Math.Min(same, context));
                break;
            }
            var part = lines.Skip(start).Take(end - start).ToList();
            var oldStart = part.FirstOrDefault(l => l.OldNumber is not null)?.OldNumber ?? OldBefore(lines, start) + 1;
            var newStart = part.FirstOrDefault(l => l.NewNumber is not null)?.NewNumber ?? NewBefore(lines, start) + 1;
            var oldCount = part.Count(l => l.Kind != DiffKind.Added);
            var newCount = part.Count(l => l.Kind != DiffKind.Removed);
            hunks.Add(new DiffHunk(oldCount == 0 ? oldStart - 1 : oldStart, oldCount, newCount == 0 ? newStart - 1 : newStart, newCount, part));
            i = end;
        }
        return hunks;
    }

    private static int OldBefore(IReadOnlyList<DiffLine> lines, int index)
    {
        for (var i = index - 1; i >= 0; i--) if (lines[i].OldNumber is { } n) return n;
        return 0;
    }

    private static int NewBefore(IReadOnlyList<DiffLine> lines, int index)
    {
        for (var i = index - 1; i >= 0; i--) if (lines[i].NewNumber is { } n) return n;
        return 0;
    }

    /// <summary>
    /// The diff as two columns: unchanged lines side by side, and each block of removed lines beside the added lines that
    /// replace it, row for row.
    /// </summary>
    public static IReadOnlyList<SideBySideRow> SideBySide(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<SideBySideRow>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind == DiffKind.Same)
            {
                rows.Add(new SideBySideRow(lines[i], lines[i]));
                i++;
                continue;
            }
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            while (i < lines.Count && lines[i].Kind != DiffKind.Same)
            {
                (lines[i].Kind == DiffKind.Removed ? removed : added).Add(lines[i]);
                i++;
            }
            for (var r = 0; r < Math.Max(removed.Count, added.Count); r++)
                rows.Add(new SideBySideRow(r < removed.Count ? removed[r] : null, r < added.Count ? added[r] : null));
        }
        return rows;
    }

    /// <summary>The diff as text, as <c>git diff</c> writes it - for the clipboard.</summary>
    public static string Unified(IReadOnlyList<DiffLine> lines, string oldName, string newName, int context = 3)
    {
        var text = new StringBuilder();
        text.Append("--- ").AppendLine(oldName);
        text.Append("+++ ").AppendLine(newName);
        foreach (var hunk in Hunks(lines, context))
        {
            text.AppendLine(hunk.Header);
            foreach (var line in hunk.Lines)
                text.Append(line.Kind switch { DiffKind.Added => '+', DiffKind.Removed => '-', _ => ' ' }).AppendLine(line.Text);
        }
        return text.ToString();
    }
}
