using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

/// <summary>An IIS site to remove, with the folder it serves.</summary>
/// <param name="InProjectsFolder">
/// The folder is one of the projects folder's - a DNN Manager project, whose files are deleted with it. Any other
/// site's files are left where they are: only its IIS site (and the database its web.config names) go.
/// </param>
public sealed record SiteToRemove(string Name, string Directory, bool InProjectsFolder);

public sealed class RemoveProjectUseCase(
    IOptions<AppOptions> opts,
    IProjectRepository projects,
    IIisManager iis,
    ISqlServerService sql,
    IWebConfigService webConfig,
    IFileLockService locks,
    IUserPrompt prompt,
    LocalSqlContainer container,
    IDatabaseProvisioner databases,
    IProjectRecords records,
    IKeepWarmRecords keepWarm,
    ILogger<RemoveProjectUseCase> log,
    OperationUndo undo,
    SiteDatabases siteDatabases)
{
    private readonly SiteDatabases _siteDatabases = siteDatabases;
    private readonly AppOptions _opts = opts.Value;
    private readonly IProjectRepository _projects = projects;
    private readonly IIisManager _iis = iis;
    private readonly ISqlServerService _sql = sql;
    private readonly IWebConfigService _webConfig = webConfig;
    private readonly IFileLockService _locks = locks;
    private readonly IUserPrompt _prompt = prompt;
    private readonly LocalSqlContainer _container = container;
    private readonly IDatabaseProvisioner _databases = databases;
    private readonly IProjectRecords _records = records;
    private readonly IKeepWarmRecords _keepWarm = keepWarm;
    private readonly ILogger<RemoveProjectUseCase> _log = log;
    private readonly OperationUndo _undo = undo;

    /// <summary>
    /// Removes the sites - each with its database - after asking once for all of them, saying what goes: the IIS site,
    /// the folder and the database. Goes on past a project that fails; fails when any did.
    /// </summary>
    public async Task<Result> ExecuteAsync(IReadOnlyList<SiteToRemove> sites, IProgressReporter reporter, CancellationToken ct)
    {
        if (sites.Count == 0) return Result.Ok();
        var nl = Environment.NewLine;
        var single = sites.Count == 1;
        var projects = sites.Select(s => _projects.Build(s.Name, s.Directory)).ToList();
        var keepsFiles = sites.Where(s => !s.InProjectsFolder).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // One question for everything: a project goes with its database - what is left of it would only be in the way.
        // Backups live outside the project folder, so removing the project keeps them.
        string question;
        if (single)
        {
            var project = projects[0];
            var deleteFiles = !keepsFiles.Contains(project.Name);
            var goes = new List<string> { $"its IIS site and app pool" };
            if (deleteFiles) goes.Add($"its folder {project.ProjectDirectory}");
            if (DatabaseText(project, deleteFiles) is { } database) goes.Add($"its database {database}");
            var kept = new List<string>();
            if (!deleteFiles) kept.Add($"Its files in {project.ProjectDirectory} are kept - they are outside the projects folder.");
            if (Elsewhere(project) is { } remote) kept.Add($"Its database [{remote.Database}] on {remote.Server} is kept - it is on another server.");
            if (SharedDatabase(project, deleteFiles) is { } shared) kept.Add($"Its database [{shared.Database}] is kept - the IIS site '{shared.Site}' uses it too.");
            if (Directory.Exists(project.BackupDirectory)) kept.Add($"Its backups in {project.BackupDirectory} are kept.");
            question = $"Remove project '{project.Name}' permanently?{nl}{nl}Deleted: {Join(goes)}." +
                       (kept.Count > 0 ? $"{nl}{nl}{string.Join(" ", kept)}" : "");
        }
        else
        {
            var list = string.Join(nl, projects.Select(p =>
            {
                var deleteFiles = !keepsFiles.Contains(p.Name);
                var parts = new List<string>();
                if (DatabaseText(p, deleteFiles) is { } database) parts.Add($"database {database}");
                if (!deleteFiles) parts.Add("its files outside the projects folder are kept");
                if (Elsewhere(p) is { } remote) parts.Add($"its database [{remote.Database}] on {remote.Server} is kept - another server");
                if (SharedDatabase(p, deleteFiles) is { } shared) parts.Add($"its database [{shared.Database}] is kept - '{shared.Site}' uses it too");
                return parts.Count > 0 ? $"• {p.Name}  ({string.Join("; ", parts)})" : $"• {p.Name}";
            }));
            var keeps = projects.Any(p => Directory.Exists(p.BackupDirectory)) ? $"{nl}{nl}Their backups are kept." : "";
            question = $"Remove these {projects.Count} projects permanently - each with its IIS site, folder and database?{nl}{nl}{list}{keeps}";
        }
        if (!await _prompt.ConfirmDangerAsync(question, single ? "Remove project" : $"Remove {projects.Count} projects", "Cancel", ct))
            return Result.Aborted();

        // Backups outlive the project unless asked otherwise: a copy of a site's data (a live one's, when it was cloned) is
        // only kept as long as it is wanted. Kept is the answer that loses nothing.
        var withBackups = projects.Where(p => Directory.Exists(p.BackupDirectory)).ToList();
        var deleteBackups = withBackups.Count > 0 && await _prompt.ConfirmDangerAsync(
            (single ? $"Also delete the backups of '{withBackups[0].Name}' in {withBackups[0].BackupDirectory}?"
                    : $"Also delete the backups of {string.Join(", ", withBackups.Select(p => $"'{p.Name}'"))}?") +
            $"{nl}{nl}Each holds a copy of the site and its database. Deleted, they can't be brought back.",
            "Delete backups", "Keep backups", ct);

        var failed = new List<string>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (!single) reporter.Step($"Removing '{project.Name}'");
            var result = await RemoveAsync(project, !keepsFiles.Contains(project.Name), reporter, ct);
            if (result.Success && deleteBackups && Directory.Exists(project.BackupDirectory))
            {
                _undo.CannotUndo($"the backups of '{project.Name}' were deleted.");
                if (await TryDeleteDirectoryAsync(project.BackupDirectory, ct)) reporter.Success($"Deleted its backups ({project.BackupDirectory}).");
                else reporter.Warn($"Could not delete its backups in {project.BackupDirectory} - delete them yourself.");
            }
            if (!result.Success)
            {
                if (!single) reporter.Fail($"'{project.Name}': {result.Error}");
                failed.Add(project.Name);
                if (single) return result;
            }
        }
        return failed.Count == 0 ? Result.Ok()
            : Result.Fail($"{failed.Count} of {projects.Count} projects weren't removed completely: {string.Join(", ", failed)}.");
    }

    /// <summary>"[shop] on localhost,1433", or "file App_Data\Database.mdf"; null when it has none DNN Manager would drop.</summary>
    private string? DatabaseText(DnnProject project, bool deleteFiles)
    {
        var database = _container.DatabaseOf(project);
        if (database is { Kind: DatabaseKind.LocalDbFile }) return deleteFiles ? $@"(the file App_Data\{database.Database})" : null;
        if (database is { Kind: DatabaseKind.SqlServer } && !SqlServerAddress.IsOnThisMachine(database.Server)) return null;
        if (SharedDatabase(project, deleteFiles) is not null) return null;
        if (database is not null) return $"[{database.Database}] on {database.Server}";
        return DatabaseNameOf(project, deleteFiles) is { } name ? $"[{name}] on the local SQL container" : null;
    }

    /// <summary>
    /// The database the site uses when it is on another SQL Server than this PC's - a shared or remote server: it isn't
    /// dropped with the project (others may use it), only named as kept. Null when the database is here.
    /// </summary>
    private DatabaseConnection? Elsewhere(DnnProject project) =>
        _container.DatabaseOf(project) is { Kind: DatabaseKind.SqlServer } database && !SqlServerAddress.IsOnThisMachine(database.Server)
            ? database : null;

    /// <summary>
    /// The database DNN Manager would drop with the project, and the other IIS site whose web.config names it too - then
    /// it is that site's as well, and is kept. Null when no other site uses it.
    /// </summary>
    private (string Database, string Site)? SharedDatabase(DnnProject project, bool deleteFiles)
    {
        var database = _container.DatabaseOf(project);
        if (database is { Kind: DatabaseKind.LocalDbFile }) return null;
        var (server, name) = database is not null ? (database.Server, database.Database) : (_container.Server, DatabaseNameOf(project, deleteFiles));
        if (name is null) return null;
        return _siteDatabases.OtherSiteUsing(project.Name, server, name) is { } other ? (name, other) : null;
    }

    /// <summary>
    /// The database to drop on the local container when web.config names none of its own: the one its web.config's
    /// SiteSqlServer names - or, for a project of the projects folder, the one named like it.
    /// </summary>
    private string? DatabaseNameOf(DnnProject project, bool deleteFiles) =>
        DeveloperDb.FromWebConfig(project, _webConfig) ?? (deleteFiles ? _opts.DatabaseNameFor(project.Name) : null);

    /// <summary>
    /// Whether <paramref name="database"/> - named by the site's web.config, which its app pool can change - is the
    /// project's own (named like it), and so may be dropped with it. Another name is asked about first, naming both: a
    /// changed web.config mustn't have DNN Manager drop someone else's database with its rights.
    /// </summary>
    private async Task<bool> IsOwnDatabaseAsync(DnnProject project, string database, string server, CancellationToken ct)
    {
        var own = _opts.DatabaseNameFor(project.Name);
        if (database.Equals(own, StringComparison.OrdinalIgnoreCase)) return true;
        var nl = Environment.NewLine;
        return await _prompt.ConfirmDangerAsync(
            $"The web.config of '{project.Name}' names the database [{database}] on {server} - not the project's own, [{own}].{nl}{nl}" +
            "Drop [" + database + "] with the project anyway? Another site or program may use it; dropped, it can't be brought back.",
            $"Drop [{database}]", "Keep it", ct);
    }

    private static string Join(List<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private async Task<Result> RemoveAsync(DnnProject project, bool deleteFiles, IProgressReporter reporter, CancellationToken ct)
    {
        var projectName = project.Name;
        try
        {
            reporter.Step("Step 1: Remove IIS site & pool");
            var iisResult = _iis.RemoveSite(projectName);
            if (!iisResult.Success)
            {
                reporter.Fail(
                    $"Could not remove the IIS site/pool: {iisResult.Error}. Its worker process may " +
                    "still be holding the project files, so the folder can't be deleted yet. Ensure the " +
                    "app is running as Administrator and try again.");
                return iisResult;
            }
            reporter.Success($"IIS site and app pool '{projectName}' removed (if they existed).");
            // Removing can't be taken back - a cancel says what is already gone.
            _undo.CannotUndo($"the IIS site and app pool '{projectName}' were removed.");
            // Whether it was kept warm goes with its site - a new site of the same name starts cold.
            _keepWarm.Remove(projectName);

            // The app pool's virtual identity got a Windows user profile auto-created at
            // C:\Users\<project>. Delete it now that the pool is gone so it doesn't linger. The
            // worker exited during RemoveSite, so the profile is unloaded and removable.
            reporter.Step("Step 2: Remove app pool user profile");
            var profile = await _iis.RemoveAppPoolProfileAsync(projectName, ct);
            if (profile.Success)
                reporter.Success($"Removed app pool profile (C:\\Users\\{projectName}) if present.");
            else
                reporter.Info($"App pool profile cleanup skipped: {profile.Error}");

            // The database goes with the project - unless another site uses it too. One that couldn't be dropped is named
            // in the result: the project's folder (and web.config, which says which database it was) goes all the same.
            string? leftover = null;
            {
                reporter.Step("Step 3: Drop project database");
                // Drop the database the site uses (web.config SiteSqlServer) where it is, falling back - for a project of
                // the projects folder - to the database named like it on the local container. Read before the directory
                // is deleted below. A site from elsewhere without a web.config database has none DNN Manager may drop.
                var database = _container.DatabaseOf(project);
                var dbName = database?.Database ?? DatabaseNameOf(project, deleteFiles);
                if (database is { Kind: DatabaseKind.LocalDbFile })
                {
                    reporter.Info(deleteFiles
                        ? $@"Its database is the file App_Data\{database.Database} - it goes with the project's folder."
                        : $@"Its database is the file App_Data\{database.Database} - it stays with its files.");
                }
                else if (database is { Kind: DatabaseKind.SqlServer } && !SqlServerAddress.IsOnThisMachine(database.Server))
                {
                    reporter.Info($"Database [{database.Database}] is on {database.Server}, another server - it is kept. Drop it there if it should go.");
                }
                else if (SharedDatabase(project, deleteFiles) is { } shared)
                {
                    reporter.Info($"Database [{shared.Database}] is kept - the IIS site '{shared.Site}' uses it too.");
                }
                else if (dbName is not null && !await IsOwnDatabaseAsync(project, dbName, database?.Server ?? _container.Server, ct))
                {
                    reporter.Info($"Database [{dbName}] is kept - it isn't the project's own ([{_opts.DatabaseNameFor(project.Name)}]). Drop it yourself if it should go.");
                }
                else if (database is { Kind: DatabaseKind.SqlServer })
                {
                    // Not on the container but on this PC: dropped on its own server, signed in as the site does (or as
                    // DNN Manager's Windows account).
                    var drop = await _databases.DropDatabaseAsync(database, ct);
                    if (drop.Success)
                    {
                        _undo.CannotUndo($"database [{database.Database}] on {database.Server} was dropped.");
                        reporter.Success($"Database [{database.Database}] dropped on {database.Server} (if it existed).");
                    }
                    else
                    {
                        reporter.Fail(drop.Error!);
                        leftover = $"[{database.Database}] on {database.Server} couldn't be dropped ({drop.Error}) - drop it yourself.";
                    }
                }
                else if (dbName is null)
                {
                    reporter.Info("No database named in its web.config - none dropped.");
                }
                else
                {
                    var drop = await _sql.DropDatabaseAsync(dbName, ct);
                    if (drop.Success)
                    {
                        _undo.CannotUndo($"database [{dbName}] was dropped.");
                        reporter.Success($"Database [{dbName}] dropped (if it existed).");
                        // The site's own login on the container goes with its database - never the container's own user, nor
                        // one another site signs in with. Named after the project, or after its name before a rename.
                        if (database?.User is { Length: > 0 } login && LocalSqlContainer.IsSiteLogin(login) &&
                            _siteDatabases.OtherSiteSigningInAs(projectName, _container.Server, login) is null)
                        {
                            var dropped = await _sql.DropLoginAsync(login, ct);
                            if (dropped.Success) reporter.Success($"Its login {login} dropped.");
                            else reporter.Warn($"Could not drop its login {login}: {dropped.Error}");
                        }
                    }
                    else
                    {
                        reporter.Fail($"Could not drop database [{dbName}]: {drop.Error}");
                        leftover = $"[{dbName}] on {_container.Server} couldn't be dropped ({drop.Error}) - drop it yourself.";
                    }
                }
            }

            // What DNN Manager remembered about it (how DNN was installed) goes with the site.
            _records.Remove(projectName);
            if (!deleteFiles)
            {
                reporter.Info($"{project.ProjectDirectory} is outside the projects folder - its files are kept.");
                reporter.Step("Removal complete");
                return leftover is null ? Result.Ok() : Result.Fail($"'{projectName}' is removed, but its database {leftover}");
            }

            reporter.Step("Step 4: Delete project directory");
            _undo.CannotUndo($"whatever was already deleted from {project.ProjectDirectory}.");
            var deleted = await DeleteProjectFolderAsync(project.ProjectDirectory, reporter, ct);
            if (!deleted.Success) return deleted;

            reporter.Step("Removal complete");
            return leftover is null ? Result.Ok() : Result.Fail($"'{projectName}' is removed, but its database {leftover}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Remove failed");
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Deletes the project folder. When files are still in use, finds the programs holding them (open files, or a
    /// terminal / editor whose working folder is inside), closes them after asking and tries again. Whatever still
    /// can't be deleted is left for Windows to delete at the next restart.
    /// </summary>
    private async Task<Result> DeleteProjectFolderAsync(string folder, IProgressReporter reporter, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            reporter.Info($"{folder} doesn't exist - nothing to delete.");
            return Result.Ok();
        }
        if (await TryDeleteDirectoryAsync(folder, ct))
        {
            reporter.Success($"Deleted {folder}");
            return Result.Ok();
        }

        reporter.Info("Some files are still in use - looking for the programs holding them…");
        var lockers = _locks.FindLockers(folder);
        foreach (var locker in lockers.Where(l => !l.CanClose))
            reporter.Info($"{locker} - DNN Manager doesn't close this one.");

        var closable = lockers.Where(l => l.CanClose).ToList();
        if (closable.Count == 0)
        {
            reporter.Info("No program that DNN Manager can close holds the folder - it may be antivirus or " +
                          "Windows Search, which let go after a moment.");
        }
        else
        {
            foreach (var locker in closable) reporter.Info($"In use by {locker}");
            var names = string.Join(Environment.NewLine, closable.Select(l => $"• {l.Name} ({l.ExeName})"));
            // Closing loses what isn't saved in them: asked as the other steps that can't be taken back - keeping them is the default.
            if (await _prompt.ConfirmDangerAsync(
                    $"These programs are using {folder}:{Environment.NewLine}{Environment.NewLine}{names}" +
                    $"{Environment.NewLine}{Environment.NewLine}Close them and delete the folder? Unsaved work in them is lost.",
                    "Close and delete", "Don't close", ct))
            {
                var stillRunning = await _locks.CloseAsync(closable, ct);
                foreach (var locker in closable.Except(stillRunning)) reporter.Success($"Closed {locker.Name} ({locker.ExeName}).");
                foreach (var locker in stillRunning) reporter.Fail($"Could not close {locker.Name} ({locker.ExeName}).");
            }
            else
            {
                reporter.Info("Left the programs open.");
            }
        }

        if (await TryDeleteDirectoryAsync(folder, ct))
        {
            reporter.Success($"Deleted {folder}");
            return Result.Ok();
        }

        // Still locked: Windows deletes what's left at the next restart, before anything can open it again.
        var scheduled = _locks.ScheduleDeleteOnRestart(folder);
        if (scheduled.Success)
        {
            reporter.Warn($"{folder} is still partly in use - what's left of it is deleted when Windows restarts.");
            return Result.Ok();
        }
        return Result.Fail($"Could not delete {folder}: a file is still in use, and scheduling it for deletion at " +
                           $"restart failed ({scheduled.Error}). Close what uses it and remove the project again.");
    }

    // DNN ships read-only files, and the IIS worker may release its handles a beat after the
    // app pool reports Stopped, so a single recursive delete often throws. Clear read-only
    // attributes and retry with backoff to absorb the transient lock.
    private static async Task<bool> TryDeleteDirectoryAsync(string path, CancellationToken ct)
    {
        const int maxAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearReadOnly(new DirectoryInfo(path));
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts) return false;
                // Escalating backoff (capped) to ride out a handle that's still being released.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 500 * attempt)), ct);
            }
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        // Never follow junctions/symlinks: Directory.Delete(recursive) removes the link itself,
        // and recursing through a reparse point could loop forever (cycle), blow the stack, or
        // strip read-only flags off files that live outside the project tree.
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return;

        TryClearReadOnly(dir);
        foreach (var file in dir.GetFiles()) TryClearReadOnly(file);
        foreach (var sub in dir.GetDirectories()) ClearReadOnly(sub);
    }

    // Best-effort: one re-locked/denied entry shouldn't abort the whole delete attempt.
    private static void TryClearReadOnly(FileSystemInfo entry)
    {
        try
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch { /* ignore and let Directory.Delete surface anything that actually blocks */ }
    }
}
