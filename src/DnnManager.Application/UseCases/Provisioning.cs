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
/// hosting an existing folder.
/// </summary>
public sealed class LocalSqlContainer
{
    private readonly AppOptions _opts;
    private readonly IDockerService _docker;
    private readonly ISqlServerService _sql;
    private readonly IBacpacService _bacpac;

    public LocalSqlContainer(IOptions<AppOptions> opts, IDockerService docker, ISqlServerService sql, IBacpacService bacpac)
    {
        _opts = opts.Value;
        _docker = docker;
        _sql = sql;
        _bacpac = bacpac;
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
    /// Starts the shared container (bringing it up from the bundled docker-compose.yml the first time),
    /// waits until SQL Server accepts connections and returns the container's published port.
    /// </summary>
    public async Task<Result<int>> EnsureReadyAsync(IProgressReporter reporter, CancellationToken ct)
    {
        var name = _opts.Docker.ContainerName;
        var state = await _docker.GetContainerStateAsync(name, ct);
        if (state is null)
        {
            var up = await _docker.ComposeUpAsync(ct);
            if (!up.Success) return Result<int>.Fail(up.Error ?? "docker compose up failed.");
        }
        else if (!state.Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            reporter.Info($"Container '{name}' exists but is {state} - starting it.");
            var start = await _docker.StartContainerAsync(name, ct);
            if (!start.Success) return Result<int>.Fail($"Could not start container '{name}': {start.Error}");
        }
        else
        {
            reporter.Info($"Reusing running SQL Server container '{name}'.");
        }

        // The shared container publishes a fixed port (1433); read it back to be certain.
        var port = await _docker.GetPublishedPortAsync(name, ct);
        if (port is null) return Result<int>.Fail($"Could not determine published port for '{name}'.");

        var ready = await _sql.WaitReadyAsync(180, reporter, ct);
        if (!ready.Success) return Result<int>.Fail(ready.Error ?? "SQL Server did not become ready.");
        return Result<int>.Ok(port.Value);
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
