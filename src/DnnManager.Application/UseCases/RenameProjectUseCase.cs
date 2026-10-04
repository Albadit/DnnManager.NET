using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class RenameProjectRequest
{
    /// <summary>The IIS site's name now.</summary>
    public required string SiteName { get; init; }

    /// <summary>The folder the site serves.</summary>
    public required string Directory { get; init; }

    public required string NewName { get; init; }

    /// <summary>Its http bindings' host name &lt;old&gt;.&lt;suffix&gt; becomes &lt;new&gt;.&lt;suffix&gt;, and DNN's portal aliases with it.</summary>
    public bool RenameHost { get; init; }

    /// <summary>Its database (not a LocalDB file) is renamed to the new name, and web.config follows.</summary>
    public bool RenameDatabase { get; init; }
}

/// <summary>
/// Renames a project: its IIS site and app pool, its folder when it is the project's own in the projects folder, its
/// records (how it was installed, keep warm) - and, when asked, its host name (with DNN's portal aliases) and its
/// database. The site is stopped meanwhile and started again if it ran. A step that fails takes back the ones before it.
/// Its backups stay under the old name: they are copies of the project as it was.
/// </summary>
public sealed class RenameProjectUseCase(
    IOptions<AppOptions> opts,
    IIisManager iis,
    IProjectRepository projects,
    LocalSqlContainer sql,
    IDatabaseProvisioner databases,
    IWebConfigService webConfig,
    IProjectRecords records,
    IKeepWarmRecords keepWarm,
    OperationUndo undo)
{
    private readonly AppOptions _opts = opts.Value;
    private readonly IIisManager _iis = iis;
    private readonly IProjectRepository _projects = projects;
    private readonly LocalSqlContainer _sql = sql;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly IProjectRecords _records = records;
    private readonly IKeepWarmRecords _keepWarm = keepWarm;
    private readonly OperationUndo _undo = undo;

    public async Task<Result> ExecuteAsync(RenameProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var (oldName, newName) = (req.SiteName, req.NewName.Trim());
        if (ProjectName.Validate(newName) is { Success: false } invalid) return invalid;
        if (newName == oldName) return Result.Ok();
        var sameIgnoringCase = newName.Equals(oldName, StringComparison.OrdinalIgnoreCase);
        if (!sameIgnoringCase && _iis.GetSiteStates().ContainsKey(newName))
            return Result.Fail($"IIS already has a site named '{newName}'.");
        if (_iis.GetSiteDetails(oldName) is not { } site) return Result.Fail($"IIS has no site named '{oldName}'.");

        // The project's own folder in the projects folder is renamed with it; a folder elsewhere stays where it is.
        var oldDirectory = Path.GetFullPath(req.Directory).TrimEnd('\\');
        var ownFolder = string.Equals(oldDirectory, Path.GetFullPath(_projects.Build(oldName).ProjectDirectory).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
        var newDirectory = ownFolder ? Path.GetFullPath(_projects.Build(newName).ProjectDirectory).TrimEnd('\\') : oldDirectory;
        if (ownFolder && !sameIgnoringCase && Directory.Exists(newDirectory))
            return Result.Fail($"The folder {newDirectory} exists already - choose another name.");

        var database = _sql.DatabaseOf(_projects.Build(oldName, oldDirectory));
        var renameDatabase = req.RenameDatabase && database is { Kind: not DatabaseKind.LocalDbFile } &&
                             !database.Database.Equals(newName, StringComparison.OrdinalIgnoreCase);
        var wasRunning = site.State.Equals("Started", StringComparison.OrdinalIgnoreCase);

        reporter.Plan("IIS site", "Folder", "Host name", "Database", "Records", "Start");
        reporter.Context($"{oldName} → {newName}");
        try
        {
            reporter.Step($"Renaming the IIS site '{oldName}' to '{newName}'", "IIS site");
            _undo.Add($"Rename the IIS site back to '{oldName}'", () => _iis.RenameSite(newName, oldName, oldDirectory));
            var renamed = _iis.RenameSite(oldName, newName, newDirectory);
            if (!renamed.Success) return await FailAsync($"Could not rename the IIS site: {renamed.Error}", reporter);
            reporter.Success($"IIS site and app pool are '{newName}' (stopped for the rename).");

            reporter.Step("Renaming the folder", "Folder");
            if (ownFolder && !sameIgnoringCase)
            {
                _undo.Add($"Move the folder back to {oldDirectory}", () => Move(newDirectory, oldDirectory));
                var moved = Move(oldDirectory, newDirectory);
                if (!moved.Success)
                    return await FailAsync($"Could not rename {oldDirectory} to {newDirectory}: {moved.Error} " +
                                           "Close what has files of the project open (an editor, a terminal) and try again.", reporter);
                reporter.Success($"Folder: {newDirectory}");
            }
            else reporter.Info(ownFolder ? "The folder keeps its name - only its capitals differ." : $"The folder stays where it is: {oldDirectory}");
            // The renamed app pool runs as IIS APPPOOL\<new name> - a new Windows identity.
            var grant = _iis.GrantPermissions(newDirectory, [$"IIS APPPOOL\\{newName}"]);
            if (!grant.Success) reporter.Warn($"Could not give IIS APPPOOL\\{newName} access to {newDirectory}: {grant.Error}");

            reporter.Step("Host name", "Host name");
            if (req.RenameHost) await RenameHostAsync(site, oldName, newName, newDirectory, database, reporter, ct);
            else reporter.Info("Kept as it is.");

            reporter.Step("Database", "Database");
            var siteDatabase = database;
            if (renameDatabase)
            {
                var webConfigPath = Path.Combine(newDirectory, "web.config");
                _undo.RestoreFileOnUndo(webConfigPath);
                _undo.Add($"Rename the database back to [{database!.Database}]", () => _databases.RenameDatabaseAsync(database with { Database = newName }, database.Database, CancellationToken.None));
                var dbRenamed = await _databases.RenameDatabaseAsync(database!, newName, ct);
                if (!dbRenamed.Success) return await FailAsync(dbRenamed.Error!, reporter);
                siteDatabase = database with { Database = newName };
                var written = _webConfig.WriteDatabaseConnection(webConfigPath, siteDatabase);
                if (!written.Success) return await FailAsync($"The database is [{newName}], but web.config couldn't be updated: {written.Error}", reporter);
                reporter.Success($"Database [{database.Database}] is [{newName}]; web.config points at it.");
            }
            else reporter.Info(database is null ? "web.config names no database of its own." : $"Kept as it is: [{database.Database}].");
            // Signing in as the app pool, the site has a new Windows login now.
            if (siteDatabase is { Kind: not DatabaseKind.LocalDbFile, UsesWindowsAuthentication: true })
            {
                var access = await _databases.GrantSiteAccessAsync(siteDatabase, $"IIS APPPOOL\\{newName}", ct);
                if (access.Success) reporter.Success($"IIS APPPOOL\\{newName} may use [{siteDatabase.Database}].");
                else reporter.Warn(access.Error!);
            }

            reporter.Step("Records", "Records");
            MoveRecords(oldName, newName);
            reporter.Success("How it was installed and its keep-warm choice go with the new name.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(ex.Message, reporter);
        }

        reporter.Step(wasRunning ? $"Starting '{newName}'" : "Start", "Start");
        if (wasRunning)
        {
            var started = _iis.StartSite(newName);
            if (started.Success) reporter.Success($"'{newName}' runs.");
            else reporter.Warn($"'{newName}' couldn't be started: {started.Error}");
        }
        else reporter.Info("It was stopped - it stays stopped.");
        // The old app pool identity's Windows profile is left over - nothing runs as it any more.
        await _iis.RemoveAppPoolProfileAsync(oldName, CancellationToken.None);
        return Result.Ok();
    }

    /// <summary>The http bindings on &lt;old&gt;.&lt;suffix&gt; become &lt;new&gt;.&lt;suffix&gt;, and DNN's portal aliases follow.</summary>
    private async Task RenameHostAsync(IisSiteDetails site, string oldName, string newName, string directory, DatabaseConnection? database,
        IProgressReporter reporter, CancellationToken ct)
    {
        var (oldHost, newHost) = (_opts.HostnameFor(oldName), _opts.HostnameFor(newName));
        var http = site.Bindings.Where(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && b.Port is not null)
            .Select(b => (b.Host, Port: b.Port!.Value)).ToList();
        if (!http.Any(b => b.Host.Equals(oldHost, StringComparison.OrdinalIgnoreCase)))
        {
            reporter.Info($"No binding on {oldHost} - nothing to change.");
            return;
        }
        var next = http.Select(b => b.Host.Equals(oldHost, StringComparison.OrdinalIgnoreCase) ? (newHost, b.Port) : b).ToList();
        _undo.Add("Put the bindings back", () => _iis.ReplaceHttpBindings(newName, http));
        var bound = _iis.ReplaceHttpBindings(newName, next);
        if (!bound.Success) throw new InvalidOperationException($"Could not change the bindings: {bound.Error}");
        reporter.Success($"Bound to {newHost} instead of {oldHost}.");

        if (database is null)
        {
            reporter.Warn($"web.config names no database - DNN's portal alias {oldHost} stays as it is.");
            return;
        }
        var aliases = http.Where(b => b.Host.Equals(oldHost, StringComparison.OrdinalIgnoreCase))
            .Select(b => (DnnSiteAddress.AliasFor(oldHost, b.Port), DnnSiteAddress.AliasFor(newHost, b.Port))).ToList();
        _undo.Add("Put DNN's portal aliases back", async () => (await _databases.UpdatePortalAliasesAsync(database, directory,
            aliases.Select(a => (a.Item2, a.Item1)).ToList(), [], CancellationToken.None)).WithoutValue());
        var updated = await _databases.UpdatePortalAliasesAsync(database, directory, aliases, [], ct);
        if (updated.Success) reporter.Success($"DNN's portal alias is {newHost} ({updated.Value} changed).");
        else reporter.Warn($"{updated.Error} - add {newHost} as a site alias in DNN (Settings → Site Settings → Site Aliases).");
    }

    private void MoveRecords(string oldName, string newName)
    {
        if (_records.Find(oldName) is { } record)
        {
            _undo.Add("Put the project record back", () =>
            {
                _records.Remove(newName);
                _records.Save(record);
                return Result.Ok();
            });
            _records.Save(record with { Site = newName });
            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) _records.Remove(oldName);
        }
        if (_keepWarm.Find(oldName) is { } warm)
        {
            _undo.Add("Put the keep-warm record back", () =>
            {
                _keepWarm.Remove(newName);
                _keepWarm.Save(warm);
                return Result.Ok();
            });
            _keepWarm.Save(warm with { Site = newName });
            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) _keepWarm.Remove(oldName);
        }
    }

    /// <summary>A failed step takes back the ones before it - a project half renamed is worse than one not renamed.</summary>
    private async Task<Result> FailAsync(string error, IProgressReporter reporter)
    {
        reporter.Fail(error);
        reporter.Step("Taking the rename back");
        await _undo.RunAsync(reporter);
        return Result.Fail(error);
    }

    private static Result Move(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(ex.Message);
        }
    }
}
