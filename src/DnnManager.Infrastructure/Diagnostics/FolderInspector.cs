using System.Security.AccessControl;
using System.Security.Principal;

namespace DnnManager.Infrastructure.Diagnostics;

/// <summary>
/// One of the folders DNN writes to or needs: whether it is there, read-only, and whether the site's Windows account
/// may write to it by the folder's permissions.
/// </summary>
/// <param name="Writable">True or false by the folder's ACL; null when the account isn't known or the ACL can't be read.</param>
/// <param name="Via">Which entry gives (or denies) the write: the account itself, IIS_IUSRS, Users…</param>
public sealed record FolderCheck(string Folder, bool Exists, bool ReadOnly, bool? Writable, string? Via, bool Required, bool NeedsWrite);

/// <summary>
/// Checks a DNN site's folders against the account its app pool runs as. The account's own entries count, and those of
/// the groups every app pool identity is in (IIS_IUSRS, Users, Authenticated Users, Everyone); a deny wins. That is what
/// the ACL grants - not a sign-in as the account.
/// </summary>
public static class FolderInspector
{
    /// <summary>DNN's folders: what it needs - and where it writes (uploads, logs, installs, the cache).</summary>
    private static readonly (string Folder, bool Required, bool NeedsWrite)[] DnnFolders =
    [
        (".", true, true),
        ("bin", true, true),
        ("App_Data", true, true),
        ("Portals", true, true),
        (@"Portals\_default", true, true),
        ("DesktopModules", true, true),
        ("Providers", true, false),
    ];

    private static readonly string[] AppPoolGroups = ["BUILTIN\\IIS_IUSRS", "BUILTIN\\Users", "NT AUTHORITY\\Authenticated Users", "Everyone"];

    public static IReadOnlyList<FolderCheck> Inspect(string root, string? account) =>
        DnnFolders.Select(f =>
        {
            var path = f.Folder == "." ? root : System.IO.Path.Combine(root, f.Folder);
            var name = f.Folder == "." ? "(site root)" : "/" + f.Folder.Replace('\\', '/');
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return new FolderCheck(name, false, false, null, null, f.Required, f.NeedsWrite);
            var readOnly = dir.Attributes.HasFlag(FileAttributes.ReadOnly);
            var (writable, via) = account is null ? (null, null) : CanWrite(dir, account);
            return new FolderCheck(name, true, readOnly, writable, via, f.Required, f.NeedsWrite);
        }).ToList();

    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData;

    /// <summary>Whether <paramref name="account"/> (or a group every app pool identity is in) may write to <paramref name="dir"/>.</summary>
    private static (bool? Writable, string? Via) CanWrite(DirectoryInfo dir, string account)
    {
        try
        {
            var who = new[] { account }.Concat(AppPoolGroups).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rules = dir.GetAccessControl().GetAccessRules(true, true, typeof(NTAccount)).OfType<FileSystemAccessRule>()
                .Where(r => who.Contains(r.IdentityReference.Value) && (r.FileSystemRights & WriteRights) != 0)
                .ToList();
            if (rules.FirstOrDefault(r => r.AccessControlType == AccessControlType.Deny) is { } deny)
                return (false, $"denied for {Short(deny.IdentityReference.Value)}");
            if (rules.FirstOrDefault(r => r.AccessControlType == AccessControlType.Allow) is { } allow)
                return (true, $"{Short(allow.IdentityReference.Value)} - {Rights(allow.FileSystemRights)}{(allow.IsInherited ? ", inherited" : "")}");
            return (false, "no entry gives it write access");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or IdentityNotMappedException or InvalidOperationException)
        {
            return (null, $"permissions can't be read: {ex.Message}");
        }
    }

    private static string Short(string identity) =>
        identity.StartsWith("BUILTIN\\", StringComparison.OrdinalIgnoreCase) ? identity[8..] : identity;

    private static string Rights(FileSystemRights rights) =>
        rights.HasFlag(FileSystemRights.FullControl) ? "Full control"
        : rights.HasFlag(FileSystemRights.Modify) ? "Modify"
        : "Write";
}
