using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface IPrerequisiteChecker
{
    Task<Result> EnsureIisFeaturesAsync(IProgressReporter reporter, IUserPrompt prompt, CancellationToken ct);

    /// <summary>Which of the required IIS features are enabled: feature name -> enabled.</summary>
    Task<IReadOnlyDictionary<string, bool>> GetIisFeatureStatesAsync(CancellationToken ct);

    /// <summary>Docker Desktop, its engine, and the state of the container <paramref name="containerName"/>.</summary>
    Task<DockerStatus> GetDockerStatusAsync(string containerName, CancellationToken ct);

    /// <summary>Installs Docker Desktop with winget.</summary>
    Task<Result> InstallDockerDesktopAsync(IProgressReporter reporter, CancellationToken ct);

    /// <summary>Starts Docker Desktop as the signed-in user (not elevated, like DNN Manager itself).</summary>
    Result StartDockerDesktop();
}
