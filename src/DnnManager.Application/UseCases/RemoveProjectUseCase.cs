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
    private readonly IUserPrompt _prompt;
    private readonly ILogger<RemoveProjectUseCase> _log;

    public RemoveProjectUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IIisManager iis,
        ISqlServerService sql,
        IWebConfigService webConfig,
        IUserPrompt prompt,
        ILogger<RemoveProjectUseCase> log)
    {
        _opts = opts.Value;
        _projects = projects;
        _iis = iis;
        _sql = sql;
        _webConfig = webConfig;
        _prompt = prompt;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(string projectName, IProgressReporter reporter, CancellationToken ct)
    {
        try
        {
            var project = _projects.Build(projectName);
            var dropDb = await _prompt.ConfirmAsync("Also drop the project's database?", false, ct);
            if (!await _prompt.ConfirmAsync($"Remove project '{projectName}' permanently?", false, ct))
                return Result.Fail("Aborted by user.");

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
                // conventional {project}_dnndev name. Read it before the directory is deleted below.
                var dbName = DeveloperDb.FromWebConfig(project, _webConfig) ?? _opts.DatabaseNameFor(projectName);
                await _sql.DropDatabaseAsync(dbName, ct);
            }

            reporter.Step("Step 4: Delete project directory");
            if (Directory.Exists(project.ProjectDirectory))
            {
                if (await TryDeleteDirectoryAsync(project.ProjectDirectory, ct))
                    reporter.Success($"Deleted {project.ProjectDirectory}");
                else
                    reporter.Fail(
                        $"Could not delete {project.ProjectDirectory}. A file is still locked - " +
                        "close anything using it (an open editor/terminal in the folder, a file " +
                        "explorer window, or antivirus) and remove the project again.");
            }

            reporter.Step("Removal complete");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Remove failed");
            return Result.Fail(ex.Message);
        }
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
