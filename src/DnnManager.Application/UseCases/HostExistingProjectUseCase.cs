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

    /// <summary>
    /// A new project made from a copy of another one (Import): its database is always its own - named after the
    /// project - and web.config is always pointed at it, whatever database the copy's web.config names.
    /// </summary>
    public bool OwnDatabase { get; init; }
}

/// <summary>
/// Hosts a project folder that already holds a DNN site (copied by hand, checked out from git, left
/// over from an earlier setup…): creates its IIS website, its local database, or both. The site's
/// files are never downloaded, copied or overwritten.
/// </summary>
public sealed class HostExistingProjectUseCase(
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
    ILogger<HostExistingProjectUseCase> log,
    OperationUndo undo,
    SiteDatabases siteDatabases,
    IDatabaseProvisioner databases)
{
    private readonly AppOptions _opts = opts.Value;
    private readonly IProjectRepository _projects = projects;
    private readonly IIisManager _iis = iis;
    private readonly IisSiteProvisioner _site = site;
    private readonly LocalSqlContainer _sqlContainer = sqlContainer;
    private readonly ISqlServerService _sql = sql;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly IHttpConnectivityChecker _http = http;
    private readonly IPrerequisiteChecker _prereq = prereq;
    private readonly IUserPrompt _prompt = prompt;
    private readonly ILogger<HostExistingProjectUseCase> _log = log;
    private readonly OperationUndo _undo = undo;
    private readonly SiteDatabases _siteDatabases = siteDatabases;
    private readonly IDatabaseProvisioner _databases = databases;

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

            // A site of this name that serves another folder is another project's - never replaced from here.
            if (req.SetupIis && _site.SiteNameTaken(req.ProjectName, project.ProjectDirectory) is { } taken)
                return Result.Fail(taken);

            var webConfigPath = Path.Combine(project.ProjectDirectory, "web.config");
            var hasWebConfig = File.Exists(webConfigPath);
            reporter.Info($"Using the existing files in {project.ProjectDirectory} - nothing is downloaded or overwritten.");
            if (!hasWebConfig)
                reporter.Info("No web.config in the folder - it doesn't look like a DNN site yet. Continuing anyway.");

            var step = 0;
            var siteCreated = false;
            IReadOnlyList<string> disabledRules = Array.Empty<string>();
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
                    disabledRules = DisableHttpsRedirects(webConfigPath, reporter);
                }
                else
                {
                    reporter.Fail("IIS is not available on this machine - enable it in Settings → IIS and retry.");
                }
            }

            if (req.SetupDatabase)
            {
                reporter.Step($"Step {++step}: Database");
                var db = await SetupDatabaseAsync(project, webConfigPath, hasWebConfig, req.BackupFilePath, req.OwnDatabase, reporter, ct);
                if (!db.Success)
                {
                    // Alongside a website the database is a best-effort extra - unless its data was asked for (a backup to
                    // restore, Import): a site without it isn't what was asked for. On its own it is the whole job.
                    if (!req.SetupIis || req.BackupFilePath is not null || req.OwnDatabase) return db;
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
            if (disabledRules.Count > 0)
                reporter.Warn(ProductionReminder(disabledRules));
            return Result.Ok();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled, not failed: the runner says so and undoes what was done.
            _log.LogInformation("Hosting existing project {Project} cancelled", req.ProjectName);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hosting existing project {Project} failed", req.ProjectName);
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// A production web.config often redirects every request to https://. The local site is HTTP-only
    /// (and with IIS URL Rewrite installed the rule is live), so it would never load - switch such rules
    /// off and say so loudly, since they must be back on before the site goes to production.
    /// </summary>
    private IReadOnlyList<string> DisableHttpsRedirects(string webConfigPath, IProgressReporter reporter)
    {
        // The site's own file: a cancel puts it back as it was.
        if (File.Exists(webConfigPath)) _undo.RestoreFileOnUndo(webConfigPath);
        var result = _webConfig.DisableHttpsRedirectRules(webConfigPath);
        if (!result.Success)
        {
            reporter.Fail($"Could not check web.config for HTTPS redirects: {result.Error}");
            return Array.Empty<string>();
        }
        var rules = result.Value!;
        if (rules.SwitchedOff.Count > 0)
            reporter.Info($"Switched off the HTTPS redirect rule{Plural(rules.SwitchedOff)} {Quoted(rules.SwitchedOff)} in web.config - " +
                          "the local site has no HTTPS, so it would redirect to an address that doesn't answer.");
        if (rules.AlreadyOff.Count > 0)
            reporter.Info($"The HTTPS redirect rule{Plural(rules.AlreadyOff)} {Quoted(rules.AlreadyOff)} in web.config " +
                          $"{(rules.AlreadyOff.Count == 1 ? "was" : "were")} already switched off for local development.");

        var all = rules.All;
        if (all.Count > 0) reporter.Warn(ProductionReminder(all));
        return all;
    }

    private static string Plural(IReadOnlyList<string> names) => names.Count == 1 ? "" : "s";

    private static string ProductionReminder(IReadOnlyList<string> names) =>
        $"Before deploying this site to production, switch {Quoted(names)} in web.config back on " +
        "(remove enabled=\"false\" - look for \"Disabled by DNN Manager\").";

    private static string Quoted(IReadOnlyList<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    // Restores backupFile into the database when one is given (asking first if the database already
    // exists); otherwise creates the database only when it's missing. Existing data is never dropped
    // without a yes. Failures come back as a result for the caller to report, never thrown. A declined or
    // failed web.config update is reported but not a failure: the database itself is ready and its
    // connection details are shown.
    private async Task<Result> SetupDatabaseAsync(DnnProject project, string webConfigPath, bool hasWebConfig,
        string? backupFile, bool ownDatabase, IProgressReporter reporter, CancellationToken ct)
    {
        if (backupFile is not null)
        {
            if (!File.Exists(backupFile)) return Result.Fail($"Backup file not found: {backupFile}");
            if (!LocalSqlContainer.IsBackupFile(backupFile))
                return Result.Fail($"Not a .bacpac or .bak file: {backupFile}");
        }

        var ready = await _sqlContainer.CheckAsync(reporter, ct);
        if (!ready.Success) return Result.Fail(ready.Error ?? "The local SQL Server is not reachable.");
        var port = ready.Value;

        // When web.config already points at a database of its own in the local container, keep it (creating it if
        // it's gone) and leave web.config alone. Otherwise - a production connection string, the DNN package's LocalDB
        // placeholder, no web.config, a copy that must have its own (Import), or a database another IIS site uses -
        // use this project's conventional database, so two sites copied from the same source never share one.
        var current = hasWebConfig ? _webConfig.ReadSiteSqlServer(webConfigPath) : null;
        var currentConn = current is { Success: true } ? current.Value : null;
        var alreadyLocal = !ownDatabase && currentConn is { Database.Length: > 0 } && _sqlContainer.IsLocalContainer(currentConn.Server, port);
        if (alreadyLocal && _siteDatabases.OtherSiteUsing(project.Name, currentConn!.Server, currentConn.Database) is { } other)
        {
            reporter.Info($"web.config names [{currentConn.Database}], the database of the IIS site '{other}' - " +
                          $"this project gets a database of its own, [{_opts.DatabaseNameFor(project.Name)}].");
            alreadyLocal = false;
        }
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
                !await _prompt.ConfirmDangerAsync($"Database [{db.DatabaseName}] already exists - replace it with {fileName}?",
                    "Replace database", "Keep existing", ct))
            {
                reporter.Info($"Kept the existing database [{db.DatabaseName}] - {fileName} was not restored.");
            }
            else
            {
                // Restored beside the database that is there, under a name of its own, and swapped in only once it is
                // complete: a failed or cancelled restore leaves that one as it was. One restored into new is dropped
                // again by a cancel or a failure.
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var incoming = exists.Value ? db with { DatabaseName = $"{db.DatabaseName}_restore_{stamp}" } : db;
                _undo.Add($"Drop database [{incoming.DatabaseName}]", undoCt => _sql.DropDatabaseAsync(incoming.DatabaseName, undoCt));
                reporter.Info(exists.Value
                    ? $"Restoring {fileName} beside [{db.DatabaseName}] - it replaces it once it is complete…"
                    : $"Restoring [{db.DatabaseName}] from {fileName}…");
                var restore = await _sqlContainer.RestoreAsync(incoming, backupFile, reporter, ct);
                if (!restore.Success)
                    return Result.Fail($"Restoring {fileName} failed: {restore.Error}" +
                                       (exists.Value ? $" [{db.DatabaseName}] is left as it was." : ""));
                reporter.Success($"Database [{incoming.DatabaseName}] restored from {fileName}.");

                // A backup from another environment carries that site's portal aliases; without one for this
                // hostname DNN can't match the request and the site fails to load. Not fatal - it can be
                // added by hand - but the site won't answer at its local address until it is.
                // The address the site is bound to - with its port when that isn't 80, or DNN doesn't match the request.
                var hostname = DnnSiteAddress.AliasFor(_opts.HostnameFor(project.Name), _opts.SitePort);
                var alias = await _sql.RemapPortalAliasesAsync(incoming.DatabaseName, _opts.HostnameSuffix, hostname, ct);
                if (alias.Success)
                    reporter.Success($"PortalAlias set to {hostname}.");
                else
                    reporter.Fail($"Could not update PortalAlias: {alias.Error}. Add '{hostname}' as a site alias " +
                                  "or the site will not load at that address.");

                await DisableSslAsync(_sql, incoming.DatabaseName, reporter, ct);

                if (exists.Value)
                {
                    var swapped = await LocalDatabaseSwap.SwapInAsync(_sqlContainer, _databases, _sql, _undo, db.DatabaseName,
                        incoming.DatabaseName, $"{db.DatabaseName}_before_restore_{stamp}", $"the database restored from {fileName}", reporter, ct);
                    if (!swapped.Success) return swapped;
                }
            }
        }
        else if (exists.Value)
        {
            reporter.Success($"Database [{db.DatabaseName}] already exists on {db.Server} - keeping its data.");
        }
        else
        {
            _undo.Add($"Drop database [{db.DatabaseName}]", undoCt => _sql.DropDatabaseAsync(db.DatabaseName, undoCt));
            var create = await _sql.CreateDatabaseAsync(db, ct);
            if (!create.Success)
                return Result.Fail($"Database creation reported an error: {create.Error}");
            created = true;
            reporter.Success($"Created empty database [{db.DatabaseName}] on {db.Server}.");
        }

        if (alreadyLocal)
        {
            reporter.Info($"web.config already uses [{db.DatabaseName}] on {db.Server} - left unchanged.");
        }
        else if (hasWebConfig)
        {
            reporter.Info(currentConn is not null
                ? $"web.config currently connects to [{currentConn.Database}] on {currentConn.Server}."
                : "web.config has no usable SiteSqlServer connection yet.");

            // A copy that must have a database of its own isn't asked: left as it is, it would use the other one.
            if (ownDatabase || await _prompt.ConfirmAsync($"Point web.config at [{db.DatabaseName}] on {db.Server}?",
                    "Update web.config", "Leave as is", true, ct))
            {
                // The site signs in with a login of its own, owner of its database only - not the container's sa.
                var login = await _sqlContainer.GrantSiteLoginAsync(project, db, ct);
                if (!login.Success) return login.WithoutValue();
                _undo.Add($"Drop the login {login.Value!.User}", undoCt => _sql.DropLoginAsync(login.Value.User, undoCt));
                _undo.RestoreFileOnUndo(webConfigPath);
                var write = _webConfig.WriteSiteSqlServer(webConfigPath, login.Value);
                if (write.Success)
                    reporter.Success("web.config updated.");
                else if (ownDatabase)
                    return Result.Fail($"Could not point web.config at [{db.DatabaseName}]: {write.Error}");
                else
                    reporter.Fail($"Could not update web.config: {write.Error}");
            }
            else
            {
                reporter.Info($"web.config left unchanged. Connect with: server '{db.Server}', " +
                              $"database '{db.DatabaseName}', user '{_opts.Docker.SqlUser}' and its password from Settings → Database server.");
            }
        }

        if (created)
            reporter.Info("The database is empty: open the site to run the DNN install wizard, or restore a " +
                          "backup by running 'Host project' again with 'database only' and a backup file.");
        return Result.Ok();
    }

    /// <summary>
    /// A database from a live site often has DNN's SSL on (the whole site, or pages marked secure): DNN would then
    /// redirect every http:// request to https://, which the local site doesn't answer. Turns it off in the local
    /// copy. Not fatal when it fails - it can be switched off in Settings → Site Settings instead.
    /// </summary>
    internal static async Task DisableSslAsync(ISqlServerService sql, string database, IProgressReporter reporter, CancellationToken ct)
    {
        var ssl = await sql.DisableSslAsync(database, ct);
        if (!ssl.Success)
            reporter.Fail($"Could not turn off DNN's SSL setting: {ssl.Error}. If the site redirects to https://, " +
                          "switch SSL off in its Site Settings.");
        else if (ssl.Value > 0)
            reporter.Warn($"Turned off DNN's SSL in [{database}] ({ssl.Value} setting{(ssl.Value == 1 ? "" : "s")} and " +
                          "secure pages) - the local site has no https. Switch it back on before this database goes live again.");
    }
}
