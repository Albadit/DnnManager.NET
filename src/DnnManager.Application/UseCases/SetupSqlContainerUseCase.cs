using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Sets up the shared SQL Server in Docker from the given settings: runs <c>docker compose up -d</c> with their
/// docker-compose.yml (no file is written) and waits until SQL Server accepts the sa login.
/// </summary>
public sealed class SetupSqlContainerUseCase(IDockerComposeService compose, ISqlConnectionTester tester)
{
    // A fresh container needs a while to initialise its system databases before the first login works.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private readonly IDockerComposeService _compose = compose;
    private readonly ISqlConnectionTester _tester = tester;

    public async Task<Result> ExecuteAsync(DockerOptions docker, IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Step("Starting the SQL Server container");
        var up = await _compose.UpAsync(docker, reporter, ct);
        if (!up.Success) return up;
        reporter.Success($"Container '{docker.ContainerName}' is up.");

        var server = $"{docker.ContainerIp},{docker.DefaultPort}";
        reporter.Step($"Waiting for SQL Server at {server}");
        // sa, whatever Settings → Database server → User says: a new container has no other login yet.
        var login = new SiteSqlConnection(server, "master", "sa", docker.SaPassword);
        var started = DateTime.UtcNow;
        string? lastError = null;
        while (DateTime.UtcNow - started < ReadyTimeout)
        {
            var test = await _tester.TestAsync(login, ct, timeoutSeconds: 5);
            if (test.Success)
            {
                reporter.Success($"SQL Server is ready - {test.Value}.");
                return Result.Ok();
            }
            lastError = test.Error;
            reporter.Progress($"Waiting for SQL Server to accept the sa login… {(int)(DateTime.UtcNow - started).TotalSeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        // An existing data volume keeps the sa password it was first created with, whatever the settings say.
        return Result.Fail($"SQL Server at {server} did not accept the sa login within {ReadyTimeout.TotalMinutes:0} minutes: " +
                           $"{lastError} If the volume '{docker.VolumeName}' already existed, its sa password is the one " +
                           "it was first created with.");
    }
}
