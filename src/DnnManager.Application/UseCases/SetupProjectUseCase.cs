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

    /// <summary>The host name the site answers on; null for <c>{project}.{hostname suffix}</c>.</summary>
    public string? HostName { get; init; }

    /// <summary>The port the site answers on; null for the settings' site port.</summary>
    public int? Port { get; init; }

    public DnnInstallMode InstallMode { get; init; } = DnnInstallMode.Manual;

    /// <summary>The host account and website an automatic install creates.</summary>
    public DnnAccount? Account { get; init; }

    /// <summary>The site's database; null for a database named like the project on the local SQL container.</summary>
    public DatabaseConnection? Database { get; init; }
}

/// <summary>
/// "New project": downloads a DNN release into a folder of its own, creates its IIS website and its database, and -
/// with <see cref="DnnInstallMode.Automatic"/> - installs DNN, so the first visit shows the new site instead of DNN's
/// installation wizard. Everything that can be checked is checked before anything is created.
/// </summary>
public sealed class SetupProjectUseCase(
    IOptions<AppOptions> opts,
    IProjectRepository projects,
    IDnnReleaseService releases,
    IDnnPackageInstaller packages,
    IProjectScaffolder scaffolder,
    IIisManager iis,
    IisSiteProvisioner site,
    LocalSqlContainer sqlContainer,
    ISqlServerService sql,
    IDatabaseProvisioner databases,
    IDnnInstaller dnn,
    IWebConfigService webConfig,
    IProjectRecords records,
    IHttpConnectivityChecker http,
    IPrerequisiteChecker prereq,
    IUserPrompt prompt,
    ILogger<SetupProjectUseCase> log,
    OperationUndo undo)
{
    private readonly AppOptions _opts = opts.Value;
    private readonly IProjectRepository _projects = projects;
    private readonly IDnnReleaseService _releases = releases;
    private readonly IDnnPackageInstaller _packages = packages;
    private readonly IProjectScaffolder _scaffolder = scaffolder;
    private readonly IIisManager _iis = iis;
    private readonly IisSiteProvisioner _site = site;
    private readonly LocalSqlContainer _sqlContainer = sqlContainer;
    private readonly ISqlServerService _sql = sql;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly IDnnInstaller _dnn = dnn;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly IProjectRecords _records = records;
    private readonly IHttpConnectivityChecker _http = http;
    private readonly IPrerequisiteChecker _prereq = prereq;
    private readonly IUserPrompt _prompt = prompt;
    private readonly ILogger<SetupProjectUseCase> _log = log;
    private readonly OperationUndo _undo = undo;

    /// <summary>The set-up's stages, by their short names in the Output tab's stage list.</summary>
    private static class Stage
    {
        public const string Version = "DNN version", Iis = "Check IIS", Database = "Test database", Folder = "Create project folder",
            Download = "Download DNN", Site = "Create IIS site", CreateDatabase = "Create database", Configure = "Configure DNN",
            Install = "Install DNN", Host = "Create host account", Start = "Start website", Verify = "Verify site";
    }

    public async Task<Result> ExecuteAsync(SetupProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var nameCheck = ProjectName.Validate(req.ProjectName);
        if (!nameCheck.Success) return nameCheck;

        var automatic = req.InstallMode == DnnInstallMode.Automatic;
        if (automatic)
        {
            if (req.Account is null) return Result.Fail("An automatic install needs the host account and the website.");
            var problems = DnnAccountRules.Problems(req.Account);
            if (problems.Count > 0) return Result.Fail(string.Join(" ", problems));
        }

        var hostName = string.IsNullOrWhiteSpace(req.HostName) ? _opts.HostnameFor(req.ProjectName) : req.HostName.Trim();
        var port = req.Port ?? _opts.SitePort;
        var alias = DnnSiteAddress.AliasFor(hostName, port);
        var url = $"http://{alias}";
        var project = _projects.Build(req.ProjectName);
        var siteDirectory = project.ProjectDirectory;
        // The site's database: the one chosen, or as before a database named like the project on the local container.
        var database = req.Database ?? _sqlContainer.Connection(_opts.DatabaseNameFor(project.Name));
        var chosenDatabase = req.Database is not null;

        try
        {
            // A new project gets a folder of its own - an existing one is set up with Host project instead.
            if (Directory.Exists(siteDirectory))
                return Result.Fail($"A project named '{req.ProjectName}' already exists ({siteDirectory}). " +
                                   "Choose another name, or set it up on Host project.");
            // Nor does it take over an IIS site of that name serving another folder: making its site would replace that one.
            if (_site.SiteNameTaken(req.ProjectName, siteDirectory) is { } taken)
                return Result.Fail(taken);
            if (automatic && siteDirectory.Length > DnnAccountRules.MaxSitePathLength)
                return Result.Fail($"The project's folder is too deep for DNN's installer ({siteDirectory.Length} characters, at most " +
                                   $"{DnnAccountRules.MaxSitePathLength}) - use a shorter projects folder (Settings → Projects).");

            // The stages, up front: the Output tab shows those still to come - and as skipped those it never gets to.
            reporter.Plan(automatic
                ? [Stage.Version, Stage.Iis, Stage.Database, Stage.Folder, Stage.Download, Stage.Site, Stage.CreateDatabase,
                   Stage.Configure, Stage.Install, Stage.Host, Stage.Start]
                : [Stage.Version, Stage.Iis, Stage.Database, Stage.Folder, Stage.Download, Stage.Site, Stage.CreateDatabase, Stage.Verify]);
            reporter.Context(database.Server);

            reporter.Step("Step 1: DNN version", Stage.Version);
            var releaseResult = await _releases.GetReleaseAsync(req.ReleaseApiUrl, req.Version, ct);
            if (!releaseResult.Success || releaseResult.Value is null)
                return Result.Fail(releaseResult.Error ?? "Could not resolve a DNN release.");
            var release = releaseResult.Value;
            reporter.Success($"Using DNN {release.Version} ({release.DownloadUrl})");

            reporter.Step("Step 2: Checking IIS", Stage.Iis);
            var iisAvailable = _iis.IsAvailable();
            if (iisAvailable)
            {
                // Before anything is downloaded: a second site with the same address wouldn't start, and DNN's installer
                // would talk to the first one. New project says so as it is typed; this is for what changed since.
                if (IisHostNames.SiteUsing(_iis.GetSiteRuntimes() ?? new Dictionary<string, IisSiteRuntime>(), hostName, port, project.Name) is { } other)
                    return Result.Fail($"{url} is already the address of the IIS site '{other}' - choose another host name or port.");
                await _prereq.EnsureIisFeaturesAsync(reporter, _prompt, ct);
            }
            else if (automatic)
            {
                return Result.Fail("An automatic install needs IIS - set it up in Settings → IIS, or choose Manual DNN setup.");
            }
            else
            {
                reporter.Info("IIS not found - skipping website creation. Install IIS and re-run " +
                              "setup to host the site, or use your own web server.");
            }

            reporter.Step("Step 3: Testing database connection", Stage.Database);
            var databaseReady = await CheckDatabaseAsync(project.Name, database, release, automatic, reporter, ct);
            if (!databaseReady.Success && (automatic || chosenDatabase))
                return Result.Fail($"The database isn't ready: {databaseReady.Error}");
            var createDatabase = databaseReady.Success && databaseReady.Value;
            if (!databaseReady.Success)
                reporter.Info("Skipping the database. Start the SQL Server container and set the project up again, " +
                              "or point the site's web.config at your own database.");

            reporter.Step("Step 4: Creating project directory", Stage.Folder);
            // A cancel takes the new project away again - its folder goes last, once nothing uses it any more.
            _undo.DeleteFolderOnUndo(siteDirectory);
            Directory.CreateDirectory(siteDirectory);
            reporter.Success($"Project directory ready: {siteDirectory}");

            reporter.Step($"Step 5: Downloading DNN {release.Version}", Stage.Download);
            var extract = await _packages.DownloadAndExtractAsync(release, siteDirectory, reporter, ct);
            if (!extract.Success) return extract;
            // Drop a DNN-tuned .gitignore next to the freshly extracted site so the project is ready
            // to commit without dragging in runtime data, caches, logs or portal uploads.
            var gitignore = _scaffolder.EnsureGitignore(siteDirectory);
            if (gitignore.Success) reporter.Info("Project .gitignore ready.");
            else reporter.Info($"Could not write .gitignore: {gitignore.Error}");

            if (automatic)
            {
                var package = _dnn.CheckPackage(siteDirectory);
                if (!package.Success) return package;
            }
            if (database.Kind == DatabaseKind.LocalDbFile && databaseReady.Success)
            {
                reporter.Info($@"Preparing the database file App_Data\{database.Database}…");
                var prepared = await _dnn.PrepareLocalDbFileAsync(siteDirectory, database, ct);
                if (!prepared.Success) return prepared;
            }

            reporter.Step("Step 6: Creating IIS application pool and website", Stage.Site);
            var siteCreated = false;
            if (iisAvailable)
            {
                reporter.Info($"Creating IIS application pool and website '{project.Name}' for {url}…");
                siteCreated = _site.TryCreateSite(project, hostName, port, reporter);
                if (siteCreated) reporter.Context($"IIS · {alias}");
                if (siteCreated && database.Kind == DatabaseKind.LocalDbFile)
                {
                    var profile = _iis.EnableUserProfile(project.Name);
                    if (!profile.Success) reporter.Fail($"Could not load the app pool's user profile, which LocalDB needs: {profile.Error}");
                }
            }
            else
            {
                reporter.Info("Skipped - IIS not available.");
            }
            if (automatic && !siteCreated)
                return Result.Fail("The IIS website couldn't be created, so DNN can't be installed - see the messages above.");

            reporter.Step("Step 7: Creating database", Stage.CreateDatabase);
            if (databaseReady.Success)
            {
                var created = await ProvisionDatabaseAsync(project, database, createDatabase, siteCreated, reporter, ct);
                if (!created.Success && (automatic || chosenDatabase)) return created;
            }
            else
            {
                reporter.Info("Skipped - the database isn't reachable.");
            }

            if (!automatic)
                return await FinishManualAsync(project, release, database, databaseReady.Success, siteCreated, url, reporter, ct);

            var account = req.Account!;
            reporter.Step("Step 8: Configuring DNN", Stage.Configure);
            // On the local container the site signs in with a login of its own, owner of its database only - not sa.
            if (database.Kind == DatabaseKind.Container && createDatabase)
            {
                var login = await _sqlContainer.GrantSiteLoginAsync(project, _sqlContainer.DatabaseFor(project, database.Database, _opts.Docker.DefaultPort), ct);
                if (!login.Success) return login.WithoutValue();
                _undo.Add($"Drop the login {login.Value!.User}", () => _sql.DropLoginAsync(login.Value.User, CancellationToken.None));
                database = database with { User = login.Value.User, Password = login.Value.Password };
            }
            var configured = _webConfig.WriteDatabaseConnection(Path.Combine(siteDirectory, "web.config"), database);
            if (!configured.Success) return Result.Fail($"Could not write the site's connection string: {configured.Error}");
            reporter.Success($"web.config connects to {database.Describe()}.");

            reporter.Step("Step 9: Running DNN installation", Stage.Install);
            var site = new DnnSiteAddress(siteDirectory, alias, port);
            var installStarted = DateTime.UtcNow;
            var installed = await _dnn.InstallAsync(site, account, database, reporter, ct);
            if (!installed.Success)
            {
                _dnn.CleanUp(siteDirectory, installed: false);
                // Left to look into: the folder, the site and the database stay, as the message says.
                _undo.Keep();
                return Result.Fail($"DNN's installation failed: {installed.Error} The project is left as it is to look into - " +
                                   "remove it and set it up again (DNN can't install twice into the same files and database).");
            }

            reporter.Step("Step 10: Creating host account", Stage.Host);
            // A LocalDB file database is only opened by DNN Manager while the site doesn't use it.
            if (database.Kind == DatabaseKind.LocalDbFile) _iis.StopSite(project.Name);
            Result completed;
            try
            {
                completed = await _dnn.CompleteAsync(site, account, database, ct);
            }
            finally
            {
                // Started again whatever happened: a site left stopped looks broken.
                if (database.Kind == DatabaseKind.LocalDbFile) _iis.StartSite(project.Name);
                else _iis.RecycleAppPool(project.Name); // DNN caches its users - it reads the host account again
            }
            if (!completed.Success)
            {
                _dnn.CleanUp(siteDirectory, installed: false);
                _undo.Keep();
                return Result.Fail($"DNN's installation didn't finish: {completed.Error} The project is left as it is to look into - " +
                                   "remove it and set it up again.");
            }
            reporter.Success($"Host account '{account.UserName}' ready - it signs in without being asked to change its password.");

            reporter.Step("Step 11: Starting website", Stage.Start);
            var warmUp = await _dnn.WarmUpAsync(site, installStarted, reporter, ct);
            _dnn.CleanUp(siteDirectory, installed: true);
            _records.Save(new ProjectRecord(project.Name, DnnInstallMode.Automatic, DateTime.UtcNow, release.Version, account.UserName));
            // Installed: a site that is slow to answer is still the project.
            _undo.Keep();
            if (!warmUp.Success) return Result.Fail($"DNN is installed, but {Lower(warmUp.Error)}");

            reporter.Step("Setup complete");
            reporter.Link(url);
            reporter.Success($"DNN installation completed. Open {url} - sign in as '{account.UserName}'.");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Setup failed for {Project}", req.ProjectName);
            return Result.Fail(ex.Message);
        }
        finally
        {
            // Whatever happened: the install template holds the host password, and runs DNN's installer for anyone.
            if (automatic && Directory.Exists(siteDirectory)) _dnn.CleanUp(siteDirectory, installed: false);
        }
    }

    /// <summary>
    /// Tests the database: reachable, signed in, new enough for <paramref name="release"/>, and usable for the new site.
    /// A database of that name that already exists can be dropped and created again, after asking. Returns whether the
    /// database still has to be created.
    /// </summary>
    private async Task<Result<bool>> CheckDatabaseAsync(string siteName, DatabaseConnection database, DnnRelease release, bool automatic,
        IProgressReporter reporter, CancellationToken ct)
    {
        // With Windows authentication the site signs in as its app pool's identity (IIS APPPOOL\<site> for a new site).
        var options = new DatabaseCheckOptions(
            ForNewInstall: true,
            SiteLogin: database is { Kind: DatabaseKind.SqlServer, UsesWindowsAuthentication: true } ? _iis.AppPoolIdentity(siteName) : null,
            MinimumMajorVersion: MinimumSqlServerFor(release));

        if (database.Kind != DatabaseKind.LocalDbFile)
        {
            var exists = await _databases.DatabaseExistsAsync(database, ct);
            if (exists is { Success: true, Value: true })
            {
                if (await _prompt.ConfirmDangerAsync($"Database [{database.Database}] already exists on {database.Server}. " +
                                                     "Drop it and create it again? Everything in it is lost.",
                                                     "Drop and recreate", "Keep it", ct))
                {
                    var dropped = await _databases.DropDatabaseAsync(database, ct);
                    if (!dropped.Success) return Result<bool>.Fail(dropped.Error!);
                    _undo.CannotUndo($"database [{database.Database}] on {database.Server} was dropped, as you chose - what was in it is gone.");
                    reporter.Info($"Dropped database [{database.Database}].");
                }
                else if (!automatic)
                {
                    // As before: a manual install can go into the database that is there.
                    reporter.Info($"Keeping database [{database.Database}] as it is.");
                    return Result<bool>.Ok(false);
                }
            }
        }

        var report = await _databases.CheckAsync(database, options, ct);
        foreach (var check in report.Checks)
        {
            var line = $"{check.Name}: {check.Detail}";
            switch (check.Outcome)
            {
                case CheckOutcome.Passed: reporter.Success(line); break;
                case CheckOutcome.Warning: reporter.Warn(line); break;
                default: reporter.Fail(line); break;
            }
        }
        if (!report.Passed) return Result<bool>.Fail(report.Problem ?? "The database test failed.");
        if (database.Kind == DatabaseKind.LocalDbFile) return Result<bool>.Ok(false);
        var stillThere = await _databases.DatabaseExistsAsync(database, ct);
        return Result<bool>.Ok(!(stillThere.Success && stillThere.Value));
    }

    /// <summary>DNN 10 needs SQL Server 2017 (14) or later; DNN 9 runs on SQL Server 2012 (11) and later.</summary>
    private static int MinimumSqlServerFor(DnnRelease release) =>
        int.TryParse(release.Version.Split('.')[0].TrimStart('v', 'V'), out var major) && major < 10 ? 11 : 14;

    /// <summary>
    /// Creates the database when it isn't there and, with Windows authentication, lets the site's app pool identity own
    /// it. A LocalDB file database needs neither: it is the package's own file, attached by the site.
    /// </summary>
    private async Task<Result> ProvisionDatabaseAsync(DnnProject project, DatabaseConnection database, bool create, bool siteCreated,
        IProgressReporter reporter, CancellationToken ct)
    {
        if (database.Kind == DatabaseKind.LocalDbFile)
        {
            reporter.Success($@"The site's database is App_Data\{database.Database} - LocalDB attaches it when the site first opens it.");
            return Result.Ok();
        }
        if (create)
        {
            // Before it is made: a cancel while it is being created drops it too.
            _undo.Add($"Drop database [{database.Database}] on {database.Server}",
                () => _databases.DropDatabaseAsync(database, CancellationToken.None));
            reporter.Info($"Creating database [{database.Database}] on {database.Server}…");
            var created = await _databases.CreateDatabaseAsync(database,
                database.Kind == DatabaseKind.Container ? _opts.Docker.Collation : null, ct);
            if (!created.Success)
            {
                reporter.Fail(created.Error!);
                return created;
            }
            reporter.Success($"Database [{database.Database}] ready on {database.Server}.");
        }
        if (database.UsesWindowsAuthentication && siteCreated)
        {
            var identity = _iis.AppPoolIdentity(project.Name);
            reporter.Info($"Giving the site's identity ({identity}) access to the database…");
            var granted = await _databases.GrantSiteAccessAsync(database, identity, ct);
            if (!granted.Success)
            {
                reporter.Fail(granted.Error!);
                return granted;
            }
            reporter.Success($"{identity} owns [{database.Database}].");
        }
        return Result.Ok();
    }

    /// <summary>Manual DNN setup: everything is in place for DNN's installation wizard, which the user runs on the first visit.</summary>
    private async Task<Result> FinishManualAsync(DnnProject project, DnnRelease release, DatabaseConnection database, bool databaseReady,
        bool siteCreated, string url, IProgressReporter reporter, CancellationToken ct)
    {
        if (databaseReady)
            reporter.Info($"In DNN's installation wizard, connect to: server '{database.Server}', " +
                          (database.Kind == DatabaseKind.LocalDbFile
                              ? $"the database file {database.Database} (SQL Server Express File)."
                              : $"database '{database.Database}', " + (database.UsesWindowsAuthentication
                                  ? "Windows authentication (integrated security)."
                                  : database.Kind == DatabaseKind.Container
                                      ? "user 'sa' and the SA password from Settings → Database server."
                                      : $"user '{database.User}' and its password.")));

        if (siteCreated)
        {
            reporter.Step("Verify site", Stage.Verify);
            var http = await _http.CheckAsync(url, 15, ct);
            if (http.Success) reporter.Success($"HTTP {http.Value} from {url}");
            else reporter.Info($"HTTP probe: {http.Error} (expected before install wizard runs)");
        }

        _records.Save(new ProjectRecord(project.Name, DnnInstallMode.Manual, DateTime.UtcNow, release.Version, null));
        reporter.Step("Setup complete");
        if (siteCreated)
        {
            reporter.Link(url);
            reporter.Success($"Open {url} to complete the DNN Installation Wizard.");
        }
        else
            reporter.Success($"DNN files are ready in {project.ProjectDirectory}. " +
                             "Point a web server (and database) at them to run the install wizard.");
        return Result.Ok();
    }

    private static string Lower(string? text) =>
        string.IsNullOrEmpty(text) ? "something went wrong." : char.ToLowerInvariant(text[0]) + text[1..];
}
