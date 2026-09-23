using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Reports progress / status from use cases back to the presentation layer.</summary>
public interface IProgressReporter
{
    void Step(string title);
    void Info(string message);
    void Success(string message);
    void Fail(string message);
    /// <summary>Updates a single status line in place (e.g. a running download percentage).</summary>
    void Progress(string message);
}

public interface IUserPrompt
{
    Task<bool> ConfirmAsync(string question, bool defaultYes = false, CancellationToken ct = default);

    /// <summary>Prompts for a line of text. Returns null if the user cancels (Esc) or enters nothing.</summary>
    Task<string?> PromptTextAsync(string question, CancellationToken ct = default);
}

public interface IProjectRepository
{
    IReadOnlyList<string> ListConfiguredProjects();
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

    /// <summary>
    /// One-shot snapshot of every IIS site: name -> state. Loading applicationHost.config is what a
    /// <c>ServerManager</c> actually costs, so callers that need the status of many sites take one
    /// snapshot rather than querying site by site. Empty when IIS is unavailable.
    /// </summary>
    IReadOnlyDictionary<string, string> GetSiteStates();

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

public interface IPrerequisiteChecker
{
    Task<Result> CheckDockerAsync(IProgressReporter reporter, CancellationToken ct);
    Task<Result> EnsureIisFeaturesAsync(IProgressReporter reporter, IUserPrompt prompt, CancellationToken ct);
}

public interface IDockerService
{
    Task<bool> IsContainerRunningAsync(string containerName, CancellationToken ct);
    Task<bool> DoesContainerExistAsync(string containerName, CancellationToken ct);

    /// <summary>
    /// The container's state as Docker reports it (<c>running</c>, <c>exited</c>, <c>created</c>…), or
    /// null when no such container exists. One CLI call answers both "exists?" and "running?".
    /// </summary>
    Task<string?> GetContainerStateAsync(string containerName, CancellationToken ct);

    Task<int?> GetPublishedPortAsync(string containerName, CancellationToken ct);
    Task<Result> StartContainerAsync(string containerName, CancellationToken ct);

    /// <summary>Brings up the shared SQL container from the docker-compose.yml shipped next to the app.</summary>
    Task<Result> ComposeUpAsync(CancellationToken ct);

    /// <summary>Tears down the shared SQL container (optionally removing its volume).</summary>
    Task<Result> ComposeDownAsync(bool removeVolumes, CancellationToken ct);

    Task<Result<string>> ExecAsync(string containerName, IReadOnlyList<string> args, CancellationToken ct);
}

public interface ISqlServerService
{
    Task<Result> WaitReadyAsync(int timeoutSeconds, IProgressReporter reporter, CancellationToken ct);
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

public enum CloneSourceKind { LocalFolder, Ftp }

public sealed record CloneSource(
    CloneSourceKind Kind,
    string? LocalPath,
    string? FtpHost,
    int FtpPort,
    string? FtpUser,
    string? FtpPassword,
    string? FtpRemotePath);

public interface IProjectFileCopier
{
    /// <summary>Copies a DNN project's website files from the given source into <paramref name="destinationDirectory"/>.</summary>
    Task<Result> CopyAsync(CloneSource source, string destinationDirectory, IProgressReporter reporter, CancellationToken ct);
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

public interface IFtpBrowser
{
    /// <summary>Connects to the FTP server and returns immediate subdirectories of <paramref name="remotePath"/>.</summary>
    Task<Result<IReadOnlyList<string>>> ListDirectoriesAsync(
        string host, int port, string user, string password, string remotePath, CancellationToken ct);
}

public sealed record FtpProfile(string Name, string Host, int Port, string User, string EncryptedPassword, string RemotePath = "/");

/// <summary>Stores a single FTP connection per project (keyed by project name).</summary>
public interface IFtpProfileStore
{
    FtpProfile? Get(string project);
    void Save(string project, FtpProfile profile);
    /// <summary>Project names that have a saved FTP connection.</summary>
    IReadOnlyList<string> ListProjects();
    string Protect(string plain);
    string Unprotect(string encrypted);
}

public sealed record SqlProfile(string Name, string Server, string Database, string User, string EncryptedPassword);

/// <summary>Stores a single SQL connection per project (keyed by project name).</summary>
public interface ISqlProfileStore
{
    SqlProfile? Get(string project);
    void Save(string project, SqlProfile profile);
    /// <summary>Project names that have a saved SQL connection.</summary>
    IReadOnlyList<string> ListProjects();
    string Protect(string plain);
    string Unprotect(string encrypted);
}

public sealed record SiteSqlConnection(string Server, string Database, string User, string Password);

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
}

/// <summary>
/// Exports a (possibly Azure) SQL database to a <c>.bacpac</c> and imports it into a local SQL Server,
/// using Microsoft's SqlPackage tool. Used to clone Azure SQL sources, which do not support BACKUP DATABASE.
/// </summary>
public interface IBacpacService
{
    /// <summary>True when the SqlPackage tool can be located on this machine.</summary>
    bool IsAvailable();

