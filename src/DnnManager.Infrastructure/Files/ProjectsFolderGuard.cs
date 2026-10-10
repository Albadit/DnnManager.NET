using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DnnManager.Application;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Keeps the projects folder (<c>C:\DNN</c>) to SYSTEM, Administrators and you. A folder made at the root of C: inherits
/// "Authenticated Users: Modify" - every account on the PC, and every program they run, could read each site's
/// web.config (its database password) and change its bin folder, which the site runs. Each site's folder keeps the
/// rights DNN Manager gives IIS on it - its app pool changes it, IIS_IUSRS and IUSR read it - and no more: what older
/// versions gave (Full control for all three) is lowered to that. Done at the start and whenever the projects folder
/// changes, in the background - Windows passes the change on to every file in it.
/// </summary>
public sealed class ProjectsFolderGuard(IOptions<AppOptions> options, ILogger<ProjectsFolderGuard> log)
{
    private readonly AppOptions _options = options.Value;
    private readonly ILogger<ProjectsFolderGuard> _log = log;
    private int _started;

    // The groups everyone is in: none of them may read or change the projects folder.
    private static readonly SecurityIdentifier[] Everyone =
    [
        new(WellKnownSidType.WorldSid, null), new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.BuiltinUsersSid, null), new(WellKnownSidType.InteractiveSid, null)
    ];

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _options.Changed += () => _ = Task.Run(() => Secure(_options.BaseDirectory));
        _ = Task.Run(() => Secure(_options.BaseDirectory));
    }

    /// <summary>
    /// Makes <paramref name="folder"/> - or sets it to - SYSTEM, Administrators and the signed-in user only, when it lets
    /// everyone in now. True when it is kept so.
    /// </summary>
    /// <remarks>
    /// The setting comes from the user's settings (or an environment variable), which any program of theirs can change,
    /// and this runs with administrator rights - so it never gives the user rights they didn't have: never a drive, a
    /// system folder or anything in one (<see cref="SettingRules.ProjectsFolderProblem"/>), nothing reached through a
    /// link or junction, and of a folder that is already there only one the user owns (they could change its rights
    /// anyway). A folder an administrator made - a program's or a service's - is left as it is.
    /// </remarks>
    public bool Secure(string folder)
    {
        try
        {
            if (SettingRules.ProjectsFolderProblem(folder) is { } problem)
            {
                _log.LogWarning("The projects folder {Folder} is left as it is: it {Problem}", folder, problem);
                return false;
            }
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (ThroughLink(full)) return false;
            var dir = new DirectoryInfo(full);
            if (!dir.Exists)
            {
                dir.Create(Rules());
                // Made by another program in between, it would have been kept as it was: only ours counts.
                dir.Refresh();
                return !dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && !LetsEveryoneIn(dir.GetAccessControl());
            }
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var security = dir.GetAccessControl();
            if (LetsEveryoneIn(security))
            {
                if (OwnedByCurrentUser(security))
                {
                    dir.SetAccessControl(Rules());
                    _log.LogInformation("The projects folder {Folder} is now only for SYSTEM, Administrators and {User}", full, Environment.UserName);
                }
                // Made by an administrator - an older DNN Manager made C:\DNN so: when it holds DNN sites only, everyone's
                // groups are taken out and nothing else changes - nobody gets a right they didn't have. Any other folder
                // an administrator made (a program's, a service's) is left as it is.
                else if (HoldsOnlyDnnSites(dir))
                {
                    dir.SetAccessControl(WithoutEveryone(security));
                    _log.LogInformation("The projects folder {Folder} no longer lets every user in", full);
                }
                else
                {
                    _log.LogWarning("The projects folder {Folder} lets every user in and isn't yours: DNN Manager doesn't change the rights of " +
                                    "a folder an administrator made - choose a folder of your own (it makes one when it isn't there)", full);
                    return false;
                }
            }
            TightenSites(dir);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            _log.LogWarning(ex, "Could not keep the projects folder {Folder} to administrators and you", folder);
            return false;
        }
    }

    private static readonly SecurityIdentifier IisUsers = new("S-1-5-32-568"); // IIS_IUSRS
    private static readonly SecurityIdentifier Iusr = new("S-1-5-17");
    private const string AppPoolSidPrefix = "S-1-5-82-";

    /// <summary>
    /// Each site's folder in <paramref name="projects"/> to the rights DNN Manager gives IIS today: IIS_IUSRS (every
    /// site's app pool) and IUSR read it, its own app pool changes its files but not their rights. Older versions gave
    /// all three Full control - so any site's app pool could change every other site, its web.config and bin included.
    /// </summary>
    private void TightenSites(DirectoryInfo projects)
    {
        foreach (var site in projects.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                if (TightenSite(site)) _log.LogInformation("The site folder {Folder}: IIS_IUSRS and IUSR now only read it, its app pool no longer changes its rights", site.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
            {
                _log.LogWarning(ex, "Could not limit IIS's rights on {Folder}", site.FullName);
            }
        }
    }

    /// <summary>Lowers what IIS's identities may do in <paramref name="site"/> (its own rules only). True when something changed.</summary>
    internal static bool TightenSite(DirectoryInfo site)
    {
        var security = site.GetAccessControl();
        var changed = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid) continue;
            FileSystemRights? allowed = sid == IisUsers || sid == Iusr ? FileSystemRights.ReadAndExecute
                : sid.Value.StartsWith(AppPoolSidPrefix, StringComparison.Ordinal) ? FileSystemRights.Modify : null;
            if (allowed is not { } limit || (rule.FileSystemRights & ~limit & ~FileSystemRights.Synchronize) == 0) continue;
            security.RemoveAccessRuleSpecific(rule);
            security.AddAccessRule(new FileSystemAccessRule(sid, limit, rule.InheritanceFlags, rule.PropagationFlags, AccessControlType.Allow));
            changed = true;
        }
        if (changed) site.SetAccessControl(security);
        return changed;
    }

    /// <summary>Every folder in it is a DNN site (its bin holds DotNetNuke.dll) - DNN Manager's projects folder, not a program's.</summary>
    private static bool HoldsOnlyDnnSites(DirectoryInfo folder)
    {
        var sites = folder.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        return sites.Count > 0 && sites.All(s => File.Exists(Path.Combine(s.FullName, "bin", "DotNetNuke.dll")));
    }

    /// <summary>The folder's rules - its own and those it inherits, kept as its own from now on - without everyone's groups.</summary>
    internal static DirectorySecurity WithoutEveryone(DirectorySecurity current)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in current.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            if (!(rule.AccessControlType == AccessControlType.Allow && Everyone.Contains((SecurityIdentifier)rule.IdentityReference)))
                security.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, rule.FileSystemRights, rule.InheritanceFlags,
                    rule.PropagationFlags, rule.AccessControlType));
        return security;
    }

    /// <summary>Owned by the signed-in user, who can change its rights anyway.</summary>
    private static bool OwnedByCurrentUser(DirectorySecurity security)
    {
        using var me = WindowsIdentity.GetCurrent();
        return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner == me.User;
    }

    /// <summary>Whether a folder on the way to <paramref name="full"/> - from its drive - is a link or junction.</summary>
    private static bool ThroughLink(string full) =>
        Path.GetDirectoryName(full) is { } parent && Path.GetPathRoot(full) is { Length: > 0 } drive && SafePath.HasLink(drive, parent);

    /// <summary>Whether one of the groups everyone is in may do anything with the folder.</summary>
    internal static bool LetsEveryoneIn(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow && Everyone.Contains((SecurityIdentifier)r.IdentityReference));

    /// <summary>SYSTEM, Administrators and the signed-in user, each in full - for the folder and everything in it.</summary>
    internal static DirectorySecurity Rules()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var me = WindowsIdentity.GetCurrent();
        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     me.User!
                 }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
