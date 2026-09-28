using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// The default <c>appsettings.json</c> and <c>docker-compose.yml</c> live here, in code. Neither file
/// has to exist in the source tree or the publish folder: a copy missing from next to the exe (first
/// run, a partial copy, a cleaned publish folder…) is written from these defaults instead of the app
/// failing to start or to bring up SQL Server. An existing file is never touched, so edits stick.
/// </summary>
public static class BundledFiles
{
    public const string AppSettings = "appsettings.json";
    public const string DockerCompose = "docker-compose.yml";

    /// <summary>Full path of <paramref name="fileName"/> next to the app.</summary>
    public static string PathOf(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    /// <summary>
    /// Writes the default <paramref name="fileName"/> next to the app when it is missing.
    /// Returns true when the file was created; an existing file is never touched.
    /// </summary>
    public static bool EnsureExists(string fileName)
    {
        var path = PathOf(fileName);
        if (File.Exists(path)) return false;

        // Write beside it and move into place, so a crash never leaves a half-written file behind.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, DefaultContent(fileName));
        File.Move(tmp, path, overwrite: false);
        return true;
    }

    public static string DefaultContent(string fileName) => fileName switch
    {
        AppSettings => DefaultAppSettings,
        DockerCompose => ComposeFor(new DockerOptions()),
        _ => throw new ArgumentException($"No built-in default for {fileName}.", nameof(fileName))
    };

    private const string DefaultAppSettings = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft": "Warning",
              "System.Net.Http": "Warning"
            }
          },
          "DnnManager": {
            "BaseDirectory": "C:\\DNN",
            "SitePort": 80,
            "HostnameSuffix": "dnndev.me",
            "Theme": "System",
            "SsmsRememberPassword": false,
            "GitHubReleaseApis": [
              "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases",
              "https://api.github.com/repos/DNN-Connect/Dnn.Platform/releases"
            ],
            "Docker": {
              "ContainerName": "dnn-sqlserver",
              "ContainerIp": "localhost",
              "VolumeName": "dnn_sqlserver_data",
              "SaPassword": "Admin@123",
              "DefaultPort": 1433,
              "Collation": "Latin1_General_CI_AS",
              "MssqlPid": "Developer",
              "DefaultDbNameSuffix": "_dnndev"
            },
            "RequiredIisFeatures": [
              { "Name": "IIS-WebServerRole",        "Label": "IIS Web Server" },
              { "Name": "IIS-WebServer",            "Label": "World Wide Web Services" },
              { "Name": "IIS-ManagementConsole",    "Label": "IIS Management Console" },
              { "Name": "IIS-NetFxExtensibility",   "Label": ".NET Extensibility 3.5" },
              { "Name": "IIS-NetFxExtensibility45", "Label": ".NET Extensibility 4.8" },
              { "Name": "IIS-ASPNET",               "Label": "ASP.NET 3.5" },
              { "Name": "IIS-ASPNET45",             "Label": "ASP.NET 4.8" },
              { "Name": "IIS-ISAPIExtensions",      "Label": "ISAPI Extensions" },
              { "Name": "IIS-ISAPIFilter",          "Label": "ISAPI Filters" },
              { "Name": "IIS-DefaultDocument",      "Label": "Default Document" },
              { "Name": "IIS-DirectoryBrowsing",    "Label": "Directory Browsing" },
              { "Name": "IIS-HttpErrors",           "Label": "HTTP Errors" },
              { "Name": "IIS-StaticContent",        "Label": "Static Content" },
              { "Name": "IIS-BasicAuthentication",  "Label": "Basic Authentication" },
              { "Name": "IIS-RequestFiltering",     "Label": "Request Filtering" },
              { "Name": "IIS-HostableWebCore",      "Label": "IIS Hostable Web Core" }
            ]
          }
        }

        """;

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
