using System.IO.Enumeration;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Application.UseCases;

public sealed class ListProjectsUseCase
{
    private readonly AppOptions _opts;
    private readonly IProjectRepository _projects;
    private readonly IIisManager _iis;
    private readonly LocalSqlContainer _sqlContainer;
    private readonly IWebConfigService _webConfig;

    public ListProjectsUseCase(
        IOptions<AppOptions> opts,
        IProjectRepository projects,
        IIisManager iis,
        LocalSqlContainer sqlContainer,
        IWebConfigService webConfig)
    {
        _opts = opts.Value;
        _projects = projects;
        _iis = iis;
        _sqlContainer = sqlContainer;
        _webConfig = webConfig;
    }

    public async Task<IReadOnlyList<ProjectStatus>> ExecuteAsync(CancellationToken ct)
    {
        // One shared SQL Server, so whether it answers (and on which port) applies to every project.
        // Started now so the login overlaps the folder scan below.
        var sqlCheck = _sqlContainer.IsReachableAsync(ct);

        // One applicationHost.config read for all sites instead of two ServerManager instances per project.
        var siteStates = _iis.GetSiteStates();

        // Sizing a DNN site walks tens of thousands of files, and each project also parses a
        // web.config. Run that per-project work on the thread pool so the projects overlap and the
        // UI isn't blocked by a serial scan.
        var scanned = await Task.WhenAll(_projects.ListAllProjectDirectories().Select(name => Task.Run(() =>
        {
            var project = _projects.Build(name);
            return (
                Project: project,
                Size: DirectorySize(project.ProjectDirectory),
                // The database the site uses comes from its web.config; before the DNN wizard wires
                // that up, fall back to the conventional {project}_dnndev name setup creates.
                WebConfigDb: DeveloperDb.FromWebConfig(project, _webConfig));
        }, ct)));

        var sqlReachable = await sqlCheck;
        int? sqlPort = sqlReachable ? _opts.Docker.DefaultPort : null;

        var list = new List<ProjectStatus>(scanned.Length);
        foreach (var (project, size, webConfigDb) in scanned)
        {
            var siteExists = siteStates.TryGetValue(project.Name, out var siteState);

            list.Add(new ProjectStatus(
                project.Name,
                project.ProjectDirectory,
                siteExists,
                siteExists ? siteState : null,
                size,
                sqlReachable,
                webConfigDb ?? _opts.DatabaseNameFor(project.Name),
                sqlPort,
                _opts.SiteUrlFor(project.Name)));
        }
        return list;
    }

    /// <summary>
    /// Total bytes of every file under <paramref name="path"/>. Sums the sizes the directory scan
    /// already returns rather than stat-ing each file separately, skips reparse points (a junction
    /// would double-count its target or loop) and keeps counting past directories it cannot read
    /// instead of losing the whole total to one <c>UnauthorizedAccessException</c>.
    /// </summary>
    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var lengths = new FileSystemEnumerable<long>(path, (ref FileSystemEntry e) => e.Length, options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory
            };

            long total = 0;
            foreach (var length in lengths) total += length;
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
