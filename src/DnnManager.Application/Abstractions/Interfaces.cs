using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Reports progress / status from use cases back to the presentation layer.</summary>
public interface IProgressReporter
{
    void Step(string title);
    void Info(string message);
    void Success(string message);
    void Fail(string message);
    /// <summary>Something the user must act on later, without failing the operation.</summary>
    void Warn(string message);
    /// <summary>Updates a single status line in place (e.g. a running download percentage).</summary>
    void Progress(string message);
}

public interface IUserPrompt
{
    Task<bool> ConfirmAsync(string question, bool defaultYes = false, CancellationToken ct = default);
}

public interface IProjectRepository
{
    IReadOnlyList<string> ListAllProjectDirectories();
    DnnProject Build(string projectName);
    bool ProjectExists(string projectName);
}

public interface IDnnReleaseService
{
    Task<Result<DnnRelease>> GetReleaseAsync(string apiUrl, string? version, CancellationToken ct);
    IReadOnlyList<string> KnownReleaseApis { get; }
}

public interface IDnnPackageInstaller
{
    Task<Result> DownloadAndExtractAsync(DnnRelease release, string projectDirectory, IProgressReporter reporter, CancellationToken ct);
}

public interface IIisManager
{
    Result CreateSite(string siteName, string physicalPath, string hostname, int port);
    Result RemoveSite(string siteName);
    Result StartSite(string siteName);

    /// <summary>True when IIS is installed and its configuration is reachable on this machine.
    /// Lets setup skip website creation gracefully instead of failing when IIS is absent.</summary>
    bool IsAvailable();

    /// <summary>Restarts all IIS services (<c>iisreset /restart</c>).</summary>
    Task<Result> ResetAsync(CancellationToken ct);

    /// <summary>
    /// One-shot snapshot of every IIS site: name -> state. Loading applicationHost.config is what a
    /// <c>ServerManager</c> actually costs, so callers that need the status of many sites take one
    /// snapshot rather than querying site by site. Empty when IIS is unavailable.
    /// </summary>
    IReadOnlyDictionary<string, string> GetSiteStates();

    /// <summary>A site's details for the project details view, or null when there is no such site.</summary>
    IisSiteInfo? GetSiteInfo(string siteName);

    Result GrantPermissions(string path, IEnumerable<string> identities);

    /// <summary>
    /// Deletes the Windows user profile auto-created for the app pool's virtual identity
    /// (<c>IIS APPPOOL\&lt;poolName&gt;</c>) - i.e. the leftover <c>C:\Users\&lt;poolName&gt;</c> folder and
    /// its ProfileList registry entry. Matched strictly by the deterministic app-pool SID, so it
    /// only ever removes this pool's profile. Best-effort and idempotent: a no-op (still Ok) when no
    /// such profile exists. Call only after the pool's worker has exited (see <see cref="RemoveSite"/>).
    /// </summary>
    Task<Result> RemoveAppPoolProfileAsync(string poolName, CancellationToken ct);
}

/// <param name="Bindings">e.g. <c>http://site.dnndev.me:80</c>.</param>
/// <param name="ClrVersion">The app pool's .NET CLR version, or "No Managed Code".</param>
public sealed record IisSiteInfo(
    string State,
    string PhysicalPath,
    IReadOnlyList<string> Bindings,
    string AppPool,
    string? AppPoolState,
    string? ClrVersion,
    string? PipelineMode,
    string? Identity);

/// <param name="DesktopInstalled">Docker Desktop (or at least the docker CLI) is on this PC.</param>
/// <param name="ContainerState">Docker's state of the container ("running", "exited"…); null when it doesn't exist or the engine is down.</param>
/// <param name="ContainerStatus">Docker's description, e.g. "Up 2 hours (healthy)".</param>
public sealed record DockerStatus(bool DesktopInstalled, string? ClientVersion, bool EngineRunning, string? EngineVersion,
    string? ContainerState, string? ContainerStatus);

public interface IPrerequisiteChecker
{
    Task<Result> EnsureIisFeaturesAsync(IProgressReporter reporter, IUserPrompt prompt, CancellationToken ct);

    /// <summary>Which of the required IIS features are enabled: feature name -> enabled.</summary>
    Task<IReadOnlyDictionary<string, bool>> GetIisFeatureStatesAsync(CancellationToken ct);

    /// <summary>Docker Desktop, its engine, and the state of the container <paramref name="containerName"/>.</summary>
    Task<DockerStatus> GetDockerStatusAsync(string containerName, CancellationToken ct);

    /// <summary>Installs Docker Desktop with winget.</summary>
    Task<Result> InstallDockerDesktopAsync(IProgressReporter reporter, CancellationToken ct);

    /// <summary>Starts Docker Desktop as the signed-in user (not elevated, like DNN Manager itself).</summary>
    Result StartDockerDesktop();
}

/// <summary>The shared SQL Server's <c>docker-compose.yml</c> next to the app, and bringing it up.</summary>
public interface IDockerComposeService
{
    string ComposeFilePath { get; }

