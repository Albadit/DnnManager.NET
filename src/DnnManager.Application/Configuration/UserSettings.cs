using System.Text.Json.Serialization;
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
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public ProjectSettings Projects { get; set; } = new();
    public SqlServerSettings SqlServer { get; set; } = new();
    public DockerSettings Docker { get; set; } = new();
    public SsmsSettings Ssms { get; set; } = new();
    public IisSettings Iis { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public TerminalSettings Terminal { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public KeyboardSettings Keyboard { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();

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
        static bool IsSystemFolder(string folder)
        {
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetPathRoot(full + Path.DirectorySeparatorChar)?.TrimEnd(Path.DirectorySeparatorChar) == full) return true;
            return new[]
                {
                    Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                    Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData
                }
                .Select(Environment.GetFolderPath)
                .Any(system => system.Length > 0 && full.Equals(system.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        }

        Check(Has(Projects.BaseDirectory) && Path.IsPathFullyQualified(Projects.BaseDirectory),
            "projects.baseDirectory", "must be a full path, e.g. C:\\DNN.");
        // A site in the projects folder is DNN Manager's: removing it deletes its folder. A drive or a system folder as
        // the projects folder would make every site on it one.
        Check(!Has(Projects.BaseDirectory) || !Path.IsPathFullyQualified(Projects.BaseDirectory) || !IsSystemFolder(Projects.BaseDirectory),
            "projects.baseDirectory", "can't be a drive or a system folder (Windows, Program Files, your user folder) - use a folder of its own, e.g. C:\\DNN.");
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

        var keepWarm = Projects.KeepWarm;
        Check(KeepWarmSettings.MinutesProblem(keepWarm.PingMinutes) is null,
            "projects.keepWarm.pingMinutes", KeepWarmSettings.MinutesProblem(keepWarm.PingMinutes) ?? "");
        Check(KeepWarmSettings.PathProblem(keepWarm.WarmUpPath) is null,
            "projects.keepWarm.warmUpPath", KeepWarmSettings.PathProblem(keepWarm.WarmUpPath) ?? "");
        Check(KeepWarmSettings.PathProblem(keepWarm.PingPath) is null,
            "projects.keepWarm.pingPath", KeepWarmSettings.PathProblem(keepWarm.PingPath) ?? "");

        Check(SqlServerSettings.Types.Contains(SqlServer.Type, StringComparer.OrdinalIgnoreCase),
            "sqlServer.type", $"must be one of: {string.Join(", ", SqlServerSettings.Types)}.");
        if (!SqlServer.IsContainer)
            Check(Has(SqlServer.Server), "sqlServer.server", "is required.");
        if (SqlServer.Type.Equals("sqlServer", StringComparison.OrdinalIgnoreCase))
        {
            Check(SqlServerSettings.Authentications.Contains(SqlServer.Authentication, StringComparer.OrdinalIgnoreCase),
                "sqlServer.authentication", $"must be one of: {string.Join(", ", SqlServerSettings.Authentications)}.");
            Check(!SqlServer.UsesSqlAuthentication || Has(SqlServer.UserName),
                "sqlServer.userName", "is required for SQL Server authentication.");
        }
        Check(Has(SqlServer.Host), "sqlServer.host", "is required.");
        Check(IsPort(SqlServer.Port), "sqlServer.port", "must be a number between 1 and 65535.");
        Check(!string.IsNullOrEmpty(SqlServer.SaPassword), "sqlServer.saPassword", "is required.");
        Check(Has(Docker.ContainerName), "docker.containerName", "is required.");
        Check(Has(Docker.VolumeName), "docker.volumeName", "is required.");
        Check(Has(Docker.Edition), "docker.edition", "is required.");
        // Goes into CREATE DATABASE … COLLATE as it is - a collation name is only letters, digits and _.
        Check(Has(Docker.Collation) && Docker.Collation.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
            "docker.collation", "must be a collation name such as Latin1_General_CI_AS (letters, digits and _).");

        foreach (var feature in Iis.RequiredFeatures)
            Check(Has(feature?.Name), "iis.requiredFeatures", "has a feature without a name.");

        Check(AppearanceSettings.Themes.Contains(Appearance.Theme, StringComparer.OrdinalIgnoreCase),
            "appearance.theme", $"must be one of: {string.Join(", ", AppearanceSettings.Themes)}.");
        Check(Appearance.UiScale is >= AppearanceSettings.MinUiScale and <= AppearanceSettings.MaxUiScale, "appearance.uiScale",
            $"must be a percentage between {AppearanceSettings.MinUiScale} and {AppearanceSettings.MaxUiScale}.");
        Check(Appearance.FontSize is >= AppearanceSettings.MinFontSize and <= AppearanceSettings.MaxFontSize, "appearance.fontSize",
            $"must be a number between {AppearanceSettings.MinFontSize} and {AppearanceSettings.MaxFontSize}.");
        Check(Terminal.FontSize is >= TerminalSettings.MinFontSize and <= TerminalSettings.MaxFontSize, "terminal.fontSize",
            $"must be a number between {TerminalSettings.MinFontSize} and {TerminalSettings.MaxFontSize}.");
        void OneOf(string value, string[] allowed, string key) =>
            Check(allowed.Contains(value, StringComparer.OrdinalIgnoreCase), key, $"must be one of: {string.Join(", ", allowed)}.");
        OneOf(Layout.SidebarPosition, LayoutSettings.SidebarPositions, "layout.sidebarPosition");
        OneOf(Layout.PanelAlignment, LayoutSettings.PanelAlignments, "layout.panelAlignment");
        OneOf(Layout.QuickInputPosition, LayoutSettings.QuickInputPositions, "layout.quickInputPosition");
        OneOf(Layout.Density, LayoutSettings.Densities, "layout.density");
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
        KeepWarm = new KeepWarmSettings
        {
            PingMinutes = Projects.KeepWarm.PingMinutes,
            WarmUpPath = KeepWarmSettings.NormalizePath(Projects.KeepWarm.WarmUpPath),
            PingPath = KeepWarmSettings.NormalizePath(Projects.KeepWarm.PingPath)
        },
        DatabaseServer = new DatabaseServerOptions
        {
            Type = SqlServer.Type,
            Server = SqlServer.Server.Trim(),
            Authentication = SqlServer.Authentication,
            UserName = SqlServer.UserName.Trim()
        },
        Theme = Appearance.Theme,
        ProjectColumns = Appearance.ProjectColumns.ToList(),
        KeyboardShortcuts = new Dictionary<string, string>(Keyboard.Shortcuts, StringComparer.Ordinal),
        Layout = Layout.Copy(),
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
            SqlUser = SqlServer.ContainerUserName,
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

    /// <summary>How a site switched to "keep warm" is kept warm, unless it has its own values (Settings → Projects → Keep warm).</summary>
    public KeepWarmSettings KeepWarm { get; set; } = new();
}

/// <summary>
/// Keep warm: while DNN Manager runs, a site switched on with the flame in its row is requested now and then, so IIS
/// doesn't shut its worker process down for being idle (after 20 minutes, by default) and the next page opens at once
/// instead of after DNN starting up again. A site can have its own values (its overview, IIS tab).
/// </summary>
public sealed class KeepWarmSettings
{
    public const int MinPingMinutes = 1, MaxPingMinutes = 60;

    /// <summary>The intervals the settings and a site's overview offer, in minutes.</summary>
    public static readonly int[] PingIntervals = [1, 2, 5, 10, 15, 20, 30, 60];

    public const string DefaultWarmUpPath = "/", DefaultPingPath = "/KeepAlive.aspx";

    /// <summary>
    /// Minutes between two requests at most - fewer when the site's app pool shuts down sooner than twice that, and
    /// none while the site is in use anyway (it served other requests meanwhile).
    /// </summary>
    public int PingMinutes { get; set; } = 5;

    /// <summary>
    /// The page requested when the site has no worker process (it was recycled or shut down): DNN starts up and the
    /// page is compiled, so the next visit is fast. Same-site redirects are followed.
    /// </summary>
    public string WarmUpPath { get; set; } = DefaultWarmUpPath;

    /// <summary>
    /// The page requested to keep a running site warm - DNN's <c>KeepAlive.aspx</c> is tiny and reads nothing from the
    /// database. <c>/</c> keeps the home page's caches warm too, as Azure's Always On does.
    /// </summary>
    public string PingPath { get; set; } = DefaultPingPath;

    public KeepWarmSettings Copy() => (KeepWarmSettings)MemberwiseClone();

    /// <summary>
    /// <paramref name="path"/> as it is requested: trimmed, starting with a <c>/</c>. Empty stays empty (no value).
    /// </summary>
    public static string NormalizePath(string? path)
    {
        var trimmed = (path ?? "").Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>
    /// What is wrong with <paramref name="path"/> as a page to request on the site; null when it can be requested. Never
    /// a page of DNN's installer: requesting it can install or upgrade DNN.
    /// </summary>
    public static string? PathProblem(string? path)
    {
        var p = NormalizePath(path);
        if (p.Length == 0) return "is required, e.g. /KeepAlive.aspx.";
        if (p.Length > 2000) return "can have at most 2000 characters.";
        if (p.StartsWith("//", StringComparison.Ordinal) || p.Contains("://", StringComparison.Ordinal))
            return "must be a page of the site, e.g. /KeepAlive.aspx - not an address.";
        if (p.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "can't contain spaces.";
        if (p.Contains('\\')) return "must use / between folders.";
        if (p.Contains('#')) return "can't contain #.";
        if (p.Split('?', 2)[0].Split('/').Any(segment => segment is "." or ".."))
            return "can't contain . or .. folders.";
        if (IsInstallerPath(p) || p.Contains("mode=", StringComparison.OrdinalIgnoreCase))
            return "can't be a page of DNN's installer - requesting it can install or upgrade DNN.";
        return null;
    }

    /// <summary>
    /// Whether <paramref name="pathAndQuery"/> is - or may be - a page of DNN's installer: a folder named Install anywhere
    /// in it, however it is written (dot segments, %-escapes, trailing dots or spaces Windows ignores), or something that
    /// can't be read as a path at all. Requesting the installer can install or upgrade DNN: such a page is never requested.
    /// </summary>
    public static bool IsInstallerPath(string pathAndQuery)
    {
        // Read as the path of one fixed address - not as a reference, where //Install/… would be a host named Install.
        if (!Uri.TryCreate("http://localhost" + (pathAndQuery.StartsWith('/') ? "" : "/") + pathAndQuery, UriKind.Absolute, out var uri))
            return true;
        var path = uri.AbsolutePath;
        // Escapes inside escapes too (%2549 is %49 is I) - IIS decodes until nothing changes.
        for (var i = 0; i < 4; i++)
        {
            var unescaped = Uri.UnescapeDataString(path);
            if (unescaped == path) break;
            path = unescaped;
        }
        return path.Replace('\\', '/').Split('/')
            .Any(segment => segment.TrimEnd('.', ' ').Equals("install", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What is wrong with <paramref name="minutes"/> as an interval; null when it is allowed.</summary>
    public static string? MinutesProblem(int minutes) =>
        minutes is >= MinPingMinutes and <= MaxPingMinutes ? null : $"must be between {MinPingMinutes} and {MaxPingMinutes} minutes.";
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

/// <summary>
/// The SQL Server new projects get their database on (Settings → Database server): the local Docker container, a SQL
/// Server / SQL Server Express instance, or a LocalDB file. <see cref="Host"/>, <see cref="Port"/> and
/// <see cref="SaPassword"/> are the container's - it publishes SQL Server there (see <see cref="DockerSettings"/>).
/// A project keeps the database it was made with when this changes.
/// </summary>
public sealed class SqlServerSettings
{
    public const string ContainerType = "container";
    public static readonly string[] Types = [ContainerType, "sqlServer", "localDbFile"];
    public static readonly string[] Authentications = ["windows", "sql"];

    /// <summary>
    /// "container" (the local SQL Server container, signed in to as <see cref="ContainerUserName"/>), "sqlServer" (SQL Server or SQL Server Express)
    /// or "localDbFile" (LocalDB with the site's own database file).
    /// </summary>
    public string Type { get; set; } = ContainerType;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    /// <summary>
    /// The password of <see cref="UserName"/> on the container - and the sa password Set up docker-compose creates
    /// the container with.
    /// </summary>
    public string SaPassword { get; set; } = "Admin@123";
    /// <summary>For "sqlServer" and "localDbFile": e.g. <c>.\SQLEXPRESS</c>, <c>localhost,1433</c> or <c>(LocalDB)\MSSQLLocalDB</c>.</summary>
    public string Server { get; set; } = @".\SQLEXPRESS";
    /// <summary>For "sqlServer": "windows" or "sql".</summary>
    public string Authentication { get; set; } = "windows";
    /// <summary>
    /// The login: for "container" the one DNN Manager signs in to the container with (sa when empty - another one has
    /// to exist on it already); for "sqlServer" with "sql" authentication the SQL Server login, whose password is in
    /// the Windows Credential Manager.
    /// </summary>
    public string UserName { get; set; } = "";

    /// <summary>The login on the container: <see cref="UserName"/>, or sa.</summary>
    [JsonIgnore] public string ContainerUserName => UserName.Trim() is { Length: > 0 } name ? name : "sa";

    [JsonIgnore] public bool IsContainer => Type.Equals(ContainerType, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool UsesSqlAuthentication => Authentication.Equals("sql", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The Docker container Settings → Docker container sets up for the shared SQL Server.</summary>
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
    /// <summary>The IIS Windows features Settings → IIS checks (and can enable).</summary>
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

    /// <summary>The UI scales Settings - General offers, in percent.</summary>
    public static readonly int[] UiScales = [80, 90, 100, 110, 125, 150, 175];
    public const int MinUiScale = 50, MaxUiScale = 200;

    /// <summary>The font sizes Settings - General offers, in pixels; 13 is the default.</summary>
    public static readonly int[] FontSizes = [11, 12, 13, 14, 15, 16, 17, 18];
    public const double MinFontSize = 8, MaxFontSize = 32;

    /// <summary>
    /// Everything in the window - text, icons and spacing - at this percentage, like a browser's zoom. Set in
    /// Settings - General; applies at once.
    /// </summary>
    public int UiScale { get; set; } = 100;

    /// <summary>
    /// The size of the app's text in pixels (13 by default); titles and hints keep their proportions to it. The
    /// terminal has its own (terminal.fontSize). Set in Settings - General; applies at once.
    /// </summary>
    public double FontSize { get; set; } = 13;

    /// <summary>
    /// The columns the Projects table starts with (and its Columns menu's "Default" goes back to) - what is looked at
    /// every day while working on DNN sites: the site's address (a click opens it), which DNN version it runs, its
    /// database and whether that is there, what its worker process costs, the process ID to attach a debugger to, and
    /// since when it runs (it starts again with every recycle and rebuild). The site's ID, ports, I/O, size and path
    /// are one click away, in the row's details and the Columns menu.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultProjectColumns = ["url", "dnn", "database", "sql", "cpu", "memory", "pid", "lastStarted"];

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

public sealed class KeyboardSettings
{
    /// <summary>
    /// The keyboard shortcuts changed from their defaults (Settings - Keyboard shortcuts), by command:
    /// <c>"project.start": "Ctrl+F5"</c>; an empty one takes the command's shortcut away. A command not listed has its default.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// How the window is laid out, as VS Code's Customize Layout sets it: where the sidebar is, how far the bottom panel
/// reaches, the status bar, where the command palette opens, how roomy the title bar and sidebar are. Changed from the
/// title bar's Customize Layout (or the gear's menu) - applied and saved at once. Whether the sidebar and the panel are
/// shown is the workspace's (state\window.json), as in VS Code.
/// </summary>
public sealed class LayoutSettings
{
    public static readonly string[] SidebarPositions = ["left", "right"];
    public static readonly string[] PanelAlignments = ["left", "right", "center", "justify"];
    public static readonly string[] QuickInputPositions = ["top", "center"];
    public static readonly string[] Densities = ["default", "compact"];

    /// <summary>"left" or "right" - the side of the window the sidebar is on.</summary>
    public string SidebarPosition { get; set; } = "left";
    /// <summary>
    /// How far the bottom panel reaches: "center" - under the page only, the sidebar beside it full height; "justify" -
    /// the window's width, under the sidebar too; "left" / "right" - to that edge of the window (under the sidebar when it
    /// is on that side).
    /// </summary>
    public string PanelAlignment { get; set; } = "center";
    /// <summary>The title bar's menu bar - for now the app's name beside its icon.</summary>
    public bool MenuBarVisible { get; set; } = true;
    public bool StatusBarVisible { get; set; } = true;
    /// <summary>"top" (over the title bar, as VS Code's) or "center" - where the command palette opens.</summary>
    public string QuickInputPosition { get; set; } = "top";
    /// <summary>"default" or "compact" - the frame smaller in width and height: sidebar, title bar, status bar.</summary>
    public string Density { get; set; } = "default";

    [JsonIgnore] public bool SidebarRight => SidebarPosition.Equals("right", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool Compact => Density.Equals("compact", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool QuickInputCentered => QuickInputPosition.Equals("center", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the panel reaches under the sidebar - with its alignment towards the sidebar's side, or justified.</summary>
    [JsonIgnore]
    public bool PanelUnderSidebar => PanelAlignment.ToLowerInvariant() switch
    {
        "justify" => true,
        "left" => !SidebarRight,
        "right" => SidebarRight,
        _ => false
    };

    public LayoutSettings Copy() => (LayoutSettings)MemberwiseClone();
}

/// <summary>A value in <c>settings.json</c> that isn't allowed: <paramref name="Key"/> is its path, e.g. <c>projects.sitePort</c>.</summary>
public sealed record SettingsProblem(string Key, string Message)
{
    public override string ToString() => $"{Key} {Message}";
}
