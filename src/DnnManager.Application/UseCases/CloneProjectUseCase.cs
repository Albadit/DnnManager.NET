using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class CloneProjectRequest
{
    public required string TargetProjectName { get; init; }
    public required CloneSource Source { get; init; }
    /// <summary>Path where the source SQL Server should write the .bak (must be readable from this host too).</summary>
    public required string SourceBackupServerPath { get; init; }
    public bool CreateIisSite { get; init; } = true;

    /// <summary>
    /// Optional SQL credentials for the source database. Used to fill in / override what is read
    /// from the cloned site's web.config when that connection string lacks usable credentials.
    /// Blank Server/Database fall back to the web.config values.
    /// </summary>
    public SiteSqlConnection? SourceDbOverride { get; init; }

    /// <summary>Copy/overwrite the website files. When false, existing files are kept as-is.</summary>
    public bool CopyFiles { get; init; } = true;

    /// <summary>Back up the source DB and (re)seed the local database. When false, the database is left untouched.</summary>
    public bool SeedDatabase { get; init; } = true;
}

public sealed class CloneProjectUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IProjectFileCopier _copier;
    private readonly IProjectScaffolder _scaffolder;
    private readonly IWebConfigService _webConfig;
    private readonly IRemoteSqlBackupService _remoteBackup;
    private readonly IBacpacService _bacpac;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly ISqlServerService _sql;
    private readonly IIisManager _iis;
    private readonly IisSiteProvisioner _site;
    private readonly IPrerequisiteChecker _prereq;
    private readonly ILogger<CloneProjectUseCase> _log;

    public CloneProjectUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IProjectFileCopier copier,
        IProjectScaffolder scaffolder,
        IWebConfigService webConfig,
        IRemoteSqlBackupService remoteBackup,
        IBacpacService bacpac,
        LocalSqlContainer sqlContainer,
        ISqlServerService sql,
        IIisManager iis,
        IisSiteProvisioner site,
        IPrerequisiteChecker prereq,
        ILogger<CloneProjectUseCase> log)
    {
        _opts = opts.Value;
        _projects = projects;
        _copier = copier;
        _scaffolder = scaffolder;
        _webConfig = webConfig;
        _remoteBackup = remoteBackup;
        _bacpac = bacpac;
        _sqlContainer = sqlContainer;
        _sql = sql;
        _iis = iis;
        _site = site;
        _prereq = prereq;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(CloneProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var nameCheck = ProjectName.Validate(req.TargetProjectName);
        if (!nameCheck.Success) return nameCheck;

        try
        {
            var project = _projects.Build(req.TargetProjectName);
            var hostname = _opts.HostnameFor(req.TargetProjectName);

            // 1) Target directory.
            reporter.Step($"Preparing target project '{req.TargetProjectName}'");
            Directory.CreateDirectory(project.ProjectDirectory);

            // 2) Copy website files (skipped for a database-only run).
            if (req.CopyFiles)
            {
                reporter.Step("Copying website files");
                var copy = await _copier.CopyAsync(req.Source, project.ProjectDirectory, reporter, ct);
                if (!copy.Success) return copy;
            }
            else
            {
                reporter.Step("Keeping existing website files");
                reporter.Info("Skipped file copy - using the files already in the target folder.");
            }

            // Strip the IIS URL Rewrite section. Those rules (HTTPS redirect, request blocking)
            // are production-only and need the URL Rewrite module, which is usually absent locally
            // - otherwise IIS returns HTTP 500.19. DNN doesn't need them for local dev.
            var siteWebConfig = Path.Combine(project.ProjectDirectory, "web.config");
            if (File.Exists(siteWebConfig))
            {
                var stripped = _webConfig.RemoveRewriteRules(siteWebConfig);
                if (stripped.Success) reporter.Info("Removed URL Rewrite rules (not needed locally).");
            }

            // Lay down a DNN-tuned .gitignore so the cloned project is ready to commit. Skips silently
            // when the source already shipped one, so a site's own .gitignore is preserved.
            var gitignore = _scaffolder.EnsureGitignore(project.ProjectDirectory);
            if (gitignore.Success)
                reporter.Info("Project .gitignore ready.");
            else
                reporter.Info($"Could not write .gitignore: {gitignore.Error}");

            // Seeding restores the source DB into the local Docker SQL container, so it needs Docker.
            // If Docker is absent we skip seeding (like a files-only clone) instead of hard-failing.
            var dockerAvailable = false;
            if (req.SeedDatabase)
            {
                dockerAvailable = (await _prereq.CheckDockerAsync(reporter, ct)).Success;
                if (!dockerAvailable)
                    reporter.Info("Docker not found - skipping database seeding. The cloned files are kept; " +
                                  "configure a database and point the site's web.config at it yourself.");
            }

            if (!req.SeedDatabase || !dockerAvailable)
            {
                if (!req.SeedDatabase)
                    reporter.Info("Skipping database - website files only.");
                else
                    reporter.Info("Docker unavailable - the cloned files are kept; point the site's " +
                                  "web.config at a database yourself.");
            }
            else
            {

            // 3) Read source connection string from web.config, optionally overlaying
            //    the SQL credentials the user supplied (web.config often has no usable
            //    user/password, e.g. trusted-connection or stripped Azure strings).
            reporter.Step("Reading SiteSqlServer from web.config");
            var webConfigPath = Path.Combine(project.ProjectDirectory, "web.config");
            var srcConn = _webConfig.ReadSiteSqlServer(webConfigPath);

            SiteSqlConnection src;
            if (srcConn.Success && srcConn.Value is not null)
            {
                src = srcConn.Value;
                if (req.SourceDbOverride is not null)
                {
                    var o = req.SourceDbOverride;
                    src = src with
                    {
                        Server   = string.IsNullOrWhiteSpace(o.Server)   ? src.Server   : o.Server,
                        Database = string.IsNullOrWhiteSpace(o.Database) ? src.Database : o.Database,
                        User     = o.User,
                        Password = o.Password
                    };
                    reporter.Info("Using supplied SQL credentials for the source database.");
                }
            }
            else if (req.SourceDbOverride is not null &&
                     !string.IsNullOrWhiteSpace(req.SourceDbOverride.Server) &&
                     !string.IsNullOrWhiteSpace(req.SourceDbOverride.Database))
            {
                // web.config unreadable, but the user gave us a full connection.
                src = req.SourceDbOverride;
                reporter.Info("web.config had no usable connection - using supplied SQL connection.");
            }
            else
            {
                return Result.Fail(srcConn.Error ?? "Could not read web.config, and no SQL credentials were supplied.");
            }
            reporter.Success($"Source DB: [{src.Database}] on {src.Server} (user: {src.User})");

            // Azure SQL Database can't produce a .bak, so it is cloned via a BACPAC
            // (SqlPackage export+import) instead of BACKUP/RESTORE. Detect it up front and
            // provision SqlPackage now - failing fast before any Docker setup if it can't be installed.
            var sourceIsAzure = src.Server.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase);
            if (sourceIsAzure)
            {
                var ensuredEarly = await _bacpac.EnsureAvailableAsync(reporter, ct);
                if (!ensuredEarly.Success) return ensuredEarly;
            }

            // 4) Ensure shared docker SQL container is up, write compose, get port
            reporter.Step("Preparing local SQL Server (Docker)");
            var ready = await _sqlContainer.EnsureReadyAsync(reporter, ct);
            if (!ready.Success) return Result.Fail(ready.Error!);
            var port = ready.Value;

            // 6) Create the local database (by name only; the site connects as the container sa)
            var db = _sqlContainer.DatabaseFor(project, _opts.DatabaseNameFor(req.TargetProjectName), port);

            // If the local DB already exists, drop it first (the chosen action already
            // authorized overwriting the database).
            var exists = await _sql.DatabaseExistsAsync(db.DatabaseName, ct);
            if (exists.Success && exists.Value)
            {
                reporter.Info($"Local database [{db.DatabaseName}] exists - dropping and recreating.");
                var drop = await _sql.DropDatabaseAsync(db.DatabaseName, ct);
                if (!drop.Success) return drop;
            }

            Directory.CreateDirectory(project.BackupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            if (sourceIsAzure)
            {
                // 6) Export the Azure database to a BACPAC, then import it locally.
                // SqlPackage was already provisioned up front (see the sourceIsAzure check above).
                var bacpacTmp = Path.Combine(Path.GetTempPath(), $"dnnmgr_clone_{req.TargetProjectName}_{stamp}.bacpac");
                var export = await _bacpac.ExportAsync(src, bacpacTmp, reporter, ct);
                if (!export.Success) return export;

                // Cache a copy under the project for traceability.
                var cached = Path.Combine(project.BackupDirectory, $"clone_{stamp}_{req.TargetProjectName}.bacpac");
                try { File.Copy(bacpacTmp, cached, overwrite: true); reporter.Info($"Cached BACPAC at {cached}"); } catch { }

                var import = await _bacpac.ImportAsync(db.Server, "sa", _opts.Docker.SaPassword,
                    db.DatabaseName, bacpacTmp, reporter, ct);
                if (!import.Success) return import;

                // The BACPAC import already created the database; the site connects as the container
                // sa, so there is no login/user to provision afterwards.
                try { File.Delete(bacpacTmp); } catch { /* best effort */ }
                reporter.Success($"Local database [{db.DatabaseName}] ready (from BACPAC).");
            }
            else
            {
                var create = await _sql.CreateDatabaseAsync(db, ct);
                if (!create.Success) return create;
                reporter.Success($"Local database [{db.DatabaseName}] ready.");

                // 6) Back up the source DB. If the source is our local Docker container,
                //    route the backup through the container instead of a Windows path it can't see.
                reporter.Step("Backing up source database");
                string srcBakHostPath;
                if (_sqlContainer.IsLocalContainer(src.Server, port))
                {
                    reporter.Info("Source DB is on the local Docker SQL container - using container backup path.");
                    var fileName = Path.GetFileName(req.SourceBackupServerPath);
                    var dockerBak = await _sql.BackupDatabaseLocalAsync(src.Database, fileName, ct);
                    if (!dockerBak.Success || dockerBak.Value is null)
                        return Result.Fail(dockerBak.Error ?? "Source backup via Docker failed.");
                    srcBakHostPath = dockerBak.Value;
                    reporter.Success($"Source backup written to {srcBakHostPath}");
                }
                else
                {
                    var bak = await _remoteBackup.BackupAsync(src, req.SourceBackupServerPath, reporter, ct);
                    if (!bak.Success || bak.Value is null) return Result.Fail(bak.Error ?? "Source backup failed.");
                    srcBakHostPath = bak.Value!;
                }

                var projectBak = Path.Combine(project.BackupDirectory,
                    $"clone_{stamp}_{Path.GetFileName(srcBakHostPath)}");
                File.Copy(srcBakHostPath, projectBak, overwrite: true);
                reporter.Info($"Cached backup at {projectBak}");
                try { File.Delete(srcBakHostPath); } catch { /* best effort */ }

                // 7) Restore the source backup into the local DB
                reporter.Step($"Seeding [{db.DatabaseName}] from clone backup");
                var restore = await _sql.RestoreDatabaseLocalAsync(db, projectBak, ct);
                if (!restore.Success) return restore;
                reporter.Success("Database seeded.");
            }

            // 7b) Remap portal aliases so the cloned site responds at its own hostname.
            reporter.Step("Updating PortalAlias to match new hostname");
            var alias = await _sql.RemapPortalAliasesAsync(db.DatabaseName, _opts.HostnameSuffix, hostname, ct);
            if (!alias.Success) return alias;
            reporter.Success($"PortalAlias set to {hostname}.");

            // 8) Rewrite web.config to point at the local DB, connecting as the container sa.
            reporter.Step("Rewriting web.config to use local database");
            var newConn = new SiteSqlConnection(db.Server, db.DatabaseName, "sa", _opts.Docker.SaPassword);
            var write = _webConfig.WriteSiteSqlServer(webConfigPath, newConn);
            if (!write.Success) return write;
            reporter.Success("web.config updated.");
            } // end database block (req.SeedDatabase)

            // 10) Optional IIS site - skipped (not fatal) when IIS is absent or creation fails.
            var siteCreated = false;
            if (req.CreateIisSite && _iis.IsAvailable())
            {
                reporter.Step("Creating IIS site");
                siteCreated = _site.TryCreateSite(project, reporter);
            }
            else if (req.CreateIisSite)
            {
                reporter.Info("Skipped IIS site - IIS not available.");
            }

            reporter.Step("Clone complete");
            if (siteCreated)
                reporter.Success($"Open {_opts.SiteUrlFor(req.TargetProjectName)} to use the cloned site.");
            else
                reporter.Success($"Cloned files are ready in {project.ProjectDirectory}. " +
                                 "Point a web server (and database) at them to use the site.");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clone failed");
            return Result.Fail(ex.Message);
        }
    }
}
