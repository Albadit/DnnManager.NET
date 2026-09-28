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

    public IisSiteProvisioner(IOptions<AppOptions> opts, IIisManager iis)
    {
        _opts = opts.Value;
        _iis = iis;
    }

    /// <summary>
    /// Creates (or recreates) the site and its app pool, grants the IIS identities access to the project
    /// folder and starts the site. Failures are reported and return false - a missing website is never
    /// fatal to the calling flow.
    /// </summary>
    public bool TryCreateSite(DnnProject project, IProgressReporter reporter)
    {
        // CreateSite tears down any existing site/pool of this name itself (waiting for its worker to
        // exit), so callers must not call RemoveSite first - that just repeats the whole teardown.
        var create = _iis.CreateSite(project.Name, project.ProjectDirectory, _opts.HostnameFor(project.Name), _opts.SitePort);
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
        reporter.Success($"IIS site '{project.Name}' bound to {_opts.SiteUrlFor(project.Name)}");
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

    public LocalSqlContainer(IOptions<AppOptions> opts, ISqlServerService sql, ISqlConnectionTester tester, IBacpacService bacpac)
    {
        _opts = opts.Value;
        _sql = sql;
        _tester = tester;
        _bacpac = bacpac;
    }

    /// <summary>The local SQL Server's address (<c>ip,port</c>) from the settings.</summary>
    public string Server => _opts.ServerFor(_opts.Docker.DefaultPort);

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

    /// <summary>Logs in to the local SQL Server as sa - true when it accepts the connection.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct) => (await TestAsync(ct)).Success;

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
                                    "(Settings → Set up Docker container) and check the SQL Server settings.");
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
    /// True when a connection string's <paramref name="server"/> (<c>host[,port]</c>) is this machine's
    /// shared container, given the port the container currently publishes.
    /// </summary>
    public bool IsLocalContainer(string server, int publishedPort)
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
            string.Equals(host, _opts.Docker.ContainerIp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "(local)",   StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, ".",         StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        // If the connection names an explicit port, it must be the container's.
        return isLocalHost && (port is null || port.Value == publishedPort);
    }
}
