using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Creates a project's IIS website. Shared by setup, clone and hosting an existing folder so all three
/// bind, permission and start the site the same way.
/// </summary>
public sealed class IisSiteProvisioner
{
    private readonly AppOptions _opts;
    private readonly IIisManager _iis;
    private readonly OperationUndo _undo;

    public IisSiteProvisioner(IOptions<AppOptions> opts, IIisManager iis, OperationUndo undo)
    {
        _opts = opts.Value;
        _iis = iis;
        _undo = undo;
    }

    /// <summary>
    /// Creates (or recreates) the site and its app pool, grants the IIS identities access to the project
    /// folder and starts the site. Failures are reported and return false - a missing website is never
    /// fatal to the calling flow.
    /// </summary>
    public bool TryCreateSite(DnnProject project, IProgressReporter reporter) =>
        TryCreateSite(project, _opts.HostnameFor(project.Name), _opts.SitePort, reporter);

    /// <summary>The same, bound to <paramref name="hostName"/> on <paramref name="port"/>.</summary>
    public bool TryCreateSite(DnnProject project, string hostName, int port, IProgressReporter reporter)
    {
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
public sealed class LocalSqlContainer
{
    // A local server either answers straight away or isn't running - no point waiting the default 15s.
    private const int ConnectTimeoutSeconds = 5;

    private readonly AppOptions _opts;
    private readonly ISqlServerService _sql;
    private readonly ISqlConnectionTester _tester;
    private readonly IBacpacService _bacpac;
    private readonly IWebConfigService _webConfig;

    public LocalSqlContainer(IOptions<AppOptions> opts, ISqlServerService sql, ISqlConnectionTester tester,
        IBacpacService bacpac, IWebConfigService webConfig)
    {
        _opts = opts.Value;
        _sql = sql;
        _tester = tester;
        _bacpac = bacpac;
        _webConfig = webConfig;
    }

    /// <summary>The local SQL Server's address (<c>ip,port</c>) from the settings.</summary>
    public string Server => _opts.ServerFor(_opts.Docker.DefaultPort);

    /// <summary>The local SQL Server itself (no particular database), as sa.</summary>
    public SiteSqlConnection DefaultConnection => new(Server, "", "sa", _opts.Docker.SaPassword);

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

    /// <summary>Database <paramref name="database"/> on the local SQL Server container, as sa - the built-in database profile.</summary>
    public DatabaseConnection Connection(string database) =>
        new(DatabaseKind.Container, Server, database, SqlAuthentication.Sql, "sa", _opts.Docker.SaPassword);

    /// <summary>
    /// How to reach the database <paramref name="project"/>'s site uses: its web.config connection when that has
    /// a SQL login, otherwise the site's database on the local SQL Server as sa.
    /// </summary>
    public SiteSqlConnection ConnectionOf(DnnProject project)
    {
        var conn = _webConfig.ReadSiteSqlServer(Path.Combine(project.ProjectDirectory, "web.config"));
        if (conn is { Success: true, Value: { } c } && c.User.Length > 0 && c.Database.Length > 0)
            return c;

        var database = DeveloperDb.FromWebConfig(project, _webConfig) ?? _opts.DatabaseNameFor(project.Name);
        return new SiteSqlConnection(Server, database, "sa", _opts.Docker.SaPassword);
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
        return await _bacpac.ImportAsync(db.Server, "sa", _opts.Docker.SaPassword,
            db.DatabaseName, backupFile, reporter, ct);
    }

    /// <summary>
    /// The databases on the local SQL Server (case-insensitive), or null when it doesn't answer - one query for
    /// the whole projects list.
    /// </summary>
    public async Task<IReadOnlySet<string>?> DatabasesAsync(CancellationToken ct)
    {
        var list = await _tester.ListDatabasesAsync(DefaultConnection, ct, ConnectTimeoutSeconds);
        return list.Success ? new HashSet<string>(list.Value!, StringComparer.OrdinalIgnoreCase) : null;
    }

    /// <summary>
    /// Checks that the local SQL Server accepts the configured sa login, reporting the outcome, and returns
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
        _tester.TestAsync(new SiteSqlConnection(Server, "master", "sa", _opts.Docker.SaPassword), ct, ConnectTimeoutSeconds);

    /// <summary>A database in the shared container for <paramref name="project"/>.</summary>
    public DatabaseConfig DatabaseFor(DnnProject project, string databaseName, int port) =>
        new(
            Server: _opts.ServerFor(port),
            DatabaseName: databaseName,
            Collation: _opts.Docker.Collation,
            Port: port,
            BackupDirectory: project.BackupDirectory);

    /// <summary>
    /// True when <paramref name="server"/> (<c>host[,port]</c>) is this machine - the local container, whose
    /// certificate is self-signed - on whatever port.
    /// </summary>
    public bool IsOnThisMachine(string server)
    {
        var commaIdx = server.IndexOf(',');
        var host = (commaIdx > 0 ? server[..commaIdx] : server).Trim();
        return IsLocalContainer(host, _opts.Docker.DefaultPort);
    }

    /// <summary>
    /// True when a connection string's <paramref name="server"/> (<c>host[,port]</c>) is this machine's
    /// shared container, given the port the container currently publishes.
    /// </summary>
    public bool IsLocalContainer(string server, int publishedPort) => IsContainerServer(server, _opts.Docker.ContainerIp, publishedPort);

    /// <summary>
    /// True when <paramref name="server"/> (<c>host[,port]</c>) is this machine's shared container at
    /// <paramref name="containerHost"/>, given the port the container publishes.
    /// </summary>
    public static bool IsContainerServer(string server, string containerHost, int publishedPort)
    {
        var host = server.Trim();
        int? port = null;
        var commaIdx = host.IndexOf(',');
        if (commaIdx > 0)
        {
            if (int.TryParse(host[(commaIdx + 1)..].Trim(), out var p)) port = p;
            host = host[..commaIdx].Trim();
        }

        var isLocalHost =
            string.Equals(host, containerHost, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "(local)",   StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, ".",         StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        // If the connection names an explicit port, it must be the container's.
        return isLocalHost && (port is null || port.Value == publishedPort);
    }
}
