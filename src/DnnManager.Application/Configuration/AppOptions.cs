namespace DnnManager.Application.Configuration;

/// <summary>
/// The settings the running app works with, made from the user's <c>settings.json</c>
/// (<see cref="UserSettings.ToAppOptions"/>) at startup, with any <c>DNNMANAGER_DnnManager__*</c>
/// environment variables applied on top. Read once: saved changes apply after a restart.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "DnnManager";

    public string BaseDirectory { get; set; } = @"C:\DNN";
    public int SitePort { get; set; } = 80;
    public string HostnameSuffix { get; set; } = "dnndev.me";
    /// <summary>"light", "dark" or "system" (follow the Windows app theme). Set by the sidebar's theme button.</summary>
    public string Theme { get; set; } = "System";
    /// <summary>
    /// Tick SQL Server Management Studio's "Remember Password" when the project menu signs it in. Off by default:
    /// SSMS then keeps no copy of the password.
    /// </summary>
    public bool SsmsRememberPassword { get; set; }
    /// <summary>Keep downloaded DNN install packages in the user's packages folder and reuse them.</summary>
    public bool KeepDnnPackages { get; set; }
    public DockerOptions Docker { get; set; } = new();
    // Empty here: the defaults live in UserSettings, which fills these in.
    public IReadOnlyList<string> GitHubReleaseApis { get; set; } = Array.Empty<string>();
    public IReadOnlyList<IisFeatureSetting> RequiredIisFeatures { get; set; } = Array.Empty<IisFeatureSetting>();

    /// <summary>The host header a project's IIS site is bound to: <c>{project}.{HostnameSuffix}</c>.</summary>
    public string HostnameFor(string projectName) => $"{projectName}.{HostnameSuffix}";

    /// <summary>The URL a project's site answers on, including the port when it isn't 80.</summary>
    public string SiteUrlFor(string projectName) =>
        SitePort == 80 ? $"http://{HostnameFor(projectName)}" : $"http://{HostnameFor(projectName)}:{SitePort}";

    /// <summary>
    /// The local database a new project gets: named like the project (project <c>ceesboer</c> has database
    /// <c>ceesboer</c>). An existing site keeps the database its web.config names.
    /// </summary>
    public string DatabaseNameFor(string projectName) => projectName;

    /// <summary>The SQL Server address (<c>ip,port</c>) of the shared container for a published port.</summary>
    public string ServerFor(int port) => $"{Docker.ContainerIp},{port}";
}

public sealed class DockerOptions
{
    public string ContainerName { get; set; } = "dnn-sqlserver";
    /// <summary>The host DNN Manager connects to SQL Server on - this machine (localhost) for the Docker container's published port.</summary>
    public string ContainerIp { get; set; } = "localhost";
    public string VolumeName { get; set; } = "dnn_sqlserver_data";
    public string SaPassword { get; set; } = "Admin@123";
    public int DefaultPort { get; set; } = 1433;
    public string Collation { get; set; } = "Latin1_General_CI_AS";
    public string MssqlPid { get; set; } = "Developer";
}

public sealed class IisFeatureSetting
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
