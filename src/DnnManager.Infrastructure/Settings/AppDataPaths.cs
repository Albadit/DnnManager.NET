namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Where DNN Manager keeps the user's own files - settings, the SQL Server compose file, backups and
/// logs - apart from the installed program, so updating, reinstalling or uninstalling the app never
/// touches them:
/// <code>
/// Documents\DnnManager\
///   settings.json
///   docker-compose.yml
///   backups\
///     &lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\   a project backup: &lt;project&gt;.zip and / or &lt;project&gt;.bacpac
///     settings.*.json                          settings.json copies made before a migration or a reset
///   logs\        the activity log, one file per day
/// </code>
/// </summary>
public sealed class AppDataPaths
{
    public const string FolderName = "DnnManager";

    /// <summary>The folder's name in 2.1.0 before it was renamed - moved to <see cref="FolderName"/> on first start.</summary>
    public const string OldFolderName = "DNN Manager";

    public AppDataPaths(string root) => Root = root;

    /// <summary>The current Windows user's <c>Documents\DnnManager</c> (follows a Documents folder moved to OneDrive or elsewhere).</summary>
    public static AppDataPaths ForCurrentUser()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return new AppDataPaths(Path.Combine(documents, FolderName));
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ComposeFile => Path.Combine(Root, "docker-compose.yml");
    /// <summary>Project backups (one folder per project) and the settings.json copies.</summary>
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>The same folder under its old name (<c>Documents\DNN Manager</c>).</summary>
    public string OldRoot => Path.Combine(Path.GetDirectoryName(Root)!, OldFolderName);

    /// <summary>
    /// Where versions before 2.1 kept <c>appsettings.json</c> and <c>docker-compose.yml</c>: next to the exe.
    /// Read once, to carry them over when the Documents folder doesn't have its own yet.
    /// </summary>
    public static string LegacyDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// Renames <see cref="OldRoot"/> to <see cref="Root"/> when only the old one exists. Returns what was done,
    /// or null when there was nothing to move.
    /// </summary>
    public string? MoveFromOldName()
    {
        if (Directory.Exists(Root) || !Directory.Exists(OldRoot)) return null;
        Directory.Move(OldRoot, Root);
        return $"Moved {OldRoot} to {Root}.";
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
