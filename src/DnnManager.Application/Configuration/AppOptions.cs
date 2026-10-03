using DnnManager.Application.Abstractions;

namespace DnnManager.Application.Configuration;

/// <summary>
/// The settings the running app works with, made from the user's <c>settings.json</c>
/// (<see cref="UserSettings.ToAppOptions"/>) at startup, with any <c>DNNMANAGER_DnnManager__*</c>
/// environment variables applied on top. There is one of it, shared by everything that reads settings: saving on
/// the Settings page puts the new values into it (<see cref="Apply"/>), so they are what the next thing done uses -
/// no restart. What holds on to something made from a setting (a folder being watched, a list on a page) follows
/// <see cref="Changed"/>.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "DnnManager";

    /// <summary>The settings were changed (<see cref="Apply"/>). Raised on the thread that applied them - the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Takes over every value of <paramref name="other"/> and tells so.</summary>
    public void Apply(AppOptions other)
    {
        BaseDirectory = other.BaseDirectory;
        SitePort = other.SitePort;
        HostnameSuffix = other.HostnameSuffix;
        Theme = other.Theme;
        SsmsRememberPassword = other.SsmsRememberPassword;
        KeepDnnPackages = other.KeepDnnPackages;
        DnnDefaults = other.DnnDefaults;
        // As a whole, like Docker below.
        KeepWarm = other.KeepWarm;
        DatabaseServer = other.DatabaseServer;
        // As a whole, so nobody reads half of the old container's settings and half of the new one's.
        Docker = other.Docker;
        GitHubReleaseApis = other.GitHubReleaseApis;
        RequiredIisFeatures = other.RequiredIisFeatures;
        ProjectColumns = other.ProjectColumns;
        KeyboardShortcuts = other.KeyboardShortcuts;
        Layout = other.Layout;
        Terminal = other.Terminal;
        SaveResourcesWhileMinimized = other.SaveResourcesWhileMinimized;
        Changed?.Invoke();
    }

    public string BaseDirectory { get; set; } = @"C:\DNN";
    public int SitePort { get; set; } = 80;
    public string HostnameSuffix { get; set; } = "dnndev.me";
    /// <summary>"light", "dark" or "system" (follow the Windows app theme). Set in Settings - General.</summary>
    public string Theme { get; set; } = "System";
    /// <summary>
    /// Tick SQL Server Management Studio's "Remember Password" when the project menu signs it in. Off by default:
    /// SSMS then keeps no copy of the password.
    /// </summary>
    public bool SsmsRememberPassword { get; set; }
    /// <summary>Keep downloaded DNN install packages in the user's packages folder and reuse them.</summary>
    public bool KeepDnnPackages { get; set; }
    /// <summary>What a new project's DNN install starts with; the host password is in the Credential Manager.</summary>
    public DnnDefaultsSettings DnnDefaults { get; set; } = new();
    /// <summary>How sites switched to "keep warm" are kept warm, unless a site has its own values (paths start with /).</summary>
    public KeepWarmSettings KeepWarm { get; set; } = new();
    /// <summary>Where a new project's database goes (Settings → Database server); the password is in the Credential Manager.</summary>
    public DatabaseServerOptions DatabaseServer { get; set; } = new();

    /// <summary>The host account's e-mail for a new project: the default's, or <c>host@</c> and the hostname suffix.</summary>
    public string DefaultHostEmail => DnnDefaults.HostEmail.Length > 0 ? DnnDefaults.HostEmail : $"host@{HostnameSuffix}";
    public DockerOptions Docker { get; set; } = new();
    // Empty here: the defaults live in UserSettings, which fills these in.
    public IReadOnlyList<string> GitHubReleaseApis { get; set; } = Array.Empty<string>();
    public IReadOnlyList<IisFeatureSetting> RequiredIisFeatures { get; set; } = Array.Empty<IisFeatureSetting>();
    /// <summary>The optional columns the Projects table shows at startup; its Columns button changes (and saves) them.</summary>
    public IReadOnlyList<string> ProjectColumns { get; set; } = Array.Empty<string>();
    /// <summary>The keyboard shortcuts changed from their defaults, by command id (empty: none); Settings - Keyboard shortcuts changes (and saves) them.</summary>
    public IReadOnlyDictionary<string, string> KeyboardShortcuts { get; set; } = new Dictionary<string, string>();
    /// <summary>How the window is laid out (Customize Layout changes and saves it while the app runs).</summary>
    public LayoutSettings Layout { get; set; } = new();
    /// <summary>The terminal's settings at startup; the Settings page changes (and saves) them while the app runs.</summary>
    public TerminalSettings Terminal { get; set; } = new();
    /// <summary>
    /// While the window is minimized, pause what only it shows and, while nothing runs, let Windows run the app on its
    /// power-saving setting (the presentation's EfficiencyMode). On by default; set in Settings - General.
    /// </summary>
    public bool SaveResourcesWhileMinimized { get; set; } = true;

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

    /// <summary>
    /// <paramref name="database"/> on the server from Settings → Database server, signed in to as that server is: sa for
    /// the container, the login with <paramref name="password"/> (from the Credential Manager) for SQL Server
    /// authentication, Windows otherwise. For a LocalDB file the database is always the site's own file.
    /// </summary>
    public DatabaseConnection DatabaseOnServer(string database, string password = "")
    {
        var server = DatabaseServer;
        if (server.IsContainer)
            return new DatabaseConnection(DatabaseKind.Container, ServerFor(Docker.DefaultPort), database, SqlAuthentication.Sql, Docker.SqlUser, Docker.SaPassword);
        if (server.IsLocalDbFile)
            return new DatabaseConnection(DatabaseKind.LocalDbFile, server.Server, DatabaseConnection.LocalDbFileName, SqlAuthentication.Windows);
        return server.UsesSqlAuthentication
            ? new DatabaseConnection(DatabaseKind.SqlServer, server.Server, database, SqlAuthentication.Sql, server.UserName, password)
            : new DatabaseConnection(DatabaseKind.SqlServer, server.Server, database, SqlAuthentication.Windows);
    }
}

