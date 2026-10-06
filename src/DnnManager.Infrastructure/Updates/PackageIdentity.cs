using System.Runtime.InteropServices;

namespace DnnManager.Infrastructure.Updates;

/// <summary>
/// Whether this DNN Manager runs from an MSIX package (src/DnnManager.Package - an experiment) rather than from Setup's
/// folder or as the portable exe. A package's files can't be replaced: it is updated as a package (the Store, or a newer
/// package), never by DNN Manager's own update.
/// </summary>
public static class PackageIdentity
{
    // Windows' APPMODEL_ERROR_NO_PACKAGE: the process has no package identity.
    private const int NoPackage = 15700;

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        var length = 0;
        // Packaged, it asks for a bigger buffer (ERROR_INSUFFICIENT_BUFFER); not packaged, it says so.
        return GetCurrentPackageFullName(ref length, null) != NoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);
}
