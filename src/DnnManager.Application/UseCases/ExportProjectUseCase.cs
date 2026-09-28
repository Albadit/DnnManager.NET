using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    // Not part of the site: DNN Manager's own backups folder, and source control.
    private static readonly string[] ExcludedFolders = { "backups", ".git" };

    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IProjectFileCopier _copier;
    private readonly IWebConfigService _webConfig;
    private readonly IBacpacService _bacpac;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly ILogger<ExportProjectUseCase> _log;

    public ExportProjectUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IProjectFileCopier copier,
        IWebConfigService webConfig,
        IBacpacService bacpac,
        LocalSqlContainer sqlContainer,
        ILogger<ExportProjectUseCase> log)
    {
        _opts = opts.Value;
        _projects = projects;
        _copier = copier;
        _webConfig = webConfig;
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
        var inside = Path.GetFullPath(project.ProjectDirectory).TrimEnd('\\') + "\\";
        if (new[] { zipPath, bacpacPath }.Any(p => p is not null && p.StartsWith(inside, StringComparison.OrdinalIgnoreCase)))
            return Result.Fail("Choose a location outside the project folder - the export would end up inside the site.");

        try
        {
            if (zipPath is not null)
            {
                reporter.Step("Zipping the website files");
                var zip = await _copier.CreateZipAsync(project.ProjectDirectory, zipPath, ExcludedFolders, reporter, ct);
                if (!zip.Success) return zip;
            }

            if (bacpacPath is not null)
            {
                reporter.Step("Exporting the database");
                var source = DatabaseOf(project);
                var ensured = await _bacpac.EnsureAvailableAsync(reporter, ct);
                var export = ensured.Success ? await _bacpac.ExportAsync(source, bacpacPath, reporter, ct) : ensured;
                if (!export.Success)
                    return Result.Fail((zipPath is null ? "" : $"The site files are in {zipPath}, but ") +
                                       $"exporting database [{source.Database}] failed: {export.Error}");
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
            return Result.Fail(ex.Message);
        }
    }

    /// <summary>
    /// The database the site uses: its web.config connection when that has a SQL login, otherwise the site's
    /// database on the local SQL Server as sa.
    /// </summary>
    private SiteSqlConnection DatabaseOf(DnnProject project)
    {
        var conn = _webConfig.ReadSiteSqlServer(Path.Combine(project.ProjectDirectory, "web.config"));
        if (conn is { Success: true, Value: { } c } && c.User.Length > 0 && c.Database.Length > 0)
            return c;

        var database = DeveloperDb.FromWebConfig(project, _webConfig) ?? _opts.DatabaseNameFor(project.Name);
        return new SiteSqlConnection(_sqlContainer.Server, database, "sa", _opts.Docker.SaPassword);
    }
}
