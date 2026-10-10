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
    public const string FolderName = DnnManager.Application.Configuration.SettingRules.AppDataFolderName;

    /// <summary>The folder's name in 1.2.0 before it was renamed - moved to <see cref="FolderName"/> on first start.</summary>
    public const string OldFolderName = "DNN Manager";

    /// <summary>
    /// The current Windows user's <c>Documents\DnnManager</c> (follows a Documents folder moved to OneDrive or elsewhere -
    /// in OneDrive or on a network share, the start says so: <see cref="SyncNotice()"/>).
    /// </summary>
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

    /// <summary>
    /// The OneDrive folder <see cref="Root"/> is in - Documents backed up to OneDrive (Windows' "folder backup" moves it
    /// there) - or null when it isn't in one. Everything in it is then uploaded to Microsoft's cloud.
    /// </summary>
    public string? OneDriveFolder() => OneDriveFolderOf(Root, OneDriveRoots());

    /// <summary>
    /// The folder of <paramref name="oneDriveRoots"/> that <paramref name="path"/> is in or is, or null. A root that is
    /// empty or not a full path is skipped.
    /// </summary>
    public static string? OneDriveFolderOf(string path, IEnumerable<string?> oneDriveRoots)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        foreach (var root in oneDriveRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) continue;
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    /// <summary>
    /// Where OneDrive keeps its folders for this user: the <c>OneDrive</c>, <c>OneDriveCommercial</c> and
    /// <c>OneDriveConsumer</c> environment variables, and the folder of each account OneDrive has set up (its
    /// <c>UserFolder</c> under <c>HKCU\Software\Microsoft\OneDrive\Accounts</c>) - the variables can be missing in an
    /// elevated process started another way.
    /// </summary>
    public static IEnumerable<string?> OneDriveRoots()
    {
        foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            yield return Environment.GetEnvironmentVariable(name);
        List<string> accounts = [];
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var account in key?.GetSubKeyNames() ?? [])
            {
                using var sub = key!.OpenSubKey(account);
                if (sub?.GetValue("UserFolder") is string folder) accounts.Add(folder);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not readable: the environment variables are what there is.
        }
        foreach (var folder in accounts) yield return folder;
    }

    /// <summary>
    /// When <see cref="Root"/> is in OneDrive - or on a network share (Documents redirected there) - what that means and
    /// how to keep the data on this PC: a warning for the start; null otherwise. Nothing is moved.
    /// </summary>
    public string? SyncNotice() => SyncNotice(Root, OneDriveFolder());

    /// <summary><see cref="SyncNotice()"/> for <paramref name="root"/>, in <paramref name="oneDrive"/> (null: not in OneDrive).</summary>
    public static string? SyncNotice(string root, string? oneDrive)
    {
        const string what = "project backups and deployment packages (whole databases, and a live server's connection string " +
                            "when you gave one), a clone's copy of its source database, the logs and DNN Manager's settings database";
        if (oneDrive is not null)
            return $"DNN Manager's data folder {root} is in OneDrive ({oneDrive}) - your Documents folder is backed up there - so " +
                   $"OneDrive uploads it to Microsoft's cloud: {what}. To keep them on this PC only, stop backing up Documents in " +
                   "OneDrive (OneDrive → Settings → Sync and back up → Manage back up), or delete what you don't need in " +
                   "Troubleshoot → Clean up data. Settings → Projects → Backups can delete old backups by themselves.";
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return $"DNN Manager's data folder {root} is on a network share - your Documents folder is redirected there - so {what} " +
                   "are kept on that server, where whoever can read the share can read them. Delete what you don't need in " +
                   "Troubleshoot → Clean up data; Settings → Projects → Backups can delete old backups by themselves.";
        return null;
    }
}
