using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Docker;

public sealed class DockerService : IDockerService
{
    private readonly ProcessRunner _proc;

    public DockerService(ProcessRunner proc) => _proc = proc;

    public async Task<bool> IsContainerRunningAsync(string containerName, CancellationToken ct)
    {
        var r = await _proc.RunAsync("docker",
            new[] { "ps", "--filter", $"name=^{containerName}$", "--format", "{{.Names}}" }, ct);
        return r.Success && r.StdOut.Trim() == containerName;
    }

    public async Task<string?> GetContainerStateAsync(string containerName, CancellationToken ct)
    {
        var r = await _proc.RunAsync("docker",
            new[] { "ps", "-a", "--filter", $"name=^{containerName}$", "--format", "{{.State}}" }, ct);
        if (!r.Success) return null;
        var state = r.StdOut.Trim();
        return state.Length > 0 ? state : null;
    }

    public async Task<Result> StartContainerAsync(string containerName, CancellationToken ct)
    {
        var r = await _proc.RunAsync("docker", new[] { "start", containerName }, ct);
        return r.Success ? Result.Ok() : Result.Fail($"docker start failed: {r.StdErr}");
    }

    public async Task<int?> GetPublishedPortAsync(string containerName, CancellationToken ct)
    {
        var r = await _proc.RunAsync("docker", new[] { "port", containerName, "1433/tcp" }, ct);
        if (!r.Success) return null;
        var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        var idx = line.LastIndexOf(':');
        if (idx < 0) return null;
        return int.TryParse(line[(idx + 1)..].Trim(), out var port) ? port : null;
    }

    // The single shared compose file lives next to the app (written from the built-in default when
    // missing). All projects share one SQL container, so there is one compose file and one project name.
    private static string SharedComposeFile => BundledFiles.PathOf(BundledFiles.DockerCompose);
    private const string ComposeProjectName = "dnn-shared";

    // Writes the compose file from the built-in default if it is missing (e.g. deleted while running).
    private static Result EnsureComposeFile()
    {
        try
        {
            BundledFiles.EnsureExists(BundledFiles.DockerCompose);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail($"docker-compose.yml was not found next to the app at {SharedComposeFile} " +
                               $"and could not be recreated: {ex.Message}");
        }
    }

    public async Task<Result> ComposeUpAsync(CancellationToken ct)
    {
        var compose = EnsureComposeFile();
        if (!compose.Success) return compose;
        // The compose file is fully self-contained (all values inlined), so no --env-file is needed.
        var r = await _proc.RunAsync("docker",
            new[] { "compose", "-f", SharedComposeFile, "-p", ComposeProjectName, "up", "-d" }, ct);
        return r.Success ? Result.Ok() : Result.Fail($"docker compose up failed: {r.StdErr}");
    }

}
