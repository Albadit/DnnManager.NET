using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class CloneProjectRequest
{
    public required string TargetProjectName { get; init; }
    /// <summary>The folder holding the DNN site to copy.</summary>
    public required string SourceDirectory { get; init; }
    /// <summary>Path where the source SQL Server should write the .bak (must be readable from this host too).</summary>
    public required string SourceBackupServerPath { get; init; }
    public bool CreateIisSite { get; init; } = true;

    /// <summary>Copy/overwrite the website files. When false, existing files are kept as-is.</summary>
    public bool CopyFiles { get; init; } = true;

    /// <summary>Back up the source DB and (re)seed the local database. When false, the database is left untouched.</summary>
    public bool SeedDatabase { get; init; } = true;
}

public sealed class CloneProjectUseCase(
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
    ILogger<CloneProjectUseCase> log,
    OperationUndo undo)
{
    /// <summary>The clone's stages, by their short names in the Output tab's stage list.</summary>
    private static class Stage
    {
        public const string Prepare = "Prepare target", Copy = "Copy website files", Keep = "Keep website files",
            CheckSql = "Check local SQL Server", ReadConnection = "Read SiteSqlServer", Backup = "Back up source DB",
            Seed = "Seed database", Alias = "Update PortalAlias", WebConfig = "Rewrite web.config", Iis = "Create IIS site";
    }

    private readonly AppOptions _opts = opts.Value;
    private readonly IProjectRepository _projects = projects;
    private readonly IProjectFileCopier _copier = copier;
    private readonly IProjectScaffolder _scaffolder = scaffolder;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly IRemoteSqlBackupService _remoteBackup = remoteBackup;
    private readonly IBacpacService _bacpac = bacpac;
    private readonly LocalSqlContainer _sqlContainer = sqlContainer;
    private readonly ISqlServerService _sql = sql;
    private readonly IIisManager _iis = iis;
    private readonly IisSiteProvisioner _site = site;
    private readonly ILogger<CloneProjectUseCase> _log = log;
    private readonly OperationUndo _undo = undo;

    public async Task<Result> ExecuteAsync(CloneProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var nameCheck = ProjectName.Validate(req.TargetProjectName);
        if (!nameCheck.Success) return nameCheck;

        try
        {
            var project = _projects.Build(req.TargetProjectName);
            var hostname = _opts.HostnameFor(req.TargetProjectName);

            // The stages, up front: the Output tab shows those still to come - and as skipped those it never gets to.
            var plan = new List<string> { Stage.Prepare, req.CopyFiles ? Stage.Copy : Stage.Keep };
            if (req.SeedDatabase) plan.AddRange([Stage.CheckSql, Stage.ReadConnection, Stage.Backup, Stage.Seed, Stage.Alias, Stage.WebConfig]);
            if (req.CreateIisSite) plan.Add(Stage.Iis);
            reporter.Plan([.. plan]);

            reporter.Step($"Prepare target project '{req.TargetProjectName}'", Stage.Prepare);
            // A cancel takes a new target folder away again; files copied over an existing one can't be taken back.
            var newFolder = !Directory.Exists(project.ProjectDirectory);
            _undo.DeleteFolderOnUndo(project.ProjectDirectory);
            Directory.CreateDirectory(project.ProjectDirectory);

            if (req.CopyFiles)
            {
                if (!newFolder) _undo.CannotUndo($"the website files copied over the ones in {project.ProjectDirectory}.");
                reporter.Step("Copy website files", Stage.Copy);
                var copy = await _copier.CopyAsync(req.SourceDirectory, project.ProjectDirectory, reporter, ct);
                if (!copy.Success) return copy;
            }
            else
            {
                reporter.Step("Keep the existing website files", Stage.Keep);
                reporter.Info("Skipped file copy - using the files already in the target folder.");
            }

            // Strip the IIS URL Rewrite section. Those rules (HTTPS redirect, request blocking)
            // are production-only and need the URL Rewrite module, which is usually absent locally
            // - otherwise IIS returns HTTP 500.19. DNN doesn't need them for local dev.
            var siteWebConfig = Path.Combine(project.ProjectDirectory, "web.config");
            if (File.Exists(siteWebConfig))
            {
                _undo.RestoreFileOnUndo(siteWebConfig);
                var stripped = _webConfig.RemoveRewriteRules(siteWebConfig);
                if (stripped.Success) reporter.Info("Removed URL Rewrite rules (not needed locally).");
            }

            // Lay down a DNN-tuned .gitignore so the cloned project is ready to commit. Skips silently
            // when the source already shipped one, so a site's own .gitignore is preserved.
            _undo.RestoreFileOnUndo(Path.Combine(project.ProjectDirectory, ".gitignore"));
            var gitignore = _scaffolder.EnsureGitignore(project.ProjectDirectory);
            if (gitignore.Success)
                reporter.Info("Project .gitignore ready.");
            else
                reporter.Info($"Could not write .gitignore: {gitignore.Error}");

            // Seeding restores the source DB into the local SQL Server, so it needs that server to be
            // reachable. If it isn't we skip seeding (like a files-only clone) instead of hard-failing.
            var port = _opts.Docker.DefaultPort;
            var sqlAvailable = false;
            if (req.SeedDatabase)
            {
                reporter.Step("Check local SQL Server", Stage.CheckSql);
                var ready = await _sqlContainer.CheckAsync(reporter, ct);
                sqlAvailable = ready.Success;
                if (ready.Success)
                {
                    port = ready.Value;
                    reporter.Context(_opts.ServerFor(port));
                }
            }

            if (!req.SeedDatabase || !sqlAvailable)
            {
                if (!req.SeedDatabase)
                    reporter.Info("Skipping database - website files only.");
                else
                    reporter.Info("SQL Server not reachable - skipping database seeding. The cloned files are kept; " +
                                  "start the SQL Server container and clone again, or point the site's " +
                                  "web.config at a database yourself.");
            }
            else
            {
                reporter.Step("Read SiteSqlServer from web.config", Stage.ReadConnection);
                var webConfigPath = Path.Combine(project.ProjectDirectory, "web.config");
                var srcConn = _webConfig.ReadSiteSqlServer(webConfigPath);
                if (!srcConn.Success || srcConn.Value is null)
                    return Result.Fail(srcConn.Error ?? "Could not read the SiteSqlServer connection from the source's web.config.");
                var src = srcConn.Value;
                reporter.Success($"Source DB: [{src.Database}] on {src.Server} (user: {src.User})");

                // Azure SQL Database can't produce a .bak, so it is cloned via a BACPAC
                // (SqlPackage export+import) instead of BACKUP/RESTORE. Detect it up front and
                // provision SqlPackage now - failing fast before any local database work if it can't be installed.
                var sourceIsAzure = src.Server.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase);
                if (sourceIsAzure)
                {
                    var ensuredEarly = await _bacpac.EnsureAvailableAsync(reporter, ct);
                    if (!ensuredEarly.Success) return ensuredEarly;
                }

                // The local database, by name only - the site connects as the container sa.
                var db = _sqlContainer.DatabaseFor(project, _opts.DatabaseNameFor(req.TargetProjectName), port);

                // If the local DB already exists, drop it first (the chosen action already
                // authorized overwriting the database).
                var exists = await _sql.DatabaseExistsAsync(db.DatabaseName, ct);
                if (exists.Success && exists.Value)
                {
                    reporter.Info($"Local database [{db.DatabaseName}] exists - dropping and recreating.");
                    var drop = await _sql.DropDatabaseAsync(db.DatabaseName, ct);
                    if (!drop.Success) return drop;
                    _undo.CannotUndo($"local database [{db.DatabaseName}] that was there before was dropped - what was in it is gone.");
                }
                // The clone's database, made below (or by the BACPAC import) - dropped again by a cancel.
                _undo.Add($"Drop database [{db.DatabaseName}]", () => _sql.DropDatabaseAsync(db.DatabaseName, CancellationToken.None));

                var backupFolder = ProjectBackups.NewFolder(project, DateTime.Now);
                _undo.DeleteFolderOnUndo(backupFolder);
                Directory.CreateDirectory(backupFolder);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                if (sourceIsAzure)
                {
                    // SqlPackage was already provisioned up front (see the sourceIsAzure check above).
                    reporter.Step("Export the source database (BACPAC)", Stage.Backup);
                    var bacpacTmp = Path.Combine(Path.GetTempPath(), $"dnnmanager_clone_{req.TargetProjectName}_{stamp}.bacpac");
                    _undo.RestoreFileOnUndo(bacpacTmp);
                    var export = await _bacpac.ExportAsync(src, bacpacTmp, reporter, ct);
                    if (!export.Success) return export;

                    // Keep a copy in the project's backups for traceability.
                    var cached = Path.Combine(backupFolder, ProjectBackups.DatabaseName(project, ".bacpac"));
                    try { File.Copy(bacpacTmp, cached, overwrite: true); reporter.Info($"Cached BACPAC at {cached}"); } catch { }

                    reporter.Step($"Seed [{db.DatabaseName}] from the BACPAC", Stage.Seed);
                    var import = await _bacpac.ImportAsync(db.Server, _opts.Docker.SqlUser, _opts.Docker.SaPassword,
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

                    // If the source is our local SQL container, route the backup through the container
                    // instead of a Windows path it can't see.
                    reporter.Step("Back up source database", Stage.Backup);
                    string srcBakHostPath;
                    if (_sqlContainer.IsLocalContainer(src.Server, port))
                    {
                        reporter.Info("Source DB is on the local SQL container - using container backup path.");
                        var fileName = Path.GetFileName(req.SourceBackupServerPath);
                        var localBak = await _sql.BackupDatabaseLocalAsync(src.Database, fileName, ct);
                        if (!localBak.Success || localBak.Value is null)
                            return Result.Fail(localBak.Error ?? "Source backup on the local SQL container failed.");
                        srcBakHostPath = localBak.Value;
                        reporter.Success($"Source backup written to {srcBakHostPath}");
                    }
                    else
                    {
                        var bak = await _remoteBackup.BackupAsync(src, req.SourceBackupServerPath, reporter, ct);
                        if (!bak.Success || bak.Value is null) return Result.Fail(bak.Error ?? "Source backup failed.");
                        srcBakHostPath = bak.Value!;
                    }

                    var projectBak = Path.Combine(backupFolder, ProjectBackups.DatabaseName(project, ".bak"));
                    File.Copy(srcBakHostPath, projectBak, overwrite: true);
                    reporter.Info($"Cached backup at {projectBak}");
                    try { File.Delete(srcBakHostPath); } catch { /* best effort */ }

                    reporter.Step($"Seed [{db.DatabaseName}] from clone backup", Stage.Seed);
                    var restore = await _sql.RestoreDatabaseLocalAsync(db, projectBak, ct);
                    if (!restore.Success) return restore;
                    reporter.Success("Database seeded.");
                }

                // So the cloned site responds at its own hostname instead of the source's.
                reporter.Step("Update PortalAlias to match new hostname", Stage.Alias);
                var alias = await _sql.RemapPortalAliasesAsync(db.DatabaseName, _opts.HostnameSuffix, hostname, ct);
                if (!alias.Success) return alias;
                reporter.Success($"PortalAlias set to {hostname}.");
                await HostExistingProjectUseCase.DisableSslAsync(_sql, db.DatabaseName, reporter, ct);

                reporter.Step("Rewrite web.config to use local database", Stage.WebConfig);
                _undo.RestoreFileOnUndo(webConfigPath);
                var newConn = new SiteSqlConnection(db.Server, db.DatabaseName, _opts.Docker.SqlUser, _opts.Docker.SaPassword);
                var write = _webConfig.WriteSiteSqlServer(webConfigPath, newConn);
                if (!write.Success) return write;
                reporter.Success("web.config updated.");
            }

            // Optional IIS site - skipped (not fatal) when IIS is absent or creation fails.
            var siteCreated = false;
            if (req.CreateIisSite && _iis.IsAvailable())
            {
                reporter.Step("Create IIS site", Stage.Iis);
                siteCreated = _site.TryCreateSite(project, reporter);
                if (siteCreated) reporter.Context($"IIS · {hostname}");
            }
            else if (req.CreateIisSite)
            {
                reporter.Info("Skipped IIS site - IIS not available.");
            }

            reporter.Step("Clone complete");
            // The Output tab offers the site's address at the end of the run.
            if (siteCreated)
                reporter.Link(_opts.SiteUrlFor(req.TargetProjectName));
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
