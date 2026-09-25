using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// Resolves a project's local database from its configuration, so the projects list and project
/// removal agree on which database the site actually uses.
/// </summary>
internal static class DeveloperDb
{
    /// <summary>
    /// The database the project's site actually connects to: the Initial Catalog of the SiteSqlServer
    /// connection string in the project's web.config. Returns null when web.config has no usable SQL
    /// connection (LocalDB, missing file, or unparseable), so callers fall back to the conventional
    /// {project}_dnndev name. Only the database NAME is taken from web.config - never the server,
    /// because the database lives in the local Docker SQL Server and web.config's Data Source may point elsewhere.
    /// </summary>
    public static string? FromWebConfig(DnnProject project, IWebConfigService webConfig)
    {
        var conn = webConfig.ReadSiteSqlServer(Path.Combine(project.ProjectDirectory, "web.config"));
        return conn.Success && conn.Value is not null && !string.IsNullOrWhiteSpace(conn.Value.Database)
            ? conn.Value.Database
            : null;
    }
}
