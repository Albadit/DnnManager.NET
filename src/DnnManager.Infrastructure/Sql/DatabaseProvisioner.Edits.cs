using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Changing an existing site's database where a project is edited or exported: renaming it, DNN's portal aliases (its
/// site addresses) and its SSL setting.
/// </summary>
public sealed partial class DatabaseProvisioner
{
    public async Task<Result> RenameDatabaseAsync(DatabaseConnection connection, string newName, CancellationToken ct)
    {
        try
        {
            // Pooled connections of this process would keep the database in use.
            SqlConnection.ClearAllPools();
            await using var conn = await OpenAsync(connection, "master", ct);
            // Azure SQL Database (EngineEdition 5) has no single-user mode: renamed as it is there.
            using var cmd = new SqlCommand(
                "IF DB_ID(@new) IS NOT NULL THROW 50000, N'A database of that name exists already.', 1; " +
                "DECLARE @azure bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 5 THEN 1 ELSE 0 END; " +
                "IF @azure = 0 BEGIN DECLARE @single nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE'; EXEC (@single); END " +
                "BEGIN TRY " +
                "  DECLARE @rename nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' MODIFY NAME = ' + QUOTENAME(@new); EXEC (@rename); " +
                "  IF @azure = 0 BEGIN DECLARE @multi nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@new) + N' SET MULTI_USER'; EXEC (@multi); END " +
                "END TRY BEGIN CATCH " +
                "  IF @azure = 0 BEGIN DECLARE @back nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER'; EXEC (@back); END; THROW; " +
                "END CATCH", conn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@db", connection.Database);
            cmd.Parameters.AddWithValue("@new", newName);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not rename [{connection.Database}] to [{newName}] on {connection.Server}: {FirstLine(ex.Message)}");
        }
    }

    public async Task<Result<int>> UpdatePortalAliasesAsync(DatabaseConnection connection, string siteDirectory,
        IReadOnlyList<(string From, string To)> renamed, IReadOnlyList<string> added, CancellationToken ct)
    {
        try
        {
            return await WithSiteDatabaseAsync(connection, siteDirectory, async conn =>
            {
                var q = await DnnTables.QualifierAsync(conn, "PortalAlias", ct);
                if (q is null) return Result<int>.Fail("The database has no DNN portal aliases.");
                await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
                var changed = 0;
                foreach (var (from, to) in renamed.Where(r => !r.From.Equals(r.To, StringComparison.OrdinalIgnoreCase)))
                {
                    // host and host/child; an alias the new name has already (in that portal) is dropped instead of doubled.
                    changed += await ExecuteAsync(conn, tx,
                        $"DELETE a FROM dbo.[{q}PortalAlias] a WHERE (a.HTTPAlias = @from OR a.HTTPAlias LIKE @fromChild) AND EXISTS " +
                        $"(SELECT 1 FROM dbo.[{q}PortalAlias] b WHERE b.PortalID = a.PortalID AND b.HTTPAlias = @to + SUBSTRING(a.HTTPAlias, LEN(@from) + 1, 4000)); " +
                        $"UPDATE dbo.[{q}PortalAlias] SET HTTPAlias = @to + SUBSTRING(HTTPAlias, LEN(@from) + 1, 4000) " +
                        "WHERE HTTPAlias = @from OR HTTPAlias LIKE @fromChild;", ct,
                        ("@from", from), ("@fromChild", SqlText.EscapeLike(from) + "/%"), ("@to", to));
                }
                foreach (var alias in added)
                    changed += await InsertAliasAsync(conn, tx, q, alias, primary: false, ct);
                await tx.CommitAsync(ct);
                return Result<int>.Ok(changed);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<int>.Fail($"Could not update the portal aliases: {FirstLine(ex.Message)}");
        }
    }

    public async Task<Result> ReplacePortalAliasesAsync(DatabaseConnection connection, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        if (aliases.Count == 0) return Result.Ok();
        try
        {
            await using var conn = await OpenAsync(connection, connection.Database, ct);
            var q = await DnnTables.QualifierAsync(conn, "PortalAlias", ct);
            if (q is null) return Result.Fail("The database has no DNN portal aliases.");
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            // Portal 0's own aliases - not its child portals' (host/child), nor other portals'.
            await ExecuteAsync(conn, tx, $"DELETE FROM dbo.[{q}PortalAlias] WHERE PortalID = 0 AND CHARINDEX(N'/', HTTPAlias) = 0", ct);
            for (var i = 0; i < aliases.Count; i++)
                await InsertAliasAsync(conn, tx, q, aliases[i], primary: i == 0, ct);
            await tx.CommitAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not set the portal aliases: {FirstLine(ex.Message)}");
        }
    }

    public async Task<Result> SetSslAsync(DatabaseConnection connection, bool on, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(connection, connection.Database, ct);
            var settings = await DnnTables.QualifierAsync(conn, "PortalSettings", ct);
            var portals = await DnnTables.QualifierAsync(conn, "Portals", ct);
            if (settings is null || portals is null) return Result.Fail("The database has no DNN portal settings.");
            // DNN 10 has one SSLSetup setting (0 off, 1 on); DNN 9 has SSLEnabled and SSLEnforced.
            var dnn10 = (await ScalarAsync<int?>(conn, "SELECT MAX(Major) FROM dbo.[" + (await DnnTables.QualifierAsync(conn, "Version", ct)) + "Version]", ct) ?? 0) >= 10;
            var values = dnn10
                ? new[] { ("SSLSetup", on ? "1" : "0") }
                : new[] { ("SSLEnabled", on ? "True" : "False"), ("SSLEnforced", on ? "True" : "False") };
            foreach (var (name, value) in values)
                await ExecuteAsync(conn, null,
                    $"UPDATE dbo.[{settings}PortalSettings] SET SettingValue = @value WHERE SettingName = @name; " +
                    $"INSERT INTO dbo.[{settings}PortalSettings] (PortalID, SettingName, SettingValue, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate) " +
                    $"SELECT p.PortalID, @name, @value, -1, GETDATE(), -1, GETDATE() FROM dbo.[{portals}Portals] p " +
                    $"WHERE NOT EXISTS (SELECT 1 FROM dbo.[{settings}PortalSettings] s WHERE s.PortalID = p.PortalID AND s.SettingName = @name);", ct,
                    ("@name", name), ("@value", value));
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not switch DNN's SSL setting {(on ? "on" : "off")}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>Adds <paramref name="alias"/> to portal 0 unless a portal has it already; 1 when added.</summary>
    private static async Task<int> InsertAliasAsync(SqlConnection conn, SqlTransaction tx, string q, string alias, bool primary, CancellationToken ct)
    {
        // DNN 7.1 and later have BrowserType and IsPrimary; an older schema gets the columns every version has.
        var full = await ScalarAsync<int?>(conn, tx, $"SELECT COL_LENGTH('dbo.[{q}PortalAlias]', 'IsPrimary')", ct) is not null;
        return await ExecuteAsync(conn, tx,
            $"IF NOT EXISTS (SELECT 1 FROM dbo.[{q}PortalAlias] WHERE HTTPAlias = @alias) " +
            (full
                ? $"INSERT INTO dbo.[{q}PortalAlias] (PortalID, HTTPAlias, BrowserType, IsPrimary, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate) " +
                  "VALUES (0, @alias, N'Normal', @primary, -1, GETDATE(), -1, GETDATE());"
                : $"INSERT INTO dbo.[{q}PortalAlias] (PortalID, HTTPAlias, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate) " +
                  "VALUES (0, @alias, -1, GETDATE(), -1, GETDATE());"), ct,
            ("@alias", alias), ("@primary", primary));
    }

    private static async Task<int> ExecuteAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return Math.Max(0, await cmd.ExecuteNonQueryAsync(ct));
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection conn, SqlTransaction tx, string sql, CancellationToken ct)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>The site's database - a LocalDB file attached to DNN Manager's own instance for a moment (the site stopped).</summary>
    private static async Task<Result<T>> WithSiteDatabaseAsync<T>(DatabaseConnection connection, string siteDirectory,
        Func<SqlConnection, Task<Result<T>>> work, CancellationToken ct)
    {
        if (connection.Kind == DatabaseKind.LocalDbFile)
            return await LocalDbFiles.WithDatabaseAsync(siteDirectory, connection, work, ct);
        await using var conn = await OpenAsync(connection, connection.Database, ct);
        return await work(conn);
    }
}
