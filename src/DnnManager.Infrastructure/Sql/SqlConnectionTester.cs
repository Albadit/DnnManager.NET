using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

public sealed class SqlConnectionTester : ISqlConnectionTester
{
    public async Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct, int timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(connection.Server)) return Result<string>.Fail("SQL server is empty.");
        try
        {
            // No user: Windows authentication, as DNN Manager's own account.
            using var conn = new SqlConnection(ConnectionStrings.For(connection.Server,
                string.IsNullOrWhiteSpace(connection.Database) ? "master" : connection.Database,
                connection.User, connection.Password, timeoutSeconds).ConnectionString);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(
                "SELECT DB_NAME(), CAST(SERVERPROPERTY('EngineEdition') AS int), " +
                "CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", conn);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            await rdr.ReadAsync(ct);
            var database = rdr.GetString(0);
            var engine = rdr.GetInt32(1) == 5 ? "Azure SQL Database" : "SQL Server";
            var version = rdr.IsDBNull(2) ? "" : " " + rdr.GetString(2);
            return Result<string>.Ok($"[{database}] on {engine}{version}");
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(ConnectionStrings.Explained(ex.Message));
        }
    }

    public async Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(server.Server)) return Result<IReadOnlyList<string>>.Fail("SQL server is empty.");
        try
        {
            var builder = ConnectionStrings.For(server.Server, "master", server.User, server.Password, timeoutSeconds);
            // Asked again every few seconds to see whether the server is there (ServerStateMonitor): each time for real,
            // not answered from the last failure for up to a minute - so a server that comes back is seen at the next look.
            builder.PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock;
            using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand("SELECT name FROM sys.databases", conn);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            var names = new List<string>();
            while (await rdr.ReadAsync(ct)) names.Add(rdr.GetString(0));
            return Result<IReadOnlyList<string>>.Ok(names);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<string>>.Fail(ConnectionStrings.Explained(ex.Message));
        }
    }
}
