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
            using var conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = connection.Server,
                InitialCatalog = string.IsNullOrWhiteSpace(connection.Database) ? "master" : connection.Database,
                UserID = connection.User,
                Password = connection.Password,
                Encrypt = true,                  // Azure SQL requires TLS.
                TrustServerCertificate = true,
                ConnectTimeout = timeoutSeconds
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

    public async Task<Result<DatabaseFacts>> DescribeDatabaseAsync(SiteSqlConnection database, CancellationToken ct, int timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(database.Server) || string.IsNullOrWhiteSpace(database.Database))
            return Result<DatabaseFacts>.Fail("No database to describe.");
        try
        {
            using var conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = database.Server,
                InitialCatalog = database.Database,
                UserID = database.User,
                Password = database.Password,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = timeoutSeconds
            }.ConnectionString);
            await conn.OpenAsync(ct);

            // Data + log files, in 8 KB pages.
            double sizeMb;
            using (var size = new SqlCommand("SELECT SUM(CAST(size AS bigint)) * 8 / 1024.0 FROM sys.database_files", conn))
                sizeMb = Convert.ToDouble(await size.ExecuteScalarAsync(ct) ?? 0);

            // DNN's tables can carry an "objectQualifier" prefix (dnn_Version); find it from the Version table.
            string? qualifier;
            using (var find = new SqlCommand(
                       "SELECT TOP 1 LEFT(name, LEN(name) - 7) FROM sys.tables " +
                       "WHERE SCHEMA_NAME(schema_id) = 'dbo' AND (name = 'Version' OR name LIKE '%[_]Version') ORDER BY LEN(name)", conn))
                qualifier = await find.ExecuteScalarAsync(ct) as string;
            if (qualifier is null) return Result<DatabaseFacts>.Ok(new DatabaseFacts(sizeMb, null, null, Array.Empty<string>()));

            string? version;
            using (var v = new SqlCommand(
                       $"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.[{qualifier}Version] ORDER BY Major DESC, Minor DESC, Build DESC", conn))
                version = await v.ExecuteScalarAsync(ct) as string;

            int? portals;
            using (var p = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{qualifier}Portals]", conn))
                portals = Convert.ToInt32(await p.ExecuteScalarAsync(ct));

            var aliases = new List<string>();
            using (var a = new SqlCommand($"SELECT HTTPAlias FROM dbo.[{qualifier}PortalAlias] ORDER BY PortalID, HTTPAlias", conn))
            using (var rdr = await a.ExecuteReaderAsync(ct))
                while (await rdr.ReadAsync(ct)) aliases.Add(rdr.GetString(0));

            return Result<DatabaseFacts>.Ok(new DatabaseFacts(sizeMb, version, portals, aliases));
        }
        catch (Exception ex)
        {
            return Result<DatabaseFacts>.Fail(ex.Message);
        }
    }

    public async Task<Result<IReadOnlyList<string>>> ListDatabasesAsync(SiteSqlConnection server, CancellationToken ct, int timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(server.Server)) return Result<IReadOnlyList<string>>.Fail("SQL server is empty.");
        try
        {
            using var conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = server.Server,
                InitialCatalog = "master",
                UserID = server.User,
                Password = server.Password,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = timeoutSeconds,
                // Asked again every few seconds to see whether the server is there (ServerStateMonitor): each time
                // for real, not answered from the last failure for up to a minute - so a server that comes back is
                // seen at the next look.
                PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock
            }.ConnectionString);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand("SELECT name FROM sys.databases", conn);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            var names = new List<string>();
            while (await rdr.ReadAsync(ct)) names.Add(rdr.GetString(0));
            return Result<IReadOnlyList<string>>.Ok(names);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<string>>.Fail(ex.Message);
        }
    }
}
