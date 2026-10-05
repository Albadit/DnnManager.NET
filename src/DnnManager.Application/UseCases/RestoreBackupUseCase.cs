using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

public sealed class RestoreBackupRequest
{
    /// <summary>The IIS site's name.</summary>
    public required string SiteName { get; init; }

    /// <summary>The folder the site serves.</summary>
    public required string Directory { get; init; }

    /// <summary>The backup's site files (<c>&lt;project&gt;.zip</c>).</summary>
    public required string SiteZip { get; init; }

    /// <summary>The backup's database (<c>&lt;project&gt;.bacpac</c>); null to leave the database as it is.</summary>
    public string? Database { get; init; }
}

/// <summary>
/// Puts a project back as a backup has it (<see cref="ProjectBackups"/>): its files - those added since are deleted, what
/// the backup leaves out (<c>.git</c>, <c>_backup.filter</c>) isn't touched - and its database, on the server and with
/// the login its web.config names. The database it replaces is set aside first and dropped only once the backup's is in,
/// so a failed import leaves it as it was. The site is stopped meanwhile and started again if it ran. Once it has started
/// it runs to the end: a half-restored site is worse than either.
/// </summary>
public sealed class RestoreBackupUseCase(
    IIisManager iis,
    IProjectRepository projects,
    IProjectFileCopier copier,
    LocalSqlContainer sql,
    IDatabaseProvisioner databases,
    IBacpacService bacpac,
    IFileLockService locks,
    IProjectRecords records)
{
    private readonly IFileLockService _locks = locks;
    private readonly IIisManager _iis = iis;
    private readonly IProjectRepository _projects = projects;
    private readonly IProjectFileCopier _copier = copier;
    private readonly LocalSqlContainer _sql = sql;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly IBacpacService _bacpac = bacpac;
    private readonly IProjectRecords _records = records;

    public async Task<Result> ExecuteAsync(RestoreBackupRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(req.SiteZip)) return Result.Fail($"The backup's site files aren't there: {req.SiteZip}");
        if (req.Database is { } bacpacFile && !File.Exists(bacpacFile)) return Result.Fail($"The backup's database isn't there: {bacpacFile}");
        if (!System.IO.Directory.Exists(req.Directory)) return Result.Fail($"Project folder not found: {req.Directory}");
        var project = _projects.Build(req.SiteName, req.Directory);
        IReadOnlyList<string> filtered;
        try { filtered = BackupFilter.Read(req.Directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { filtered = []; }

        // From here on it isn't cancelled: the files and the database belong together.
        var none = CancellationToken.None;
        var wasRunning = _iis.GetSiteStates().TryGetValue(req.SiteName, out var state) && state.Equals("Started", StringComparison.OrdinalIgnoreCase);
        // Stopped even when it reads so: a worker process still ending holds the site's files (and DNN its lock).
        if (wasRunning) reporter.Info($"Stopping '{req.SiteName}' while it is put back…");
        var stopped = _iis.StopSiteAndWait(req.SiteName, TimeSpan.FromSeconds(60));
        if (!stopped.Success) return Result.Fail($"Could not stop the site: {stopped.Error}");
        try
        {
            reporter.Info("Putting the site's files back…");
            await StopSiteProgramsAsync(req.Directory, reporter);
            var extracted = await ExtractWithRetryAsync(req.SiteZip, req.Directory, reporter);
            if (!extracted.Success) return Result.Fail($"Could not put the site's files back: {extracted.Error}");
            var removed = await _copier.RemoveFilesNotInZipAsync(req.SiteZip, req.Directory, [".git", .. filtered], reporter, none);
            if (removed is { Success: true, Value: > 0 }) reporter.Info($"Deleted {removed.Value:N0} file(s) added since the backup.");
            reporter.Success("The site's files are as the backup has them.");

            if (req.Database is { } bacpacPath)
            {
                // The database the backup's own web.config names - as the site will use it.
                var database = _sql.DatabaseOf(project);
                if (database is null) reporter.Warn("The backup's web.config names no database of its own - the database is left as it is.");
                else if (database.Kind == DatabaseKind.LocalDbFile) reporter.Warn("The site uses a LocalDB file - the database is left as it is.");
                else
                {
                    var restored = await RestoreDatabaseAsync(database, bacpacPath, reporter, none);
                    if (!restored.Success) return restored;
                }
            }

            if (_records.Find(req.SiteName) is { } record && DnnInstall.Version(req.Directory) is { } version)
                _records.Save(record with { DnnVersion = version });
            return Result.Ok();
        }
        finally
        {
            if (wasRunning)
            {
                var started = _iis.StartSite(req.SiteName);
                if (!started.Success) reporter.Warn($"Could not start '{req.SiteName}' again: {started.Error}");
            }
        }
    }

    /// <summary>
    /// What still holds a file of the site once it is stopped: the C# compiler ASP.NET runs from its bin\roslyn keeps
    /// running for minutes, and the worker process takes a moment to let go. Tried again for half a minute, the site's
    /// own programs ended before each try.
    /// </summary>
    private async Task<Result> ExtractWithRetryAsync(string zip, string directory, IProgressReporter reporter)
    {
        for (var attempt = 1; ; attempt++)
        {
            Result result;
            try { result = await _copier.ExtractZipAsync(zip, directory, reporter, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result = Result.Fail(ex.Message); }
            if (result.Success || attempt == 10) return result;
            reporter.Progress($"A file of the site is still in use - trying again ({attempt} of 9)…");
            await Task.Delay(3000);
            await StopSiteProgramsAsync(directory, reporter);
        }
    }

    private async Task StopSiteProgramsAsync(string directory, IProgressReporter reporter)
    {
        var stopped = await _locks.StopProgramsRunningFromAsync(directory);
        if (stopped.Count > 0) reporter.Info($"Ended what the site still ran from its folder: {string.Join(", ", stopped.Distinct())}.");
    }

    /// <summary>
    /// The backup's database in place of the site's: the backup is imported under a name of its own, then the site's is
    /// renamed aside, the imported one given its name, and the one aside dropped. The import never meets the site's
    /// database - nor its files: a database renamed keeps its file names, so one imported under the same name would
    /// collide with them (<c>&lt;name&gt;_Primary.mdf</c>, from a backup put back before). A failed import leaves the
    /// site's database untouched.
    /// </summary>
    private async Task<Result> RestoreDatabaseAsync(DatabaseConnection site, string bacpacPath, IProgressReporter reporter, CancellationToken ct)
    {
        // The local container as its user, who may create databases; any other server as the site signs in.
        var admin = site.Kind == DatabaseKind.Container ? _sql.Connection(site.Database) : site;
        var name = admin.Database;
        reporter.Info($"Putting database [{name}] back on {admin.Server}…");
        var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
        if (!ensured.Success) return ensured;

        var exists = await _databases.DatabaseExistsAsync(admin, ct);
        if (!exists.Success) return Result.Fail($"Could not reach [{name}] on {admin.Server}: {exists.Error}");
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var incoming = admin with { Database = $"{name}_restore_{stamp}" };
        var aside = $"{name}_before_restore_{stamp}";

        var (user, password) = admin.UsesWindowsAuthentication ? ("", "") : (admin.User, admin.Password);
        var imported = await _bacpac.ImportAsync(admin.Server, user, password, incoming.Database, bacpacPath, reporter, ct);
        if (!imported.Success)
        {
            // What the import left half-made goes; the site's own database was never touched.
            if (await _databases.DatabaseExistsAsync(incoming, ct) is { Success: true, Value: true }) await _databases.DropDatabaseAsync(incoming, ct);
            return Result.Fail($"Importing the backup's database failed: {imported.Error} The database is left as it was.");
        }

        if (exists.Value)
        {
            var renamed = await _databases.RenameDatabaseAsync(admin, aside, ct);
            if (!renamed.Success)
            {
                await _databases.DropDatabaseAsync(incoming, ct);
                return Result.Fail($"Could not set [{name}] aside to put the backup's in: {renamed.Error} The database is left as it was.");
            }
        }
        var named = await _databases.RenameDatabaseAsync(incoming, name, ct);
        if (!named.Success)
        {
            if (exists.Value && await _databases.RenameDatabaseAsync(admin with { Database = aside }, name, ct) is { Success: false } back)
                return Result.Fail($"The backup's database is in as [{incoming.Database}] but couldn't be named [{name}]: {named.Error} - " +
                    $"and the site's couldn't be put back from [{aside}]: {back.Error}");
            await _databases.DropDatabaseAsync(incoming, ct);
            return Result.Fail($"The backup's database couldn't be named [{name}]: {named.Error} The database is left as it was.");
        }
        if (exists.Value && await _databases.DropDatabaseAsync(admin with { Database = aside }, ct) is { Success: false } dropped)
            reporter.Warn($"The database it replaced is still there as [{aside}] - drop it once you no longer need it ({dropped.Error}).");
        reporter.Success($"Database [{name}] is as the backup has it.");
        return Result.Ok();
    }
}