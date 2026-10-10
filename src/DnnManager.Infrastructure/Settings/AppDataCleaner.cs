namespace DnnManager.Infrastructure.Settings;

/// <summary>What Troubleshoot → Clean up data can delete from <c>Documents\DnnManager</c>.</summary>
public enum AppDataKind
{
    /// <summary>The activity log files (<c>logs\</c>).</summary>
    Logs,

    /// <summary>Downloaded DNN install packages kept for reuse (<c>packages\</c>), and the DNN versions saved with them (<c>dnn_releases</c>).</summary>
    DnnPackages,


    /// <summary>Project backups - each project's site .zip and database .bacpac (<c>backups\&lt;project&gt;\</c>).</summary>
    ProjectBackups,

    /// <summary>
    /// Packages made for a live server (<c>deployments\</c>): each a whole database and a web.config that may hold the
    /// live server's connection string - nothing anyone needs once deployed.
    /// </summary>
    Deployments,

    /// <summary>Which sites are kept warm (the database's <c>keep_warm</c>) - for a factory reset.</summary>
    KeepWarmChoices,

    /// <summary>
    /// What older versions left in <c>Documents\DnnManager</c> and this one doesn't read: the settings files of 1.7.1 and
    /// earlier (<c>settings.json</c>, <c>appsettings.json</c>, <c>docker-compose.yml</c> - they can hold the sa password in
    /// plain text - and the workspace in <c>state\</c>), and damaged databases put aside (<c>dnnmanager.damaged-*.db</c>).
    /// </summary>
    OldSettingsFiles
}

/// <summary>What <see cref="AppDataCleaner.Clean"/> did: what it freed, and how many files it couldn't delete (in use, or not allowed).</summary>
public sealed record CleanupResult(long FreedBytes, int Skipped);

/// <summary>What <see cref="AppDataCleaner.DeleteExpired"/> deleted - each backup or package folder - what it freed, and how many files it couldn't delete.</summary>
public sealed record ExpiredResult(IReadOnlyList<string> Deleted, long FreedBytes, int Skipped);

/// <summary>
/// Measures and deletes DNN Manager's own data in <c>Documents\DnnManager</c> (<see cref="AppDataPaths"/>) - never a
/// project's folder, IIS site or database. A file that can't be deleted (in use, or not allowed) is skipped and counted.
/// </summary>
public sealed class AppDataCleaner(AppDataPaths paths)
{
    private readonly AppDataPaths _paths = paths;

    private readonly Data.AppDatabase _database = new(paths);

    /// <summary>How much <paramref name="kind"/> takes now, in bytes - its files, and what it has in the database.</summary>
    public long Measure(AppDataKind kind)
    {
        var bytes = Files(kind).Sum(f => Length(f));
        if (Table(kind) is { } table)
        {
            try
            {
                using var connection = _database.Open();
                bytes += Data.AppDatabase.Scalar<long?>(connection, $"SELECT SUM({table.Size}) FROM {table.Name}") ?? 0;
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                // Not measured - shown as what the files take.
            }
        }
        return bytes;
    }

