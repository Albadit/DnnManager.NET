using System.Security.AccessControl;
using System.Security.Principal;

namespace DnnManager.Presentation;

/// <summary>
/// A named mutex that exists while DNN Manager runs. The installer and uninstaller look for it (Inno Setup's
/// <c>AppMutex</c>) and ask to close the app before replacing or removing its files.
/// </summary>
internal static class RunningMarker
{
    /// <summary>Keep in step with <c>AppMutex</c> in <c>src\DnnManager.Installer\DnnManager.iss</c>.</summary>
    public const string Name = "DnnManager.NET.Running";

    /// <summary>Creates the mutex; keep it alive until the app exits. Null when it couldn't be created (Setup then just can't tell).</summary>
    public static Mutex? Create()
    {
        try
        {
            // The app runs elevated but Setup doesn't, so let everyone wait on (i.e. see) the mutex.
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.Synchronize, AccessControlType.Allow));
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is { } user)
                security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            return MutexAcl.Create(initiallyOwned: false, Name, out _, security);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
