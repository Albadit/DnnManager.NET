using System.Text.RegularExpressions;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Site databases on any SQL Server, with SqlClient: Test connection's checks (the same ones DNN's own installation
/// wizard runs, and what DNN Manager itself needs), creating the database, letting the site's Windows identity own
/// it, and dropping it.
/// </summary>
public sealed partial class DatabaseProvisioner : IDatabaseProvisioner
{
    private const int ConnectTimeoutSeconds = 10;
    // LocalDB may have to create and start the instance first.
    private const int LocalDbConnectTimeoutSeconds = 60;

    private readonly ILogger<DatabaseProvisioner> _log;

    public DatabaseProvisioner(ILogger<DatabaseProvisioner> log) => _log = log;

    // ─── Test connection ──────────────────────────────────────────────────

    public async Task<DatabaseCheckReport> CheckAsync(DatabaseConnection connection, DatabaseCheckOptions options, CancellationToken ct)
    {
        var checks = new List<DatabaseCheck>();
        try
        {
            if (connection.Kind == DatabaseKind.LocalDbFile) await CheckLocalDbAsync(connection, options, checks, ct);
            else await CheckServerAsync(connection, options, checks, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Database check failed");
            checks.Add(Failed("Test", FirstLine(ex.Message)));
        }
        return new DatabaseCheckReport(checks);
    }

    private static async Task CheckServerAsync(DatabaseConnection c, DatabaseCheckOptions options, List<DatabaseCheck> checks, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Server))
        {
            checks.Add(Failed("Server", @"Enter the SQL Server - e.g. .\SQLEXPRESS, localhost or localhost,1433."));
            return;
        }
        if (!c.UsesWindowsAuthentication && string.IsNullOrWhiteSpace(c.User))
        {
            checks.Add(Failed("Sign in", "Enter the SQL Server login's user name."));
            return;
        }
        if (!options.ServerOnly && string.IsNullOrWhiteSpace(c.Database))
        {
            checks.Add(Failed("Database", "Enter the database's name."));
            return;
        }

        await using var conn = new SqlConnection(ConnectionStrings.ForApp(c, "master", ConnectTimeoutSeconds));
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (SqlException ex) when (IsLoginFailure(ex))
        {
            checks.Add(Passed("Server reachable", c.Server));
            checks.Add(Failed("Sign in", c.UsesWindowsAuthentication
                ? $"SQL Server doesn't accept {Environment.UserDomainName}\\{Environment.UserName} - give that Windows account a login, or use SQL Server authentication."
                : $"Login failed for '{c.User}' - check the user name and password, and that the server allows SQL Server authentication."));
            return;
        }
        catch (SqlException ex)
        {
            checks.Add(Failed("Server reachable",
                $"Can't reach {c.Server}: {FirstLine(ex.Message)} Check the server name, that SQL Server is running, and that it accepts TCP/IP connections."));
            return;
        }

