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
}

public sealed class SetupProjectUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IDnnReleaseService _releases;
    private readonly IDnnPackageInstaller _installer;
    private readonly IProjectScaffolder _scaffolder;
    private readonly IIisManager _iis;
    private readonly IDockerService _docker;
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
        IDockerService docker,
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
        _docker = docker;
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

            var project = _projects.Build(req.ProjectName);
            var hostname = $"{req.ProjectName}.{_opts.HostnameSuffix}";

            reporter.Step("Step 2: Project directory");
            Directory.CreateDirectory(_opts.BaseDirectory);
            if (Directory.Exists(project.ProjectDirectory))
            {
                reporter.Info($"Project directory already exists: {project.ProjectDirectory}");
                if (!await _prompt.ConfirmAsync("Directory exists. Continue and overwrite?", false, ct))
                    return Result.Fail("Aborted by user.");
            }
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
            {
                _iis.RemoveSite(req.ProjectName);
                var create = _iis.CreateSite(req.ProjectName, project.ProjectDirectory, hostname, _opts.SitePort);
                if (!create.Success)
                {
                    reporter.Fail($"IIS site creation failed: {create.Error}. Continuing without a website.");
                }
                else
                {
                    _iis.GrantPermissions(project.ProjectDirectory, new[]
                    {
                        "IIS_IUSRS",
                        "IUSR",
                        $"IIS APPPOOL\\{req.ProjectName}"
                    });
                    siteCreated = true;
                    reporter.Success($"IIS site '{req.ProjectName}' bound to http://{hostname}");
                }
            }
            else
            {
                reporter.Info("Skipped - IIS not available.");
            }

            reporter.Step("Step 6: Database");
            // Provision the SQL container/database only when Docker is present; otherwise fall back
            // to the default port for the wizard instructions.
            var port = dockerAvailable
                ? await TryProvisionDatabaseAsync(req, project, reporter, ct)
                : _opts.Docker.DefaultPort;
            if (!dockerAvailable)
            {
                reporter.Info("Skipped database provisioning \u2014 Docker not available. Start Docker and " +
                              "re-run setup to create the database.");
            }
            else
            {
                reporter.Info($"In the DNN install wizard, connect to: server '{_opts.Docker.ContainerIp},{port}', " +
                              $"database '{req.ProjectName}{_opts.Docker.DefaultDbNameSuffix}', " +
                              $"user 'sa', password '{_opts.Docker.SaPassword}'.");
            }

            if (siteCreated)
            {
                reporter.Step("Step 7: Start site & verify");
                _iis.StartSite(req.ProjectName);
                var http = await _http.CheckAsync($"http://{hostname}", 15, ct);
                if (http.Success)
                    reporter.Success($"HTTP {http.Value} from http://{hostname}");
                else
                    reporter.Info($"HTTP probe: {http.Error} (expected before install wizard runs)");
            }

            reporter.Step("Setup complete");
            if (siteCreated)
                reporter.Success($"Open http://{hostname} to complete the DNN Installation Wizard.");
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

    private DatabaseConfig MakeDbConfig(SetupProjectRequest req, DnnProject project, int port) =>
        new(
            Server: $"{_opts.Docker.ContainerIp},{port}",
            DatabaseName: req.ProjectName + _opts.Docker.DefaultDbNameSuffix,
            Collation: _opts.Docker.Collation,
            Port: port,
            BackupDirectory: project.BackupDirectory);

    // Best-effort SQL provisioning: starts/reuses the shared container, waits for SQL, and creates
    // the project's database (by name only; the site connects as sa). Any sub-step failure is
    // reported and skipped (not fatal) so the overall setup can still finish; returns the published port.
    private async Task<int> TryProvisionDatabaseAsync(
        SetupProjectRequest req, DnnProject project, IProgressReporter reporter, CancellationToken ct)
    {
        var containerName = _opts.Docker.ContainerName;
        var port = _opts.Docker.DefaultPort;
        try
        {
            var containerExists = await _docker.DoesContainerExistAsync(containerName, ct);
            var containerRunning = containerExists && await _docker.IsContainerRunningAsync(containerName, ct);

            if (containerExists)
            {
                // Reuse the existing shared SQL Server container. Just provision a new database inside it.
                if (!containerRunning)
                {
                    reporter.Info($"Container '{containerName}' exists but is stopped - starting it.");
                    var start = await _docker.StartContainerAsync(containerName, ct);
                    if (!start.Success)
                    {
                        reporter.Fail($"Could not start container '{containerName}': {start.Error}. Skipping database setup.");
                        return port;
                    }
                }
                else
                {
                    reporter.Info($"Reusing running SQL Server container '{containerName}'.");
                }
            }
            else
            {
                // Bring up the shared container from the docker-compose.yml shipped with the app.
                var up = await _docker.ComposeUpAsync(ct);
                if (!up.Success)
                {
                    reporter.Fail($"docker compose up failed: {up.Error}. Skipping database setup.");
                    return port;
                }
            }

            // The shared container publishes the fixed port (1433); read it back to be certain.
            var existingPort = await _docker.GetPublishedPortAsync(containerName, ct);
            if (existingPort is null)
            {
                reporter.Fail($"Could not determine published port for '{containerName}'. Skipping database setup.");
                return port;
            }
            port = existingPort.Value;

            var ready = await _sql.WaitReadyAsync(180, reporter, ct);
            if (!ready.Success)
            {
                reporter.Fail($"SQL Server did not become ready: {ready.Error}. Skipping database setup.");
                return port;
            }

            var db = MakeDbConfig(req, project, port);
            var exists = await _sql.DatabaseExistsAsync(db.DatabaseName, ct);
            if (exists.Success && exists.Value &&
                await _prompt.ConfirmAsync($"Database '{db.DatabaseName}' exists. Drop and recreate?", false, ct))
            {
                await _sql.DropDatabaseAsync(db.DatabaseName, ct);
            }
            var created = await _sql.CreateDatabaseAsync(db, ct);
            if (created.Success)
                reporter.Success($"Database '{db.DatabaseName}' ready on {_opts.Docker.ContainerIp},{port}.");
            else
                reporter.Fail($"Database creation reported an error: {created.Error}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Database provisioning failed for {Project}", req.ProjectName);
            reporter.Fail($"Database setup skipped: {ex.Message}");
        }
        return port;
    }
}
