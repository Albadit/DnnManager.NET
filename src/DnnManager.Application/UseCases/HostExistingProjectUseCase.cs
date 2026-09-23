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

    /// <summary>Also create a database in the local SQL container and point web.config at it.</summary>
    public bool SetupDatabase { get; init; }
}

/// <summary>
/// Hosts a project folder that already holds a DNN site (copied by hand, checked out from git, left
/// over from an earlier setup…): creates its IIS website and, optionally, its local database. The
/// site's files are never downloaded, copied or overwritten.
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

        try
        {
            var project = _projects.Build(req.ProjectName);
            if (!Directory.Exists(project.ProjectDirectory))
                return Result.Fail($"Project folder not found: {project.ProjectDirectory}");

            var webConfigPath = Path.Combine(project.ProjectDirectory, "web.config");
            var hasWebConfig = File.Exists(webConfigPath);
            reporter.Info($"Using the existing files in {project.ProjectDirectory} - nothing is downloaded or overwritten.");
            if (!hasWebConfig)
                reporter.Info("No web.config in the folder - it doesn't look like a DNN site yet. Creating the website anyway.");

            // IIS is the point of this flow, so offer to enable missing features before checking for it.
            reporter.Step("Step 1: IIS website");
            await _prereq.EnsureIisFeaturesAsync(reporter, _prompt, ct);
            var siteCreated = false;
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

            if (req.SetupDatabase)
            {
                reporter.Step("Step 2: Database");
                await SetupDatabaseAsync(project, webConfigPath, hasWebConfig, reporter, ct);
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

    // Best-effort, like setup: every failure is reported and skipped, never thrown, because the website
    // is already in place. Creates the database only when it's missing - existing data is never dropped.
    private async Task SetupDatabaseAsync(
        DnnProject project, string webConfigPath, bool hasWebConfig, IProgressReporter reporter, CancellationToken ct)
    {
        if (!(await _prereq.CheckDockerAsync(reporter, ct)).Success)
        {
            reporter.Info("Docker not available - skipping the database. Start Docker and run this again.");
            return;
        }

        var ready = await _sqlContainer.EnsureReadyAsync(reporter, ct);
        if (!ready.Success)
        {
            reporter.Fail($"{ready.Error} Skipping database setup.");
            return;
        }
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
        {
            reporter.Fail($"Could not check for database [{db.DatabaseName}]: {exists.Error}");
            return;
        }

        var created = false;
        if (exists.Value)
        {
            reporter.Success($"Database [{db.DatabaseName}] already exists on {db.Server} - keeping its data.");
        }
        else
        {
            var create = await _sql.CreateDatabaseAsync(db, ct);
            if (!create.Success)
            {
                reporter.Fail($"Database creation reported an error: {create.Error}");
                return;
            }
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
                          "backup via 'Database (backup / overwrite)' > 'Overwrite database'.");
    }
}
