using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class SetupProjectRequest
{
    public required string ProjectName { get; init; }
    public required string ReleaseApiUrl { get; init; }
    public string? Version { get; init; }

    /// <summary>
    /// The caller already confirmed extracting DNN over an existing project folder. When false, an
    /// existing folder is confirmed with the user before anything else runs.
    /// </summary>
    public bool AllowOverwrite { get; init; }
}

public sealed class SetupProjectUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IDnnReleaseService _releases;
    private readonly IDnnPackageInstaller _installer;
    private readonly IProjectScaffolder _scaffolder;
    private readonly IIisManager _iis;
    private readonly IisSiteProvisioner _site;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly ISqlServerService _sql;
    private readonly IHttpConnectivityChecker _http;
    private readonly IPrerequisiteChecker _prereq;
    private readonly IUserPrompt _prompt;
    private readonly ILogger<SetupProjectUseCase> _log;

    public SetupProjectUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IDnnReleaseService releases,
        IDnnPackageInstaller installer,
        IProjectScaffolder scaffolder,
        IIisManager iis,
        IisSiteProvisioner site,
        LocalSqlContainer sqlContainer,
        ISqlServerService sql,
        IHttpConnectivityChecker http,
        IPrerequisiteChecker prereq,
        IUserPrompt prompt,
        ILogger<SetupProjectUseCase> log)
    {
        _opts = opts.Value;
        _projects = projects;
        _releases = releases;
        _installer = installer;
        _scaffolder = scaffolder;
        _iis = iis;
        _site = site;
        _sqlContainer = sqlContainer;
        _sql = sql;
        _http = http;
        _prereq = prereq;
        _prompt = prompt;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(SetupProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var nameCheck = ProjectName.Validate(req.ProjectName);
        if (!nameCheck.Success) return nameCheck;

        try
        {
            var project = _projects.Build(req.ProjectName);

            // Settle an existing folder before the (slow) prerequisite checks, not halfway through.
            if (Directory.Exists(project.ProjectDirectory) && !req.AllowOverwrite)
            {
                reporter.Info($"Project directory already exists: {project.ProjectDirectory}");
                if (!await _prompt.ConfirmAsync("Directory exists. Continue and overwrite?", false, ct))
                    return Result.Fail("Aborted by user.");
            }

            reporter.Step("Step 1: Prerequisites");
            // Docker and IIS are optional. If either is missing we skip the steps that need it
            // and still lay down the project files + env, instead of aborting the whole setup.
            var dockerAvailable = (await _prereq.CheckDockerAsync(reporter, ct)).Success;
            if (!dockerAvailable)
                reporter.Info("Docker not found - skipping SQL Server/database provisioning. " +
                              "Start Docker and re-run setup, or point the site's web.config at your own database.");

            var iisAvailable = _iis.IsAvailable();
            if (iisAvailable)
                await _prereq.EnsureIisFeaturesAsync(reporter, _prompt, ct);
            else
                reporter.Info("IIS not found - skipping website creation. Install IIS and re-run " +
                              "setup to host the site, or use your own web server.");

            reporter.Step("Step 2: Project directory");
            Directory.CreateDirectory(project.ProjectDirectory);
            reporter.Success($"Project directory ready: {project.ProjectDirectory}");

            reporter.Step("Step 3: Determine DNN version");
            var releaseResult = await _releases.GetReleaseAsync(req.ReleaseApiUrl, req.Version, ct);
            if (!releaseResult.Success || releaseResult.Value is null)
                return Result.Fail(releaseResult.Error ?? "Could not resolve a DNN release.");
            var release = releaseResult.Value;
            reporter.Success($"Using DNN {release.Version} ({release.DownloadUrl})");

            reporter.Step("Step 4: Download & extract");
            var extract = await _installer.DownloadAndExtractAsync(release, project.ProjectDirectory, reporter, ct);
            if (!extract.Success) return extract;

            // Drop a DNN-tuned .gitignore next to the freshly extracted site so the project is ready
            // to commit without dragging in runtime data, caches, logs or portal uploads.
            var gitignore = _scaffolder.EnsureGitignore(project.ProjectDirectory);
            if (gitignore.Success)
                reporter.Info("Project .gitignore ready.");
            else
                reporter.Info($"Could not write .gitignore: {gitignore.Error}");

            reporter.Step("Step 5: IIS website");
            var siteCreated = false;
            if (iisAvailable)
                siteCreated = _site.TryCreateSite(project, reporter);
            else
                reporter.Info("Skipped - IIS not available.");

            reporter.Step("Step 6: Database");
            if (dockerAvailable)
            {
                var db = await TryProvisionDatabaseAsync(project, reporter, ct);
                reporter.Info($"In the DNN install wizard, connect to: server '{db.Server}', " +
                              $"database '{db.DatabaseName}', user 'sa', password '{_opts.Docker.SaPassword}'.");
            }
            else
            {
                reporter.Info("Skipped database provisioning - Docker not available. Start Docker and " +
                              "re-run setup to create the database.");
            }

            var url = _opts.SiteUrlFor(req.ProjectName);
            if (siteCreated)
            {
                reporter.Step("Step 7: Verify site");
                var http = await _http.CheckAsync(url, 15, ct);
                if (http.Success)
                    reporter.Success($"HTTP {http.Value} from {url}");
                else
                    reporter.Info($"HTTP probe: {http.Error} (expected before install wizard runs)");
            }

            reporter.Step("Setup complete");
            if (siteCreated)
                reporter.Success($"Open {url} to complete the DNN Installation Wizard.");
            else
                reporter.Success($"DNN files are ready in {project.ProjectDirectory}. " +
                                 "Point a web server (and database) at them to run the install wizard.");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Setup failed for {Project}", req.ProjectName);
            return Result.Fail(ex.Message);
        }
    }

    // Best-effort SQL provisioning: starts/reuses the shared container, waits for SQL, and creates
    // the project's database (by name only; the site connects as sa). Any sub-step failure is
    // reported and skipped (not fatal) so the overall setup can still finish. Returns the database
    // the install wizard should connect to (on the default port if the container never came up).
    private async Task<DatabaseConfig> TryProvisionDatabaseAsync(DnnProject project, IProgressReporter reporter, CancellationToken ct)
    {
        var db = _sqlContainer.DatabaseFor(project, _opts.DatabaseNameFor(project.Name), _opts.Docker.DefaultPort);
        try
        {
            var ready = await _sqlContainer.EnsureReadyAsync(reporter, ct);
            if (!ready.Success)
            {
                reporter.Fail($"{ready.Error} Skipping database setup.");
                return db;
            }
            db = _sqlContainer.DatabaseFor(project, db.DatabaseName, ready.Value);

            var exists = await _sql.DatabaseExistsAsync(db.DatabaseName, ct);
            if (exists.Success && exists.Value &&
                await _prompt.ConfirmAsync($"Database '{db.DatabaseName}' exists. Drop and recreate?", false, ct))
            {
                await _sql.DropDatabaseAsync(db.DatabaseName, ct);
            }
            var created = await _sql.CreateDatabaseAsync(db, ct);
            if (created.Success)
                reporter.Success($"Database '{db.DatabaseName}' ready on {db.Server}.");
            else
                reporter.Fail($"Database creation reported an error: {created.Error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Database provisioning failed for {Project}", project.Name);
            reporter.Fail($"Database setup skipped: {ex.Message}");
        }
        return db;
    }
}
