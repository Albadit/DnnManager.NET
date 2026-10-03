using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Projects;

public sealed class FileSystemProjectRepository(IOptions<AppOptions> opts, AppDataPaths paths) : IProjectRepository
{
    private readonly AppOptions _opts = opts.Value;
    private readonly AppDataPaths _paths = paths;

    public DnnProject Build(string projectName)
    {
        // The project directory IS the published/served DNN site. Its dated backups are kept apart from it, in
        // Documents\DnnManager\backups\<project> (see ProjectBackups), so they never end up in the site or its exports.
        return new DnnProject(
            projectName,
            Path.Combine(_opts.BaseDirectory, projectName),
            Path.Combine(_paths.BackupsDirectory, projectName));
    }

    public DnnProject Build(string siteName, string directory) =>
        new(siteName, directory, Path.Combine(_paths.BackupsDirectory, siteName));

    public IReadOnlyList<string> ListProjectsWithBackups()
    {
        if (!Directory.Exists(_paths.BackupsDirectory)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(_paths.BackupsDirectory)
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool ProjectExists(string projectName)
        => Directory.Exists(Path.Combine(_opts.BaseDirectory, projectName));

    public IReadOnlyList<string> ListAllProjectDirectories()
    {
        if (!Directory.Exists(_opts.BaseDirectory)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(_opts.BaseDirectory)
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
