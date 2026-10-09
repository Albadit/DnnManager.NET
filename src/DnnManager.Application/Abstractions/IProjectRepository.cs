using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IProjectRepository
{
    IReadOnlyList<string> ListAllProjectDirectories();

    /// <summary>The project named <paramref name="projectName"/>, in its folder in the projects folder.</summary>
    DnnProject Build(string projectName);

    /// <summary>The project of the IIS site <paramref name="siteName"/>, in the folder the site serves - wherever that is.</summary>
    DnnProject Build(string siteName, string directory);
    bool ProjectExists(string projectName);

    /// <summary>
    /// The projects with a folder in the backups folder, whether or not the project itself still exists
    /// (backups outlive a removed project).
    /// </summary>
    IReadOnlyList<string> ListProjectsWithBackups();
}
