using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// <c>%ProgramData%\DnnManager\temp</c> - DNN Manager's own temporary folder, which only Administrators and SYSTEM may
/// change. DNN Manager runs elevated; what it writes and later reads back or runs (an update, a database export, a task
/// definition) must not be somewhere every program of the signed-in user can swap it - %TEMP% is such a place. A folder
/// of that name that someone else made first, or that is a link elsewhere, isn't used: a new one is made beside it.
/// Not elevated (the tests), there is no such boundary to keep, and the user's %TEMP% is used.
/// </summary>
public sealed class PrivateTemp : IPrivateTemp
{
    private static readonly Lazy<string> Shared = new(Create);

    /// <summary>The folder, made (or checked) on first use.</summary>
    public static string Path => Shared.Value;

    private static readonly Lazy<string> SharedTools = new(() =>
    {
        // Beside the temporary folder, in the same folder only administrators can change - what was made there is theirs.
        var parent = System.IO.Path.GetDirectoryName(Shared.Value)!;
        if (!IsElevated()) return Directory.CreateDirectory(System.IO.Path.Combine(parent, "DnnManager-tools")).FullName;
        return Secure(System.IO.Path.Combine(parent, "tools")) ?? Secure(System.IO.Path.Combine(parent, "tools-" + Guid.NewGuid().ToString("N")[..8]))
               ?? throw new UnauthorizedAccessException($"Could not make a folder only administrators can change under {parent}.");
    });

    /// <summary>
    /// Where DNN Manager installs the programs it runs as Administrator (SqlPackage): only administrators can change
    /// them there - unlike a .NET tool in the user's own profile.
    /// </summary>
    public static string ToolsPath => SharedTools.Value;

    public string Folder => Shared.Value;

    private static string Create()
    {
        if (!IsElevated()) return Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DnnManager")).FullName;

        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DnnManager");
        var parent = Secure(root) ?? Secure(root + "-" + Guid.NewGuid().ToString("N")[..8])
                     ?? throw new UnauthorizedAccessException($"Could not make a folder only administrators can change under {root}.");
        return Secure(System.IO.Path.Combine(parent, "temp")) ?? Secure(System.IO.Path.Combine(parent, "temp-" + Guid.NewGuid().ToString("N")[..8]))
               ?? throw new UnauthorizedAccessException($"Could not make a folder only administrators can change under {parent}.");
    }

    /// <summary>
    /// <paramref name="path"/>, made with - or set to - Administrators and SYSTEM only; null when it is there already and
    /// isn't theirs (or is a link), so it can't be trusted.
    /// </summary>
    private static string? Secure(string path)
    {
        var dir = new DirectoryInfo(path);
        try
        {
            if (dir.Exists)
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
                var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is null || !IsTrusted(owner)) return null;
                dir.SetAccessControl(Rules());
                return dir.FullName;
            }
            dir.Create(Rules());
            return dir.FullName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return null;
        }
    }

    private static DirectorySecurity Rules()
    {
        var security = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(admins);
        // Nothing inherited from ProgramData, where every user may create files.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool IsTrusted(SecurityIdentifier owner) =>
        owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        // TrustedInstaller
        owner.Value == "S-1-5-80-956008885-3425870976-2436764453-2556521931-1409716564";

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
