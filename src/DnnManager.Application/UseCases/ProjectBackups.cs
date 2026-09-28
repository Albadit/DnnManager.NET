using System.Globalization;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// One dated backup of a project: a folder <c>&lt;project&gt;\01_backup\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\</c> holding the
/// site's <c>.zip</c> and / or the database's <c>.bacpac</c> (or <c>.bak</c>).
/// </summary>
public sealed record ProjectBackup(string Project, string Folder, DateTime Created, string? SiteZip, string? Database)
{
    /// <summary>Both halves are there, so it can be imported as a new project.</summary>
    public bool IsComplete => SiteZip is not null && Database is not null;
}

/// <summary>Where and how project backups are kept.</summary>
public static class ProjectBackups
{
    /// <summary>The backups folder inside a project. The "01_" keeps it at the top of the folder.</summary>
    public const string FolderName = "01_backup";

    /// <summary>The folder earlier versions kept backups in - still read, never written.</summary>
    public const string LegacyFolderName = "backups";

    private const string StampFormat = "yyyyMMdd_HHmmss";

    // The backups folder is inside the site, so IIS would hand out its .zip / .bacpac files: a web.config of its
    // own makes request filtering refuse every file there (404.7). Request filtering is a required IIS feature.
    private const string GuardWebConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- Written by DNN Manager: this folder holds backups of the site - never serve anything from it. -->
        <configuration>
          <system.webServer>
            <security>
              <requestFiltering>
                <fileExtensions allowUnlisted="false" />
              </requestFiltering>
            </security>
          </system.webServer>
        </configuration>

        """;

    /// <summary>
    /// Creates the project's backups folder, with the web.config that keeps IIS from serving what's in it.
    /// </summary>
    public static void EnsureFolder(DnnProject project)
    {
        Directory.CreateDirectory(project.BackupDirectory);
        var guard = Path.Combine(project.BackupDirectory, "web.config");
        if (!File.Exists(guard)) File.WriteAllText(guard, GuardWebConfig);
    }

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
            backups.Add(new ProjectBackup(project.Name, folder, created, zip, database));
        }
        return backups.OrderByDescending(b => b.Created).ToList();
    }
}
