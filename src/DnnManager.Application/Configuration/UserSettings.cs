using DnnManager.Application.Abstractions;

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
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public ProjectSettings Projects { get; set; } = new();
    public SqlServerSettings SqlServer { get; set; } = new();
    public DockerSettings Docker { get; set; } = new();
    public SsmsSettings Ssms { get; set; } = new();
    public IisSettings Iis { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public WindowSettings Window { get; set; } = new();

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

        var dnn = Projects.DnnDefaults;
        Check(DnnDefaultsSettings.InstallModes.Contains(dnn.InstallMode, StringComparer.OrdinalIgnoreCase),
            "projects.dnnDefaults.installMode", $"must be one of: {string.Join(", ", DnnDefaultsSettings.InstallModes)}.");
        Check(DnnAccountRules.UserNameProblem(dnn.HostUsername) is null,
            "projects.dnnDefaults.hostUsername", DnnAccountRules.UserNameProblem(dnn.HostUsername) ?? "");
        Check(dnn.HostEmail.Length == 0 || DnnAccountRules.EmailProblem(dnn.HostEmail) is null,
            "projects.dnnDefaults.hostEmail", "must be an e-mail address, or empty for host@ plus the hostname suffix.");
        Check(dnn.WebsiteName.Length <= 128, "projects.dnnDefaults.websiteName", "can have at most 128 characters.");
        Check(DnnAccountRules.Languages.Contains(dnn.Language),
            "projects.dnnDefaults.language", $"must be one of: {string.Join(", ", DnnAccountRules.Languages)}.");
        Check(DnnAccountRules.Templates.Contains(dnn.Template),
            "projects.dnnDefaults.template", $"must be one of: {string.Join(", ", DnnAccountRules.Templates)}.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Projects.DatabaseProfiles)
        {
            Check(Has(profile.Id) && profile.Id != DatabaseProfileSettings.ContainerId && ids.Add(profile.Id),
                "projects.databaseProfiles", $"has a profile without an id of its own: '{profile.Name}'.");
            Check(Has(profile.Name), "projects.databaseProfiles", "has a profile without a name.");
            Check(DatabaseProfileSettings.Types.Contains(profile.Type, StringComparer.OrdinalIgnoreCase),
                "projects.databaseProfiles", $"'{profile.Name}' has type '{profile.Type}' - must be one of: {string.Join(", ", DatabaseProfileSettings.Types)}.");
            Check(Has(profile.Server), "projects.databaseProfiles", $"'{profile.Name}' has no server.");
            Check(DatabaseProfileSettings.Authentications.Contains(profile.Authentication, StringComparer.OrdinalIgnoreCase),
                "projects.databaseProfiles", $"'{profile.Name}' has authentication '{profile.Authentication}' - must be one of: {string.Join(", ", DatabaseProfileSettings.Authentications)}.");
            Check(!profile.Authentication.Equals("sql", StringComparison.OrdinalIgnoreCase) || Has(profile.UserName),
                "projects.databaseProfiles", $"'{profile.Name}' uses SQL Server authentication but has no user name.");
        }
        Check(Projects.DefaultDatabaseProfile == DatabaseProfileSettings.ContainerId || ids.Contains(Projects.DefaultDatabaseProfile),
            "projects.defaultDatabaseProfile", $"must be \"{DatabaseProfileSettings.ContainerId}\" or the id of a database profile.");

        Check(Has(SqlServer.Host), "sqlServer.host", "is required.");
        Check(IsPort(SqlServer.Port), "sqlServer.port", "must be a number between 1 and 65535.");
        Check(!string.IsNullOrEmpty(SqlServer.SaPassword), "sqlServer.saPassword", "is required.");
        Check(Has(Docker.ContainerName), "docker.containerName", "is required.");
        Check(Has(Docker.VolumeName), "docker.volumeName", "is required.");
        Check(Has(Docker.Edition), "docker.edition", "is required.");
        Check(Has(Docker.Collation), "docker.collation", "is required.");

        foreach (var feature in Iis.RequiredFeatures)
            Check(Has(feature?.Name), "iis.requiredFeatures", "has a feature without a name.");

        Check(AppearanceSettings.Themes.Contains(Appearance.Theme, StringComparer.OrdinalIgnoreCase),
            "appearance.theme", $"must be one of: {string.Join(", ", AppearanceSettings.Themes)}.");
        Check(Terminal.FontSize is >= TerminalSettings.MinFontSize and <= TerminalSettings.MaxFontSize, "terminal.fontSize",
            $"must be a number between {TerminalSettings.MinFontSize} and {TerminalSettings.MaxFontSize}.");
        return problems;
    }

    public AppOptions ToAppOptions() => new()
    {
        BaseDirectory = Projects.BaseDirectory,
        SitePort = Projects.SitePort,
        HostnameSuffix = Projects.HostnameSuffix.Trim().Trim('.'),
        GitHubReleaseApis = Projects.DnnReleaseSources.ToList(),
        KeepDnnPackages = Projects.KeepDnnPackages,
        DnnDefaults = Projects.DnnDefaults.Copy(),
        DatabaseProfiles = Projects.DatabaseProfiles.Select(p => p.Copy()).ToList(),
        DefaultDatabaseProfile = Projects.DefaultDatabaseProfile,
        Theme = Appearance.Theme,
        ProjectColumns = Appearance.ProjectColumns.ToList(),
        Terminal = new TerminalSettings
        {
            Enabled = Terminal.Enabled,
            DefaultShell = Terminal.DefaultShell,
            FontFamily = Terminal.FontFamily,
            FontSize = Terminal.FontSize
        },
        SsmsRememberPassword = Ssms.RememberPassword,
        SaveResourcesWhileMinimized = Window.SaveResourcesWhileMinimized,
        Docker = new DockerOptions
        {
            ContainerName = Docker.ContainerName,
            ContainerIp = SqlServer.Host,
            VolumeName = Docker.VolumeName,
            SaPassword = SqlServer.SaPassword,
            DefaultPort = SqlServer.Port,
            Collation = Docker.Collation,
            MssqlPid = Docker.Edition
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
    /// <summary>
    /// Keep each downloaded DNN install package in <c>Documents\DnnManager\packages</c> and use it again for the
    /// next project with that version, instead of downloading it again.
    /// </summary>
    public bool KeepDnnPackages { get; set; }

    /// <summary>What a new project's automatic DNN install uses unless changed for it (Settings → Projects → DNN defaults).</summary>
    public DnnDefaultsSettings DnnDefaults { get; set; } = new();

    /// <summary>Saved database connections offered for new projects. Their passwords are in the Windows Credential Manager.</summary>
    public List<DatabaseProfileSettings> DatabaseProfiles { get; set; } = [];

    /// <summary>The database a new project starts with: "container" (the local SQL container) or a profile's id.</summary>
    public string DefaultDatabaseProfile { get; set; } = DatabaseProfileSettings.ContainerId;
}

/// <summary>
/// The defaults of a new project's DNN install. The host password isn't here: it is kept in the Windows Credential
/// Manager - <see cref="DefaultHostPassword"/> while none is saved there.
/// </summary>
public sealed class DnnDefaultsSettings
{
    public static readonly string[] InstallModes = ["automatic", "manual"];

    /// <summary>The host password a new project starts with while none is saved in the Windows Credential Manager.</summary>
    public const string DefaultHostPassword = "Admin@123";

    /// <summary>"automatic" (DNN Manager installs DNN) or "manual" (DNN's installation wizard on the first visit).</summary>
    public string InstallMode { get; set; } = "automatic";
    public string HostUsername { get; set; } = "host";
    /// <summary>Empty: <c>host@</c> and the hostname suffix, e.g. host@dnndev.me.</summary>
    public string HostEmail { get; set; } = "admin@admin.com";
    /// <summary>Empty: the project's name.</summary>
    public string WebsiteName { get; set; } = "My Website";
    /// <summary>DNN's install culture, e.g. "en-US" - another one has DNN download its language pack while installing.</summary>
    public string Language { get; set; } = "en-US";
    /// <summary>"Default Website" or "Blank Website".</summary>
    public string Template { get; set; } = "Default Website";

    public bool Automatic => InstallMode.Equals("automatic", StringComparison.OrdinalIgnoreCase);

    public DnnDefaultsSettings Copy() => (DnnDefaultsSettings)MemberwiseClone();
}

/// <summary>A saved database connection for new projects. Its password, if any, is in the Windows Credential Manager.</summary>
public sealed class DatabaseProfileSettings
{
    /// <summary>The built-in profile: the local SQL Server container from the SQL Server and Docker settings.</summary>
    public const string ContainerId = "container";

    public static readonly string[] Types = ["sqlServer", "localDbFile"];
    public static readonly string[] Authentications = ["windows", "sql"];

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"sqlServer" (SQL Server or SQL Server Express) or "localDbFile" (LocalDB with the site's own database file).</summary>
    public string Type { get; set; } = "sqlServer";
    /// <summary>e.g. <c>.\SQLEXPRESS</c>, <c>localhost,1433</c> or <c>(LocalDB)\MSSQLLocalDB</c>.</summary>
    public string Server { get; set; } = "";
    /// <summary>"windows" or "sql".</summary>
    public string Authentication { get; set; } = "windows";
    /// <summary>The SQL Server login, for "sql" authentication.</summary>
    public string UserName { get; set; } = "";

    public DatabaseProfileSettings Copy() => (DatabaseProfileSettings)MemberwiseClone();
}

/// <summary>
/// The shared SQL Server DNN Manager connects to. The Docker container (see <see cref="DockerSettings"/>) publishes it
/// on <see cref="Port"/> with <see cref="SaPassword"/>.
/// </summary>
public sealed class SqlServerSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string SaPassword { get; set; } = "Admin@123";
}

/// <summary>The Docker container the Environment page sets up for the shared SQL Server.</summary>
public sealed class DockerSettings
{
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

    /// <summary>"system" (follow the Windows app theme), "light" or "dark". Set in Settings - General.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// The columns the Projects table starts with (and its Columns menu's "Default" goes back to) - what is looked at
    /// every day while working on DNN sites: which DNN version a site runs, its database and whether that is there,
    /// what its worker process costs, the process ID to attach a debugger to, and since when it runs (it starts
    /// again with every recycle and rebuild). The site's ID, address, ports, I/O, size and path are one click away,
    /// in the row's details and the Columns menu.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultProjectColumns = ["dnn", "database", "sql", "cpu", "memory", "pid", "lastStarted"];

    /// <summary>
    /// The optional columns the Projects table shows, set by its Columns button. Name, status and actions are always
    /// shown; keys this version doesn't know are ignored.
    /// </summary>
    public List<string> ProjectColumns { get; set; } = [.. DefaultProjectColumns];
}

/// <summary>The terminal at the bottom of the window. Set on the Settings page; applies at once.</summary>
public sealed class TerminalSettings
{
    public const int MinFontSize = 8, MaxFontSize = 32;

    /// <summary>Off: the panel only shows the activity log - no shells can be opened in it.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The shell the + button opens: "powershell", "pwsh", "cmd" or "gitbash" - the first installed one when this one isn't.</summary>
    public string DefaultShell { get; set; } = "powershell";
    /// <summary>The font of the terminal and the activity log; empty for the default (Cascadia Mono, or Consolas).</summary>
    public string FontFamily { get; set; } = "";
    public int FontSize { get; set; } = 13;
}

/// <summary>How DNN Manager's window behaves. Set on the Settings page (General); applies at once.</summary>
public sealed class WindowSettings
{
    /// <summary>
    /// While the window is minimized, stop what only it shows - animations, this PC's figures, redrawing terminals,
    /// following a log, folder-size walks - and, while nothing runs, let Windows run DNN Manager on its power-saving
    /// setting (EcoQoS). Restoring the window brings everything up to date at once. Off: everything goes on as while
    /// the window is shown.
    /// </summary>
    public bool SaveResourcesWhileMinimized { get; set; } = true;
}

/// <summary>A value in <c>settings.json</c> that isn't allowed: <paramref name="Key"/> is its path, e.g. <c>projects.sitePort</c>.</summary>
public sealed record SettingsProblem(string Key, string Message)
{
    public override string ToString() => $"{Key} {Message}";
}
