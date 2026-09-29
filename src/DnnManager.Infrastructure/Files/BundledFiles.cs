using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// The default <c>docker-compose.yml</c> lives here, in code, and is written to <c>Documents\DnnManager</c>
/// when it's missing there, so the SQL Server container can always be brought up. An existing file is
/// never touched, so edits stick. (The default settings are <see cref="UserSettings"/>'s own defaults.)
/// </summary>
public static class BundledFiles
{
    /// <summary>
    /// Writes <c>docker-compose.yml</c> into the user's folder when it is missing there: the copy an older
    /// version kept next to the exe, or else the default. Returns what was done, or null when the file was there.
    /// </summary>
    public static string? EnsureDockerCompose(AppDataPaths paths)
    {
        var path = paths.ComposeFile;
        if (File.Exists(path)) return null;

        var legacy = Path.Combine(AppDataPaths.LegacyDirectory, "docker-compose.yml");
        var fromLegacy = File.Exists(legacy);
        var content = fromLegacy ? File.ReadAllText(legacy) : ComposeFor(new DockerOptions());

        // Write beside it and move into place, so a crash never leaves a half-written file behind.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: false);
        return fromLegacy ? $"Copied {legacy} to {path}." : $"Created {path} with the default SQL Server container.";
    }

    /// <summary>
    /// The shared SQL Server's <c>docker-compose.yml</c> for <paramref name="docker"/>: container name, sa
    /// password, edition, collation, published port and data volume all come from the settings.
    /// </summary>
    public static string ComposeFor(DockerOptions docker)
    {
        // No custom network or static IP: DNN Manager connects through the published port on the host, and a
        // fixed subnet clashes with whatever other compose projects on the machine already use.
        // Double-quoted YAML scalars; compose also interpolates "$", so a literal one is written "$$".
        static string Q(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$") + "\"";

        return $"""
            # Shared SQL Server for all DNN Manager projects - one container for every project.
            # Generated from the SQL Server settings (Environment page -> Set up container); changing
            # those settings and setting the container up again rewrites this file.
            services:
              sqlserver:
                image: mcr.microsoft.com/mssql/server:2022-latest
                container_name: {Q(docker.ContainerName)}
                hostname: {Q(docker.ContainerName)}
                environment:
                  ACCEPT_EULA: "Y"
                  MSSQL_SA_PASSWORD: {Q(docker.SaPassword)}
                  MSSQL_PID: {Q(docker.MssqlPid)}
                  MSSQL_COLLATION: {Q(docker.Collation)}
                ports:
                  - "{docker.DefaultPort}:1433"
                volumes:
                  - {Q(docker.VolumeName + ":/var/opt/mssql")}
                restart: unless-stopped
                healthcheck:
                  test: ["CMD", "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", {Q(docker.SaPassword)}, "-C", "-No", "-Q", "SELECT 1"]
                  interval: 30s
                  timeout: 10s
                  retries: 5
                  start_period: 60s

            volumes:
              {Q(docker.VolumeName)}:
                name: {Q(docker.VolumeName)}

            """;
    }
}
