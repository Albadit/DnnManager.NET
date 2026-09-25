namespace DnnManager.Application.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "DnnManager";

    public string BaseDirectory { get; set; } = @"C:\DNN";
    public int SitePort { get; set; } = 80;
    public string HostnameSuffix { get; set; } = "dnndev.me";
    /// <summary>"Light", "Dark" or "System" (follow the Windows app theme). Set by the sidebar's theme button.</summary>
    public string Theme { get; set; } = "System";
    public DockerOptions Docker { get; set; } = new();
    // NOTE: Intentionally empty. Microsoft.Extensions.Configuration *appends* to
    // collection defaults when binding, so any items listed here would be
    // duplicated by the matching entries in appsettings.json. Defaults live in
    // appsettings.json only.
    public IReadOnlyList<string> GitHubReleaseApis { get; set; } = Array.Empty<string>();
    public IReadOnlyList<IisFeatureSetting> RequiredIisFeatures { get; set; } = Array.Empty<IisFeatureSetting>();

    /// <summary>The host header a project's IIS site is bound to: <c>{project}.{HostnameSuffix}</c>.</summary>
    public string HostnameFor(string projectName) => $"{projectName}.{HostnameSuffix}";

    /// <summary>The URL a project's site answers on, including the port when it isn't 80.</summary>
    public string SiteUrlFor(string projectName) =>
        SitePort == 80 ? $"http://{HostnameFor(projectName)}" : $"http://{HostnameFor(projectName)}:{SitePort}";

    /// <summary>The conventional local database name for a project: <c>{project}{DefaultDbNameSuffix}</c>.</summary>
    public string DatabaseNameFor(string projectName) => projectName + Docker.DefaultDbNameSuffix;

    /// <summary>The SQL Server address (<c>ip,port</c>) of the shared container for a published port.</summary>
    public string ServerFor(int port) => $"{Docker.ContainerIp},{port}";
}

public sealed class DockerOptions
{
    public string ContainerName { get; set; } = "dnn-sqlserver";
    /// <summary>The container's static IP on the dnn_network bridge - keep in sync with docker-compose.yml.</summary>
    public string ContainerIp { get; set; } = "127.0.0.1";
    public string VolumeName { get; set; } = "dnn_sqlserver_data";
    public string SaPassword { get; set; } = "Admin@123";
    public int DefaultPort { get; set; } = 1433;
    public string Collation { get; set; } = "Latin1_General_CI_AS";
    public string MssqlPid { get; set; } = "Developer";
    public string DefaultDbNameSuffix { get; set; } = "_dnndev";
}

public sealed class IisFeatureSetting
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
