using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;

namespace DnnManager.Application.UseCases;

public sealed class ExportProjectRequest
{
    public required string ProjectName { get; init; }

    /// <summary>The folder the site serves (its IIS physical path); null for the project's folder in the projects folder.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The <c>.zip</c> to write the site's files to, or null to leave them out.</summary>
    public string? ZipPath { get; init; }

    /// <summary>The <c>.bacpac</c> to export the site's database to, or null to leave it out.</summary>
    public string? BacpacPath { get; init; }
}

/// <summary>
/// Exports a project as the pair "New project → An existing site" imports - a <c>.zip</c> of the site's files
/// and a <c>.bacpac</c> of its database - or just one of the two.
/// </summary>
public sealed class ExportProjectUseCase(
    IProjectRepository projects,
    IProjectFileCopier copier,
    IBacpacService bacpac,
    LocalSqlContainer sqlContainer,
    ILogger<ExportProjectUseCase> log,
    OperationUndo undo)
{
    // Never part of the site: source control. The site's _backup.filter adds its own paths.
    private static readonly string[] AlwaysExcluded = { ".git" };

    private readonly IProjectRepository _projects = projects;
    private readonly IProjectFileCopier _copier = copier;
    private readonly IBacpacService _bacpac = bacpac;
    private readonly LocalSqlContainer _sqlContainer = sqlContainer;
    private readonly ILogger<ExportProjectUseCase> _log = log;
    private readonly OperationUndo _undo = undo;

    public async Task<Result> ExecuteAsync(ExportProjectRequest req, IProgressReporter reporter, CancellationToken ct)
    {
        var project = req.ProjectDirectory is { } directory ? _projects.Build(req.ProjectName, directory) : _projects.Build(req.ProjectName);
        if (!Directory.Exists(project.ProjectDirectory))
            return Result.Fail($"Project folder not found: {project.ProjectDirectory}");

        if (req.ZipPath is null && req.BacpacPath is null)
            return Result.Fail("Nothing to export - choose the site files, the database, or both.");
        var zipPath = req.ZipPath is null ? null : Path.GetFullPath(req.ZipPath);
        var bacpacPath = req.BacpacPath is null ? null : Path.GetFullPath(req.BacpacPath);
        // Inside the site, the export would be served by IIS and zipped into the next export.
        var inside = Path.GetFullPath(project.ProjectDirectory).TrimEnd('\\') + "\\";
        if (new[] { zipPath, bacpacPath }.Any(p => p is not null && p.StartsWith(inside, StringComparison.OrdinalIgnoreCase)))
            return Result.Fail("Choose a location outside the project folder - the export would end up inside the site.");
        // A cancel deletes what was written so far, and the backup folder made for it.
        foreach (var path in new[] { zipPath, bacpacPath }.OfType<string>())
        {
            _undo.DeleteFolderOnUndo(Path.GetDirectoryName(path)!);
            _undo.RestoreFileOnUndo(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        try
        {
            if (zipPath is not null)
            {
                reporter.Step("Zipping the website files");
                IReadOnlyList<string> filtered;
                try { filtered = BackupFilter.Read(project.ProjectDirectory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Result.Fail($"Could not read {Path.Combine(project.ProjectDirectory, BackupFilter.FileName)}: {ex.Message}");
                }
                if (filtered.Count > 0)
                    reporter.Info($"Leaving out what {BackupFilter.FileName} lists: {string.Join(", ", filtered)}");
                var zip = await _copier.CreateZipAsync(project.ProjectDirectory, zipPath,
                    AlwaysExcluded.Concat(filtered).ToList(), reporter, ct);
                if (!zip.Success)
                {
                    RemoveIfEmpty(zipPath);
                    return zip;
                }
            }

            if (bacpacPath is not null)
            {
                reporter.Step("Exporting the database");
                // The database web.config names, as it signs in - Windows authentication too.
                var exportSource = _sqlContainer.ExportSourceOf(project);
                if (!exportSource.Success) return Result.Fail(exportSource.Error!);
                var source = exportSource.Value!;
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
                reporter.Warn("The zip's web.config still holds this machine's connection string, password included - " +
                              "Import points it at the new project's own database, but keep it in mind before sharing the zip.");
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
