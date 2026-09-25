using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

public sealed class SqlConnectionTester : ISqlConnectionTester
{
    public async Task<Result<string>> TestAsync(SiteSqlConnection connection, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.Server)) return Result<string>.Fail("SQL server is empty.");
        try
        {
            using var conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = connection.Server,
                InitialCatalog = string.IsNullOrWhiteSpace(connection.Database) ? "master" : connection.Database,
                UserID = connection.User,
                Password = connection.Password,
                Encrypt = true,                  // Azure SQL requires TLS.
                TrustServerCertificate = true,
                ConnectTimeout = 15
            }.ConnectionString);
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
            return Result<string>.Fail(ex.Message);
        }
    }
}
