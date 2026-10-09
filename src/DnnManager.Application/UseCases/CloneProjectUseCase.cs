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

/// <summary>
/// "Clone…": a copy of a project - its files, its database copied into one of its own on the local SQL Server, and an IIS
/// website of its own. Everything that can be checked is checked before a file is copied: the name, the source's
/// database, the local SQL Server, and - with a yes - a database of the clone's name that is already there. That one is
/// only replaced once the copy is in: the copy is seeded under a name of its own and swapped in at the end.
/// </summary>
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
    IDatabaseProvisioner databases,
    IIisManager iis,
    IisSiteProvisioner site,
    SiteDatabases siteDatabases,
    IPrivateTemp temp,
    IUserPrompt prompt,
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
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly IIisManager _iis = iis;
    private readonly IisSiteProvisioner _site = site;
    private readonly SiteDatabases _siteDatabases = siteDatabases;
    private readonly IPrivateTemp _temp = temp;
    private readonly IUserPrompt _prompt = prompt;
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
            // A site of this name that serves another folder is another project's: making the clone's would replace it.
            if (req.CreateIisSite && _site.SiteNameTaken(req.TargetProjectName, project.ProjectDirectory) is { } taken)
                return Result.Fail(taken);

            // The stages, up front: the Output tab shows those still to come - and as skipped those it never gets to.
            var plan = new List<string> { Stage.Prepare };
            if (req.SeedDatabase) plan.AddRange([Stage.ReadConnection, Stage.CheckSql]);
            plan.Add(req.CopyFiles ? Stage.Copy : Stage.Keep);
            if (req.SeedDatabase) plan.AddRange([Stage.Backup, Stage.Seed, Stage.Alias, Stage.WebConfig]);
            if (req.CreateIisSite) plan.Add(Stage.Iis);
            reporter.Plan([.. plan]);

            reporter.Step($"Prepare target project '{req.TargetProjectName}'", Stage.Prepare);

            // Before a file is copied: the source's database, the local SQL Server, and the clone's own database.
            DatabasePlan? database = null;
            if (req.SeedDatabase)
            {
                var prepared = await PrepareDatabaseAsync(req, project, reporter, ct);
                if (!prepared.Success) return prepared.Error is { } error ? Result.Fail(error) : Result.Aborted();
                database = prepared.Value!;
            }

            // A cancel or a failure takes a new target folder away again; files copied over an existing one can't be.
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
            var webConfigPath = Path.Combine(project.ProjectDirectory, "web.config");
            if (File.Exists(webConfigPath))
            {
                _undo.RestoreFileOnUndo(webConfigPath);
                var stripped = _webConfig.RemoveRewriteRules(webConfigPath);
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

            if (database is null)
            {
                reporter.Info("Skipping database - website files only.");
                // Its web.config is the source's: the copy would work in the source's database, which may be a live one.
                var conn = File.Exists(webConfigPath) ? _webConfig.ReadSiteSqlServer(webConfigPath) : null;
                if (conn is { Success: true, Value: { Database.Length: > 0 } c })
                    reporter.Warn($"web.config still connects to [{c.Database}] on {c.Server} - the source's database. " +
                                  "The copy changes that database's data; point it at a database of its own (Details → Database) before using it.");
            }
            else
            {
                var seeded = await SeedAsync(req, project, database, reporter, ct);
                if (!seeded.Success) return seeded;

                reporter.Step("Rewrite web.config to use local database", Stage.WebConfig);
                // The copy signs in with a login of its own, owner of its database only - not the container's sa.
                var login = await _sqlContainer.GrantSiteLoginAsync(project, database.Target, ct);
                if (!login.Success) return login.WithoutValue();
                _undo.Add($"Drop the login {login.Value!.User}", () => _sql.DropLoginAsync(login.Value.User, CancellationToken.None));
                _undo.RestoreFileOnUndo(webConfigPath);
                var write = _webConfig.WriteSiteSqlServer(webConfigPath, login.Value);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Clone failed");
            return Result.Fail(ex.Message);
        }
    }

    /// <param name="Source">The source's database, as the source's web.config has it.</param>
    /// <param name="Target">The clone's database on the local SQL Server, by its final name.</param>
    /// <param name="Replaces">A database of that name is there already: the copy goes in beside it and replaces it at the end.</param>
    private sealed record DatabasePlan(SiteSqlConnection Source, DatabaseConfig Target, bool Replaces, bool SourceIsAzure);

    /// <summary>
    /// Everything about the database that can be known before a file is copied: the source's connection (from the
    /// source's own web.config), that SqlPackage is there for an Azure source, that the local SQL Server answers, and
    /// whether a database of the clone's name may be replaced - asked, and refused when another site uses it. A failure
    /// comes back with its error; a no comes back without one (the clone doesn't happen).
    /// </summary>
    private async Task<Result<DatabasePlan>> PrepareDatabaseAsync(CloneProjectRequest req, DnnProject project, IProgressReporter reporter,
        CancellationToken ct)
    {
        reporter.Step("Read SiteSqlServer from the source's web.config", Stage.ReadConnection);
        var sourceConfig = Path.Combine(req.SourceDirectory, "web.config");
        var srcConn = File.Exists(sourceConfig)
            ? _webConfig.ReadSiteSqlServer(sourceConfig)
            : Result<SiteSqlConnection>.Fail($"The source has no web.config ({sourceConfig}).");
        if (!srcConn.Success || srcConn.Value is not { Database.Length: > 0 } src)
            return Result<DatabasePlan>.Fail(srcConn.Error ?? "Could not read the SiteSqlServer connection from the source's web.config.");
        reporter.Success($"Source DB: [{src.Database}] on {src.Server} ({(src.User.Length > 0 ? $"user: {src.User}" : "Windows authentication")})");

        // Azure SQL Database can't produce a .bak, so it is cloned via a BACPAC (SqlPackage export+import) instead of
        // BACKUP/RESTORE - SqlPackage is provisioned now, before anything else.
        var sourceIsAzure = src.Server.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase);
        if (sourceIsAzure)
        {
            var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
            if (!ensured.Success) return Result<DatabasePlan>.Fail(ensured.Error!);
        }

        reporter.Step("Check local SQL Server", Stage.CheckSql);
        var ready = await _sqlContainer.CheckAsync(reporter, ct);
        if (!ready.Success)
            return Result<DatabasePlan>.Fail($"{ready.Error} Nothing was copied - or clone the website files only.");
        var port = ready.Value;
        reporter.Context(_opts.ServerFor(port));

        var target = _sqlContainer.DatabaseFor(project, _opts.DatabaseNameFor(req.TargetProjectName), port);
        if (_sqlContainer.IsLocalContainer(src.Server, port) && src.Database.Equals(target.DatabaseName, StringComparison.OrdinalIgnoreCase))
            return Result<DatabasePlan>.Fail($"The source already uses [{target.DatabaseName}] - the clone needs a name of its own.");
        var exists = await _sql.DatabaseExistsAsync(target.DatabaseName, ct);
        if (!exists.Success) return Result<DatabasePlan>.Fail($"Could not check for database [{target.DatabaseName}]: {exists.Error}");
        if (exists.Value)
        {
            if (_siteDatabases.OtherSiteUsing(req.TargetProjectName, target.Server, target.DatabaseName) is { } other)
                return Result<DatabasePlan>.Fail($"[{target.DatabaseName}] is the database of the IIS site '{other}' - choose another name for the clone.");
            if (!await _prompt.ConfirmDangerAsync(
                    $"Database [{target.DatabaseName}] already exists on {target.Server}. Replace it with a copy of [{src.Database}]? " +
                    "Everything in it now is lost.", "Replace database", "Keep it", ct))
                return new Result<DatabasePlan>(false, null);
        }
        return Result<DatabasePlan>.Ok(new DatabasePlan(src, target, exists.Value, sourceIsAzure));
    }

    /// <summary>
    /// Copies the source's database into the clone's: a BACPAC export and import from Azure, a backup and restore from
    /// anywhere else - seeded under a name of its own when a database of the clone's name is there, and swapped in only
    /// once it is complete, with its portal aliases and SSL set for the local site. A failure leaves that database as it was.
    /// </summary>
    private async Task<Result> SeedAsync(CloneProjectRequest req, DnnProject project, DatabasePlan plan, IProgressReporter reporter,
        CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = plan.Target.DatabaseName;
        var incoming = plan.Replaces ? plan.Target with { DatabaseName = $"{name}_clone_{stamp}" } : plan.Target;
        // The copy, made below - dropped again by a cancel or a failure; the database it replaces is untouched until the end.
        _undo.Add($"Drop database [{incoming.DatabaseName}]", () => _sql.DropDatabaseAsync(incoming.DatabaseName, CancellationToken.None));

        var backupFolder = ProjectBackups.NewFolder(project, DateTime.Now);
        _undo.DeleteFolderOnUndo(backupFolder);
        Directory.CreateDirectory(backupFolder);

        if (plan.SourceIsAzure)
        {
            reporter.Step("Export the source database (BACPAC)", Stage.Backup);
            var bacpacTmp = Path.Combine(_temp.Folder, $"dnnmanager_clone_{req.TargetProjectName}_{stamp}.bacpac");
            try
            {
                var export = await _bacpac.ExportAsync(plan.Source, bacpacTmp, reporter, ct);
                if (!export.Success) return export;

                // Kept with the project's backups, so the copy can be made again from the same data.
                var cached = Path.Combine(backupFolder, ProjectBackups.DatabaseName(project, ".bacpac"));
                try
                {
                    File.Copy(bacpacTmp, cached, overwrite: true);
                    reporter.Info($"Cached BACPAC at {cached}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    reporter.Info($"Could not keep a copy of the BACPAC in the project's backups: {ex.Message}");
                }

                reporter.Step($"Seed [{name}] from the BACPAC", Stage.Seed);
                var import = await _bacpac.ImportAsync(incoming.Server, _opts.Docker.SqlUser, _opts.Docker.SaPassword,
                    incoming.DatabaseName, bacpacTmp, reporter, ct);
                if (!import.Success) return import;
            }
            finally
            {
                // The source's whole database - never left behind, whatever happened.
                TryDelete(bacpacTmp);
            }
        }
        else
        {
            var create = await _sql.CreateDatabaseAsync(incoming, ct);
            if (!create.Success) return create;

            // If the source is our local SQL container, route the backup through the container
            // instead of a Windows path it can't see.
            reporter.Step("Back up source database", Stage.Backup);
            string srcBakHostPath;
            if (_sqlContainer.IsLocalContainer(plan.Source.Server, plan.Target.Port))
            {
                reporter.Info("Source DB is on the local SQL container - using container backup path.");
                var fileName = Path.GetFileName(req.SourceBackupServerPath);
                var localBak = await _sql.BackupDatabaseLocalAsync(plan.Source.Database, fileName, ct);
                if (!localBak.Success || localBak.Value is null)
                    return Result.Fail(localBak.Error ?? "Source backup on the local SQL container failed.");
                srcBakHostPath = localBak.Value;
                reporter.Success($"Source backup written to {srcBakHostPath}");
            }
            else
            {
                var bak = await _remoteBackup.BackupAsync(plan.Source, req.SourceBackupServerPath, reporter, ct);
                if (!bak.Success || bak.Value is null) return Result.Fail(bak.Error ?? "Source backup failed.");
                srcBakHostPath = bak.Value;
            }

            var projectBak = Path.Combine(backupFolder, ProjectBackups.DatabaseName(project, ".bak"));
            try
            {
                File.Copy(srcBakHostPath, projectBak, overwrite: true);
            }
            finally
            {
                TryDelete(srcBakHostPath);
            }
            reporter.Info($"Cached backup at {projectBak}");

            reporter.Step($"Seed [{name}] from clone backup", Stage.Seed);
            var restore = await _sql.RestoreDatabaseLocalAsync(incoming, projectBak, ct);
            if (!restore.Success) return restore;
        }
        reporter.Success($"Database copied into [{incoming.DatabaseName}].");

        // So the cloned site responds at its own address instead of the source's.
        reporter.Step("Update PortalAlias to match new hostname", Stage.Alias);
        var alias = DnnSiteAddress.AliasFor(_opts.HostnameFor(req.TargetProjectName), _opts.SitePort);
        var remapped = await _sql.RemapPortalAliasesAsync(incoming.DatabaseName, _opts.HostnameSuffix, alias, ct);
        if (!remapped.Success) return remapped;
        reporter.Success($"PortalAlias set to {alias}.");
        await HostExistingProjectUseCase.DisableSslAsync(_sql, incoming.DatabaseName, reporter, ct);

        if (plan.Replaces) return await SwapInAsync(name, incoming.DatabaseName, stamp, reporter, ct);
        reporter.Success($"Local database [{name}] ready.");
        return Result.Ok();
    }

    /// <summary>
    /// The copy in place of the database it replaces: that one renamed aside, the copy given its name, the one aside dropped.
    /// A rename that fails puts the old one back - it is only gone once the copy has its name.
    /// </summary>
    private async Task<Result> SwapInAsync(string name, string incoming, string stamp, IProgressReporter reporter, CancellationToken ct)
    {
        var aside = $"{name}_before_clone_{stamp}";
        var old = _sqlContainer.Connection(name);
        var renamed = await _databases.RenameDatabaseAsync(old, aside, ct);
        if (!renamed.Success)
            return Result.Fail($"Could not set [{name}] aside to put the copy in: {renamed.Error} The database is left as it was.");
        var named = await _databases.RenameDatabaseAsync(_sqlContainer.Connection(incoming), name, ct);
        if (!named.Success)
        {
            var back = await _databases.RenameDatabaseAsync(old with { Database = aside }, name, CancellationToken.None);
            return Result.Fail(back.Success
                ? $"The copy couldn't be named [{name}]: {named.Error} The database is left as it was."
                : $"The copy couldn't be named [{name}]: {named.Error} - and the database it replaces couldn't be put back from [{aside}]: {back.Error}");
        }
        _undo.CannotUndo($"database [{name}] that was there before was replaced by the copy, as you chose.");
        var dropped = await _sql.DropDatabaseAsync(aside, CancellationToken.None);
        if (!dropped.Success)
            reporter.Warn($"The database it replaced is still there as [{aside}] - drop it once you no longer need it ({dropped.Error}).");
        reporter.Success($"Local database [{name}] ready - it replaced the one that was there.");
        return Result.Ok();
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not delete {Path}", path); }
    }
}
