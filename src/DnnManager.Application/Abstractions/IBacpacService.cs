using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>
/// Exports a (possibly Azure) SQL database to a <c>.bacpac</c> and imports it into a local SQL Server,
/// using Microsoft's SqlPackage tool. Used for project export/import, and to clone Azure SQL sources, which do
/// not support BACKUP DATABASE.
/// </summary>
public interface IBacpacService
{
    /// <summary>
    /// Ensures SqlPackage is available, installing it as a .NET global tool on demand the first time
    /// (requires the .NET SDK and <c>dotnet</c> on PATH). A no-op when SqlPackage is already present.
    /// Returns a failed <see cref="Result"/> with a manual-install hint when it cannot be provisioned.
    /// </summary>
    Task<Result> EnsureAvailableAsync(IProgressReporter reporter, CancellationToken ct);

    /// <summary>Exports <paramref name="source"/> to <paramref name="bacpacPath"/> on this host.</summary>
    Task<Result> ExportAsync(SiteSqlConnection source, string bacpacPath, IProgressReporter reporter, CancellationToken ct);

    /// <summary>
    /// Imports a <c>.bacpac</c> into a SQL Server, creating <paramref name="databaseName"/>.
    /// SqlPackage always creates a fresh database and fails if one already exists.
    /// </summary>
    Task<Result> ImportAsync(string targetServer, string saUser, string saPassword,
        string databaseName, string bacpacPath, IProgressReporter reporter, CancellationToken ct);
}
