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
                // No user: Windows authentication, as DNN Manager's own account.
                IntegratedSecurity = connection.User.Length == 0,
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
                IntegratedSecurity = database.User.Length == 0,
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
            if (qualifier is null) return Result<DatabaseFacts>.Ok(new DatabaseFacts(sizeMb, null, null));

            string? version;
            using (var v = new SqlCommand(
                       $"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.[{qualifier}Version] ORDER BY Major DESC, Minor DESC, Build DESC", conn))
                version = await v.ExecuteScalarAsync(ct) as string;

            int? portals;
            using (var p = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{qualifier}Portals]", conn))
                portals = Convert.ToInt32(await p.ExecuteScalarAsync(ct));

            return Result<DatabaseFacts>.Ok(new DatabaseFacts(sizeMb, version, portals));
        }
        catch (Exception ex)
        {
            return Result<DatabaseFacts>.Fail(ex.Message);
        }
    }

    public async Task<Result<IReadOnlyList<DnnPortal>>> ListPortalsAsync(SiteSqlConnection database, CancellationToken ct, int timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(database.Server) || string.IsNullOrWhiteSpace(database.Database))
            return Result<IReadOnlyList<DnnPortal>>.Fail("No database to read the portals from.");
        try
        {
            using var conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = database.Server,
                InitialCatalog = database.Database,
                UserID = database.User,
                Password = database.Password,
                IntegratedSecurity = database.User.Length == 0,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = timeoutSeconds
            }.ConnectionString);
            await conn.OpenAsync(ct);

            // DNN's tables can carry an "objectQualifier" prefix (dnn_Portals); find it from the Portals table.
            string? q;
            using (var find = new SqlCommand(
                       "SELECT TOP 1 LEFT(name, LEN(name) - 7) FROM sys.tables " +
                       "WHERE SCHEMA_NAME(schema_id) = 'dbo' AND (name = 'Portals' OR name LIKE '%[_]Portals') ORDER BY LEN(name)", conn))
                q = await find.ExecuteScalarAsync(ct) as string;
            if (q is null) return Result<IReadOnlyList<DnnPortal>>.Ok(Array.Empty<DnnPortal>());

            // The name is in PortalLocalization since DNN 7 (one per language - the portal's default one first),
            // in Portals itself before that. IsPrimary on aliases is DNN 7+ too.
            using var columns = new SqlCommand(
                $"SELECT OBJECT_ID('dbo.[{q}PortalLocalization]'), COL_LENGTH('dbo.[{q}PortalAlias]', 'IsPrimary'), " +
                $"COL_LENGTH('dbo.[{q}Portals]', 'PortalName')", conn);
            bool localized, primaryFlag, nameInPortals;
            using (var rdr = await columns.ExecuteReaderAsync(ct))
            {
                await rdr.ReadAsync(ct);
                localized = !rdr.IsDBNull(0);
                primaryFlag = !rdr.IsDBNull(1);
                nameInPortals = !rdr.IsDBNull(2);
            }
            var name = localized
                ? $"(SELECT TOP 1 pl.PortalName FROM dbo.[{q}PortalLocalization] pl WHERE pl.PortalID = p.PortalID " +
                  "ORDER BY CASE WHEN pl.CultureCode = p.DefaultLanguage THEN 0 ELSE 1 END, pl.CultureCode)"
                : nameInPortals ? "p.PortalName" : "NULL";

            var portals = new List<(int Id, string Name, bool Expired)>();
            using (var p = new SqlCommand(
                       $"SELECT p.PortalID, {name}, CASE WHEN p.ExpiryDate IS NOT NULL AND p.ExpiryDate < GETDATE() THEN 1 ELSE 0 END " +
                       $"FROM dbo.[{q}Portals] p ORDER BY p.PortalID", conn))
            using (var rdr = await p.ExecuteReaderAsync(ct))
                while (await rdr.ReadAsync(ct))
                    portals.Add((rdr.GetInt32(0), rdr.IsDBNull(1) ? $"Portal {rdr.GetInt32(0)}" : rdr.GetString(1), rdr.GetInt32(2) == 1));

            var aliases = new Dictionary<int, List<DnnPortalAlias>>();
            using (var a = new SqlCommand(
                       $"SELECT PortalID, HTTPAlias, {(primaryFlag ? "CAST(IsPrimary AS int)" : "0")} FROM dbo.[{q}PortalAlias] " +
                       $"ORDER BY PortalID, {(primaryFlag ? "IsPrimary DESC, " : "")}PortalAliasID", conn))
            using (var rdr = await a.ExecuteReaderAsync(ct))
                while (await rdr.ReadAsync(ct))
                {
                    var portalId = rdr.GetInt32(0);
                    if (!aliases.TryGetValue(portalId, out var list)) aliases[portalId] = list = new List<DnnPortalAlias>();
                    list.Add(new DnnPortalAlias(rdr.GetString(1), !rdr.IsDBNull(2) && rdr.GetInt32(2) == 1));
                }

            return Result<IReadOnlyList<DnnPortal>>.Ok(portals
                .Select(p => new DnnPortal(p.Id, p.Name, p.Expired, aliases.GetValueOrDefault(p.Id) ?? new List<DnnPortalAlias>()))
                .ToList());
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<DnnPortal>>.Fail(ex.Message);
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
                IntegratedSecurity = server.User.Length == 0,
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
