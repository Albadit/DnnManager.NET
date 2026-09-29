namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Where DNN Manager keeps the user's own files - settings, the SQL Server compose file, settings
/// backups and logs - apart from the installed program, so updating, reinstalling or uninstalling the
/// app never touches them:
/// <code>
/// Documents\DNN Manager\
///   settings.json
///   docker-compose.yml
///   backups\     settings.json copies made before a migration or a reset
///   logs\        the activity log, one file per day
/// </code>
/// </summary>
public sealed class AppDataPaths
{
    public const string FolderName = "DNN Manager";

    public AppDataPaths(string root) => Root = root;

    /// <summary>The current Windows user's <c>Documents\DNN Manager</c> (follows a Documents folder moved to OneDrive or elsewhere).</summary>
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
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Where versions before 2.1 kept <c>appsettings.json</c> and <c>docker-compose.yml</c>: next to the exe.
    /// Read once, to carry them over when the Documents folder doesn't have its own yet.
    /// </summary>
    public static string LegacyDirectory => AppContext.BaseDirectory;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
