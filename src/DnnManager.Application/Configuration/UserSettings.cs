namespace DnnManager.Application.Configuration;

/// <summary>
/// The user's <c>settings.json</c> (in <c>Documents\DnnManager</c>), as the file is laid out. Every value has
/// a default here, so a key missing from the file is filled in with it. The running app reads the
/// flattened <see cref="AppOptions"/> made by <see cref="ToAppOptions"/>.
/// </summary>
/// <remarks>
/// Changing the layout (renaming or moving a key, changing what a value means) needs a higher
/// <see cref="CurrentVersion"/> and a migration from the previous version, so older files keep loading.
/// Adding a key with a default doesn't - it is filled in on the next start.
/// </remarks>
public sealed class UserSettings
{
    /// <summary>The settings layout this build reads and writes.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public ProjectSettings Projects { get; set; } = new();
    public SqlServerSettings SqlServer { get; set; } = new();
    public SsmsSettings Ssms { get; set; } = new();
    public IisSettings Iis { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>The values that aren't allowed, each with the key it is about; empty when the settings are usable.</summary>
    public IReadOnlyList<SettingsProblem> Validate()
    {
        var problems = new List<SettingsProblem>();
        void Check(bool ok, string key, string message)
        {
            if (!ok) problems.Add(new SettingsProblem(key, message));
        }
        static bool IsPort(int port) => port is > 0 and <= 65535;
        static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

        Check(Has(Projects.BaseDirectory) && Path.IsPathFullyQualified(Projects.BaseDirectory),
            "projects.baseDirectory", "must be a full path, e.g. C:\\DNN.");
        Check(IsPort(Projects.SitePort), "projects.sitePort", "must be a number between 1 and 65535.");
        Check(Has(Projects.HostnameSuffix) && !Projects.HostnameSuffix.Trim().Trim('.').Contains(' '),
            "projects.hostnameSuffix", "is required and can't contain spaces.");
        Check(Projects.DnnReleaseSources.Count > 0, "projects.dnnReleaseSources", "needs at least one URL.");
        foreach (var source in Projects.DnnReleaseSources)
            Check(Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                "projects.dnnReleaseSources", $"has an invalid URL: {source}");

        Check(Has(SqlServer.Host), "sqlServer.host", "is required.");
        Check(IsPort(SqlServer.Port), "sqlServer.port", "must be a number between 1 and 65535.");
        Check(!string.IsNullOrEmpty(SqlServer.SaPassword), "sqlServer.saPassword", "is required.");
        Check(Has(SqlServer.ContainerName), "sqlServer.containerName", "is required.");
        Check(Has(SqlServer.VolumeName), "sqlServer.volumeName", "is required.");
        Check(Has(SqlServer.Edition), "sqlServer.edition", "is required.");
        Check(Has(SqlServer.Collation), "sqlServer.collation", "is required.");

        foreach (var feature in Iis.RequiredFeatures)
            Check(Has(feature?.Name), "iis.requiredFeatures", "has a feature without a name.");

        Check(AppearanceSettings.Themes.Contains(Appearance.Theme, StringComparer.OrdinalIgnoreCase),
            "appearance.theme", $"must be one of: {string.Join(", ", AppearanceSettings.Themes)}.");
        return problems;
    }

    public AppOptions ToAppOptions() => new()
    {
        BaseDirectory = Projects.BaseDirectory,
        SitePort = Projects.SitePort,
        HostnameSuffix = Projects.HostnameSuffix.Trim().Trim('.'),
        GitHubReleaseApis = Projects.DnnReleaseSources.ToList(),
        Theme = Appearance.Theme,
        SsmsRememberPassword = Ssms.RememberPassword,
        Docker = new DockerOptions
        {
            ContainerName = SqlServer.ContainerName,
            ContainerIp = SqlServer.Host,
            VolumeName = SqlServer.VolumeName,
            SaPassword = SqlServer.SaPassword,
            DefaultPort = SqlServer.Port,
            Collation = SqlServer.Collation,
            MssqlPid = SqlServer.Edition,
            DefaultDbNameSuffix = SqlServer.DatabaseNameSuffix
        },
        RequiredIisFeatures = Iis.RequiredFeatures.ToList()
    };
}

public sealed class ProjectSettings
{
    /// <summary>Where projects live - every project is a folder in here.</summary>
    public string BaseDirectory { get; set; } = @"C:\DNN";
    /// <summary>Sites answer at <c>http://{project}.{HostnameSuffix}[:SitePort]</c>.</summary>
    public string HostnameSuffix { get; set; } = "dnndev.me";
    public int SitePort { get; set; } = 80;
    /// <summary>GitHub releases API URLs offered as DNN sources for a new project.</summary>
    public List<string> DnnReleaseSources { get; set; } =
    [
        "https://api.github.com/repos/dnnsoftware/Dnn.Platform/releases",
        "https://api.github.com/repos/DNN-Connect/Dnn.Platform/releases"
    ];
}

/// <summary>The shared SQL Server: how DNN Manager connects to it, and how its Docker container is set up.</summary>
public sealed class SqlServerSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string SaPassword { get; set; } = "Admin@123";
    /// <summary>A project's local database is <c>{project}{DatabaseNameSuffix}</c>.</summary>
    public string DatabaseNameSuffix { get; set; } = "_dnndev";
    public string ContainerName { get; set; } = "dnn-sqlserver";
    public string VolumeName { get; set; } = "dnn_sqlserver_data";
    /// <summary>The container's <c>MSSQL_PID</c>.</summary>
    public string Edition { get; set; } = "Developer";
    public string Collation { get; set; } = "Latin1_General_CI_AS";
}

