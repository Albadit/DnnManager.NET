using DnnManager.Infrastructure.Updates;

namespace DnnManager.Presentation.Services;

/// <summary>A release's notes: its version and its Markdown, as <c>docs/release-notes/vX.Y.Z.md</c> has it.</summary>
public sealed record ReleaseNote(Version Version, string Markdown)
{
    /// <summary>
    /// Where a link in the notes goes: a relative one (<c>../../CHANGELOG.md#v173</c>) to the file as it is at the
    /// release's tag on GitHub - as the release workflow turns them for the GitHub release (release-notes.ps1).
    /// </summary>
    public string LinkTarget(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto" ? link
        : link.StartsWith('#') ? $"{AppReleaseFeed.Repository}/releases/tag/v{Version}"
        : new Uri(new Uri($"{AppReleaseFeed.Repository}/blob/v{Version}/docs/release-notes/"), link).ToString();
}

/// <summary>
/// The release notes built into DNN Manager (<c>docs/release-notes/v*.md</c>, embedded by DnnManager.csproj) - the same
/// text each GitHub release shows. What's new after an update shows the ones since the version before.
/// </summary>
public static class ReleaseNotes
{
    private const string Prefix = "ReleaseNotes.";

    /// <summary>The notes of the versions after <paramref name="from"/> up to <paramref name="to"/> - newest first; every one up to <paramref name="to"/> when <paramref name="from"/> is null.</summary>
    public static IReadOnlyList<ReleaseNote> Between(Version? from, Version to)
    {
        var assembly = typeof(ReleaseNotes).Assembly;
        var notes = new List<ReleaseNote>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            var tag = name[Prefix.Length..^".md".Length];
            // A pre-release's notes (v1.7.0-rc.1) aren't what an installed release changed.
            if (tag.Contains('-') || !AppReleaseFeed.TryParseVersion(tag, out var version)) continue;
            if (from is not null && !AppReleaseFeed.IsNewer(version, from) || AppReleaseFeed.IsNewer(version, to)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            notes.Add(new ReleaseNote(version, reader.ReadToEnd()));
        }
        return notes.OrderByDescending(n => n.Version).ToList();
    }
}