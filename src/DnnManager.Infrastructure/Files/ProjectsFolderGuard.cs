using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Keeps the projects folder (<c>C:\DNN</c>) to SYSTEM, Administrators and you. A folder made at the root of C: inherits
/// "Authenticated Users: Modify" - every account on the PC, and every program they run, could read each site's
/// web.config (its database password) and change its bin folder, which the site runs. Each site's folder keeps the
/// rights DNN Manager gave IIS on it (its app pool, IIS_IUSRS, IUSR): those are its own. Done at the start and whenever
/// the projects folder changes, in the background - Windows passes the change on to every file in it.
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
    /// everyone in now. Never a drive or a system folder (the settings refuse those too). True when it is kept so.
    /// </summary>
    public bool Secure(string folder)
    {
        try
        {
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) return false;
            var full = Path.GetFullPath(folder).TrimEnd('\\');
            if (Path.GetPathRoot(full + "\\")?.TrimEnd('\\') == full) return false;
            var dir = new DirectoryInfo(full);
            if (!dir.Exists)
            {
                dir.Create(Rules());
                return true;
            }
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !LetsEveryoneIn(dir.GetAccessControl())) return true;
            dir.SetAccessControl(Rules());
            _log.LogInformation("The projects folder {Folder} is now only for SYSTEM, Administrators and {User}", full, Environment.UserName);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            _log.LogWarning(ex, "Could not keep the projects folder {Folder} to administrators and you", folder);
            return false;
        }
    }

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
