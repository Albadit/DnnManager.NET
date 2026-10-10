using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>DNN Manager runs with administrator rights - what it starts runs with them too.</summary>
    public static bool IsElevated => Elevated.Value;

    // Who may change a program DNN Manager runs elevated: SYSTEM, Administrators, TrustedInstaller - and "the owner of
    // what is created here" (CREATOR OWNER), whose rights go to the owner, which is checked on its own.
    private static readonly HashSet<string> TrustedSids =
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

    /// <summary>Whether only administrators can change a program - or that couldn't be found out (a read failed).</summary>
    public enum Trust { AdminOnly, OthersCanChange, Unknown }

    /// <summary>
    /// A program DNN Manager may start with its rights: <see cref="Path"/>, the file's own path with every link, junction
    /// and short name resolved (the one checked is the one started) - or null, with <see cref="Refused"/> naming a copy
    /// that was found but not trusted and <see cref="Reason"/> saying why, for a message.
    /// </summary>
    public sealed record Resolved(string? Path, string? Refused, string? Reason)
    {
        /// <summary>Why it isn't started, as a sentence for the user - also when it isn't there at all.</summary>
        public string Problem(string name) => Refused is null
            ? $"{name} isn't installed (or not on the computer's PATH)."
            : $"DNN Manager doesn't start {Refused} as Administrator: {Reason} Install it for all users (in Program Files) - DNN Manager uses that copy.";
    }

    /// <summary>
    /// The program <paramref name="name"/> (<c>docker</c>, <c>dotnet</c>, <c>winget</c>, or a full path) as it may be
    /// started with DNN Manager's rights: a full path is checked as it is, a bare name looked up on the computer's PATH
    /// (not the user's, which any program of theirs can change) - its first copy that only administrators can change.
    /// What comes back is the file's own path, with no link or junction in it. Not elevated, there is no boundary: the
    /// name itself (any program goes).
    /// </summary>
    public static Resolved Resolve(string name)
    {
        if (!Elevated.Value) return new Resolved(name, null, null);
        if (System.IO.Path.IsPathRooted(name)) return Check(name);
        if (name.Equals("winget", StringComparison.OrdinalIgnoreCase) || name.Equals("winget.exe", StringComparison.OrdinalIgnoreCase))
            return Winget();

        Resolved? refused = null;
        foreach (var candidate in OnMachinePath(name))
        {
            var checkedOne = Check(candidate);
            if (checkedOne.Path is not null) return checkedOne;
            refused ??= checkedOne;
        }
        return refused ?? new Resolved(null, null, null);
    }

    /// <summary>
    /// Where <paramref name="name"/> is on the computer's PATH (with PATHEXT's extensions when it has none) - every copy,
    /// in PATH's order, whether it may be started or not. For a program that runs as the user (an unelevated terminal)
    /// the first is the one; for one started as Administrator, <see cref="Resolve"/>.
    /// </summary>
    public static IEnumerable<string> OnMachinePath(string name)
    {
        var extensions = System.IO.Path.HasExtension(name) ? [""] : ChildEnvironment.MachinePathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in ChildEnvironment.MachinePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var extension in extensions)
        {
            string candidate;
            try { candidate = System.IO.Path.Combine(folder, name + extension); }
            catch (ArgumentException) { continue; }
            if (System.IO.Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>
    /// The program <paramref name="name"/> as it may be started - see <see cref="Resolve"/>. Null when there is none;
    /// <paramref name="refused"/> then names a copy that was found but could be changed by others.
    /// </summary>
    public static string? Find(string name, out string? refused)
    {
        var resolved = Resolve(name);
        refused = resolved.Refused;
        return resolved.Path;
    }

    /// <summary>Whether DNN Manager may start <paramref name="file"/> with its rights - <see cref="Resolve"/> finds it.</summary>
    public static bool MayRun(string file) => Resolve(file).Path is not null;

    /// <summary><paramref name="file"/> checked: its own path when only administrators can change it.</summary>
    private static Resolved Check(string file)
    {
        if (FinalPath(file) is not { } final) return new Resolved(null, file, "it can't be opened to check it.");
        return AdminOnly(final) switch
        {
            Trust.AdminOnly => new Resolved(final, null, null),
            Trust.Unknown => new Resolved(null, file, "who may change it couldn't be read - try again in a moment."),
            _ => new Resolved(null, file, "programs without administrator rights could change it, and DNN Manager would run it as Administrator.")
        };
    }
    /// <summary>
    /// The path Windows opens for <paramref name="file"/> - every link, junction and short name on the way resolved -
    /// or null when it can't be opened.
    /// </summary>
    public static string? FinalPath(string file)
    {
        try
        {
            using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
            var path = buffer.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return null;
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    /// <summary>
    /// Whether only administrators (and Windows) can change <paramref name="file"/> - false also when that couldn't be
    /// read (<see cref="AdminOnly(string)"/> tells the two apart).
    /// </summary>
    public static bool IsAdminOnly(string file) => AdminOnly(file) == Trust.AdminOnly;

    /// <summary>
    /// Whether only administrators (and Windows) can change <paramref name="file"/>: the file itself, its folder (a file
    /// put beside it, a DLL it loads), and every folder above - none a link or junction, none that others may delete,
    /// rename or take over (a folder on the way swapped for another). <see cref="Trust.Unknown"/> when a read failed for a
    /// reason that may pass (a file in use) - not to be taken as "others can change it" for good.
    /// </summary>
    public static Trust AdminOnly(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Trust.OthersCanChange;
            if (!AdminOnly(info.GetAccessControl(), Changes)) return Trust.OthersCanChange;
            var folder = info.Directory!;
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Trust.OthersCanChange;
            // Each folder's rules are read once: the folder above one level is the folder of the next.
            FileSystemSecurity aboveRules = folder.GetAccessControl();
            if (!AdminOnly(aboveRules, Changes)) return Trust.OthersCanChange;
            // The folders above may let others make folders (C:\ does) - not delete, rename or take over one on the way.
            for (var above = folder; above.Parent is { } parent; above = parent)
            {
                if (parent.Parent is not null && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Trust.OthersCanChange;
                FileSystemSecurity parentRules = parent.GetAccessControl();
                if (!AdminOnly(aboveRules, FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                        checkOwner: false, skipInheritOnly: true) ||
                    !AdminOnly(parentRules, FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership,
                        checkOwner: false, skipInheritOnly: true))
                    return Trust.OthersCanChange;
                aboveRules = parentRules;
            }
            return Trust.AdminOnly;
        }
        catch (UnauthorizedAccessException)
        {
            // Not even an administrator may read who can change it: not one to trust.
            return Trust.OthersCanChange;
        }
        catch (IOException)
        {
            // In use, a network hiccup: unknown, not "others can change it".
            return Trust.Unknown;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Trust.OthersCanChange;
        }
    }
    private static bool AdminOnly(FileSystemSecurity security, FileSystemRights rights, bool checkOwner = true, bool skipInheritOnly = false)
    {
        if (checkOwner && security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !TrustedSids.Contains(owner.Value)) return false;
        // An inherit-only entry is for what is made inside the folder, not for the folder itself.
        return !security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & rights) != 0 &&
                      !(skipInheritOnly && r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) &&
                      !TrustedSids.Contains(r.IdentityReference.Value));
    }

    // winget is the App Installer package's: in the user's WindowsApps it is a link anyone of theirs could change; in
    // Program Files\WindowsApps it is TrustedInstaller's.
    private static Resolved Winget()
    {
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var found = Directory.EnumerateDirectories(apps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe")
                .Concat(Directory.EnumerateDirectories(apps, "Microsoft.DesktopAppInstaller_*_neutral__8wekyb3d8bbwe"))
                .Select(d => Path.Combine(d, "winget.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(Check)
                .FirstOrDefault(r => r.Path is not null);
            if (found is not null) return found;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* not readable - as not found */ }
        return new Resolved(null, "winget (the App Installer)", "no copy of it only administrators can change was found in Program Files\\WindowsApps.");
    }
}
