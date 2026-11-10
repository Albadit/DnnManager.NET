namespace DnnManager.Application.Deployment;

/// <summary>
/// A part of a merge: lines both sides agree on (<see cref="Conflict"/> false), or a place where they changed the same
/// lines differently - the project's lines, the server's, and the baseline's they both started from.
/// </summary>
public sealed record MergeChunk(IReadOnlyList<string> Lines, bool Conflict = false, IReadOnlyList<string>? Local = null,
    IReadOnlyList<string>? Remote = null, IReadOnlyList<string>? Base = null);

/// <summary>A merge of the project's file and the server's - its parts in order, and whether any is a conflict.</summary>
public sealed record MergeResult(IReadOnlyList<MergeChunk> Chunks)
{
    public bool HasConflicts => Chunks.Any(c => c.Conflict);
    public int ConflictCount => Chunks.Count(c => c.Conflict);

    /// <summary>
    /// The merged lines - each conflict written with git's markers, the project's side first, for the user to edit
    /// (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; project</c> … <c>=======</c> … <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; server</c>).
    /// </summary>
    public IReadOnlyList<string> WithMarkers()
    {
        var lines = new List<string>();
        foreach (var chunk in Chunks)
        {
            if (!chunk.Conflict)
            {
                lines.AddRange(chunk.Lines);
                continue;
            }
            lines.Add(MarkerLocal);
            lines.AddRange(chunk.Local!);
            lines.Add(MarkerSeparator);
            lines.AddRange(chunk.Remote!);
            lines.Add(MarkerRemote);
        }
        return lines;
    }

    public const string MarkerLocal = "<<<<<<< project (this PC)";
    public const string MarkerSeparator = "=======";
    public const string MarkerRemote = ">>>>>>> server";

    /// <summary>Whether <paramref name="lines"/> still hold a conflict marker - a merge that isn't finished.</summary>
    public static bool HasMarkers(IEnumerable<string> lines) =>
        lines.Any(l => l.StartsWith("<<<<<<< ", StringComparison.Ordinal) || l == MarkerSeparator || l.StartsWith(">>>>>>> ", StringComparison.Ordinal));
}

/// <summary>
/// Merges two changed versions of a file with the version they both started from (diff3, as git merges): a part only
/// one side changed takes that side's change, a part both changed the same way is taken once, and a part both changed
/// differently is a conflict. Without that common version (<c>baseLines</c> null - the file was added on
/// both sides, or its baseline content wasn't kept) every difference is a conflict: there is no telling which side changed.
/// </summary>
public static class ThreeWayMerge
{
    public static MergeResult Merge(IReadOnlyList<string>? baseLines, IReadOnlyList<string> local, IReadOnlyList<string> remote)
    {
        var twoWay = baseLines is null;
        // Without a common version, the lines both have are the common version - and any change is a conflict.
        var origin = baseLines ?? TextDiff.Matches(local, remote).Select(m => local[m.Old]).ToList();

        var toLocal = MatchMap(origin, local);
        var toRemote = MatchMap(origin, remote);
        var chunks = new List<MergeChunk>();
        var stable = new List<string>();
        int o = 0, a = 0, b = 0;
        while (true)
        {
            // Lines all three agree on, one after the other.
            while (o < origin.Count && a < local.Count && b < remote.Count && toLocal[o] == a && toRemote[o] == b)
            {
                stable.Add(origin[o]);
                o++; a++; b++;
            }
            if (o >= origin.Count && a >= local.Count && b >= remote.Count) break;

            // The next line all three agree on again - where this different part ends.
            int o2 = o, a2 = local.Count, b2 = remote.Count;
            for (; o2 < origin.Count; o2++)
                if (toLocal[o2] >= a && toRemote[o2] >= b)
                {
                    a2 = toLocal[o2];
                    b2 = toRemote[o2];
                    break;
                }

            var basePart = Slice(origin, o, o2);
            var localPart = Slice(local, a, a2);
            var remotePart = Slice(remote, b, b2);
            var localSame = localPart.SequenceEqual(basePart);
            var remoteSame = remotePart.SequenceEqual(basePart);
            if (localPart.SequenceEqual(remotePart)) stable.AddRange(localPart);
            else if (!twoWay && localSame) stable.AddRange(remotePart);
            else if (!twoWay && remoteSame) stable.AddRange(localPart);
            else
            {
                if (stable.Count > 0) chunks.Add(new MergeChunk([.. stable]));
                stable.Clear();
                chunks.Add(new MergeChunk([], true, localPart, remotePart, twoWay ? null : basePart));
            }
            o = o2; a = a2; b = b2;
        }
        if (stable.Count > 0) chunks.Add(new MergeChunk([.. stable]));
        return new MergeResult(chunks);
    }

    // For each line of the origin: the line of the other text it matches, or -1.
    private static int[] MatchMap(IReadOnlyList<string> origin, IReadOnlyList<string> other)
    {
        var map = Enumerable.Repeat(-1, origin.Count).ToArray();
        foreach (var (o, n) in TextDiff.Matches(origin, other)) map[o] = n;
        return map;
    }

    private static List<string> Slice(IReadOnlyList<string> lines, int from, int to) => [.. lines.Skip(from).Take(to - from)];
}
