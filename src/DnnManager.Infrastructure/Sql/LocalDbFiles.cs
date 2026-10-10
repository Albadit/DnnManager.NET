using System.Security.AccessControl;
using System.Security.Principal;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Processes;
using Microsoft.Data.SqlClient;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// A site's LocalDB file database (<c>App_Data\Database.mdf</c>), opened by DNN Manager itself. Under IIS the site runs
/// the file in the LocalDB instance of its app pool identity, which nobody else can sign in to - so DNN Manager attaches
/// the file to its own instance for a moment, while the site is stopped, and detaches it again. Detaching gives the file
/// permissions of its own; they are set back to the folder's, which let the site's identity in.
/// <para>
/// Its own instance is one of the file's version (<see cref="LocalDbVersions"/>): the user's <c>MSSQLLocalDB</c> may be
/// older than the site's (made by Visual Studio's LocalDB 2019, the site's by 2025) and can't open the file, or newer and
/// would upgrade it beyond what the site's instance opens.
/// </para>
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

        var until = DateTime.UtcNow + OpenFor;
        var server = await ServerForAsync(connection.Server, file, until, ct);
        var log = Path.ChangeExtension(file, null) + "_log.ldf";
        // Attaching and detaching give a file permissions for the account that did it; LocalDB runs without the
        // Administrators group, so a file the site's instance had last may not let this one in - the folder's first.
        TryResetPermissions(file);
        TryResetPermissions(log);
        var builder = ConnectionStrings.For(server, null, "", null, 60);
        builder.AttachDBFilename = file;
        // Pooled, the connection would keep the database open after this and stop it from being detached.
        builder.Pooling = false;
        try
        {
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
            await DetachAsync(server, file, CancellationToken.None);
            ResetPermissions(file);
            ResetPermissions(log);
        }
    }

    // Before attaching: best effort - if the permissions can't be set, the attach says why it can't open the file.
    private static void TryResetPermissions(string file)
    {
        try { ResetPermissions(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// <summary>
    /// The LocalDB to open <paramref name="file"/> in: <paramref name="configured"/> (the user's <c>MSSQLLocalDB</c>) when it
    /// is the version the file needs (<see cref="LocalDbVersions.MajorToOpen"/>), else DNN Manager's own instance of that
    /// version - <c>(LocalDB)\DnnManager17</c>, made the first time. As configured when it isn't LocalDB, or the file's
    /// version can't be read.
    /// </summary>
    private static async Task<string> ServerForAsync(string configured, string file, DateTime until, CancellationToken ct)
    {
        if (!ConnectionStrings.IsLocalDb(configured)) return configured;
        var installed = LocalDbVersions.Installed();
        if (installed.Count == 0 || await FileVersionAsync(file, until, ct) is not { } fileVersion) return configured;
        if (LocalDbVersions.MajorToOpen(fileVersion, installed.Keys) is not { } major)
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} is a {DatabaseProvisioner.ProductName(LocalDbVersions.MajorFor(fileVersion))} database - newer than " +
                $"any LocalDB on this PC (the newest is {DatabaseProvisioner.ProductName(installed.Keys.Max())}). Install that version's LocalDB to open it.");
        if (await MajorVersionOfAsync(configured, ct) == major) return configured;

        var server = $@"(LocalDB)\DnnManager{major}";
        await EnsureInstanceAsync(server, major, installed[major], ct);
        return server;
    }

    /// <summary>The file's version - waiting while the site's instance still holds it, as opening it does.</summary>
    private static async Task<int?> FileVersionAsync(string file, DateTime until, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return LocalDbVersions.FileVersionOf(file);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The LocalDB instance <paramref name="server"/>'s master database, as you, not pooled.</summary>
    private static string Master(string server)
    {
        var builder = ConnectionStrings.For(server, "master", "", null, 60);
        builder.Pooling = false;
        return builder.ConnectionString;
    }

    /// <summary>The major version (15, 17…) of the LocalDB instance <paramref name="server"/>; null when it doesn't start.</summary>
    private static async Task<int?> MajorVersionOfAsync(string server, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(Master(server));
            await conn.OpenAsync(ct);
            using var version = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", conn);
            return await version.ExecuteScalarAsync(ct) as int?;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The current user's LocalDB instance <paramref name="server"/> at version <paramref name="major"/> - made the first
    /// time. <c>SqlLocalDB create</c> leaves an instance that is there as it is, and answers exit code 0 even when it
    /// fails, so what counts is that the instance then answers at that version.
    /// </summary>
    private static async Task EnsureInstanceAsync(string server, int major, string instanceApi, CancellationToken ct)
    {
        if (await MajorVersionOfAsync(server, ct) == major) return;
        var name = server[(server.IndexOf('\\') + 1)..];
        var run = await new ProcessRunner().RunAsync(LocalDbVersions.SqlLocalDbExe(instanceApi), ["create", name, $"{major}.0"], ct);
        if (await MajorVersionOfAsync(server, ct) != major)
            throw new InvalidOperationException($"Could not make the LocalDB instance {name} ({DatabaseProvisioner.ProductName(major)}): " +
                                                (run.StdErr.Trim().Length > 0 ? run.StdErr : run.StdOut).Trim().Split('\n').Last().Trim());
    }

    /// <summary>Detaches the database whose data file is <paramref name="file"/> from <paramref name="server"/>, if it is attached there.</summary>
    public static async Task DetachAsync(string server, string file, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(Master(server));
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