    /// <summary>The compose file for these SQL Server settings.</summary>
    string Render(DockerOptions docker);

    /// <summary>The compose file as it is on disk, or null when there is none.</summary>
    string? ReadCurrent();

    /// <summary>Writes <paramref name="yaml"/> as the compose file and runs <c>docker compose up -d</c> with it.</summary>
    Task<Result> UpAsync(string yaml, IProgressReporter reporter, CancellationToken ct);
}

public interface ISqlServerService
{
    Task<Result<bool>> DatabaseExistsAsync(string database, CancellationToken ct);
    Task<Result> CreateDatabaseAsync(DatabaseConfig db, CancellationToken ct);
    Task<Result> DropDatabaseAsync(string database, CancellationToken ct);
    Task<Result<string>> BackupDatabaseLocalAsync(string database, string backupFileName, CancellationToken ct);
    Task<Result> RestoreDatabaseLocalAsync(DatabaseConfig db, string backupFilePath, CancellationToken ct);

    /// <summary>
    /// Rewrites every PortalAlias whose HTTPAlias ends with <paramref name="hostnameSuffix"/> so that
    /// portal 0's primary alias becomes <paramref name="newHostname"/>. Used after cloning so the new
    /// site responds at its own host header instead of the source's.
    /// </summary>
    Task<Result> RemapPortalAliasesAsync(string database, string hostnameSuffix, string newHostname, CancellationToken ct);
}

public interface IHttpConnectivityChecker
{
    Task<Result<int>> CheckAsync(string url, int timeoutSeconds, CancellationToken ct);
}

public interface IProjectFileCopier
{
    /// <summary>Copies a DNN project's website files from <paramref name="sourceDirectory"/> into <paramref name="destinationDirectory"/>.</summary>
    Task<Result> CopyAsync(string sourceDirectory, string destinationDirectory, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Extracts a zipped DNN site into <paramref name="destinationDirectory"/>. The site root is the zip's
    /// shallowest folder holding a <c>web.config</c>, so a zip with everything under one top folder works too.
    /// </summary>
    Task<Result> ExtractZipAsync(string zipPath, string destinationDirectory, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Zips every file under <paramref name="sourceDirectory"/> into <paramref name="zipPath"/>, leaving out the
    /// top-level folders named in <paramref name="excludedFolders"/>. Files that can't be read are skipped and reported.
    /// </summary>
    Task<Result> CreateZipAsync(string sourceDirectory, string zipPath, IReadOnlyCollection<string> excludedFolders,
        IProgressReporter reporter, CancellationToken ct);
}

/// <param name="Name">A readable name, e.g. "Visual Studio Code".</param>
/// <param name="Reason">Why it blocks the folder, e.g. "has files open" or "working folder is inside it".</param>
/// <param name="CanClose">False for Windows itself, services and DNN Manager - those are never closed.</param>
public sealed record LockingProcess(int Id, string Name, string ExeName, string Reason, bool CanClose)
{
    public override string ToString() => $"{Name} ({ExeName}, pid {Id}) - {Reason}";
}

/// <summary>Finds and closes the programs that keep a folder from being deleted.</summary>
public interface IFileLockService
{
    /// <summary>
    /// Processes with files under <paramref name="directory"/> open, or with their working folder inside it.
    /// A helper process is reported as the app that owns it (e.g. a VS Code helper as VS Code).
    /// </summary>
    IReadOnlyList<LockingProcess> FindLockers(string directory);

    /// <summary>
    /// Asks each process to close, then force-closes it (with its child processes) if it doesn't within a few
    /// seconds. Returns the ones that are still running.
    /// </summary>
    Task<IReadOnlyList<LockingProcess>> CloseAsync(IReadOnlyList<LockingProcess> processes, CancellationToken ct);

    /// <summary>Has Windows delete whatever is left of <paramref name="directory"/> at the next restart.</summary>
    Result ScheduleDeleteOnRestart(string directory);
}

/// <summary>Lays down supporting source-control files in a managed DNN project directory.</summary>
public interface IProjectScaffolder
{
    /// <summary>
    /// Writes a DNN-tuned <c>.gitignore</c> into <paramref name="projectDirectory"/> when one is not
    /// already present, so a freshly set-up or cloned site is ready to commit without dragging in
    /// build output, runtime data, caches, logs or portal uploads. A no-op (still Ok) when the
    /// project already has a <c>.gitignore</c>, so an existing site's file is never clobbered.
    /// </summary>
    Result EnsureGitignore(string projectDirectory);
}

/// <param name="DnnVersion">The newest row of DNN's Version table, e.g. "9.13.9"; null when it isn't a DNN database.</param>
public sealed record DatabaseFacts(double SizeMb, string? DnnVersion, int? Portals, IReadOnlyList<string> PortalAliases);

/// <param name="Debug">&lt;compilation debug&gt;; null when not set.</param>
/// <param name="DisabledHttpsRules">HTTPS redirect rules DNN Manager switched off for local development.</param>
public sealed record WebConfigFacts(bool? Debug, string? TargetFramework, string? CustomErrors, IReadOnlyList<string> DisabledHttpsRules);

public sealed record SiteSqlConnection(string Server, string Database, string User, string Password);

public interface ISqlConnectionTester
{
    /// <summary>
    /// Logs in to <paramref name="connection"/>'s database (not [master] - contained users only exist in
    /// their own database) and describes what it reached, e.g. "[db] on Azure SQL Database 12.0.2000.8".
    /// </summary>
    Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct, int timeoutSeconds = 15);

    /// <summary>
    /// Size of <paramref name="database"/>'s database and, when it holds a DNN site, the DNN version recorded in it,
    /// its portals and their aliases.
    /// </summary>
    Task<Result<DatabaseFacts>> DescribeDatabaseAsync(SiteSqlConnection database, CancellationToken ct, int timeoutSeconds = 15);

    /// <summary>The names of all databases on <paramref name="server"/>'s SQL Server.</summary>
    Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15);
}

