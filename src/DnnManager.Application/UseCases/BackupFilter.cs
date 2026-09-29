namespace DnnManager.Application.UseCases;

/// <summary>
/// A site's <c>_backup.filter</c>: the files and folders a backup leaves out, one per line, in the format Azure App
/// Service uses - paths from <c>D:\home</c>, so <c>\site\wwwroot\App_Data\Search</c> is the site's
/// <c>App_Data\Search</c>. A path without the <c>\site\wwwroot</c> prefix is taken as relative to the site root.
/// A folder leaves out everything in it.
/// </summary>
public static class BackupFilter
{
    public const string FileName = "_backup.filter";

    private const string AzureSiteRoot = @"site\wwwroot";

    /// <summary>
    /// The paths to leave out, relative to <paramref name="siteDirectory"/> (e.g. <c>App_Data\Search</c>) - empty
    /// when the site has no <c>_backup.filter</c>. Blank lines, the site root itself and paths leaving the site are ignored.
    /// </summary>
    public static IReadOnlyList<string> Read(string siteDirectory)
    {
        var path = Path.Combine(siteDirectory, FileName);
        if (!File.Exists(path)) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            var entry = line.Trim().Replace('/', '\\').Trim('\\');
            if (entry.StartsWith(AzureSiteRoot + @"\", StringComparison.OrdinalIgnoreCase))
                entry = entry[(AzureSiteRoot.Length + 1)..].Trim('\\');
            else if (entry.Equals(AzureSiteRoot, StringComparison.OrdinalIgnoreCase))
                continue; // the whole site
            if (entry.Length == 0 || entry.Split('\\').Any(part => part is "." or ".."))
                continue;
            if (!paths.Contains(entry, StringComparer.OrdinalIgnoreCase)) paths.Add(entry);
        }
        return paths;
    }
}
