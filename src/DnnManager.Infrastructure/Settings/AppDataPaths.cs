namespace DnnManager.Infrastructure.Settings;

/// <summary>
/// Where DNN Manager keeps the user's own data - its database, backups, logs, kept DNN packages - apart from the
/// installed program, so updating, reinstalling or uninstalling the app never touches them:
/// <code>
/// Documents\DnnManager\
///   dnnmanager.db   the settings (and their copies), the workspace, the project records, keep warm, saved DNN versions (AppDatabase)
///   backups\     project backups: &lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\ with &lt;project&gt;.zip and / or &lt;project&gt;.bacpac
///   deployments\ packages made by Export for deployment: &lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\ with .zip, .bacpac, DEPLOY.txt
///   logs\        the activity log, one file per day
///   packages\    downloaded DNN install packages, when they are kept for reuse
/// </code>
/// Every folder is there from the start (<see cref="EnsureCreated"/>), empty until something goes in it.
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
    /// <summary>Project backups, one folder per project.</summary>
    public string BackupsDirectory => Path.Combine(Root, "backups");
    public string LogsDirectory => Path.Combine(Root, "logs");
    /// <summary>Packages made by Export for deployment, one dated folder each - unless another folder was chosen.</summary>
    public string DeploymentsDirectory => Path.Combine(Root, "deployments");
    /// <summary>Downloaded DNN install packages kept for reuse (setting <c>projects.keepDnnPackages</c>), one folder per repository.</summary>
    public string PackagesDirectory => Path.Combine(Root, "packages");

    /// <summary>The same folder under its old name (<c>Documents\DNN Manager</c>).</summary>
    public string OldRoot => Path.Combine(Path.GetDirectoryName(Root)!, OldFolderName);

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

    /// <summary>
    /// Makes every folder of <see cref="Root"/> that isn't there yet - at start, so each can be opened (Settings → About)
    /// before anything has gone in it, and nothing has to make one first.
    /// </summary>
    public void EnsureCreated()
    {
        foreach (var folder in new[] { Root, BackupsDirectory, DeploymentsDirectory, LogsDirectory, PackagesDirectory })
            Directory.CreateDirectory(folder);
    }
}
