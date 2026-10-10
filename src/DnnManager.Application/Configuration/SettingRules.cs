namespace DnnManager.Application.Configuration;

/// <summary>
/// The rules for settings that DNN Manager - running with administrator rights - turns into folders it deletes in or
/// changes the permissions of, addresses it downloads from, or SQL. The same for the saved settings
/// (<see cref="UserSettings.Validate"/>) and for <c>DNNMANAGER_*</c> environment variables (<see cref="AppOptions.Problems"/>):
/// both can be changed by any program of the user, without administrator rights.
/// </summary>
public static class SettingRules
{
    /// <summary>
    /// Why <paramref name="folder"/> can't be the projects folder, or null when it can. DNN Manager keeps that folder to
    /// administrators and the user, and deletes a removed project's folder in it: never a drive, Windows, Program Files,
    /// ProgramData, the user folder itself or another user's - nor anything in those, where the folder of a program or
    /// a service is. Nor Windows' own folders at the top of a drive (the recycle bin, System Volume Information),
    /// AppData\Local\Programs (programs installed for the user only) or DNN Manager's own data folder (Documents\DnnManager).
    /// </summary>
    public static string? ProjectsFolderProblem(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return "must be a full path, e.g. C:\\DNN.";
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "must be a full path, e.g. C:\\DNN.";
        }
        const string useOwn = " - use a folder of its own, e.g. C:\\DNN.";
        if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(full, StringComparison.OrdinalIgnoreCase))
            return "can't be a drive" + useOwn;
        // A share or a device path: the folders below can't be told apart there.
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "must be a folder on this computer, e.g. C:\\DNN.";

        foreach (var system in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData
                 })
            if (SafePath.IsSameOrInside(full, Environment.GetFolderPath(system)))
                return "can't be a system folder or a folder in one (Windows, Program Files, ProgramData)" + useOwn;

        // The folders Windows keeps at the top of every drive for itself: the recycle bin, restore points, recovery.
        var firstFolder = full[(Path.GetPathRoot(full)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar, 2)[0];
        if (WindowsDriveFolders.Contains(firstFolder, StringComparer.OrdinalIgnoreCase))
            return $"can't be in {firstFolder}, a folder Windows keeps for itself" + useOwn;

        // DNN Manager's own data (Documents\DnnManager) - its database, backups and logs aren't projects.
        foreach (var data in AppDataRoots())
            if (SafePath.IsSameOrInside(full, data))
                return $"can't be DNN Manager's own data folder ({data}) or a folder in it" + useOwn;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
        {
            if (Path.TrimEndingDirectorySeparator(profile).Equals(full, StringComparison.OrdinalIgnoreCase))
                return "can't be your user folder itself" + useOwn;
            // The programs installed for this user only: AppData\Local\Programs (DNN Manager's own user install too) and
            // the Store's app links.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0 &&
                (SafePath.IsSameOrInside(full, Path.Combine(local, "Programs")) || SafePath.IsSameOrInside(full, Path.Combine(local, "Microsoft", "WindowsApps"))))
                return "can't be a folder of programs installed for you (AppData\\Local\\Programs) or a folder in one" + useOwn;
            // C:\Users: only in this user's own folder - not Public, Default or someone else's.
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile)) is { Length: > 0 } users)
            {
                if (Path.TrimEndingDirectorySeparator(users).Equals(full, StringComparison.OrdinalIgnoreCase))
                    return $"can't be {users} itself, the folder of every account" + useOwn;
                if (SafePath.IsSameOrInside(full, users) && !SafePath.IsSameOrInside(full, profile))
                    return $"can't be in another account's folder in {users} (another user's, Public or Default) - only in yours" + useOwn;
            }
        }
        return null;
    }

    /// <summary>The name of DNN Manager's data folder in Documents (Infrastructure's AppDataPaths uses it too).</summary>
    public const string AppDataFolderName = "DnnManager";

    // Folders at the top of a drive that are Windows' own.
    private static readonly string[] WindowsDriveFolders =
        ["$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi", "$WinREAgent", "$WINDOWS.~BT", "$Windows.~WS", "$SysReset"];

    /// <summary>Where DNN Manager's data is: Documents\DnnManager - where Documents is now, and where it is by default.</summary>
    private static IEnumerable<string> AppDataRoots()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0) yield return Path.Combine(documents, AppDataFolderName);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) yield return Path.Combine(profile, "Documents", AppDataFolderName);
    }

    /// <summary>
    /// A DNN release source: https, or http only to this computer (a mirror on localhost) - over plain http anyone on
    /// the way could change the package that becomes the site.
    /// </summary>
    public static bool IsReleaseSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>A collation name - it goes into CREATE DATABASE … COLLATE as it is: only letters, digits and _.</summary>
    public static bool IsCollation(string? collation) =>
        !string.IsNullOrWhiteSpace(collation) && collation.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// A hostname suffix - it goes into host names, IIS bindings, the hosts file and SQL: labels of RFC 1123 (letters,
    /// digits and hyphens, 1 to 63 of them, neither starting nor ending with a hyphen) between dots, at most 200
    /// characters (a project's name goes in front), and not an IP address - the last label isn't digits only. Leading
    /// and trailing spaces and dots are ignored: the app uses it without them.
    /// </summary>
    public static bool IsHostnameSuffix(string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix) || suffix.Trim().Trim('.') is not { Length: > 0 and <= 200 } s) return false;
        var labels = s.Split('.');
        return labels.All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
                                   label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) &&
               !labels[^1].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// A Docker container or volume name, as Docker allows them: a letter or digit, then letters, digits, '_', '.' and
    /// '-' - it goes to docker on the command line and into docker-compose.yml.
    /// </summary>
    public static bool IsDockerName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>
    /// A Windows feature name as dism and Enable-WindowsOptionalFeature know them: ASCII letters, digits, '-', '_' and
    /// '.' only - it goes to PowerShell, run with administrator rights.
    /// </summary>
    public static bool IsIisFeatureName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