public sealed class DockerOptions
{
    public string ContainerName { get; set; } = "dnn-sqlserver";
    /// <summary>The host DNN Manager connects to SQL Server on - this machine (localhost) for the Docker container's published port.</summary>
    public string ContainerIp { get; set; } = "localhost";
    public string VolumeName { get; set; } = "dnn_sqlserver_data";
    /// <summary>The login DNN Manager signs in to the container with (Settings → Database server → User) - sa by default.</summary>
    public string SqlUser { get; set; } = "sa";
    /// <summary>The password of <see cref="SqlUser"/>, and the sa password the container is created with.</summary>
    public string SaPassword { get; set; } = "Admin@123";
    public int DefaultPort { get; set; } = 1433;
    public string Collation { get; set; } = "Latin1_General_CI_AS";
    public string MssqlPid { get; set; } = "Developer";
}

/// <summary>The kind of SQL Server new projects get their database on - see <see cref="SqlServerSettings"/>.</summary>
public sealed class DatabaseServerOptions
{
    /// <summary>"container", "sqlServer" or "localDbFile".</summary>
    public string Type { get; set; } = SqlServerSettings.ContainerType;
    /// <summary>The instance for "sqlServer" and "localDbFile", e.g. <c>.\SQLEXPRESS</c>.</summary>
    public string Server { get; set; } = "";
    /// <summary>"windows" or "sql", for "sqlServer".</summary>
    public string Authentication { get; set; } = "windows";
    public string UserName { get; set; } = "";

    public bool IsContainer => Type.Equals(SqlServerSettings.ContainerType, StringComparison.OrdinalIgnoreCase);
    public bool IsLocalDbFile => Type.Equals("localDbFile", StringComparison.OrdinalIgnoreCase);
    public bool UsesSqlAuthentication => !IsContainer && !IsLocalDbFile && Authentication.Equals("sql", StringComparison.OrdinalIgnoreCase);
}

public sealed class IisFeatureSetting
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
