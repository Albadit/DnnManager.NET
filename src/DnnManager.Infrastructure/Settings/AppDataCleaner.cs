namespace DnnManager.Infrastructure.Settings;

/// <summary>What Troubleshoot → Clean up data can delete from <c>Documents\DnnManager</c>.</summary>
public enum AppDataKind
{
    /// <summary>The activity log files (<c>logs\</c>).</summary>
    Logs,

    /// <summary>Downloaded DNN install packages kept for reuse (<c>packages\</c>).</summary>
    DnnPackages,

    /// <summary>The copies of settings.json made before an upgrade or a reset (<c>backups\settings.*.json</c>).</summary>
    SettingsCopies,

    /// <summary>Project backups - each project's site .zip and database .bacpac (<c>backups\&lt;project&gt;\</c>).</summary>
    ProjectBackups,

    /// <summary>Which sites are kept warm, and their own keep-warm values (<c>projects\keep-warm\</c>) - for a factory reset.</summary>
    KeepWarmChoices
}

/// <summary>What <see cref="AppDataCleaner.Clean"/> did: what it freed, and how many files it couldn't delete (in use, or not allowed).</summary>
public sealed record CleanupResult(long FreedBytes, int Skipped);

/// <summary>
/// Measures and deletes DNN Manager's own data in <c>Documents\DnnManager</c> (<see cref="AppDataPaths"/>) - never a
/// project's folder, IIS site or database. A file that can't be deleted (in use, or not allowed) is skipped and counted.
/// </summary>
public sealed class AppDataCleaner
{
    private readonly AppDataPaths _paths;

    public AppDataCleaner(AppDataPaths paths) => _paths = paths;

    /// <summary>How much <paramref name="kind"/> takes now, in bytes.</summary>
    public long Measure(AppDataKind kind) => Files(kind).Sum(f => Length(f));

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
        return new CleanupResult(freed, skipped);
    }

    private IEnumerable<string> Files(AppDataKind kind) => kind switch
    {
        AppDataKind.Logs => AllFiles(_paths.LogsDirectory),
        AppDataKind.DnnPackages => AllFiles(_paths.PackagesDirectory),
        AppDataKind.SettingsCopies => Directory.Exists(_paths.BackupsDirectory)
            ? Directory.EnumerateFiles(_paths.BackupsDirectory, "settings.*.json", SearchOption.TopDirectoryOnly)
            : [],
        // The projects' folders in backups\ - not the settings copies next to them.
        AppDataKind.ProjectBackups => Directory.Exists(_paths.BackupsDirectory)
            ? Directory.EnumerateDirectories(_paths.BackupsDirectory).SelectMany(AllFiles)
            : [],
        AppDataKind.KeepWarmChoices => Directory.Exists(_paths.KeepWarmDirectory)
            ? Directory.EnumerateFiles(_paths.KeepWarmDirectory, "*.json", SearchOption.TopDirectoryOnly)
            : [],
        _ => []
    };

    private IEnumerable<string> EmptiedFolders(AppDataKind kind) => kind switch
    {
        AppDataKind.DnnPackages => [_paths.PackagesDirectory],
        AppDataKind.KeepWarmChoices => [_paths.KeepWarmDirectory],
        AppDataKind.ProjectBackups when Directory.Exists(_paths.BackupsDirectory) => Directory.GetDirectories(_paths.BackupsDirectory),
        _ => []
    };

    private static IEnumerable<string> AllFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            : [];

    private static long Length(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Removes the empty folders inside <paramref name="folder"/>, and it too when it is left empty.</summary>
    private static void DeleteEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var child in Directory.GetDirectories(folder)) DeleteEmptyFolders(child);
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
