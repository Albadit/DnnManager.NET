using System.Diagnostics;

namespace DnnManager.Application.UseCases;

/// <summary>What a project folder's DNN install looks like on disk.</summary>
public static class DnnInstall
{
    /// <summary>
    /// The DNN version from <c>bin\DotNetNuke.dll</c>, e.g. <c>9.13.4</c>, or null when the folder holds no DNN.
    /// Read from the file version: the product version carries a build suffix (<c>9.13.4+Branch.master.Sha…</c>).
    /// </summary>
    public static string? Version(string projectDirectory)
    {
        var dll = Path.Combine(projectDirectory, "bin", "DotNetNuke.dll");
        if (!File.Exists(dll)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch
        {
            return null;
        }
    }
}
