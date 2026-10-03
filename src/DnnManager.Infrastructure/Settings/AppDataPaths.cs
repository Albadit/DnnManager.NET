namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Where DNN Manager keeps the user's own files - settings, backups, logs and kept DNN packages - apart from the
/// installed program, so updating, reinstalling or uninstalling the app never touches them:
/// <code>
/// Documents\DnnManager\
///   settings.json
///   backups\
///     &lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\   a project backup: &lt;project&gt;.zip and / or &lt;project&gt;.bacpac
///     settings.*.json                          settings.json copies made before a migration or a reset
///   logs\        the activity log, one file per day
///   packages\    downloaded DNN install packages, when they are kept for reuse
///   projects\    what DNN Manager remembers about the projects it set up (how DNN was installed), one file each
///     keep-warm\  the sites kept warm, and their own keep-warm values - one file each
/// </code>
/// </summary>
public sealed class AppDataPaths(string root)
{
    public const string FolderName = "DnnManager";

    /// <summary>The folder's name in 1.2.0 before it was renamed - moved to <see cref="FolderName"/> on first start.</summary>
    public const string OldFolderName = "DNN Manager";

    /// <summary>The current Windows user's <c>Documents\DnnManager</c> (follows a Documents folder moved to OneDrive or elsewhere).</summary>
    public static AppDataPaths ForCurrentUser()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return new AppDataPaths(Path.Combine(documents, FolderName));
    }

    public string Root { get; } = root;
    public string SettingsFile => Path.Combine(Root, "settings.json");
    /// <summary>Project backups (one folder per project) and the settings.json copies.</summary>
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string LogsDirectory => Path.Combine(Root, "logs");
    /// <summary>Downloaded DNN install packages kept for reuse (setting <c>projects.keepDnnPackages</c>), one folder per repository.</summary>
    public string PackagesDirectory => Path.Combine(Root, "packages");
    /// <summary>One <c>&lt;site&gt;.json</c> per project DNN Manager set up: how DNN was installed, when.</summary>
    public string ProjectRecordsDirectory => Path.Combine(Root, "projects");
    /// <summary>One <c>&lt;site&gt;.json</c> per site switched to "keep warm" (or with keep-warm values of its own).</summary>
    public string KeepWarmDirectory => Path.Combine(ProjectRecordsDirectory, "keep-warm");
    /// <summary>
    /// What is kept between starts besides the settings - where the user was, the window, unsaved form values - one file
    /// per area (<c>StateStore</c>).
    /// </summary>
    public string StateDirectory => Path.Combine(Root, "state");

    /// <summary>The same folder under its old name (<c>Documents\DNN Manager</c>).</summary>
    public string OldRoot => Path.Combine(Path.GetDirectoryName(Root)!, OldFolderName);

    /// <summary>
    /// Where versions before 1.2 kept <c>appsettings.json</c>: next to the exe.
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