    /// <summary>Deletes <paramref name="kind"/>, then the folders it leaves empty.</summary>
    public CleanupResult Clean(AppDataKind kind)
    {
        var (freed, skipped) = Delete(Files(kind).ToList());
        foreach (var folder in EmptiedFolders(kind)) DeleteEmptyFolders(folder);
        if (Table(kind) is { } table)
        {
            try
            {
                using var connection = _database.Open();
                freed += Data.AppDatabase.Scalar<long?>(connection, $"SELECT SUM({table.Size}) FROM {table.Name}") ?? 0;
                Data.AppDatabase.Execute(connection, $"DELETE FROM {table.Name}");
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return new CleanupResult(freed, skipped);
    }

    /// <summary>
    /// Deletes the project backups (<c>backups\&lt;project&gt;\&lt;project&gt;_&lt;date&gt;\</c>) and deployment packages
    /// (<c>deployments\&lt;project&gt;_&lt;date&gt;\</c>) made more than <paramref name="keepDays"/> days before
    /// <paramref name="now"/> - setting <c>backups.keepDays</c>; nothing when it is 0 or less. A folder's age is the date in
    /// its name, or when it was last written to. Never through a junction or link.
    /// </summary>
    public ExpiredResult DeleteExpired(int keepDays, DateTime now)
    {
        if (keepDays <= 0) return new ExpiredResult([], 0, 0);
        var before = now - TimeSpan.FromDays(keepDays);
        var expired = new List<string>();
        if (Usable(_paths.BackupsDirectory))
            foreach (var project in SafeFolders(_paths.BackupsDirectory))
                expired.AddRange(SafeFolders(project).Where(f => MadeAt(f) < before));
        if (Usable(_paths.DeploymentsDirectory))
            expired.AddRange(SafeFolders(_paths.DeploymentsDirectory).Where(f => MadeAt(f) < before));

        long freed = 0;
        var skipped = 0;
        var deleted = new List<string>();
        foreach (var folder in expired)
        {
            var (bytes, left) = Delete(AllFiles(folder).ToList());
            freed += bytes;
            skipped += left;
            DeleteEmptyFolders(folder);
            if (left == 0) deleted.Add(folder);
        }
        // A project with no backup left keeps no empty folder.
        if (Usable(_paths.BackupsDirectory))
            foreach (var project in SafeFolders(_paths.BackupsDirectory)) DeleteEmptyFolders(project);
        return new ExpiredResult(deleted, freed, skipped);
    }

    /// <summary>The folders in <paramref name="folder"/>, but junctions and links; none when it can't be read.</summary>
    private static List<string> SafeFolders(string folder)
    {
        try { return Directory.GetDirectories(folder).Where(d => !IsLink(d)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>When a backup or package folder was made: the date in its name (<c>&lt;project&gt;_yyyyMMdd_HHmmss</c>), or when it was last written to.</summary>
    internal static DateTime MadeAt(string folder)
    {
        const string stamp = "yyyyMMdd_HHmmss";
        var name = Path.GetFileName(folder);
        if (name.Length > stamp.Length &&
            DateTime.TryParseExact(name[^stamp.Length..], stamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var made))
            return made;
        try { return Directory.GetLastWriteTime(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

    /// <summary>Deletes <paramref name="files"/>: what that freed, and how many couldn't be deleted (in use, or not allowed).</summary>
    private static (long Freed, int Skipped) Delete(IEnumerable<string> files)
    {
        long freed = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            var length = Length(file);
            try
            {
                File.SetAttributes(file, FileAttributes.Normal); // a read-only file would refuse
                File.Delete(file);
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return (freed, skipped);
    }

    /// <summary>The names in <c>Documents\DnnManager</c> of the settings files of 1.7.1 and earlier.</summary>
    private static readonly string[] OldSettingsFileNames = ["settings.json", "settings.json.bak", "appsettings.json", "docker-compose.yml"];

    /// <summary>
    /// What <see cref="AppDataKind.OldSettingsFiles"/> is: the old settings files and damaged databases at the top of
    /// <c>Documents\DnnManager</c>, and the old workspace's <c>.json</c> files in <c>state\</c> - files only, never a
    /// link, never through one.
    /// </summary>
    private IEnumerable<string> OldSettingsFiles()
    {
        if (!Usable(_paths.Root)) return [];
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_paths.Root, "*", SafePathTopLevel))
            {
                var name = Path.GetFileName(file);
                if (OldSettingsFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    (name.StartsWith("dnnmanager.damaged-", StringComparison.OrdinalIgnoreCase) &&
                     (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".db-journal", StringComparison.OrdinalIgnoreCase))))
                    files.Add(file);
            }
            var state = Path.Combine(_paths.Root, "state");
            if (Usable(state)) files.AddRange(Directory.EnumerateFiles(state, "*.json", SafePathTopLevel));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What was found so far.
        }
        return files;
    }

    private static EnumerationOptions SafePathTopLevel => new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private IEnumerable<string> Files(AppDataKind kind) => kind switch
    {
        AppDataKind.Logs => AllFiles(_paths.LogsDirectory),
        AppDataKind.DnnPackages => AllFiles(_paths.PackagesDirectory),
        AppDataKind.ProjectBackups => Usable(_paths.BackupsDirectory)
            ? Directory.EnumerateDirectories(_paths.BackupsDirectory).Where(d => !IsLink(d)).SelectMany(AllFiles)
            : [],
        AppDataKind.Deployments => Usable(_paths.DeploymentsDirectory)
            ? Directory.EnumerateDirectories(_paths.DeploymentsDirectory).Where(d => !IsLink(d)).SelectMany(AllFiles)
            : [],
        AppDataKind.OldSettingsFiles => OldSettingsFiles(),
        _ => []
    };

    /// <summary>
    /// The table <paramref name="kind"/> has in the database - keep warm's, the DNN versions saved with the kept packages -
    /// and what a row of it takes (its text).
    /// </summary>
    private static (string Name, string Size)? Table(AppDataKind kind) => kind switch
    {
        AppDataKind.KeepWarmChoices => ("keep_warm", "length(site)"),
        AppDataKind.DnnPackages => ("dnn_releases", "length(api) + length(version) + length(tag) + length(url)"),

        _ => null
    };

    private IEnumerable<string> EmptiedFolders(AppDataKind kind) => kind switch
    {
        AppDataKind.DnnPackages when Usable(_paths.PackagesDirectory) => [_paths.PackagesDirectory],
        AppDataKind.ProjectBackups when Usable(_paths.BackupsDirectory) => Directory.GetDirectories(_paths.BackupsDirectory).Where(d => !IsLink(d)),
        AppDataKind.Deployments when Usable(_paths.DeploymentsDirectory) =>
            Directory.GetDirectories(_paths.DeploymentsDirectory).Where(d => !IsLink(d)),
        AppDataKind.OldSettingsFiles when Usable(Path.Combine(_paths.Root, "state")) => [Path.Combine(_paths.Root, "state")],
        _ => []
    };

    // Hidden and system files too - but never through a junction or link, the folder itself or one on the way from
    // Documents\DnnManager included: what it points to isn't DNN Manager's, and it deletes with administrator rights.
    private IEnumerable<string> AllFiles(string folder) =>
        Directory.Exists(folder) && !DnnManager.Application.SafePath.HasLink(_paths.Root, folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions
              {
                  RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
              })
            : [];

    /// <summary>There, and neither it nor a folder on the way from Documents\DnnManager a junction or link.</summary>
    private bool Usable(string folder) => Directory.Exists(folder) && !DnnManager.Application.SafePath.HasLink(_paths.Root, folder);

    /// <summary>A junction or link - not followed: what it points to is somewhere else, and not DNN Manager's to delete.</summary>
    private static bool IsLink(string folder)
    {
        try { return new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static long Length(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Removes the empty folders inside <paramref name="folder"/>, and it too when it is left empty.</summary>
    private static void DeleteEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var child in Directory.GetDirectories(folder).Where(d => !IsLink(d))) DeleteEmptyFolders(child);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (Explorer has it open) - an empty folder costs nothing.
        }
    }
}
