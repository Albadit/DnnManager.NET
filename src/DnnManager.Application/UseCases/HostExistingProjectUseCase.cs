using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class HostExistingProjectRequest
{
    /// <summary>A folder that already exists under the base directory.</summary>
    public required string ProjectName { get; init; }

    /// <summary>Create (or recreate) the IIS website. False for a database-only run.</summary>
    public bool SetupIis { get; init; } = true;

    /// <summary>Also create a database in the local SQL container and point web.config at it.</summary>
    public bool SetupDatabase { get; init; }

    /// <summary>
    /// A <c>.bacpac</c> (or <c>.bak</c>) to restore into the database. Null leaves an existing database as
    /// it is and creates a missing one empty.
    /// </summary>
    public string? BackupFilePath { get; init; }
}

/// <summary>
/// Hosts a project folder that already holds a DNN site (copied by hand, checked out from git, left
/// over from an earlier setup…): creates its IIS website, its local database, or both. The site's
/// files are never downloaded, copied or overwritten.
/// </summary>
public sealed class HostExistingProjectUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IIisManager _iis;
    private readonly IisSiteProvisioner _site;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly ISqlServerService _sql;
    private readonly IWebConfigService _webConfig;
    private readonly IHttpConnectivityChecker _http;
    private readonly IPrerequisiteChecker _prereq;
    private readonly IUserPrompt _prompt;
    private readonly ILogger<HostExistingProjectUseCase> _log;

    public HostExistingProjectUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IIisManager iis,
        IisSiteProvisioner site,
        LocalSqlContainer sqlContainer,
        ISqlServerService sql,
        IWebConfigService webConfig,
        IHttpConnectivityChecker http,
        IPrerequisiteChecker prereq,
        IUserPrompt prompt,
        ILogger<HostExistingProjectUseCase> log)
    {
        _opts = opts.Value;
        _projects = projects;
        _iis = iis;
        _site = site;
        _sqlContainer = sqlContainer;
        _sql = sql;
        _webConfig = webConfig;
        _http = http;
        _prereq = prereq;
        _prompt = prompt;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(HostExistingProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var nameCheck = ProjectName.Validate(req.ProjectName);
        if (!nameCheck.Success) return nameCheck;
        if (!req.SetupIis && !req.SetupDatabase)
            return Result.Fail("Nothing to set up - choose the IIS website, the database, or both.");

        try
        {
            var project = _projects.Build(req.ProjectName);
            if (!Directory.Exists(project.ProjectDirectory))
                return Result.Fail($"Project folder not found: {project.ProjectDirectory}");

            var webConfigPath = Path.Combine(project.ProjectDirectory, "web.config");
            var hasWebConfig = File.Exists(webConfigPath);
            reporter.Info($"Using the existing files in {project.ProjectDirectory} - nothing is downloaded or overwritten.");
            if (!hasWebConfig)
                reporter.Info("No web.config in the folder - it doesn't look like a DNN site yet. Continuing anyway.");

            var step = 0;
            var siteCreated = false;
            if (req.SetupIis)
            {
                // IIS is the point of this step, so offer to enable missing features before checking for it.
                reporter.Step($"Step {++step}: IIS website");
                await _prereq.EnsureIisFeaturesAsync(reporter, _prompt, ct);
                if (_iis.IsAvailable())
                {
                    if (_iis.GetSiteStates().ContainsKey(req.ProjectName))
                        reporter.Info($"IIS site '{req.ProjectName}' already exists - recreating it.");
                    siteCreated = _site.TryCreateSite(project, reporter);
                }
                else
                {
                    reporter.Fail("IIS is not available on this machine - enable it (see 'Check prerequisites') and retry.");
                }
            }

            if (req.SetupDatabase)
            {
                reporter.Step($"Step {++step}: Database");
                var db = await SetupDatabaseAsync(project, webConfigPath, hasWebConfig, req.BackupFilePath, reporter, ct);
                if (!db.Success)
                {
                    // Alongside a website the database is a best-effort extra; on its own it is the whole job.
                    if (!req.SetupIis) return db;
                    reporter.Fail($"{db.Error} Skipping database setup.");
                }
            }

            if (!req.SetupIis)
            {
                reporter.Step("Done");
                reporter.Success("Database ready.");
                return Result.Ok();
            }

            if (!siteCreated)
                return Result.Fail("The IIS website could not be created.");

            var url = _opts.SiteUrlFor(req.ProjectName);
            reporter.Step("Verify");
            var http = await _http.CheckAsync(url, 15, ct);
            if (http.Success)
                reporter.Success($"HTTP {http.Value} from {url}");
            else
                reporter.Info($"HTTP probe: {http.Error}");

            reporter.Step("Done");
            reporter.Success($"Open {url} to use the site.");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hosting existing project {Project} failed", req.ProjectName);
            return Result.Fail(ex.Message);
        }
    }

    // Restores backupFile into the database when one is given (asking first if the database already
    // exists); otherwise creates the database only when it's missing. Existing data is never dropped
    // without a yes. Failures come back as a result for the caller to report, never thrown. A declined or
    // failed web.config update is reported but not a failure: the database itself is ready and its
    // connection details are shown.
    private async Task<Result> SetupDatabaseAsync(DnnProject project, string webConfigPath, bool hasWebConfig,
        string? backupFile, IProgressReporter reporter, CancellationToken ct)
    {
        if (backupFile is not null)
        {
            if (!File.Exists(backupFile)) return Result.Fail($"Backup file not found: {backupFile}");
            if (!LocalSqlContainer.IsBackupFile(backupFile))
                return Result.Fail($"Not a .bacpac or .bak file: {backupFile}");
        }

        if (!(await _prereq.CheckDockerAsync(reporter, ct)).Success)
            return Result.Fail("Docker not available - start Docker and run this again.");

        var ready = await _sqlContainer.EnsureReadyAsync(reporter, ct);
        if (!ready.Success) return Result.Fail(ready.Error ?? "The SQL container is not ready.");
        var port = ready.Value;

        // When web.config already points at the local container, keep the database it names (creating it
        // if it's gone) and leave web.config alone. Otherwise - a production connection string, the DNN
        // package's LocalDB placeholder, or no web.config - use this project's conventional database, so
        // two sites copied from the same source never end up sharing one database.
        var current = hasWebConfig ? _webConfig.ReadSiteSqlServer(webConfigPath) : null;
        var currentConn = current is { Success: true } ? current.Value : null;
        var alreadyLocal = currentConn is not null && _sqlContainer.IsLocalContainer(currentConn.Server, port);
        var db = _sqlContainer.DatabaseFor(project,
            alreadyLocal ? currentConn!.Database : _opts.DatabaseNameFor(project.Name), port);

        var exists = await _sql.DatabaseExistsAsync(db.DatabaseName, ct);
        if (!exists.Success)
            return Result.Fail($"Could not check for database [{db.DatabaseName}]: {exists.Error}");

        var created = false;
        if (backupFile is not null)
        {
            // Restoring replaces the database, so an existing one is only overwritten on an explicit yes.
            var fileName = Path.GetFileName(backupFile);
            if (exists.Value &&
                !await _prompt.ConfirmAsync($"Database [{db.DatabaseName}] already exists - replace it with {fileName}?", false, ct))
            {
                reporter.Info($"Kept the existing database [{db.DatabaseName}] - {fileName} was not restored.");
            }
            else
            {
                reporter.Info($"Restoring [{db.DatabaseName}] from {fileName}…");
                var restore = await _sqlContainer.RestoreAsync(db, backupFile, reporter, ct);
                if (!restore.Success)
                    return Result.Fail($"Restoring {fileName} failed: {restore.Error}");
                reporter.Success($"Database [{db.DatabaseName}] restored from {fileName}.");

                // A backup from another environment carries that site's portal aliases; without one for this
                // hostname DNN can't match the request and the site fails to load. Not fatal - it can be
                // added by hand - but the site won't answer at its local address until it is.
                var hostname = _opts.HostnameFor(project.Name);
                var alias = await _sql.RemapPortalAliasesAsync(db.DatabaseName, _opts.HostnameSuffix, hostname, ct);
                if (alias.Success)
                    reporter.Success($"PortalAlias set to {hostname}.");
                else
                    reporter.Fail($"Could not update PortalAlias: {alias.Error}. Add '{hostname}' as a site alias " +
                                  "or the site will not load at that address.");
            }
        }
        else if (exists.Value)
        {
            reporter.Success($"Database [{db.DatabaseName}] already exists on {db.Server} - keeping its data.");
        }
        else
        {
            var create = await _sql.CreateDatabaseAsync(db, ct);
            if (!create.Success)
                return Result.Fail($"Database creation reported an error: {create.Error}");
            created = true;
            reporter.Success($"Created empty database [{db.DatabaseName}] on {db.Server}.");
        }

        if (alreadyLocal)
        {
            reporter.Info("web.config already uses the local SQL container - left unchanged.");
        }
        else if (hasWebConfig)
        {
            reporter.Info(currentConn is not null
                ? $"web.config currently connects to [{currentConn.Database}] on {currentConn.Server}."
                : "web.config has no usable SiteSqlServer connection yet.");

            if (await _prompt.ConfirmAsync($"Point web.config at [{db.DatabaseName}] on the local SQL container?", true, ct))
            {
                var write = _webConfig.WriteSiteSqlServer(webConfigPath,
                    new SiteSqlConnection(db.Server, db.DatabaseName, "sa", _opts.Docker.SaPassword));
                if (write.Success)
                    reporter.Success("web.config updated.");
                else
                    reporter.Fail($"Could not update web.config: {write.Error}");
            }
            else
            {
                reporter.Info($"web.config left unchanged. Connect with: server '{db.Server}', " +
                              $"database '{db.DatabaseName}', user 'sa', password '{_opts.Docker.SaPassword}'.");
            }
        }

        if (created)
            reporter.Info("The database is empty: open the site to run the DNN install wizard, or restore a " +
                          "backup by running 'Existing folder' again with 'local database only' and a backup file.");
        return Result.Ok();
    }
}
