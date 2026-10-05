using System.Globalization;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// One dated backup of a project: a folder <c>&lt;backups&gt;\&lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\</c> holding the
/// site's <c>.zip</c> and / or the database's <c>.bacpac</c> (or <c>.bak</c>).
/// </summary>
/// <param name="Note">What it was made for - the first line of its <see cref="ProjectBackups.NoteFile"/>, e.g. "Before upgrading DNN 09.13.09 → 10.02.05".</param>
public sealed record ProjectBackup(string Project, string Folder, DateTime Created, string? SiteZip, string? Database, string? Note = null)
{
    /// <summary>Both halves are there, so it can be imported as a new project.</summary>
    public bool IsComplete => SiteZip is not null && Database is not null;
}

/// <summary>
/// Where and how project backups are kept: in <see cref="DnnProject.BackupDirectory"/>, one folder per project in
/// the user's <c>Documents\DnnManager\backups</c> - outside the site, so IIS never serves them, a site export
/// never includes them, and they are kept when the project is removed.
/// </summary>
public static class ProjectBackups
{
    private const string StampFormat = "yyyyMMdd_HHmmss";

    /// <summary>A backup's notes, when something says why it was made (an upgrade's stages do): its first line names it.</summary>
    public const string NoteFile = "backup.txt";

    /// <summary>A new backup folder for a backup taken at <paramref name="when"/> (not created yet).</summary>
    public static string NewFolder(DnnProject project, DateTime when) =>
        Path.Combine(project.BackupDirectory, $"{project.Name}_{when.ToString(StampFormat, CultureInfo.InvariantCulture)}");

    /// <summary>The site zip's name inside a backup folder.</summary>
    public static string SiteZipName(DnnProject project) => project.Name + ".zip";

    /// <summary>The database file's name inside a backup folder, for <paramref name="extension"/> ".bacpac" or ".bak".</summary>
    public static string DatabaseName(DnnProject project, string extension) => project.Name + extension;

    /// <summary>The project's dated backups, newest first.</summary>
    public static IReadOnlyList<ProjectBackup> List(DnnProject project)
    {
        if (!Directory.Exists(project.BackupDirectory)) return Array.Empty<ProjectBackup>();

        var backups = new List<ProjectBackup>();
        foreach (var folder in Directory.EnumerateDirectories(project.BackupDirectory))
        {
            var zip = Directory.EnumerateFiles(folder, "*.zip").FirstOrDefault();
            var database = Directory.EnumerateFiles(folder)
                .Where(LocalSqlContainer.IsBackupFile)
                .OrderByDescending(f => f.EndsWith(".bacpac", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (zip is null && database is null) continue;

            // "<project>_20260928_154210" - fall back to the folder's date for anything named by hand.
            var name = Path.GetFileName(folder);
            var created = name.Length > StampFormat.Length &&
                          DateTime.TryParseExact(name[^StampFormat.Length..], StampFormat, CultureInfo.InvariantCulture,
                              DateTimeStyles.None, out var stamp)
                ? stamp
                : Directory.GetCreationTime(folder);
            backups.Add(new ProjectBackup(project.Name, folder, created, zip, database, Note(folder)));
        }
        return backups.OrderByDescending(b => b.Created).ToList();
    }

    private static string? Note(string folder)
    {
        try
        {
            var file = Path.Combine(folder, NoteFile);
            return File.Exists(file) ? File.ReadLines(file).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
