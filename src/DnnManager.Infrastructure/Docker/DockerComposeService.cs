using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Docker;

/// <summary>
/// The shared SQL Server container's docker-compose.yml, made from the settings. Shown (and copied) with a placeholder
/// instead of the sa password; run by DNN Manager with the real one, handed to <c>docker compose</c> on standard input,
/// so it is never written to a file.
/// </summary>
public sealed class DockerComposeService : IDockerComposeService
{
    // All projects share one SQL container, so there is one compose project name.
    private const string ComposeProjectName = "dnn-mssql";

    // What the compose project was called before - its container is handed over to the new name.
    private const string OldComposeProjectName = "dnn-shared";

    private const string PasswordPlaceholder = "<your-sa-password>";

    private readonly ProcessRunner _proc;

    public DockerComposeService(ProcessRunner proc) => _proc = proc;

    public string Render(DockerOptions docker) => Build(docker, withPassword: false);

    public async Task<Result> UpAsync(DockerOptions docker, IProgressReporter reporter, CancellationToken ct)
    {
        await RemoveOldProjectContainerAsync(docker.ContainerName, reporter, ct);

        // "-f -": the definition comes on standard input. The first run pulls the SQL Server image (well over a GB) -
        // show docker's progress as it comes.
        reporter.Info("Running docker compose up -d with the settings (the first run downloads the SQL Server image)…");
        var r = await _proc.RunAsync("docker",
            new[] { "compose", "-p", ComposeProjectName, "-f", "-", "up", "-d" }, ct,
            onOutput: line => { if (line.Trim().Length > 0) reporter.Progress(line.Trim()); },
            stdin: Build(docker, withPassword: true));
        if (r.Success) return Result.Ok();

        if (r.ExitCode == -1)
            return Result.Fail("Docker is not installed or not on PATH - install Docker Desktop, start it and try again.");
        var error = r.StdErr.Trim();
        return Result.Fail($"docker compose up failed: {(error.Length > 0 ? error : r.StdOut.Trim())}");
    }

    /// <summary>
    /// A container made under the old compose project name would block the new project with "container name already in
    /// use" - remove it, so compose creates it again under the new name. The databases are kept: they live in the volume.
    /// </summary>
    private async Task RemoveOldProjectContainerAsync(string container, IProgressReporter reporter, CancellationToken ct)
    {
        var inspect = await _proc.RunAsync("docker",
            new[] { "inspect", "-f", "{{ index .Config.Labels \"com.docker.compose.project\" }}", container }, ct);
        if (!inspect.Success || inspect.StdOut.Trim() != OldComposeProjectName) return;

        reporter.Info($"Moving '{container}' from the compose project '{OldComposeProjectName}' to '{ComposeProjectName}' - " +
                      "the container is made again, the databases stay in the volume…");
        var removed = await _proc.RunAsync("docker", new[] { "rm", "-f", container }, ct);
        if (!removed.Success)
        {
            reporter.Warn($"Could not remove the old container: {removed.StdErr.Trim()}");
            return;
        }
        // The old project's network is empty now (best effort - another container may still use it).
        await _proc.RunAsync("docker", new[] { "network", "rm", OldComposeProjectName + "_default" }, ct);
    }

    /// <summary>
    /// Where the container's port is published. Reached as localhost (the default), only this PC can reach it - the sa
    /// login with its default password isn't offered to the rest of the network. Another host in the settings (this
    /// PC's address on the network, for a VM) publishes it on every network interface, as before.
    /// </summary>
    private static string PublishedOn(DockerOptions docker) =>
        docker.ContainerIp.Trim() is var host && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or ".")
            ? "127.0.0.1:" : "";

    private static string Build(DockerOptions docker, bool withPassword)
    {
        // No custom network or static IP: DNN Manager connects through the published port on the host, and a
        // fixed subnet clashes with whatever other compose projects on the machine already use.
        // Double-quoted YAML scalars; compose also interpolates "$", so a literal one is written "$$".
        static string Q(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$") + "\"";

        // $$"""…""": {{…}} is interpolated, so the healthcheck's $$ can be written as it is.
        return $$"""
            # Shared SQL Server for all DNN Manager projects - one container for every project.
            # Made from DNN Manager's settings (Settings -> SQL Server and Docker container).
            {{(withPassword ? "# Run by DNN Manager (Environment -> Set up docker-compose)." : $"# Replace {PasswordPlaceholder} below with the SA password from Settings -> SQL Server, then run:")}}
            #   docker compose -p {{ComposeProjectName}} -f docker-compose.yml up -d
            services:
              sqlserver:
                image: mcr.microsoft.com/mssql/server:2022-latest
                container_name: {{Q(docker.ContainerName)}}
                hostname: {{Q(docker.ContainerName)}}
                environment:
                  ACCEPT_EULA: "Y"
                  MSSQL_SA_PASSWORD: {{(withPassword ? Q(docker.SaPassword) : $"\"{PasswordPlaceholder}\"")}}
                  MSSQL_PID: {{Q(docker.MssqlPid)}}
                  MSSQL_COLLATION: {{Q(docker.Collation)}}
                ports:
                  - "{{PublishedOn(docker)}}{{docker.DefaultPort}}:1433"
                volumes:
                  - {{Q(docker.VolumeName + ":/var/opt/mssql")}}
                restart: unless-stopped
                healthcheck:
                  # $$ keeps compose from filling it in: the shell in the container reads its own MSSQL_SA_PASSWORD.
                  test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -C -No -Q \"SELECT 1\" || exit 1"]
                  interval: 30s
                  timeout: 10s
                  retries: 5
                  start_period: 60s

            volumes:
              {{Q(docker.VolumeName)}}:
                name: {{Q(docker.VolumeName)}}

            """;
    }
}
