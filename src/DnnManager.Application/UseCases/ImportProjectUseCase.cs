using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;

namespace DnnManager.Application.UseCases;

public sealed class ImportProjectRequest
{
    public required string ProjectName { get; init; }

    /// <summary>A <c>.zip</c> of the site's files.</summary>
    public required string ZipPath { get; init; }

    /// <summary>The site's database: a <c>.bacpac</c> (or <c>.bak</c>) restored into the project's database.</summary>
    public required string BackupFilePath { get; init; }
}

/// <summary>
/// Creates a project from a zipped DNN site and its database backup: extracts the zip into a new project
/// folder, then hosts it the way "Host project" does - the IIS website, plus the database restored from the
/// backup.
/// </summary>
public sealed class ImportProjectUseCase
{
    private readonly IProjectRepository _projects;
    private readonly IProjectFileCopier _copier;
    private readonly IProjectScaffolder _scaffolder;
    private readonly HostExistingProjectUseCase _host;
    private readonly ILogger<ImportProjectUseCase> _log;

    public ImportProjectUseCase(
        IProjectRepository projects,
        IProjectFileCopier copier,
        IProjectScaffolder scaffolder,
        HostExistingProjectUseCase host,
        ILogger<ImportProjectUseCase> log)
    {
        _projects = projects;
        _copier = copier;
        _scaffolder = scaffolder;
        _host = host;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(ImportProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var nameCheck = ProjectName.Validate(req.ProjectName);
        if (!nameCheck.Success) return nameCheck;
        if (!File.Exists(req.BackupFilePath)) return Result.Fail($"Backup file not found: {req.BackupFilePath}");
        if (!LocalSqlContainer.IsBackupFile(req.BackupFilePath))
            return Result.Fail($"Not a .bacpac or .bak file: {req.BackupFilePath}");

        var project = _projects.Build(req.ProjectName);
        if (Directory.Exists(project.ProjectDirectory))
            return Result.Fail($"The folder {project.ProjectDirectory} already exists - choose another name, " +
                               "or use 'Host project' for a folder that's already there.");

        reporter.Step($"Extracting {Path.GetFileName(req.ZipPath)}");
        try
        {
            Directory.CreateDirectory(project.ProjectDirectory);
            var extracted = await _copier.ExtractZipAsync(req.ZipPath, project.ProjectDirectory, reporter, ct);
            if (!extracted.Success)
            {
                RemoveFolder(project.ProjectDirectory);
                return Result.Fail(extracted.Error ?? "Extracting the zip failed.");
            }
        }
        catch (Exception ex)
        {
            // The folder is new, so a half-extracted one is ours to clean up (on cancel too).
            RemoveFolder(project.ProjectDirectory);
            if (ex is OperationCanceledException) throw;
            _log.LogError(ex, "Extracting {Zip} failed", req.ZipPath);
            return Result.Fail($"Extracting the zip failed: {ex.Message}");
        }

        var gitignore = _scaffolder.EnsureGitignore(project.ProjectDirectory);
        if (gitignore.Success)
            reporter.Info("Project .gitignore ready.");
        else
            reporter.Info($"Could not write .gitignore: {gitignore.Error}");

        reporter.Info($"Database backup: {Path.GetFileName(req.BackupFilePath)}.");
        return await _host.ExecuteAsync(new HostExistingProjectRequest
        {
            ProjectName = req.ProjectName,
            SetupIis = true,
            SetupDatabase = true,
            BackupFilePath = req.BackupFilePath
        }, reporter, ct);
    }

    private void RemoveFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not remove {Path} after a failed import", path); }
    }
}
