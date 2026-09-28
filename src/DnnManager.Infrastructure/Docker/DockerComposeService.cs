using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Docker;

public sealed class DockerComposeService : IDockerComposeService
{
    // All projects share one SQL container, so there is one compose file and one compose project name.
    private const string ComposeProjectName = "dnn-shared";

    private readonly ProcessRunner _proc;

    public DockerComposeService(ProcessRunner proc) => _proc = proc;

    public string ComposeFilePath => BundledFiles.PathOf(BundledFiles.DockerCompose);

    public string Render(DockerOptions docker) => BundledFiles.ComposeFor(docker);

    public string? ReadCurrent() => File.Exists(ComposeFilePath) ? File.ReadAllText(ComposeFilePath) : null;

    public async Task<Result> UpAsync(string yaml, IProgressReporter reporter, CancellationToken ct)
    {
        try
        {
            // Write beside it and move into place, so a failed write never leaves half a compose file.
            var tmp = ComposeFilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, yaml, ct);
            File.Move(tmp, ComposeFilePath, overwrite: true);
            reporter.Success($"Wrote {ComposeFilePath}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not write {ComposeFilePath}: {ex.Message}");
        }

        // The first run pulls the SQL Server image (well over a GB) - show docker's progress as it comes.
        reporter.Info("Running docker compose up -d (the first run downloads the SQL Server image)…");
        var r = await _proc.RunAsync("docker",
            new[] { "compose", "-f", ComposeFilePath, "-p", ComposeProjectName, "up", "-d" }, ct,
            onOutput: line => { if (line.Trim().Length > 0) reporter.Progress(line.Trim()); });
        if (r.Success) return Result.Ok();

        if (r.ExitCode == -1)
            return Result.Fail("Docker is not installed or not on PATH - install Docker Desktop, start it and try again.");
        var error = r.StdErr.Trim();
        return Result.Fail($"docker compose up failed: {(error.Length > 0 ? error : r.StdOut.Trim())}");
    }
}