    /// <summary>
    /// Ensures SqlPackage is available, installing it as a .NET global tool on demand the first time
    /// (requires the .NET SDK and <c>dotnet</c> on PATH). A no-op when SqlPackage is already present.
    /// Returns a failed <see cref="Result"/> with a manual-install hint when it cannot be provisioned.
    /// </summary>
    Task<Result> EnsureAvailableAsync(IProgressReporter reporter, CancellationToken ct);

    /// <summary>Install hint shown when SqlPackage is missing.</summary>
    string InstallHint { get; }

    /// <summary>Exports <paramref name="source"/> to <paramref name="bacpacPath"/> on this host.</summary>
    Task<Result> ExportAsync(SiteSqlConnection source, string bacpacPath, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Imports a <c>.bacpac</c> into a SQL Server (local or remote/Azure), creating <paramref name="databaseName"/>.
    /// SqlPackage always creates a fresh database and fails if one already exists. Optional
    /// <paramref name="properties"/> are passed through as SqlPackage <c>/p:Key=Value</c> arguments
    /// (e.g. DatabaseEdition / DatabaseServiceObjective to control the created Azure SQL tier).
    /// </summary>
    Task<Result> ImportAsync(string targetServer, string saUser, string saPassword,
        string databaseName, string bacpacPath, IProgressReporter reporter, CancellationToken ct,
        IReadOnlyDictionary<string, string>? properties = null);
}

public sealed record RemoteDbInfo(bool IsAzure, bool Exists, string? Edition, string? ServiceObjective);

/// <summary>
/// Administrative operations against a remote (possibly Azure) SQL Server, used when overwriting a
/// production database: inspecting the target and dropping the existing database before a BACPAC import.
/// </summary>
public interface IRemoteSqlAdminService
{
    /// <summary>
    /// Connects to <paramref name="target"/> via [master] and reports whether it is Azure SQL Database,
    /// whether the named database exists, and - for Azure - its current edition and service
    /// objective so a recreated database can keep the same tier.
    /// </summary>
    Task<Result<RemoteDbInfo>> InspectAsync(SiteSqlConnection target, CancellationToken ct);

    /// <summary>Drops <paramref name="target"/>'s database if it exists.</summary>
    Task<Result> DropDatabaseAsync(SiteSqlConnection target, bool isAzure, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Renames a database on <paramref name="target"/>'s server from <paramref name="fromName"/> to
    /// <paramref name="toName"/> (<c>ALTER DATABASE … MODIFY NAME</c>). Used to swap a freshly imported
    /// copy into place after the old database has been dropped.
    /// </summary>
    Task<Result> RenameDatabaseAsync(SiteSqlConnection target, string fromName, string toName, IProgressReporter reporter, CancellationToken ct);
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
