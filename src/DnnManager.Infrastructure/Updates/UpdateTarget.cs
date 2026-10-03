using Microsoft.Win32;

namespace DnnManager.Infrastructure.Updates;

/// <summary>How this DNN Manager is updated: by running the new Setup over it, or by replacing the portable exe.</summary>
public enum UpdateKind { Installer, Portable }

/// <summary>
/// What an update replaces: the exe that runs now and, when it was installed, for whom (Setup's <c>/CURRENTUSER</c> or
/// <c>/ALLUSERS</c>, so the update goes where the installation is).
/// </summary>
public sealed record UpdateTarget(UpdateKind Kind, string AppExe, bool AllUsers = false)
{
    /// <summary>Keep in step with <c>AppGuid</c> in <c>src\DnnManager.Installer\DnnManager.iss</c>.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AD68C57A-D887-4297-A905-B6F28C1D66D1}_is1";

    /// <summary>
    /// This DNN Manager: installed by Setup when the exe is in an installation's folder, portable when it is a single-file
    /// exe anywhere else. Null for a development build (a <c>dotnet build</c> output) - it isn't updated, it is rebuilt.
    /// </summary>
    public static UpdateTarget? Detect(string? exe, bool isSingleFile) => Detect(exe, isSingleFile, InstalledLocations());

    public static UpdateTarget? Detect(string? exe, bool isSingleFile, IEnumerable<(string Folder, bool AllUsers)> installations)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        var folder = Normalize(Path.GetDirectoryName(exe)!);
        foreach (var (installed, allUsers) in installations)
            if (string.Equals(Normalize(installed), folder, StringComparison.OrdinalIgnoreCase))
                return new UpdateTarget(UpdateKind.Installer, exe, allUsers);
        return isSingleFile ? new UpdateTarget(UpdateKind.Portable, exe) : null;
    }

    /// <summary>Where Setup installed DNN Manager - for the current user, and for all users.</summary>
    public static IEnumerable<(string Folder, bool AllUsers)> InstalledLocations()
    {
        foreach (var (hive, allUsers) in new[] { (RegistryHive.CurrentUser, false), (RegistryHive.LocalMachine, true) })
        {
            string? location = null;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(UninstallKey);
                location = key?.GetValue("InstallLocation") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable - not this installation, as far as the update can tell.
            }
            if (!string.IsNullOrWhiteSpace(location)) yield return (location, allUsers);
        }
    }

    private static string Normalize(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/');
}
