using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Creates a project's IIS website. Shared by setup, clone and hosting an existing folder so all three
/// bind, permission and start the site the same way.
/// </summary>
public sealed class IisSiteProvisioner(IOptions<AppOptions> opts, IIisManager iis, OperationUndo undo)
{
    private readonly AppOptions _opts = opts.Value;
    private readonly IIisManager _iis = iis;
    private readonly OperationUndo _undo = undo;

    /// <summary>
    /// Creates (or recreates) the site and its app pool, grants the IIS identities access to the project
    /// folder and starts the site. Failures are reported and return false - a missing website is never
    /// fatal to the calling flow.
    /// </summary>
    public bool TryCreateSite(DnnProject project, IProgressReporter reporter) =>
        TryCreateSite(project, _opts.HostnameFor(project.Name), _opts.SitePort, reporter);

    /// <summary>
    /// The folder of the IIS site named <paramref name="siteName"/> when that site serves another folder than
    /// <paramref name="directory"/> - a site this project must not take over, since making the project's site would
    /// replace it. Null when there is no such site, or it serves that folder.
    /// </summary>
    public string? SiteServingAnotherFolder(string siteName, string directory)
    {
        var sites = _iis.GetSiteRuntimes();
        if (sites is null) return null;
        var site = sites.FirstOrDefault(s => s.Key.Equals(siteName, StringComparison.OrdinalIgnoreCase)).Value;
        if (site is null) return null;
        return SameFolder(site.PhysicalPath, directory) ? null : site.PhysicalPath;
    }

    /// <summary>Why <paramref name="siteName"/> can't be this project's site, for a message; null when it can.</summary>
    public string? SiteNameTaken(string siteName, string directory) =>
        SiteServingAnotherFolder(siteName, directory) is { } other
            ? $"IIS already has a site named '{siteName}', serving {(other.Length > 0 ? other : "another folder")} - choose another name."
            : null;

