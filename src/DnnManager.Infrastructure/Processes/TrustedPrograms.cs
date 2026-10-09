using System.Security.AccessControl;
using System.Security.Principal;

namespace DnnManager.Infrastructure.Processes;

/// <summary>
/// Which programs DNN Manager - running as Administrator - may start with its rights: only one that nobody but
/// administrators can change. A program in a folder the signed-in user can write to (their PATH, %LOCALAPPDATA%, a
/// per-user install) could be swapped by any program they run, which would then run as Administrator. Not elevated
/// (the tests), there is no such boundary, and any program goes.
/// </summary>
public static class TrustedPrograms
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    // Who may change a program DNN Manager runs elevated: SYSTEM, Administrators, TrustedInstaller - and "the owner of
    // what is created here" (CREATOR OWNER), whose rights go to the owner, which is checked on its own.
    private static readonly HashSet<string> Trusted =
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null).Value,
        "S-1-5-80-956008885-3425870976-2436764453-2556521931-1409716564" // TrustedInstaller
    ];

    // Changing a file, or putting another in its place (or a DLL beside it).
    private const FileSystemRights Changes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
                                            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
                                            FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    /// <summary>
    /// The program <paramref name="name"/> (<c>docker</c>, <c>dotnet</c>, <c>winget</c>) as it may be started: a full
    /// path is checked as it is, a bare name looked up on PATH - its first copy that only administrators can change.
    /// Null when there is none; <paramref name="refused"/> then names a copy that was found but could be changed by others.
    /// </summary>
    public static string? Find(string name, out string? refused)
    {
        refused = null;
        if (!Elevated.Value) return name;
        if (Path.IsPathRooted(name))
        {
            if (IsAdminOnly(name)) return name;
            refused = name;
            return null;
        }
        if (name.Equals("winget", StringComparison.OrdinalIgnoreCase) || name.Equals("winget.exe", StringComparison.OrdinalIgnoreCase))
            return Winget(out refused);

        var extensions = Path.HasExtension(name) ? [""] :
            (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var extension in extensions)
        {
            string candidate;
            try { candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder), name + extension); }
            catch (ArgumentException) { continue; }
            if (!File.Exists(candidate)) continue;
            if (IsAdminOnly(candidate)) return candidate;
            refused ??= candidate;
        }
        return null;
    }

    /// <summary>Whether DNN Manager may start <paramref name="file"/> with its rights: not elevated, any; elevated, an admin-only one.</summary>
    public static bool MayRun(string file) => !Elevated.Value || IsAdminOnly(file);

    /// <summary>
    /// Whether only administrators (and Windows) can change <paramref name="file"/>: the file itself, its folder (a file
    /// put beside it, a DLL it loads) and the folder above (its folder swapped for another).
    /// </summary>
    public static bool IsAdminOnly(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (!AdminOnly(info.GetAccessControl(), Changes)) return false;
            var folder = info.Directory!;
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || !AdminOnly(folder.GetAccessControl(), Changes)) return false;
            // The folder above may let others make folders (C:\ does) - not delete or rename this one.
            return folder.Parent is not { } parent ||
                   AdminOnly(parent.GetAccessControl(), FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                       checkOwner: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool AdminOnly(FileSystemSecurity security, FileSystemRights rights, bool checkOwner = true)
    {
        if (checkOwner && security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !Trusted.Contains(owner.Value)) return false;
        return !security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & rights) != 0 &&
                      !Trusted.Contains(r.IdentityReference.Value));
    }

    // winget is the App Installer package's: in the user's WindowsApps it is a link anyone of theirs could change; in
    // Program Files\WindowsApps it is TrustedInstaller's.
    private static string? Winget(out string? refused)
    {
        refused = null;
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var found = Directory.EnumerateDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .Concat(Directory.EnumerateDirectories(apps, "Microsoft.DesktopAppInstaller_*_neutral__8wekyb3d8bbwe"))
                .Select(d => Path.Combine(d, "winget.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(IsAdminOnly);
            if (found is not null) return found;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* not readable - as not found */ }
        refused = "winget (the App Installer)";
        return null;
    }
}
