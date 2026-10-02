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

public sealed class RemoveProjectUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IIisManager _iis;
    private readonly ISqlServerService _sql;
    private readonly IWebConfigService _webConfig;
    private readonly IFileLockService _locks;
    private readonly IUserPrompt _prompt;
    private readonly LocalSqlContainer _container;
    private readonly IDatabaseProvisioner _databases;
    private readonly IProjectRecords _records;
    private readonly IKeepWarmRecords _keepWarm;
    private readonly ILogger<RemoveProjectUseCase> _log;
    private readonly OperationUndo _undo;

    public RemoveProjectUseCase(
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
        OperationUndo undo)
    {
        _opts = opts.Value;
        _projects = projects;
        _iis = iis;
        _sql = sql;
        _webConfig = webConfig;
        _locks = locks;
        _prompt = prompt;
        _container = container;
        _databases = databases;
        _records = records;
        _keepWarm = keepWarm;
        _log = log;
        _undo = undo;
    }

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
                return parts.Count > 0 ? $"• {p.Name}  ({string.Join("; ", parts)})" : $"• {p.Name}";
            }));
            var keeps = projects.Any(p => Directory.Exists(p.BackupDirectory)) ? $"{nl}{nl}Their backups are kept." : "";
            question = $"Remove these {projects.Count} projects permanently - each with its IIS site, folder and database?{nl}{nl}{list}{keeps}";
        }
        if (!await _prompt.ConfirmDangerAsync(question, single ? "Remove project" : $"Remove {projects.Count} projects", "Cancel", ct))
            return Result.Aborted();

        var failed = new List<string>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (!single) reporter.Step($"Removing '{project.Name}'");
            var result = await RemoveAsync(project, !keepsFiles.Contains(project.Name), reporter, ct);
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
    /// The database to drop on the local container when web.config names none of its own: the one its web.config's
    /// SiteSqlServer names - or, for a project of the projects folder, the one named like it.
    /// </summary>
    private string? DatabaseNameOf(DnnProject project, bool deleteFiles) =>
        DeveloperDb.FromWebConfig(project, _webConfig) ?? (deleteFiles ? _opts.DatabaseNameFor(project.Name) : null);

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

            // The database goes with the project, always.
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
                        reporter.Fail(drop.Error!);
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
                    }
                    else
                        reporter.Fail($"Could not drop database [{dbName}]: {drop.Error}");
                }
            }

            // What DNN Manager remembered about it (how DNN was installed) goes with the site.
            _records.Remove(projectName);
            if (!deleteFiles)
            {
                reporter.Info($"{project.ProjectDirectory} is outside the projects folder - its files are kept.");
                reporter.Step("Removal complete");
                return Result.Ok();
            }

            reporter.Step("Step 4: Delete project directory");
            _undo.CannotUndo($"whatever was already deleted from {project.ProjectDirectory}.");
            var deleted = await DeleteProjectFolderAsync(project.ProjectDirectory, reporter, ct);
            if (!deleted.Success) return deleted;

            reporter.Step("Removal complete");
            return Result.Ok();
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
            if (await _prompt.ConfirmAsync(
                    $"These programs are using {folder}:{Environment.NewLine}{Environment.NewLine}{names}" +
                    $"{Environment.NewLine}{Environment.NewLine}Close them and delete the folder? Unsaved work in them is lost.",
                    "Close and delete", "Don't close", true, ct))
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
