using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

public interface ISqlConnectionTester
{
    /// <summary>
    /// Logs in to <paramref name="connection"/>'s database (not [master] - contained users only exist in
    /// their own database) and describes what it reached, e.g. "[db] on Azure SQL Database 12.0.2000.8".
    /// </summary>
    Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct, int timeoutSeconds = 15);

    /// <summary>The names of all databases on <paramref name="server"/>'s SQL Server.</summary>
    Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15);
}