/// <param name="SwitchedOff">Rules switched off just now.</param>
/// <param name="AlreadyOff">Rules DNN Manager had switched off before.</param>
public sealed record HttpsRedirectRules(IReadOnlyList<string> SwitchedOff, IReadOnlyList<string> AlreadyOff)
{
    public static readonly HttpsRedirectRules None = new(Array.Empty<string>(), Array.Empty<string>());
    public IReadOnlyList<string> All => SwitchedOff.Concat(AlreadyOff).ToList();
}

public interface IWebConfigService
{
    /// <summary>Reads the SiteSqlServer connection string from the project's web.config.</summary>
    Result<SiteSqlConnection> ReadSiteSqlServer(string webConfigPath);

    /// <summary>
    /// Rewrites both connectionStrings/add[@name='SiteSqlServer'] and
    /// appSettings/add[@key='SiteSqlServer'] to point at a new database.
    /// </summary>
    Result WriteSiteSqlServer(string webConfigPath, SiteSqlConnection newConnection);

    /// <summary>
    /// Removes the IIS URL Rewrite section (system.webServer/rewrite). Those rules are
    /// production-only (HTTPS redirects, request blocking) and require the URL Rewrite module,
    /// which is usually absent locally - otherwise IIS returns HTTP 500.19. Safe no-op if absent.
    /// </summary>
    Result RemoveRewriteRules(string webConfigPath);

    /// <summary>
    /// Switches off (<c>enabled="false"</c>, with a comment) every enabled URL Rewrite rule that
    /// redirects to an <c>https://</c> address. A local site has no HTTPS binding, so such a rule sends
    /// every request to an address that doesn't answer. Also reports rules switched off by an earlier
    /// run (recognised by that comment), since those still have to go back on for production.
    /// Both lists are empty when there are none (or no web.config).
    /// </summary>
    Result<HttpsRedirectRules> DisableHttpsRedirectRules(string webConfigPath);

    /// <summary>A few settings worth knowing about a site's web.config, for the project details view.</summary>
    Result<WebConfigFacts> ReadFacts(string webConfigPath);
}

/// <summary>
/// Exports a (possibly Azure) SQL database to a <c>.bacpac</c> and imports it into a local SQL Server,
/// using Microsoft's SqlPackage tool. Used to clone Azure SQL sources, which do not support BACKUP DATABASE.
/// </summary>
public interface IBacpacService
{
    /// <summary>
    /// Ensures SqlPackage is available, installing it as a .NET global tool on demand the first time
    /// (requires the .NET SDK and <c>dotnet</c> on PATH). A no-op when SqlPackage is already present.
    /// Returns a failed <see cref="Result"/> with a manual-install hint when it cannot be provisioned.
    /// </summary>
    Task<Result> EnsureAvailableAsync(IProgressReporter reporter, CancellationToken ct);

    /// <summary>Exports <paramref name="source"/> to <paramref name="bacpacPath"/> on this host.</summary>
    Task<Result> ExportAsync(SiteSqlConnection source, string bacpacPath, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Imports a <c>.bacpac</c> into a SQL Server, creating <paramref name="databaseName"/>.
    /// SqlPackage always creates a fresh database and fails if one already exists.
    /// </summary>
    Task<Result> ImportAsync(string targetServer, string saUser, string saPassword,
        string databaseName, string bacpacPath, IProgressReporter reporter, CancellationToken ct);
}

public interface IRemoteSqlBackupService
{
    /// <summary>
    /// Issues BACKUP DATABASE against the given (possibly external) SQL Server using SQL auth.
    /// Returns the host-side path of the resulting <c>.bak</c> when it can be read by this process.
    /// The caller supplies <paramref name="backupServerPath"/> \u2014 a path the SQL Server service can write to.
    /// For sources whose Data Source resolves to this machine that's a normal local path; for remote
    /// servers it must be a UNC share readable from here.
    /// </summary>
    Task<Result<string>> BackupAsync(SiteSqlConnection source, string backupServerPath, IProgressReporter reporter, CancellationToken ct);
}
