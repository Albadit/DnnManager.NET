using System.IO.Compression;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// The user already agreed to DNN Manager signing in to the site's database server with their Windows account (an
    /// upgrade asks before it starts, then puts a step's backup back): not asked again.
    /// </summary>
    public bool SignInAgreed { get; init; }
}

/// <summary>
/// Puts a project back as a backup has it (<see cref="ProjectBackups"/>): its files - those added since are deleted, what
/// the backup leaves out (<c>.git</c>, <c>_backup.filter</c>) isn't touched - and its database, on the server and with
/// the login its web.config names. The backup's database is imported first, under a name of its own, before anything of
/// the site is touched - a failed import leaves the project as it was. Then the files are put back and the imported
/// database swapped in for the site's, which is dropped only then. The site is stopped meanwhile and started again if it
/// ran. Once it has started it runs to the end: a half-restored site is worse than either.
/// </summary>
public sealed class RestoreBackupUseCase(
    IIisManager iis,
    IProjectRepository projects,
    IProjectFileCopier copier,
    LocalSqlContainer sql,
    IDatabaseProvisioner databases,
    IBacpacService bacpac,
    IFileLockService locks,
    IProjectRecords records,
    OperationUndo undo,
    IUserPrompt prompt,
    IOptions<AppOptions> options)
{
    private readonly OperationUndo _undo = undo;
    private readonly IUserPrompt _prompt = prompt;
    private readonly AppOptions _options = options.Value;
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
        // Every file of the backup checked before one is written: one that can't be put back (outside the folder, through
        // a link) would otherwise stop the restore half-way, with the site's files a mix of both.
        if (CheckZip(req.SiteZip, req.Directory) is { } refused) return Result.Fail($"{refused} Nothing was changed.");
        var project = _projects.Build(req.SiteName, req.Directory);
        IReadOnlyList<string> filtered;
        try { filtered = BackupFilter.Read(req.Directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { filtered = []; }

        // From here on it isn't cancelled: the files and the database belong together.
        var none = CancellationToken.None;

        // The backup's database first, beside the site's - the database the site's web.config names, as the site uses it.
        DatabaseConnection? target = null;
        string? incoming = null;
        if (req.Database is { } bacpacPath)
        {
            var database = _sql.DatabaseOf(project);
            if (database is null) reporter.Warn("The site's web.config names no database of its own - the database is left as it is.");
            else if (database.Kind == DatabaseKind.LocalDbFile) reporter.Warn("The site uses a LocalDB file - the database is left as it is.");
            else
            {
                target = database.Kind == DatabaseKind.Container ? _sql.Connection(database.Database) : database;
                // Windows authentication to a server web.config names - not on this PC, not the one in Settings → Database
                // server - would hand it the user's sign-in: asked first, before anything changes.
                if (!req.SignInAgreed && target.Authentication == SqlAuthentication.Windows &&
                    !SqlServerAddress.MaySignInAsUser(target.Server, _options.DatabaseServer) &&
                    !await _prompt.ConfirmAsync(SqlServerAddress.SignInQuestion(target.Server, "Restoring the backup's database"),
                                                $"Sign in to {target.Server}", "Don't sign in", false, ct))
                    return Result.Fail($"Not signed in to {target.Server} with your Windows account - the backup's database goes there. Nothing was changed.");
                var imported = await ImportAsideAsync(target, bacpacPath, reporter, none);
                if (!imported.Success) return imported.WithoutValue();
                incoming = imported.Value;
            }
        }
        // A cancel can't take a restore back once it changes the site - said, so nobody counts on it.
        _undo.CannotUndo($"the files of '{req.SiteName}' were put back as the backup has them.");

        var wasRunning = _iis.GetSiteStates().TryGetValue(req.SiteName, out var state) && IisStates.IsStarted(state);
        // Stopped even when it reads so: a worker process still ending holds the site's files (and DNN its lock).
        if (wasRunning) reporter.Info($"Stopping '{req.SiteName}' while it is put back…");
        var stopped = _iis.StopSiteAndWait(req.SiteName, TimeSpan.FromSeconds(60));
        if (!stopped.Success) return Result.Fail($"Could not stop the site: {stopped.Error}");
        try
        {
            reporter.Info("Putting the site's files back…");
            await StopSiteProgramsAsync(req.Directory, reporter);
            var extracted = await ExtractWithRetryAsync(req.SiteZip, req.Directory, reporter);
            if (!extracted.Success)
            {
                if (target is not null && incoming is not null) await _databases.DropDatabaseAsync(target with { Database = incoming }, none);
                return Result.Fail($"Could not put the site's files back: {extracted.Error} The site's files are now a mix - some " +
                                   "as the backup has them, the rest as they were. Run Restore again once nothing holds them " +
                                   "(an editor, a terminal in the folder). The database is left as it was.");
            }
            var removed = await _copier.RemoveFilesNotInZipAsync(req.SiteZip, req.Directory, [".git", .. filtered], reporter, none);
            if (removed is { Success: true, Value: > 0 }) reporter.Info($"Deleted {removed.Value:N0} file(s) added since the backup.");
            reporter.Success("The site's files are as the backup has them.");

            if (target is not null && incoming is not null)
            {
                var swapped = await SwapInAsync(target, incoming, reporter, none);
                if (!swapped.Success) return swapped;
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
    /// own programs ended before each try - but only for a file in use: anything else (access denied, a full disk, a
    /// file of the backup that can't be put back) fails the same way each time, and is said at once.
    /// </summary>
    private async Task<Result> ExtractWithRetryAsync(string zip, string directory, IProgressReporter reporter)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _copier.ExtractZipAsync(zip, directory, reporter, CancellationToken.None);
            }
            catch (IOException ex) when (FileInUse.Is(ex) && attempt < 10)
            {
                reporter.Progress($"A file of the site is still in use - trying again ({attempt} of 9)…");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Fail(ex.Message);
            }
            await Task.Delay(3000);
            await StopSiteProgramsAsync(directory, reporter);
        }
    }

    /// <summary>
    /// Why <paramref name="zip"/> can't be put back into <paramref name="directory"/> - or null when every file in it can:
    /// none outside the folder ("..", a full path, a drive), none through a link or junction in the folder, none that is
    /// itself a link (a zip made on Linux can hold one). The same files as the extraction writes: those under the zip's
    /// site root (its shallowest folder with a web.config).
    /// </summary>
    internal static string? CheckZip(string zip, string directory)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            static string Normalized(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');
            var files = archive.Entries.Where(e => !Normalized(e).EndsWith('/')).ToList();
            if (files.Count == 0) return "The backup's site files are empty.";
            var webConfig = files
                .Where(e => e.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => Normalized(e).Count(c => c == '/'))
                .FirstOrDefault();
            var root = webConfig is null ? "" : Normalized(webConfig)[..^webConfig.Name.Length];
            foreach (var entry in files)
            {
                var name = Normalized(entry);
                if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                // The Unix file type in the upper bits: 0xA000 is a symbolic link.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    return $"The backup's site files hold a link ({entry.FullName}) - DNN Manager doesn't put links back.";
                if (SafePath.Under(directory, name[root.Length..]) is null)
                    return $"The backup's site files hold a file that would land outside the site's folder, or go through a link " +
                           $"or junction in it: {entry.FullName}";
            }
            return null;
        }
        catch (InvalidDataException)
        {
            return $"The backup's site files aren't a valid zip: {zip}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read the backup's site files: {ex.Message}";
        }
    }

    private async Task StopSiteProgramsAsync(string directory, IProgressReporter reporter)
    {
        var stopped = await _locks.StopProgramsRunningFromAsync(directory);
        if (stopped.Count > 0) reporter.Info($"Ended what the site still ran from its folder: {string.Join(", ", stopped.Distinct())}.");
    }

    /// <summary>
    /// The backup's database imported beside the site's, under a name of its own (returned) - <paramref name="admin"/> is the
    /// local container as its user, who may create databases, or any other server as the site signs in. The import never
    /// meets the site's database - nor its files: a database renamed keeps its file names, so one imported under the same
    /// name would collide with them (<c>&lt;name&gt;_Primary.mdf</c>, from a backup put back before). A failed import
    /// leaves nothing behind.
    /// </summary>
    private async Task<Result<string>> ImportAsideAsync(DatabaseConnection admin, string bacpacPath, IProgressReporter reporter, CancellationToken ct)
    {
        var name = admin.Database;
        reporter.Info($"Importing the backup's database beside [{name}] on {admin.Server}.");
        var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
        if (!ensured.Success) return Result<string>.Fail(ensured.Error!);

        var incoming = admin with { Database = $"{name}_restore_{DateTime.Now:yyyyMMddHHmmss}" };
        var (user, password) = admin.UsesWindowsAuthentication ? ("", "") : (admin.User, admin.Password);
        var imported = await _bacpac.ImportAsync(admin.Server, user, password, incoming.Database, bacpacPath, reporter, ct);
        if (!imported.Success)
        {
            // What the import left half-made goes; the site's own database was never touched.
            if (await _databases.DatabaseExistsAsync(incoming, ct) is { Success: true, Value: true }) await _databases.DropDatabaseAsync(incoming, ct);
            return Result<string>.Fail($"Importing the backup's database failed: {imported.Error} Nothing was changed.");
        }
        return Result<string>.Ok(incoming.Database);
    }

    /// <summary>
    /// The imported database in place of the site's: the site's renamed aside, the imported one given its name, and the one
    /// aside dropped. A rename that fails puts the site's back.
    /// </summary>
    private async Task<Result> SwapInAsync(DatabaseConnection admin, string incomingName, IProgressReporter reporter, CancellationToken ct)
    {
        var name = admin.Database;
        var incoming = admin with { Database = incomingName };
        reporter.Info($"Putting database [{name}] back on {admin.Server}…");
        var exists = await _databases.DatabaseExistsAsync(admin, ct);
        if (!exists.Success)
        {
            await _databases.DropDatabaseAsync(incoming, ct);
            return Result.Fail($"Could not reach [{name}] on {admin.Server}: {exists.Error} The database is left as it was.");
        }
        var aside = $"{name}_before_restore_{DateTime.Now:yyyyMMddHHmmss}";

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