using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Docker;

public sealed class DockerService : IDockerService
{
    private readonly ProcessRunner _proc;
    private readonly ILogger<DockerService> _log;

    public DockerService(ProcessRunner proc, ILogger<DockerService> log)
    {
        _proc = proc; _log = log;
    }

    public async Task<bool> IsContainerRunningAsync(string containerName, CancellationToken ct)
    {
        var r = await _proc.RunAsync("docker",
            new[] { "ps", "--filter", $"name=^{containerName}$", "--format", "{{.Names}}" }, ct);
        return r.Success && r.StdOut.Trim() == containerName;
    }

    public async Task<bool> DoesContainerExistAsync(string containerName, CancellationToken ct)
    {
        var r = await _proc.RunAsync("docker",
            new[] { "ps", "-a", "--filter", $"name=^{containerName}$", "--format", "{{.Names}}" }, ct);
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

    // The single shared compose file ships next to the app (copied to the build output / publish
    // folder). All projects share one SQL container, so there is one compose file and one project name.
    private static string SharedComposeFile => Path.Combine(AppContext.BaseDirectory, "docker-compose.yml");
    private const string ComposeProjectName = "dnn-shared";

    // Recreates the bundled compose file if it went missing while the app was running.
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

    public async Task<Result> ComposeDownAsync(bool removeVolumes, CancellationToken ct)
    {
        var compose = EnsureComposeFile();
        if (!compose.Success) return compose;
        var args = new List<string> { "compose", "-f", SharedComposeFile, "-p", ComposeProjectName, "down" };
        if (removeVolumes) args.Add("-v");
        var r = await _proc.RunAsync("docker", args, ct);
        return r.Success ? Result.Ok() : Result.Fail($"docker compose down failed: {r.StdErr}");
    }

    public async Task<Result<string>> ExecAsync(string containerName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var full = new List<string> { "exec", containerName };
        full.AddRange(args);
        var r = await _proc.RunAsync("docker", full, ct);
        return r.Success ? Result<string>.Ok(r.StdOut) : Result<string>.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
    }
}
