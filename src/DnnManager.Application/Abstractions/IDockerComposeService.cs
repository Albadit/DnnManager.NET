using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>The shared SQL Server container's docker-compose.yml, made from the settings, and running it.</summary>
public interface IDockerComposeService
{
    /// <summary>The docker-compose.yml for these settings, with a placeholder instead of the sa password - to show or copy.</summary>
    string Render(DockerOptions docker);

    /// <summary>
    /// Runs <c>docker compose up -d</c> with the docker-compose.yml for <paramref name="docker"/>, sa password included -
    /// creates the container, or updates it after the settings changed. No file is written.
    /// </summary>
    Task<Result> UpAsync(DockerOptions docker, IProgressReporter reporter, CancellationToken ct);
}
