using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>The projects folder is SYSTEM's, Administrators' and yours - not every account's on the PC.</summary>
[TestClass]
public sealed class ProjectsFolderGuardTests
{
    private static ProjectsFolderGuard Guard() => new(Options.Create(new AppOptions()), NullLogger<ProjectsFolderGuard>.Instance);

    [TestMethod]
    public void A_folder_everyone_may_read_is_kept_to_administrators_and_you()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"dnnmanager-projects-{Guid.NewGuid():N}");
        var site = Directory.CreateDirectory(Path.Combine(folder, "shop")).FullName;
        try
        {
            var dir = new DirectoryInfo(folder);
            var open = dir.GetAccessControl();
            // A folder of your own, as Explorer makes one. Run elevated (CI's runner), Windows would make the Administrators
            // group its owner - a folder an administrator made, which the guard rightly leaves alone.
            using (var me = WindowsIdentity.GetCurrent()) open.SetOwner(me.User!);
            open.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.SetAccessControl(open);
            Assert.IsTrue(ProjectsFolderGuard.LetsEveryoneIn(dir.GetAccessControl()));

            Assert.IsTrue(Guard().Secure(folder));

            Assert.IsFalse(ProjectsFolderGuard.LetsEveryoneIn(dir.GetAccessControl()), "Everyone may still get in.");
            Assert.IsTrue(dir.GetAccessControl().AreAccessRulesProtected, "It still inherits from the folder it is in.");
            Assert.IsFalse(ProjectsFolderGuard.LetsEveryoneIn(new DirectoryInfo(site).GetAccessControl()), "A site's folder still lets everyone in.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void A_drive_is_never_changed() => Assert.IsFalse(Guard().Secure(Path.GetPathRoot(Path.GetTempPath())!));
}
