using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Abstractions;
using DnnManager.Application;

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
    // PublicationOnly: a failure (the folder couldn't be made just now) isn't kept for the rest of the session - the next
    // use tries again.
    private static readonly Lazy<string> Shared = new(Create, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>The folder, made (or checked) on first use.</summary>
    public static string Path => Shared.Value;

    private static readonly Lazy<string> SharedTools = new(() =>
    {
        // Beside the temporary folder, in the same folder only administrators can change - what was made there is theirs.
        var parent = System.IO.Path.GetDirectoryName(Shared.Value)!;
        if (!IsElevated()) return Directory.CreateDirectory(System.IO.Path.Combine(parent, "DnnManager-tools")).FullName;
        return SecureOrBeside(parent, "tools");
    }, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>
    /// Where DNN Manager installs the programs it runs as Administrator (SqlPackage): only administrators can change
    /// them there - unlike a .NET tool in the user's own profile.
    /// </summary>
    public static string ToolsPath => SharedTools.Value;

    private static readonly Lazy<string> SharedDockerConfig = new(() =>
    {
        var parent = System.IO.Path.GetDirectoryName(Shared.Value)!;
        if (!IsElevated()) return Directory.CreateDirectory(System.IO.Path.Combine(parent, "DnnManager-docker")).FullName;
        return SecureOrBeside(parent, "docker");
    }, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>
    /// docker's configuration folder (<c>DOCKER_CONFIG</c>) for the docker DNN Manager runs as Administrator: its own,
    /// which only administrators can change - not the user's <c>.docker</c>, whose context, plugins and credential
    /// helpers any program of theirs can set.
    /// </summary>
    public static string DockerConfigPath => SharedDockerConfig.Value;

    public string Folder => Shared.Value;

    /// <summary>What is left in the temporary folder longer than this is from a run that didn't clean up after itself.</summary>
    internal static readonly TimeSpan KeptFor = TimeSpan.FromHours(24);

    private static string Create()
    {
        if (!IsElevated()) return Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DnnManager")).FullName;

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var parent = SecureOrBeside(programData, "DnnManager");
        var temp = SecureOrBeside(parent, "temp");
        // What an earlier run left behind (a crash, a cancelled export) - in the background, the first use doesn't wait.
        _ = Task.Run(() => Sweep(temp, DateTime.UtcNow - KeptFor));
        return temp;
    }

    /// <summary>
    /// <paramref name="name"/> in <paramref name="parent"/>, made or kept to Administrators and SYSTEM - or, when that one
    /// can't be trusted (someone else made it first, or it is a link), one beside it: an earlier run's <c>name-…</c> that
    /// is still only administrators', or a new one. Not a new folder at every start.
    /// </summary>
    private static string SecureOrBeside(string parent, string name)
    {
        if (Secure(System.IO.Path.Combine(parent, name)) is { } own) return own;
        try
        {
            foreach (var earlier in Directory.EnumerateDirectories(parent, name + "-*", SafePath.TopLevel).Order(StringComparer.OrdinalIgnoreCase))
                if (Secure(earlier) is { } reused) return reused;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* none to reuse */ }
        return Secure(System.IO.Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N")[..8]))
               ?? throw new UnauthorizedAccessException($"Could not make a folder only administrators can change under {parent}.");
    }

    /// <summary>
    /// Deletes what in <paramref name="folder"/> was last written before <paramref name="before"/> - files, then the
    /// folders left empty - never going into a link or junction. Best effort: what is in use stays.
    /// </summary>
    internal static void Sweep(string folder, DateTime before)
    {
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SafePath.Recursive))
                if (file.LastWriteTimeUtc < before)
                    try
                    {
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use */ }
            // Deepest first, so a folder emptied of its folders goes too.
            foreach (var sub in new DirectoryInfo(folder).EnumerateDirectories("*", SafePath.Recursive).OrderByDescending(d => d.FullName.Length))
                if (sub.LastWriteTimeUtc < before && !sub.EnumerateFileSystemInfos().Any())
                    try { sub.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not now */ }
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
                // Already closed with these rules (an earlier run's): what is in it was put there by an administrator.
                var wasClosed = MadeByUs(dir);
                if (!IsOurs(dir) && !wasClosed) return null;
                dir.SetAccessControl(Rules());
                // Something put in it before it was closed (it may have let others in then) isn't trusted either.
                using var me = WindowsIdentity.GetCurrent();
                foreach (var entry in dir.EnumerateFileSystemInfos())
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        (OwnerOf(entry) is var owner && !IsTrusted(owner) && !(wasClosed && owner == me.User)))
                        return null;
                return dir.FullName;
            }
            dir.Create(Rules());
            // Made by another program between the check and here, Create keeps theirs as it is: checked again.
            dir.Refresh();
            if (IsOurs(dir)) return dir.FullName;
            if (!MadeByUs(dir)) return null;
            // Made by us, owned by us (Group Policy): Administrators are made its owner where Windows lets them be.
            try { dir.SetAccessControl(Rules()); } catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException) { }
            return dir.FullName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return null;
        }
    }

    /// <summary>
    /// Made by DNN Manager though its owner is the signed-in user, not Administrators: with the Group Policy "Default owner
    /// for objects created by members of the Administrators group: Object creator", Windows makes the creator the owner of
    /// what it makes. Taken as ours when it has exactly the rules DNN Manager gives it - no link, nothing inherited, only
    /// Administrators and SYSTEM, and OWNER RIGHTS limited to reading: an owner may otherwise always change the rules,
    /// and the user's programs (unelevated) would be that owner.
    /// </summary>
    private static bool MadeByUs(DirectoryInfo dir)
    {
        if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        using var me = WindowsIdentity.GetCurrent();
        var security = dir.GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != me.User || !security.AreAccessRulesProtected)
            return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Any(r => r.IdentityReference.Equals(OwnerRights)) &&
               rules.All(r => r.IdentityReference is SecurityIdentifier sid &&
                              (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                               sid.Equals(OwnerRights) && r.AccessControlType == AccessControlType.Allow &&
                               (r.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) == 0));
    }

    // OWNER RIGHTS: with an entry for it, the owner gets what that entry says - not the right to change the rules that an
    // owner otherwise always has.
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

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
        // Whoever Windows makes the owner (the user, with the Group Policy above) may only read.
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    /// <summary>Not a link, and owned by Administrators, SYSTEM or TrustedInstaller.</summary>
    private static bool IsOurs(DirectoryInfo dir) => dir.Exists && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsTrusted(OwnerOf(dir));

    private static SecurityIdentifier? OwnerOf(FileSystemInfo entry) => entry switch
    {
        DirectoryInfo d => d.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
        FileInfo f => f.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
        _ => null
    };

    private static bool IsTrusted(SecurityIdentifier? owner) =>
        owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        // TrustedInstaller
        owner.Value == "S-1-5-80-956008885-3425870976-2436764453-2556521931-1409716564");

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
