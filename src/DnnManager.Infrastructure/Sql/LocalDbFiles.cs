using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Abstractions;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// A site's LocalDB file database (<c>App_Data\Database.mdf</c>), opened by DNN Manager itself. Under IIS the site runs
/// the file in the LocalDB instance of its app pool identity, which nobody else can sign in to - so DNN Manager attaches
/// the file to its own instance for a moment, while the site is stopped, and detaches it again. Detaching gives the file
/// permissions of its own; they are set back to the folder's, which let the site's identity in.
/// </summary>
public static class LocalDbFiles
{
    // The site may have to be stopped a moment before LocalDB lets go of the file.
    private static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(60);

    /// <summary>The database file's full path for a site in <paramref name="siteDirectory"/>.</summary>
    public static string PathOf(string siteDirectory, DatabaseConnection connection) =>
        Path.Combine(siteDirectory, "App_Data", connection.Database);

    /// <summary>Runs <paramref name="work"/> on the site's database file, then detaches it.</summary>
    public static async Task<T> WithDatabaseAsync<T>(string siteDirectory, DatabaseConnection connection,
        Func<SqlConnection, Task<T>> work, CancellationToken ct)
    {
        var file = PathOf(siteDirectory, connection);
        if (!File.Exists(file)) throw new FileNotFoundException($"The site's database file isn't there: {file}", file);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = connection.Server,
            AttachDBFilename = file,
            IntegratedSecurity = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
            ConnectTimeout = 60,
            // Pooled, the connection would keep the database open after this and stop it from being detached.
            Pooling = false
        };
        try
        {
            var until = DateTime.UtcNow + OpenFor;
            while (true)
            {
                try
                {
                    await using var conn = new SqlConnection(builder.ConnectionString);
                    await conn.OpenAsync(ct);
                    return await work(conn);
                }
                // In use by the site's own instance - it closes a database a moment after the site lets go.
                catch (SqlException ex) when (ex.Number is 5120 or 1832 or 32 && DateTime.UtcNow < until)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        finally
        {
            await DetachAsync(connection.Server, file, CancellationToken.None);
            ResetPermissions(file);
            ResetPermissions(Path.ChangeExtension(file, null) + "_log.ldf");
        }
    }

    /// <summary>Detaches the database whose data file is <paramref name="file"/> from <paramref name="server"/>, if it is attached there.</summary>
    public static async Task DetachAsync(string server, string file, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Optional,
                ConnectTimeout = 60,
                Pooling = false
            }.ConnectionString);
            await conn.OpenAsync(ct);
            // Attached by its path the database is named after it - or, for a long path, a shortened name: find it by its file.
            using var find = new SqlCommand(
                "SELECT TOP 1 DB_NAME(database_id) FROM sys.master_files WHERE type = 0 AND LOWER(physical_name) = LOWER(@file)", conn);
            find.Parameters.AddWithValue("@file", file);
            if (await find.ExecuteScalarAsync(ct) is not string name) return;
            using var detach = new SqlCommand(
                "DECLARE @single nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE'; EXEC (@single); " +
                "EXEC sp_detach_db @dbname = @db;", conn);
            detach.Parameters.AddWithValue("@db", name);
            await detach.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException)
        {
            // Not attached here, or LocalDB not there: nothing to let go of.
        }
    }

    /// <summary>
    /// Gives <paramref name="file"/> its folder's permissions back (drops what a detach set on it and inherits again) -
    /// the folder lets the site's identity in.
    /// </summary>
    public static void ResetPermissions(string file)
    {
        if (!File.Exists(file)) return;
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            security.RemoveAccessRuleSpecific(rule);
        info.SetAccessControl(security);
    }
}
