using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Sets up the shared SQL Server in Docker from the given settings: runs <c>docker compose up -d</c> with their
/// docker-compose.yml (no file is written) and waits until SQL Server accepts the sa login. A data volume made by DNN
/// Manager 1.7.1 or older keeps that version's default sa password: it is found, and taken into the settings when the
/// caller can save it - the container is then made again with it.
/// </summary>
public sealed class SetupSqlContainerUseCase(IDockerComposeService compose, ISqlConnectionTester tester,
    SiteDatabases? sites = null, IWebConfigService? webConfig = null)
{
    // A fresh container needs a while to initialise its system databases before the first login works.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    private readonly IDockerComposeService _compose = compose;
    private readonly ISqlConnectionTester _tester = tester;

    /// <param name="adoptPassword">
    /// Saves the sa password the data volume turned out to have as the settings' (Settings → Database server) - or null:
    /// it is only said.
    /// </param>
    public async Task<Result> ExecuteAsync(DockerOptions docker, IProgressReporter reporter, CancellationToken ct,
        Func<string, Result>? adoptPassword = null)
    {
        var (result, volumePassword) = await UpAndWaitAsync(docker, reporter, ct);
        if (volumePassword is null) return Reachable(result, docker, reporter);

        var volume = docker.VolumeName;
        if (adoptPassword is null || adoptPassword(volumePassword) is { Success: false })
            return Result.Fail($"SQL Server is up, but its data volume '{volume}' still has DNN Manager's old default sa password " +
                               $"({volumePassword}). Put it in Settings → Database server, Save, and set it up again.");

        reporter.Warn($"The data volume '{volume}' still has DNN Manager's old default sa password - Settings → Database server uses it now. " +
                      "Change it there and in SQL Server if this PC is on a network you don't trust.");
        // Made again with it: the container's health check signs in with the password it is given.
        return Reachable((await UpAndWaitAsync(docker.WithSaPassword(volumePassword), reporter, ct)).Result, docker, reporter);
    }

    /// <summary>
    /// Once the container answers: the sites whose web.config reaches it as <c>localhost,&lt;port&gt;</c> are pointed at
    /// <c>127.0.0.1,&lt;port&gt;</c>. It is published on this PC's loopback only, and a site's SqlClient (.NET Framework's)
    /// goes to localhost by this computer's name and network address, where it doesn't listen - "could not connect".
    /// </summary>
    private Result Reachable(Result result, DockerOptions docker, IProgressReporter reporter)
    {
        if (!result.Success || _sites is null || _webConfig is null) return result;
        foreach (var (site, webConfig) in _sites.ReachingAsLocalhost($"{docker.ContainerIp},{docker.DefaultPort}"))
        {
            var changed = _webConfig.UseLoopbackAddress(webConfig);
            if (changed is { Success: true, Value: true })
                reporter.Info($"Site '{site}' connects to 127.0.0.1,{docker.DefaultPort} now - its SqlClient didn't reach the container as localhost.");
            else if (!changed.Success)
                reporter.Warn($"Site '{site}' still connects as localhost,{docker.DefaultPort} and may not reach the container: {changed.Error}");
        }
        return result;
    }

    private readonly SiteDatabases? _sites = sites;
    private readonly IWebConfigService? _webConfig = webConfig;

    /// <summary>
    /// Up, then waiting for the sa login. <c>VolumePassword</c>: SQL Server refused the settings' password and took the old
    /// default instead - said at once, not after minutes of waiting for a password that will never work.
    /// </summary>
    private async Task<(Result Result, string? VolumePassword)> UpAndWaitAsync(DockerOptions docker, IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Step("Starting the SQL Server container");
        var up = await _compose.UpAsync(docker, reporter, ct);
        if (!up.Success) return (up, null);
        reporter.Success($"Container '{docker.ContainerName}' is up.");

        var server = $"{docker.ContainerIp},{docker.DefaultPort}";
        reporter.Step($"Waiting for SQL Server at {server}");
        // sa, whatever Settings → Database server → User says: a new container has no other login yet.
        var login = new SiteSqlConnection(server, "master", "sa", docker.SaPassword);
        var started = DateTime.UtcNow;
        string? lastError = null;
        var legacyTried = false;
        while (DateTime.UtcNow - started < ReadyTimeout)
        {
            var test = await _tester.TestAsync(login, ct, timeoutSeconds: 5);
            if (test.Success)
            {
                reporter.Success($"SQL Server is ready - {test.Value}.");
                return (Result.Ok(), null);
            }
            lastError = test.Error;
            if (!legacyTried && IsLoginFailure(test.Error) && docker.SaPassword != LegacySaPassword)
            {
                legacyTried = true;
                if ((await _tester.TestAsync(login with { Password = LegacySaPassword }, ct, timeoutSeconds: 5)).Success)
                    return (Result.Ok(), LegacySaPassword);
            }
            reporter.Progress($"Waiting for SQL Server to accept the sa login… {(int)(DateTime.UtcNow - started).TotalSeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        // An existing data volume keeps the sa password it was first created with, whatever the settings say.
        return (Result.Fail(IsLoginFailure(lastError)
            ? $"SQL Server at {server} is up, but doesn't take the sa password in Settings → Database server: the data volume " +
              $"'{docker.VolumeName}' keeps the sa password it was first created with. Put that one in Settings → Database server and Save, " +
              "then Set up docker-compose again."
            : $"SQL Server at {server} did not accept the sa login within {ReadyTimeout.TotalMinutes:0} minutes: " +
              $"{lastError} If the volume '{docker.VolumeName}' already existed, its sa password is the one it was first created with."), null);
    }

    /// <summary>The sa password DNN Manager 1.7.1 and older gave every container - a volume made then still has it.</summary>
    internal const string LegacySaPassword = "Admin@123";

    // SQL Server answered, and refused the login (18456) - not "not there yet".
    private static bool IsLoginFailure(string? error) =>
        error is not null && error.Contains("Login failed", StringComparison.OrdinalIgnoreCase);
}