        string version, edition, login;
        int engine;
        using (var facts = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), " +
                                          "CAST(SERVERPROPERTY('EngineEdition') AS int), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), SUSER_SNAME()", conn))
        using (var rdr = await facts.ExecuteReaderAsync(ct))
        {
            await rdr.ReadAsync(ct);
            version = rdr.GetString(0);
            engine = rdr.GetInt32(1);
            edition = rdr.GetString(2);
            login = rdr.GetString(3);
        }
        var azure = engine == 5;
        var major = int.TryParse(version.Split('.')[0], out var m) ? m : 0;
        checks.Add(Passed("Server reachable", azure ? $"Azure SQL Database ({version})" : $"{ProductName(major)} {edition} ({version})"));
        checks.Add(Passed("Signed in", $"as {login}"));
        if (!azure && major < options.MinimumMajorVersion)
        {
            checks.Add(Failed("Version", $"{ProductName(major)} is too old for this DNN version - it needs {ProductName(options.MinimumMajorVersion)} or later."));
            return;
        }

        if (options.ServerOnly)
        {
            var mayCreate = await ScalarAsync<int>(conn, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')", ct) == 1;
            checks.Add(mayCreate
                ? Passed("Create databases", $"{login} may create databases - new projects get theirs here.")
                : Failed("Create databases", $"{login} may not create databases - give it the dbcreator role, or create each project's database first with {login} as its owner."));
            return;
        }

        var exists = await ScalarAsync<int>(conn, "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END", ct, ("@db", c.Database)) == 1;
        if (!exists)
        {
            if (azure)
            {
                checks.Add(Failed("Database", $"[{c.Database}] doesn't exist - create it in Azure first, with {login} as its owner."));
                return;
            }
            var canCreate = await ScalarAsync<int>(conn, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')", ct) == 1;
            checks.Add(canCreate
                ? Passed("Database", $"[{c.Database}] doesn't exist yet - it will be created.")
                : Failed("Database", $"[{c.Database}] doesn't exist, and {login} may not create databases - create it first with {login} as its owner, or use a login with the dbcreator role."));
        }
        else
        {
            try
            {
                await conn.ChangeDatabaseAsync(c.Database, ct);
            }
            catch (SqlException)
            {
                checks.Add(Failed("Database", $"[{c.Database}] exists, but {login} can't open it - make {login} its owner (db_owner)."));
                return;
            }

            var dnnVersion = await DnnVersionAsync(conn, ct);
            var tables = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0", ct);
            if (dnnVersion is not null)
                checks.Add(options.ForNewInstall
                    ? Failed("Database", $"[{c.Database}] already holds a DNN site (DNN {dnnVersion}) - choose another database, or remove that site first.")
                    : Passed("Database", $"[{c.Database}] holds DNN {dnnVersion}."));
            else if (tables > 0 && options.ForNewInstall)
                checks.Add(Failed("Database", $"[{c.Database}] isn't empty ({tables} tables) - DNN is installed into an empty database."));
            else
                checks.Add(Passed("Database", $"[{c.Database}] exists and is empty - DNN will be installed into it."));

            // DNN's own permission check: its install creates tables, procedures and roles.
            var owner = await ScalarAsync<int>(conn, "SELECT CASE WHEN IS_ROLEMEMBER('db_owner') = 1 OR IS_SRVROLEMEMBER('sysadmin') = 1 THEN 1 ELSE 0 END", ct) == 1;
            if (!owner)
            {
                checks.Add(Failed("Permissions", $"{login} isn't an owner (db_owner) of [{c.Database}] - DNN creates tables, procedures and roles, so it needs db_owner."));
                return;
            }
            var table = $"FakeTable_{DateTime.UtcNow.Ticks:x16}";
            try
            {
                using var fake = new SqlCommand($"CREATE TABLE [dbo].[{table}]([fakeColumn] [int] NULL); SELECT * FROM [dbo].[{table}]; DROP TABLE [dbo].[{table}];", conn);
                await fake.ExecuteNonQueryAsync(ct);
                checks.Add(Passed("Permissions", "db_owner - can create and drop tables."));
            }
            catch (SqlException ex)
            {
                checks.Add(Failed("Permissions", $"{login} can't create a table in [{c.Database}]: {FirstLine(ex.Message)}"));
                return;
            }
        }

        // With Windows authentication the site signs in as its app pool's identity, which needs a login of its own.
        if (options.SiteLogin is { } siteLogin && c.UsesWindowsAuthentication && !azure)
        {
            if (!IsThisMachine(c.Server))
            {
                checks.Add(new DatabaseCheck("Site login", CheckOutcome.Warning,
                    $"On another server the site signs in as this computer's account ({Environment.UserDomainName}\\{Environment.MachineName}$) - give it db_owner on [{c.Database}] there."));
                return;
            }
            var loginExists = await ScalarAsync<int>(conn, "SELECT CASE WHEN SUSER_ID(@login) IS NULL THEN 0 ELSE 1 END", ct, ("@login", siteLogin)) == 1;
            var mayCreate = await ScalarAsync<int>(conn, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN')", ct) == 1;
            checks.Add(loginExists || mayCreate
                ? Passed("Site login", loginExists
                    ? $"{siteLogin} exists - it will be made db_owner of [{c.Database}]."
                    : $"{siteLogin} will be created and made db_owner of [{c.Database}].")
                : Failed("Site login", $"{login} may not create the login {siteLogin} the site signs in as - use a SQL Server administrator, or SQL Server authentication."));
        }
    }

    private static async Task CheckLocalDbAsync(DatabaseConnection c, DatabaseCheckOptions options, List<DatabaseCheck> checks, CancellationToken ct)
    {
        var installed = InstalledLocalDbMajorVersion();
        if (installed is null)
        {
            checks.Add(Failed("LocalDB", "SQL Server Express LocalDB isn't installed - install it (SQL Server Express setup → LocalDB, or Visual Studio), or use SQL Server."));
            return;
        }
        if (installed < options.MinimumMajorVersion)
        {
            checks.Add(Failed("LocalDB", $"LocalDB {ProductName(installed.Value)} is too old for this DNN version - it needs {ProductName(options.MinimumMajorVersion)} or later."));
            return;
        }
        checks.Add(Passed("LocalDB", $"{ProductName(installed.Value)} Express LocalDB is installed."));

        // Proves LocalDB starts on this PC; the site gets an instance of its own under its app pool identity.
        await using var conn = new SqlConnection(ConnectionStrings.ForApp(c with { Kind = DatabaseKind.SqlServer }, "master", LocalDbConnectTimeoutSeconds));
        try
        {
            await conn.OpenAsync(ct);
            checks.Add(Passed("Signed in", $"as {Environment.UserDomainName}\\{Environment.UserName} to {c.Server}."));
        }
        catch (SqlException ex)
        {
            checks.Add(Failed("Signed in", $"LocalDB didn't start: {FirstLine(ex.Message)}"));
            return;
        }
        checks.Add(new DatabaseCheck("Site database", CheckOutcome.Warning,
            $"App_Data\\{c.Database} runs in the LocalDB instance of the site's app pool identity - fine for trying things out; " +
            "DNN Manager can't open it to show portals or change the host password. Use SQL Server for anything you keep."));
    }

    /// <summary>The highest LocalDB major version installed (13 = 2016, 14 = 2017, 15 = 2019, 16 = 2022, 17 = 2025), or null.</summary>
    private static int? InstalledLocalDbMajorVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");
        if (key is null) return null;
        var majors = key.GetSubKeyNames()
            .Select(name => int.TryParse(name.Split('.')[0], out var major) ? major : 0)
            .Where(major => major > 0)
            .ToList();
        return majors.Count == 0 ? null : majors.Max();
    }

    // ─── Creating, granting, dropping ─────────────────────────────────────

    public async Task<Result<bool>> DatabaseExistsAsync(DatabaseConnection connection, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(connection, "master", ct);
            return Result<bool>.Ok(await ScalarAsync<int>(conn, "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END", ct, ("@db", connection.Database)) == 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<bool>.Fail(FirstLine(ex.Message));
        }
    }

    public async Task<Result> CreateDatabaseAsync(DatabaseConnection connection, string? collation, CancellationToken ct)
    {
        if (collation is not null && !CollationPattern().IsMatch(collation))
            return Result.Fail($"'{collation}' isn't a collation name.");
        try
        {
            await using var conn = await OpenAsync(connection, "master", ct);
            using var cmd = new SqlCommand(
                "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@db) + COALESCE(N' COLLATE ' + @collation, N''); EXEC (@sql);", conn);
            cmd.Parameters.AddWithValue("@db", connection.Database);
            cmd.Parameters.AddWithValue("@collation", (object?)collation ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not create [{connection.Database}] on {connection.Server}: {FirstLine(ex.Message)}");
        }
    }

    public async Task<Result> GrantSiteAccessAsync(DatabaseConnection connection, string windowsLogin, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(connection, "master", ct);
            using (var cmd = new SqlCommand(
                       "IF SUSER_ID(@login) IS NULL BEGIN DECLARE @sql nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@login) + N' FROM WINDOWS'; EXEC (@sql); END", conn))
            {
                cmd.Parameters.AddWithValue("@login", windowsLogin);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await conn.ChangeDatabaseAsync(connection.Database, ct);
            // The login may be in the database already - as its owner (dbo, e.g. when it created it) or under another
            // name; a sysadmin owns every database anyway.
            using (var cmd = new SqlCommand(
                       "DECLARE @user sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(@login)); " +
                       "IF @user = N'dbo' OR IS_SRVROLEMEMBER('sysadmin', @login) = 1 RETURN; " +
                       "IF @user IS NULL BEGIN DECLARE @create nvarchar(max) = N'CREATE USER ' + QUOTENAME(@login) + N' FOR LOGIN ' + QUOTENAME(@login); EXEC (@create); SET @user = @login; END; " +
                       "IF IS_ROLEMEMBER('db_owner', @user) = 0 BEGIN DECLARE @role nvarchar(max) = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@user); EXEC (@role); END;", conn))
            {
                cmd.Parameters.AddWithValue("@login", windowsLogin);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not give {windowsLogin} access to [{connection.Database}]: {FirstLine(ex.Message)}");
        }
    }

    public async Task<Result> DropDatabaseAsync(DatabaseConnection connection, CancellationToken ct)
    {
        try
        {
            // Pooled connections of this process would keep the database in use.
            SqlConnection.ClearAllPools();
            await using var conn = await OpenAsync(connection, "master", ct);
            using var cmd = new SqlCommand(
                "IF DB_ID(@db) IS NOT NULL BEGIN " +
                "DECLARE @single nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE'; EXEC (@single); " +
                "DECLARE @drop nvarchar(max) = N'DROP DATABASE ' + QUOTENAME(@db); EXEC (@drop); END", conn);
            cmd.Parameters.AddWithValue("@db", connection.Database);
            await cmd.ExecuteNonQueryAsync(ct);
            return Result.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail($"Could not drop [{connection.Database}] on {connection.Server}: {FirstLine(ex.Message)}");
        }
    }

    public async Task<Result<IReadOnlyList<DnnHostAccount>>> ListHostAccountsAsync(DatabaseConnection connection, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(connection, connection.Database, ct);
            var q = await DnnTables.QualifierAsync(conn, "Users", ct);
            if (q is null) return Result<IReadOnlyList<DnnHostAccount>>.Ok(Array.Empty<DnnHostAccount>());
            var deleted = await ScalarAsync<int?>(conn, $"SELECT COL_LENGTH('dbo.[{q}Users]', 'IsDeleted')", ct) is not null;
            using var cmd = new SqlCommand(
                $"SELECT UserID, Username, ISNULL(Email, '') FROM dbo.[{q}Users] WHERE IsSuperUser = 1{(deleted ? " AND IsDeleted = 0" : "")} ORDER BY UserID", conn);
            using var rdr = await cmd.ExecuteReaderAsync(ct);
            var hosts = new List<DnnHostAccount>();
            while (await rdr.ReadAsync(ct)) hosts.Add(new DnnHostAccount(rdr.GetInt32(0), rdr.GetString(1), rdr.GetString(2)));
            return Result<IReadOnlyList<DnnHostAccount>>.Ok(hosts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<IReadOnlyList<DnnHostAccount>>.Fail(FirstLine(ex.Message));
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static async Task<SqlConnection> OpenAsync(DatabaseConnection connection, string database, CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionStrings.ForApp(connection, database,
            connection.Kind == DatabaseKind.LocalDbFile ? LocalDbConnectTimeoutSeconds : ConnectTimeoutSeconds));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>The DNN version recorded in the database, or null when it holds no DNN (DNN's own "is it empty" test).</summary>
    private static async Task<string?> DnnVersionAsync(SqlConnection conn, CancellationToken ct)
    {
        var q = await DnnTables.QualifierAsync(conn, "Version", ct);
        if (q is null) return null;
        return await ScalarAsync<string>(conn,
            $"SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Build) FROM dbo.[{q}Version] ORDER BY Major DESC, Minor DESC, Build DESC", ct);
    }

    private static async Task<T?> ScalarAsync<T>(SqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var cmd = new SqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    // Login failed, untrusted domain, account disabled, password expired / must change.
    private static bool IsLoginFailure(SqlException ex) => ex.Number is 18456 or 18452 or 18470 or 18487 or 18488;

    private static bool IsThisMachine(string server)
    {
        var host = server.Split(',', '\\')[0].Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host is "." or "(local)" || host.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1" ||
               host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProductName(int major) => major switch
    {
        >= 17 => "SQL Server 2025",
        16 => "SQL Server 2022",
        15 => "SQL Server 2019",
        14 => "SQL Server 2017",
        13 => "SQL Server 2016",
        12 => "SQL Server 2014",
        11 => "SQL Server 2012",
        _ => $"SQL Server {major}"
    };

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    private static DatabaseCheck Passed(string name, string detail) => new(name, CheckOutcome.Passed, detail);

    private static DatabaseCheck Failed(string name, string detail) => new(name, CheckOutcome.Failed, detail);

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex CollationPattern();
}