    private static bool SameFolder(string a, string b)
    {
        static string Normal(string path)
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path.TrimEnd('\\', '/'); }
        }
        return a.Length > 0 && b.Length > 0 && Normal(a).Equals(Normal(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same, bound to <paramref name="hostName"/> on <paramref name="port"/>.</summary>
    public bool TryCreateSite(DnnProject project, string hostName, int port, IProgressReporter reporter)
    {
        // Never another site's: one of this name serving another folder stays as it is.
        if (SiteNameTaken(project.Name, project.ProjectDirectory) is { } taken)
        {
            reporter.Fail(taken);
            return false;
        }

        // A cancel takes a new site away again; one that was there is replaced, and can't be brought back as it was.
        if (_iis.GetSiteStates().ContainsKey(project.Name))
            _undo.CannotUndo($"the IIS site '{project.Name}' that was there before was replaced by a new one.");
        else
            _undo.Add($"Remove the IIS site and app pool '{project.Name}'", async () =>
            {
                var removed = _iis.RemoveSite(project.Name);
                if (removed.Success) await _iis.RemoveAppPoolProfileAsync(project.Name, CancellationToken.None);
                return removed;
            });

        // CreateSite tears down any existing site/pool of this name itself (waiting for its worker to
        // exit), so callers must not call RemoveSite first - that just repeats the whole teardown.
        var create = _iis.CreateSite(project.Name, project.ProjectDirectory, hostName, port);
        if (!create.Success)
        {
            reporter.Fail($"IIS site creation failed: {create.Error}. Continuing without a website.");
            return false;
        }

        var grant = _iis.GrantPermissions(project.ProjectDirectory, new[]
        {
            "IIS_IUSRS",
            "IUSR",
            $"IIS APPPOOL\\{project.Name}"
        });
        if (!grant.Success)
            reporter.Fail($"Could not grant IIS access to {project.ProjectDirectory}: {grant.Error}. " +
                          "The site may return 401/500 errors until the folder permissions are fixed.");

        _iis.StartSite(project.Name);
        reporter.Success($"IIS site '{project.Name}' bound to http://{DnnSiteAddress.AliasFor(hostName, port)}");
        return true;
    }
}

/// <summary>
/// The shared local SQL Server container every project's database lives in. Shared by setup, clone and
/// hosting an existing folder. The container is never started from here - it must already be running.
/// </summary>
public sealed class LocalSqlContainer(IOptions<AppOptions> opts, ISqlServerService sql, ISqlConnectionTester tester,
    IBacpacService bacpac, IWebConfigService webConfig, SiteDatabases sites)
{
    // A local server either answers straight away or isn't running - no point waiting the default 15s.
    private const int ConnectTimeoutSeconds = 5;

    private readonly AppOptions _opts = opts.Value;
    private readonly ISqlServerService _sql = sql;
    private readonly ISqlConnectionTester _tester = tester;
    private readonly IBacpacService _bacpac = bacpac;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly SiteDatabases _sites = sites;

    /// <summary>The local SQL Server's address (<c>ip,port</c>) from the settings.</summary>
    public string Server => _opts.ServerFor(_opts.Docker.DefaultPort);

    /// <summary>The local SQL Server itself (no particular database), as the container's user (sa by default).</summary>
    public SiteSqlConnection DefaultConnection => new(Server, "", _opts.Docker.SqlUser, _opts.Docker.SaPassword);

    /// <summary>
    /// The database <paramref name="project"/>'s site uses, as its web.config has it - the local container when that is
    /// where it points; null when web.config has no database of its own yet (DNN's shipped "SQL Server Express File").
    /// </summary>
    public DatabaseConnection? DatabaseOf(DnnProject project)
    {
        var read = _webConfig.ReadDatabaseConnection(Path.Combine(project.ProjectDirectory, "web.config"));
        if (!read.Success || read.Value is not { } connection) return null;
        // DNN's shipped connection (.\SQLExpress with a User Instance) is what a site has until it is installed.
        if (connection.Kind == DatabaseKind.LocalDbFile && !connection.Server.TrimStart().StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            return null;
        return connection is { Kind: DatabaseKind.SqlServer, UsesWindowsAuthentication: false } &&
               IsLocalContainer(connection.Server, _opts.Docker.DefaultPort)
            ? connection with { Kind = DatabaseKind.Container }
            : connection;
    }

    /// <summary>
    /// How to reach the database <paramref name="project"/>'s site uses, exactly as its web.config says - its server,
    /// database and login, or Windows authentication (as you) when it signs in as its app pool. Null when web.config
    /// names no database; never DNN Manager's settings in its place.
    /// </summary>
    public SiteSqlConnection? SiteConnectionOf(DnnProject project) => DatabaseOf(project) is { } c ? SiteConnection(c) : null;

    /// <summary>How to reach <paramref name="database"/> - as its login, or Windows authentication (as you).</summary>
    public static SiteSqlConnection SiteConnection(DatabaseConnection database) =>
        database.UsesWindowsAuthentication
            ? new SiteSqlConnection(database.Server, database.Database, "", "")
            : new SiteSqlConnection(database.Server, database.Database, database.User, database.Password);

    /// <summary>Database <paramref name="database"/> on the local SQL Server container, as its user (sa by default).</summary>
    public DatabaseConnection Connection(string database) =>
        new(DatabaseKind.Container, Server, database, SqlAuthentication.Sql, _opts.Docker.SqlUser, _opts.Docker.SaPassword);

    /// <summary>
    /// How to reach the database <paramref name="project"/>'s site uses: its web.config connection when that has
    /// a SQL login, otherwise the site's database on the local SQL Server as the container's user.
    /// </summary>
    public SiteSqlConnection ConnectionOf(DnnProject project)
    {
        var conn = _webConfig.ReadSiteSqlServer(Path.Combine(project.ProjectDirectory, "web.config"));
        if (conn is { Success: true, Value: { } c } && c.User.Length > 0 && c.Database.Length > 0)
            return c;

        var database = DeveloperDb.FromWebConfig(project, _webConfig) ?? _opts.DatabaseNameFor(project.Name);
        return new SiteSqlConnection(Server, database, _opts.Docker.SqlUser, _opts.Docker.SaPassword);
    }

    /// <summary>
    /// The database to export <paramref name="project"/>'s data from: the one its web.config names, signed in to as it says
    /// (Windows authentication as you); without one of its own, its database on the local SQL Server. A LocalDB file can't
    /// be exported - its instance is the site's own.
    /// </summary>
    public Result<SiteSqlConnection> ExportSourceOf(DnnProject project)
    {
        if (DatabaseOf(project) is { Kind: DatabaseKind.LocalDbFile } file)
            return Result<SiteSqlConnection>.Fail($"The site's database is a LocalDB file (App_Data\\{file.Database}), which can't be exported as " +
                                                  ".bacpac - move it to SQL Server first (Details → Database → Change connection).");
        return Result<SiteSqlConnection>.Ok(SiteConnectionOf(project) ?? ConnectionOf(project));
    }

    /// <summary>True for a file <see cref="RestoreAsync"/> can restore: a <c>.bacpac</c> or a native <c>.bak</c>.</summary>
    public static bool IsBackupFile(string path)
    {
        if (path.EndsWith(".bacpac", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return false;

        // DNN sites are full of hand-made copies like web.config.bak - those aren't database backups.
        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(path));
        return !CopiedFileExtensions.Contains(inner);
    }

    private static readonly HashSet<string> CopiedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".config", ".json", ".xml", ".txt", ".resources", ".resx", ".js", ".css", ".aspx", ".ascx", ".cs", ".dll"
    };

    /// <summary>
    /// Restores <paramref name="backupFile"/> into <paramref name="db"/>, replacing any existing database of
    /// that name: a <c>.bak</c> via RESTORE, a <c>.bacpac</c> via a SqlPackage import (installing SqlPackage
    /// on first use). Callers confirm the overwrite first.
    /// </summary>
    public async Task<Result> RestoreAsync(DatabaseConfig db, string backupFile, IProgressReporter reporter, CancellationToken ct)
    {
        // Native .bak -> RESTORE DATABASE (handles overwrite itself).
        if (!backupFile.EndsWith(".bacpac", StringComparison.OrdinalIgnoreCase))
            return await _sql.RestoreDatabaseLocalAsync(db, backupFile, ct);

        var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
        if (!ensured.Success) return ensured;

        // SqlPackage import always creates a fresh database, so drop any existing copy first.
        var exists = await _sql.DatabaseExistsAsync(db.DatabaseName, ct);
        if (exists.Success && exists.Value)
        {
            var drop = await _sql.DropDatabaseAsync(db.DatabaseName, ct);
            if (!drop.Success) return drop;
        }

        // The import creates the database; the site connects as the container sa, so there is no
        // login/user to remap afterwards.
        return await _bacpac.ImportAsync(db.Server, _opts.Docker.SqlUser, _opts.Docker.SaPassword,
            db.DatabaseName, backupFile, reporter, ct);
    }

    /// <summary>
    /// Checks that the local SQL Server accepts the configured login, reporting the outcome, and returns
    /// the port it listens on.
    /// </summary>
    public async Task<Result<int>> CheckAsync(IProgressReporter reporter, CancellationToken ct)
    {
        var test = await TestAsync(ct);
        if (!test.Success)
        {
            reporter.Fail($"Cannot connect to SQL Server at {Server}: {test.Error}");
            return Result<int>.Fail($"SQL Server at {Server} is not reachable - start the SQL Server container " +
                                    "(Settings → Docker container → Set up docker-compose) and check Settings → Database server.");
        }
        reporter.Success($"Connected to SQL Server at {Server} ({test.Value}).");
        return Result<int>.Ok(_opts.Docker.DefaultPort);
    }

    private Task<Result<string>> TestAsync(CancellationToken ct) =>
        _tester.TestAsync(new SiteSqlConnection(Server, "master", _opts.Docker.SqlUser, _opts.Docker.SaPassword), ct, ConnectTimeoutSeconds);

    /// <summary>
    /// The SQL login a project's site signs in to the container with: one of its own (<c>dnn_shop</c>), owner of its
    /// database only - never the container's sa, which could reach every project's database.
    /// </summary>
    public static string SiteLoginFor(string projectName) => $"dnn_{projectName}";

    /// <summary>Whether <paramref name="login"/> is named as a project's own login is (<see cref="SiteLoginFor"/>) - never the container's user.</summary>
    public static bool IsSiteLogin(string login) => login.StartsWith("dnn_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Makes <paramref name="project"/>'s own login on the container, owner of <paramref name="database"/>, with a new
    /// password - and returns how the site signs in with it, for its web.config.
    /// </summary>
    public async Task<Result<SiteSqlConnection>> GrantSiteLoginAsync(DnnProject project, DatabaseConfig database, CancellationToken ct)
    {
        var login = FreeLoginFor(project.Name);
        var password = SqlPasswords.New();
        var granted = await _sql.GrantSiteLoginAsync(database.DatabaseName, login, password, ct);
        return granted.Success
            ? Result<SiteSqlConnection>.Ok(new SiteSqlConnection(database.Server, database.DatabaseName, login, password))
            : Result<SiteSqlConnection>.Fail($"Could not make the site's login {login}: {granted.Error}");
    }

    /// <summary>
    /// <see cref="SiteLoginFor"/>, or <c>dnn_shop_2</c>, … when another site signs in with it: a project renamed keeps its
    /// login, and a new password for it - or the undo's drop - would lock that site out.
    /// </summary>
    private string FreeLoginFor(string projectName)
    {
        var login = SiteLoginFor(projectName);
        for (var n = 2; _sites.OtherSiteSigningInAs(projectName, Server, login) is not null; n++)
            login = $"{SiteLoginFor(projectName)}_{n}";
        return login;
    }

    /// <summary>A database in the shared container for <paramref name="project"/>.</summary>
    public DatabaseConfig DatabaseFor(DnnProject project, string databaseName, int port) =>
        new(
            Server: _opts.ServerFor(port),
            DatabaseName: databaseName,
            Collation: _opts.Docker.Collation,
            Port: port,
            BackupDirectory: project.BackupDirectory);

    /// <summary>
    /// True when <paramref name="server"/> is the local container's host - whose certificate is self-signed - on whatever port.
    /// </summary>
    public bool IsContainerHost(string server) => SqlServerAddress.Parse(server).IsContainerHost(_opts.Docker.ContainerIp);

    /// <summary>
    /// True when a connection string's <paramref name="server"/> is this machine's shared container, given the port the
    /// container currently publishes.
    /// </summary>
    public bool IsLocalContainer(string server, int publishedPort) => IsContainerServer(server, _opts.Docker.ContainerIp, publishedPort);

    /// <summary>
    /// True when <paramref name="server"/> is the shared container at <paramref name="containerHost"/> publishing
    /// <paramref name="publishedPort"/> - see <see cref="SqlServerAddress.IsContainer"/>.
    /// </summary>
    public static bool IsContainerServer(string server, string containerHost, int publishedPort) =>
        SqlServerAddress.Parse(server).IsContainer(containerHost, publishedPort);
}
