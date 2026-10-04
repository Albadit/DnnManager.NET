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

    /// <summary>Which sites are kept warm (the database's <c>keep_warm</c>) - for a factory reset.</summary>
    KeepWarmChoices
}

/// <summary>What <see cref="AppDataCleaner.Clean"/> did: what it freed, and how many files it couldn't delete (in use, or not allowed).</summary>
public sealed record CleanupResult(long FreedBytes, int Skipped);

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
        long freed = 0;
        var skipped = 0;
        foreach (var file in Files(kind).ToList())
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

    private IEnumerable<string> Files(AppDataKind kind) => kind switch
    {
        AppDataKind.Logs => AllFiles(_paths.LogsDirectory),
        AppDataKind.DnnPackages => AllFiles(_paths.PackagesDirectory),
        AppDataKind.ProjectBackups => Directory.Exists(_paths.BackupsDirectory)
            ? Directory.EnumerateDirectories(_paths.BackupsDirectory).Where(d => !IsLink(d)).SelectMany(AllFiles)
            : [],
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
        AppDataKind.DnnPackages => [_paths.PackagesDirectory],
        AppDataKind.ProjectBackups when Directory.Exists(_paths.BackupsDirectory) => Directory.GetDirectories(_paths.BackupsDirectory).Where(d => !IsLink(d)),
        _ => []
    };

    // Hidden and system files too - but never through a junction or link: what it points to isn't DNN Manager's.
    private static IEnumerable<string> AllFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions
              {
                  RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
              })
            : [];

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
