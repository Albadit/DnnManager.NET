using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;

namespace DnnManager.Infrastructure.Docker;

/// <summary>
/// The shared SQL Server container's docker-compose.yml, made from the settings. Shown (and copied) with a placeholder
/// instead of the sa password; run by DNN Manager with the real one, handed to <c>docker compose</c> on standard input,
/// so it is never written to a file.
/// </summary>
public sealed class DockerComposeService(ProcessRunner proc) : IDockerComposeService
{
    // All projects share one SQL container, so there is one compose project name.
    private const string ComposeProjectName = "dnn-mssql";

    // What the compose project was called before - its container is handed over to the new name.
    private const string OldComposeProjectName = "dnn-shared";

    private const string PasswordPlaceholder = "<your-sa-password>";

    private readonly ProcessRunner _proc = proc;

    public string Render(DockerOptions docker) => Build(docker, withPassword: false);

    /// <summary>
    /// The compose plugin docker would run when it is one others could change, else null. The docker DNN Manager runs
    /// reads DNN Manager's own configuration folder (<see cref="ChildEnvironment"/>), not the user's <c>.docker</c>: it
    /// looks for plugins there, then in <c>%ProgramData%\Docker\cli-plugins</c> - a folder any user may make, when Docker
    /// didn't - and then in Docker Desktop's (Program Files).
    /// </summary>
    private static string? UntrustedComposePlugin()
    {
        // Not elevated, or docker itself runs as the user (installed for this account only): a plugin it loads gets no
        // more rights than the user has.
        if (!TrustedPrograms.IsElevated || ProcessRunner.StartsAsUser("docker")) return null;
        string config;
        try
        {
            config = PrivateTemp.DockerConfigPath;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            config = "";
        }
        foreach (var folder in new[]
                 {
                     config.Length > 0 ? Path.Combine(config, "cli-plugins") : "",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Docker", "cli-plugins"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "cli-plugins")
                 })
        {
            if (folder.Length == 0) continue;
            var plugin = Path.Combine(folder, "docker-compose.exe");
            if (!File.Exists(plugin)) continue;
            // The copy checked: the plugin itself, or where a link in its place leads.
            var resolved = TrustedPrograms.Resolve(plugin);
            return resolved.Path is null ? resolved.Refused ?? plugin : null;
        }
        return null;
    }

    /// <summary>
    /// The SQL Server image: one known release (2022 CU27) by its digest - not 2022-latest, which would put a different
    /// SQL Server under the projects' databases whenever Microsoft publishes one, unseen, and whatever the registry hands
    /// out under that name. Raised by hand: the tag and its digest from <c>docker buildx imagetools inspect</c>.
    /// </summary>
    internal const string SqlServerImage =
        "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090";

    public async Task<Result> UpAsync(DockerOptions docker, IProgressReporter reporter, CancellationToken ct)
    {
        if ((DockerNames.Refusal(docker.ContainerName, "container") ?? DockerNames.Refusal(docker.VolumeName, "volume")) is { } refused)
            return Result.Fail(refused);
        await RemoveOldProjectContainerAsync(docker.ContainerName, reporter, ct);
        // There already: its databases are kept - and so is the sa password it was first made with.
        var volume = await _proc.RunAsync("docker", new[] { "volume", "inspect", "--format", "{{.Name}}", docker.VolumeName }, ct,
            timeout: TimeSpan.FromSeconds(30));
        if (volume.Success)
            reporter.Info($"The data volume '{docker.VolumeName}' exists already - its databases are kept, and its sa password is the one it was made with.");

        // "-f -": the definition comes on standard input. The first run pulls the SQL Server image (well over a GB) -
        // show docker's progress as it comes.
        reporter.Info("Running docker compose up -d with the settings (the first run downloads the SQL Server image)…");
        if (UntrustedComposePlugin() is { } plugin)
            return Result.Fail($"Didn't run docker compose: Docker would use {plugin}, which programs without administrator rights could change - " +
                               "and DNN Manager runs it as Administrator. Remove it (Docker Desktop has its own, in Program Files) and try again.");
        var r = await _proc.RunAsync("docker",
            new[] { "compose", "-p", ComposeProjectName, "-f", "-", "up", "-d" }, ct,
            onOutput: line => { if (line.Trim().Length > 0) reporter.Progress(line.Trim()); },
            stdin: Build(docker, withPassword: true));
        if (r.Success) return Result.Ok();

        // Not started: why, as ProcessRunner found it - not there at all, or a copy DNN Manager doesn't run (yours only).
        if (r.ExitCode == -1)
            return Result.Fail(r.StdErr.Trim() is { Length: > 0 } why
                ? why
                : "Docker is not installed or not on PATH - install Docker Desktop, start it and try again.");
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
    /// Where the container's port is published, as compose's <c>ports</c> items. Reached as localhost (the default), only
    /// this PC can reach it - the sa login with its default password isn't offered to the rest of the network - on both
    /// loopback addresses: Windows resolves localhost to ::1 first, and SqlClient (DNN Manager's, and a site's) waits its
    /// whole timeout there instead of going on to 127.0.0.1. Another host in the settings (this PC's address on the
    /// network, for a VM) publishes it on every network interface, as before.
    /// </summary>
    internal static IReadOnlyList<string> PublishedOn(DockerOptions docker, bool ipv6)
    {
        var port = $"{docker.DefaultPort}:1433";
        var local = docker.ContainerIp.Trim() is var host &&
                    (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "." or "::1");
        if (!local) return [port];
        // Without IPv6 on Windows, compose would fail on [::1] - and localhost is 127.0.0.1 only then anyway.
        return ipv6 ? [$"127.0.0.1:{port}", $"[::1]:{port}"] : [$"127.0.0.1:{port}"];
    }

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
                image: {{SqlServerImage}}
                container_name: {{Q(docker.ContainerName)}}
                hostname: {{Q(docker.ContainerName)}}
                environment:
                  ACCEPT_EULA: "Y"
                  MSSQL_SA_PASSWORD: {{(withPassword ? Q(docker.SaPassword) : $"\"{PasswordPlaceholder}\"")}}
                  MSSQL_PID: {{Q(docker.MssqlPid)}}
                  MSSQL_COLLATION: {{Q(docker.Collation)}}
                ports:
                  - {{string.Join("\n      - ", PublishedOn(docker, System.Net.Sockets.Socket.OSSupportsIPv6).Select(p => $"\"{p}\""))}}
                volumes:
                  - {{Q(docker.VolumeName + ":/var/opt/mssql")}}
                restart: unless-stopped
                healthcheck:
                  # $$ keeps compose from filling it in: the shell in the container reads its own MSSQL_SA_PASSWORD - and hands
                  # it to sqlcmd in SQLCMDPASSWORD, not with -P, where the container's process list would show it.
                  test: ["CMD-SHELL", "SQLCMDPASSWORD=\"$$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -No -Q \"SELECT 1\" || exit 1"]
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
