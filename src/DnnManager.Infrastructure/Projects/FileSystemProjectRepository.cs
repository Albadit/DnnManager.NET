using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Projects;

public sealed class FileSystemProjectRepository : IProjectRepository
{
    private readonly AppOptions _opts;

    public FileSystemProjectRepository(IOptions<AppOptions> opts) => _opts = opts.Value;

    public DnnProject Build(string projectName)
    {
        // The project directory IS the published/served DNN site; the backups folder lives at its
        // root. There is no per-project docker-compose - one shared compose file ships with the app.
        var projectDir = Path.Combine(_opts.BaseDirectory, projectName);
        return new DnnProject(
            projectName,
            projectDir,
            Path.Combine(projectDir, "backups"));
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
