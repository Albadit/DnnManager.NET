using System.Runtime.InteropServices;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Windows' redirection trust for this process: a junction (mount point) that a program without administrator rights
/// made - in a site folder, %TEMP%, Documents - is no longer followed by DNN Manager. With administrator rights a
/// delete, write or permission change through such a junction would reach wherever that program pointed it (a
/// Program Files folder, System32). Opening a path through one fails with "untrusted mount point"; a junction an
/// administrator made still works. Other programs, IIS among them, aren't affected.
/// </summary>
public static class RedirectionTrust
{
    private const int ProcessRedirectionTrustPolicy = 16;
    private const int EnforceRedirectionTrust = 0x1;

    /// <summary>Turns it on; false where Windows doesn't have it (an older Windows without its recent updates).</summary>
    public static bool Enforce()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var flags = EnforceRedirectionTrust;
        try
        {
            return SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref flags, sizeof(int));
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Windows' Developer Mode is on (<c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock</c>,
    /// <c>AllowDevelopmentWithoutDevLicense</c> = 1): any program, without administrator rights, may then make symbolic
    /// links - which redirection trust (it covers junctions) doesn't stop. DNN Manager's own link checks
    /// (Application's SafePath) still hold; this is for a start-up notice.
    /// </summary>
    public static bool DeveloperModeAllowsLinks
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
                return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int value && value != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref int buffer, nint length);
}
