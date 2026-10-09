using System.Text;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// Reading what DNN logs - its own logs (<c>Portals\_default\Logs</c>) and its database scripts' (<c>Providers\
/// DataProviders\SqlDataProvider</c>) - the same way for the installer and the upgrade's checks.
/// </summary>
internal static class DnnLogFiles
{
    /// <summary>The file's lines, read while DNN may still be writing it; none when it can't be read.</summary>
    public static IReadOnlyList<string> ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A log line short enough for a message: its first 300 characters.</summary>
    public static string Short(string text) => text.Length > 300 ? text[..300] + "\u2026" : text;

    /// <summary>
    /// DNN's database scripts that reported a problem since <paramref name="sinceUtc"/>, with the first line they logged: a
    /// script that ran cleanly leaves an empty log behind.
    /// </summary>
    public static IEnumerable<(string File, string FirstLine)> ScriptProblems(string siteDirectory, DateTime sinceUtc)
    {
        var scripts = Path.Combine(siteDirectory, "Providers", "DataProviders", "SqlDataProvider");
        if (!Directory.Exists(scripts)) yield break;
        foreach (var file in Directory.EnumerateFiles(scripts, "*.log.resources"))
            if (File.GetLastWriteTimeUtc(file) >= sinceUtc &&
                ReadShared(file).FirstOrDefault(l => l.Trim().Trim('\uFEFF').Length > 0) is { } first)
                yield return (Path.GetFileName(file), Short(first));
    }
}
