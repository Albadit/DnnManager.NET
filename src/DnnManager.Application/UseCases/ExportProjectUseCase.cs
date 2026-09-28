using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;

namespace DnnManager.Application.UseCases;

public sealed class ExportProjectRequest
{
    public required string ProjectName { get; init; }

    /// <summary>The <c>.zip</c> to write the site's files to, or null to leave them out.</summary>
    public string? ZipPath { get; init; }

    /// <summary>The <c>.bacpac</c> to export the site's database to, or null to leave it out.</summary>
    public string? BacpacPath { get; init; }
}

/// <summary>
/// Exports a project as the pair "New project → An existing site" imports - a <c>.zip</c> of the site's files
/// and a <c>.bacpac</c> of its database - or just one of the two.
/// </summary>
public sealed class ExportProjectUseCase
{
    // Not part of the site: DNN Manager's own backups folders (so backups never end up inside backups), and source control.
    private static readonly string[] ExcludedFolders = { ProjectBackups.FolderName, ProjectBackups.LegacyFolderName, ".git" };

    private readonly IProjectRepository _projects;
    private readonly IProjectFileCopier _copier;
    private readonly IBacpacService _bacpac;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly ILogger<ExportProjectUseCase> _log;

    public ExportProjectUseCase(
        IProjectRepository projects,
        IProjectFileCopier copier,
        IBacpacService bacpac,
        LocalSqlContainer sqlContainer,
        ILogger<ExportProjectUseCase> log)
    {
        _projects = projects;
        _copier = copier;
        _bacpac = bacpac;
        _sqlContainer = sqlContainer;
        _log = log;
    }

    public async Task<Result> ExecuteAsync(ExportProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var project = _projects.Build(req.ProjectName);
        if (!Directory.Exists(project.ProjectDirectory))
            return Result.Fail($"Project folder not found: {project.ProjectDirectory}");

        if (req.ZipPath is null && req.BacpacPath is null)
            return Result.Fail("Nothing to export - choose the site files, the database, or both.");
        var zipPath = req.ZipPath is null ? null : Path.GetFullPath(req.ZipPath);
        var bacpacPath = req.BacpacPath is null ? null : Path.GetFullPath(req.BacpacPath);
        // Inside the site is only allowed in its backups folder, which the zip leaves out.
        var inside = Path.GetFullPath(project.ProjectDirectory).TrimEnd('\\') + "\\";
        var backups = Path.GetFullPath(project.BackupDirectory).TrimEnd('\\') + "\\";
        if (new[] { zipPath, bacpacPath }.Any(p => p is not null && p.StartsWith(inside, StringComparison.OrdinalIgnoreCase)
                                                && !p.StartsWith(backups, StringComparison.OrdinalIgnoreCase)))
            return Result.Fail($"Choose a location outside the project folder (or in its {ProjectBackups.FolderName} folder) - " +
                               "the export would end up inside the site.");
        foreach (var path in new[] { zipPath, bacpacPath }.OfType<string>())
        {
            if (path.StartsWith(backups, StringComparison.OrdinalIgnoreCase)) ProjectBackups.EnsureFolder(project);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        try
        {
            if (zipPath is not null)
            {
                reporter.Step("Zipping the website files");
                var zip = await _copier.CreateZipAsync(project.ProjectDirectory, zipPath, ExcludedFolders, reporter, ct);
                if (!zip.Success)
                {
                    RemoveIfEmpty(zipPath);
                    return zip;
                }
            }

            if (bacpacPath is not null)
            {
                reporter.Step("Exporting the database");
                var source = _sqlContainer.ConnectionOf(project);
                var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
                var export = ensured.Success ? await _bacpac.ExportAsync(source, bacpacPath, reporter, ct) : ensured;
                if (!export.Success)
                {
                    RemoveIfEmpty(bacpacPath);
                    return Result.Fail((zipPath is null ? "" : $"The site files are in {zipPath}, but ") +
                                       $"exporting database [{source.Database}] failed: {export.Error}");
                }
                reporter.Success($"Database [{source.Database}] exported to {bacpacPath}");
            }

            reporter.Step("Export complete");
            if (zipPath is not null) reporter.Success($"Site files: {zipPath}");
            if (bacpacPath is not null) reporter.Success($"Database:   {bacpacPath}");
            if (zipPath is not null && bacpacPath is not null)
                reporter.Info("Import both with New project → An existing site.");
            if (zipPath is not null)
                reporter.Warn("The zip's web.config still holds this machine's connection string, sa password included - " +
                              "importing rewrites it, but keep it in mind before sharing the zip.");
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Exporting {Project} failed", req.ProjectName);
            RemoveIfEmpty(zipPath ?? bacpacPath);
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>A backup folder left empty by a failed export is removed again.</summary>
    private static void RemoveIfEmpty(string? file)
    {
        try
        {
            var folder = file is null ? null : Path.GetDirectoryName(file);
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch { /* best effort */ }
    }
}
