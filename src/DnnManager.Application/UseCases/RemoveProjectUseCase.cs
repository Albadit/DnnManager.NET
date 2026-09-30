using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class RemoveProjectUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IIisManager _iis;
    private readonly ISqlServerService _sql;
    private readonly IWebConfigService _webConfig;
    private readonly IFileLockService _locks;
    private readonly IUserPrompt _prompt;
    private readonly ILogger<RemoveProjectUseCase> _log;

    public RemoveProjectUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IIisManager iis,
        ISqlServerService sql,
        IWebConfigService webConfig,
        IFileLockService locks,
        IUserPrompt prompt,
        ILogger<RemoveProjectUseCase> log)
    {
        _opts = opts.Value;
        _projects = projects;
        _iis = iis;
        _sql = sql;
        _webConfig = webConfig;
        _locks = locks;
        _prompt = prompt;
        _log = log;
    }

    /// <summary>
    /// Removes the projects after asking once for all of them - whether to drop their databases too, then for the
    /// final confirmation. Goes on past a project that fails; fails when any did.
    /// </summary>
    public async Task<Result> ExecuteAsync(IReadOnlyList<string> projectNames, IProgressReporter reporter, CancellationToken ct)
    {
        if (projectNames.Count == 0) return Result.Ok();
        var nl = Environment.NewLine;
        var single = projectNames.Count == 1;
        var projects = projectNames.Select(_projects.Build).ToList();

        var dropDb = await _prompt.ConfirmAsync(single
            ? "Also drop the project's database?"
            : $"Also drop the databases of these {projects.Count} projects?", false, ct);

        // Backups live outside the project folder, so removing the project keeps them.
        string question;
        if (single)
        {
            var keeps = Directory.Exists(projects[0].BackupDirectory)
                ? $"{nl}{nl}Its backups in {projects[0].BackupDirectory} are kept."
                : "";
            question = $"Remove project '{projects[0].Name}' permanently?{keeps}";
        }
        else
        {
            var list = string.Join(nl, projects.Select(p => $"• {p.Name}"));
            var keeps = projects.Any(p => Directory.Exists(p.BackupDirectory)) ? $"{nl}{nl}Their backups are kept." : "";
            question = $"Remove these {projects.Count} projects permanently?{nl}{nl}{list}{keeps}";
        }
        if (!await _prompt.ConfirmAsync(question, false, ct))
            return Result.Aborted();

        var failed = new List<string>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (!single) reporter.Step($"Removing '{project.Name}'");
            var result = await RemoveAsync(project, dropDb, reporter, ct);
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

    private async Task<Result> RemoveAsync(DnnProject project, bool dropDb, IProgressReporter reporter, CancellationToken ct)
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

            // The app pool's virtual identity got a Windows user profile auto-created at
            // C:\Users\<project>. Delete it now that the pool is gone so it doesn't linger. The
            // worker exited during RemoveSite, so the profile is unloaded and removable.
            reporter.Step("Step 2: Remove app pool user profile");
            var profile = await _iis.RemoveAppPoolProfileAsync(projectName, ct);
            if (profile.Success)
                reporter.Success($"Removed app pool profile (C:\\Users\\{projectName}) if present.");
            else
                reporter.Info($"App pool profile cleanup skipped: {profile.Error}");

            if (dropDb)
            {
                reporter.Step("Step 3: Drop project database");
                // Drop the database the site uses (web.config SiteSqlServer), falling back to the
                // database named like the project. Read it before the directory is deleted below.
                var dbName = DeveloperDb.FromWebConfig(project, _webConfig) ?? _opts.DatabaseNameFor(projectName);
                var drop = await _sql.DropDatabaseAsync(dbName, ct);
                if (drop.Success)
                    reporter.Success($"Database [{dbName}] dropped (if it existed).");
                else
                    reporter.Fail($"Could not drop database [{dbName}]: {drop.Error}");
            }

            reporter.Step("Step 4: Delete project directory");
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
                    true, ct))
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
