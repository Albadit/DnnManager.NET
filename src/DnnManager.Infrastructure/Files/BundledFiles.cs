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

    /// <summary>The built-in default content of <paramref name="fileName"/>.</summary>
    public static string DefaultContent(string fileName) => fileName switch
    {
        AppSettings => DefaultAppSettings,
        DockerCompose => DefaultDockerCompose,
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
            "GitHubReleaseApis": [
              "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases",
              "https://api.github.com/repos/DNN-Connect/Dnn.Platform/releases"
            ],
            "Docker": {
              "ContainerName": "dnn-sqlserver",
              "ContainerIp": "127.0.0.1",
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

    private const string DefaultDockerCompose = """
        # Shared SQL Server for all DnnManager projects.
        # Written next to the app by DNN Manager when missing, and the single compose file the tool runs -
        # there is no per-project compose. One shared container, fixed port 1433.
        # Keep these values in sync with the "Docker" section of appsettings.json (the tool connects using
        # those settings).
        services:
          sqlserver:
            image: mcr.microsoft.com/mssql/server:2022-latest
            container_name: dnn-sqlserver
            hostname: dnn-sqlserver
            environment:
              - ACCEPT_EULA=Y
              - SA_PASSWORD=Admin@123
              - MSSQL_SA_PASSWORD=Admin@123
              - MSSQL_PID=Developer
              - MSSQL_COLLATION=Latin1_General_CI_AS
            ports:
              - "1433:1433"
            volumes:
              - dnn_sqlserver_data:/var/opt/mssql
            restart: unless-stopped
            networks:
              dnn_network:
                # Static IP - keep in sync with Docker.ContainerIp in appsettings.json.
                ipv4_address: 172.20.0.10
            healthcheck:
              test: /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P Admin@123 -C -No -Q "SELECT 1"
              interval: 30s
              timeout: 10s
              retries: 5
              start_period: 60s

        volumes:
          dnn_sqlserver_data:
            name: dnn_sqlserver_data

        networks:
          dnn_network:
            driver: bridge
            name: dnn_network
            ipam:
              config:
                - subnet: 172.20.0.0/16

        """;
}