public sealed class SsmsSettings
{
    /// <summary>Tick SQL Server Management Studio's "Remember Password" when the project menu signs it in.</summary>
    public bool RememberPassword { get; set; }
}

public sealed class IisSettings
{
    /// <summary>The IIS Windows features the Environment page checks (and can enable).</summary>
    public List<IisFeatureSetting> RequiredFeatures { get; set; } =
    [
        new() { Name = "IIS-WebServerRole",        Label = "IIS Web Server" },
        new() { Name = "IIS-WebServer",            Label = "World Wide Web Services" },
        new() { Name = "IIS-ManagementConsole",    Label = "IIS Management Console" },
        new() { Name = "IIS-NetFxExtensibility",   Label = ".NET Extensibility 3.5" },
        new() { Name = "IIS-NetFxExtensibility45", Label = ".NET Extensibility 4.8" },
        new() { Name = "IIS-ASPNET",               Label = "ASP.NET 3.5" },
        new() { Name = "IIS-ASPNET45",             Label = "ASP.NET 4.8" },
        new() { Name = "IIS-ISAPIExtensions",      Label = "ISAPI Extensions" },
        new() { Name = "IIS-ISAPIFilter",          Label = "ISAPI Filters" },
        new() { Name = "IIS-DefaultDocument",      Label = "Default Document" },
        new() { Name = "IIS-DirectoryBrowsing",    Label = "Directory Browsing" },
        new() { Name = "IIS-HttpErrors",           Label = "HTTP Errors" },
        new() { Name = "IIS-StaticContent",        Label = "Static Content" },
        new() { Name = "IIS-BasicAuthentication",  Label = "Basic Authentication" },
        new() { Name = "IIS-RequestFiltering",     Label = "Request Filtering" },
        new() { Name = "IIS-HostableWebCore",      Label = "IIS Hostable Web Core" }
    ];
}

public sealed class AppearanceSettings
{
    public static readonly string[] Themes = ["system", "light", "dark"];

    /// <summary>"system" (follow the Windows app theme), "light" or "dark". Set by the sidebar's theme button.</summary>
    public string Theme { get; set; } = "system";
}

/// <summary>A value in <c>settings.json</c> that isn't allowed: <paramref name="Key"/> is its path, e.g. <c>projects.sitePort</c>.</summary>
public sealed record SettingsProblem(string Key, string Message)
{
    public override string ToString() => $"{Key} {Message}";
}
