using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Docker;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Talks to SQL Server by running <c>sqlcmd</c> inside the shared container.
/// </summary>
public sealed class SqlServerService(ProcessRunner proc, IOptions<AppOptions> opts) : ISqlServerService
{
    private const string BackupDir = "/var/opt/mssql/backup";

    // How long sqlcmd waits to sign in: a container that runs but doesn't answer (SQL Server still starting, out of
    // memory) mustn't hold an operation - or the undo after it - for good.
    private const string LoginTimeoutSeconds = "15";

    /// <summary>How long a cancelled BACKUP or RESTORE may take to stop inside SQL Server (a RESTORE rolls back first).</summary>
    internal static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(60);

    // A file in the container removed after a cancel or a failure: bounded, never cancelled itself.
    private static readonly TimeSpan CleanupWithin = TimeSpan.FromSeconds(30);

    private readonly ProcessRunner _proc = proc;
    private readonly AppOptions _opts = opts.Value;

    // Checked where it goes on docker's command line: a name starting with '-' would be read as an option.
    private string Container => DockerNames.IsValid(_opts.Docker.ContainerName)
        ? _opts.Docker.ContainerName
        : throw new InvalidOperationException(DockerNames.Refusal(_opts.Docker.ContainerName, "container"));
    private string SaPassword => _opts.Docker.SaPassword;

    /// <param name="tag">
    /// The session's workstation name (sqlcmd <c>-H</c>, <c>host_name</c> in <c>sys.dm_exec_sessions</c>) - how a statement
    /// is found again to be stopped (<see cref="StopSessionAsync"/>).
    /// </param>
    /// <param name="viaStdin">
    /// The query goes to sqlcmd on its standard input instead of the command line - for one with a password in it: docker
    /// keeps an exec's arguments, the container's process list shows them, and so does Windows' for docker.exe.
    /// </param>
    private async Task<ProcessResult> SqlcmdAsync(string? database, string query, CancellationToken ct,
        bool plain = false, string? tag = null, bool viaStdin = false, TimeSpan? timeout = null)
    {
        // The password through sqlcmd's SQLCMDPASSWORD, which docker exec -e takes from docker's own environment - not
        // on a command line, where Docker keeps the exec's arguments for anyone allowed to use Docker to read.
        var args = new List<string> { "exec" };
        if (viaStdin) args.Add("-i");
        args.AddRange(
        [
            "-e", "SQLCMDPASSWORD", Container,
            "/opt/mssql-tools18/bin/sqlcmd",
            "-S", "localhost",
            "-U", _opts.Docker.SqlUser,
            "-l", LoginTimeoutSeconds,
            // -x: no $(variables) in the query, -X: no :!! commands - names from a site's web.config or a backup are text.
            "-C", "-No", "-b", "-x", "-X"
        ]);
        if (tag is not null) { args.Add("-H"); args.Add(tag); }
        // Only the values: no header, no padding.
        if (plain) args.AddRange(["-h", "-1", "-W"]);
        if (!string.IsNullOrEmpty(database)) { args.Add("-d"); args.Add(database); }
        if (viaStdin) { args.Add("-i"); args.Add("/dev/stdin"); }
        else { args.Add("-Q"); args.Add(query); }
        return await _proc.RunAsync("docker", args, ct, env: new Dictionary<string, string?> { ["SQLCMDPASSWORD"] = SaPassword },
            stdin: viaStdin ? query : null, timeout: timeout);
    }

    /// <summary>
    /// A statement that can run for long (BACKUP, RESTORE) - and that a cancel stops in SQL Server too. Ending docker on
    /// this side leaves sqlcmd and its statement running in the container: a RESTORE would go on writing the database
    /// the undo is about to drop. So a cancel ends the session (<see cref="StopSessionAsync"/>) before it is passed on.
    /// </summary>
    private async Task<ProcessResult> StoppableSqlcmdAsync(string query, CancellationToken ct)
    {
        var tag = SessionTag();
        try
        {
            return await SqlcmdAsync(null, query, ct, tag: tag);
        }
        catch (OperationCanceledException)
        {
            await StopSessionAsync(tag);
            throw;
        }
    }

    /// <summary>A workstation name nothing else uses - DNN Manager's, and this statement's only.</summary>
    internal static string SessionTag() => "dnnmanager-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Ends the session tagged <paramref name="tag"/> and waits until it is gone - a RESTORE or BACKUP that is killed rolls
    /// back first, and holds its database until then. The sqlcmd in the container is ended too, so one that hadn't
    /// signed in yet can't start its statement afterwards. Done once the session has been gone for two looks in a row;
    /// false when it was still there after <see cref="StopWithin"/>. Never cancelled itself: it is what a cancel does.
    /// </summary>
    internal async Task<bool> StopSessionAsync(string tag)
    {
        using var limit = new CancellationTokenSource(StopWithin);
        var gone = 0;
        try
        {
            while (true)
            {
                // Best effort - an image without pkill still has the session killed below.
                await _proc.RunAsync("docker", ["exec", Container, "pkill", "-f", tag], limit.Token, timeout: TimeSpan.FromSeconds(10));
                var r = await SqlcmdAsync(null, KillSessionSql(tag), limit.Token, plain: true, timeout: TimeSpan.FromSeconds(20));
                if (r.Success && SessionsLeft(r.StdOut) == 0)
                {
                    if (++gone >= 2) return true;
                }
                else
                {
                    gone = 0;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), limit.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>KILLs the session tagged <paramref name="tag"/>, if there is one, and says how many such are left (<c>LEFT=n</c>).</summary>
    internal static string KillSessionSql(string tag) => $@"
SET NOCOUNT ON;
DECLARE @s int = (SELECT MIN(session_id) FROM sys.dm_exec_sessions WHERE host_name = N'{Literal(tag)}' AND session_id <> @@SPID);
IF @s IS NOT NULL
BEGIN
  DECLARE @kill nvarchar(40) = N'KILL ' + CAST(@s AS nvarchar(10));
  -- One already being killed (rolling back) only says how far it is.
  BEGIN TRY EXEC (@kill); END TRY BEGIN CATCH END CATCH
END
SELECT N'LEFT=' + CAST(COUNT(*) AS nvarchar(10)) FROM sys.dm_exec_sessions WHERE host_name = N'{Literal(tag)}' AND session_id <> @@SPID;";

    /// <summary>The <c>LEFT=n</c> of <see cref="KillSessionSql"/>'s output; -1 when it isn't there.</summary>
    internal static int SessionsLeft(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("LEFT=", StringComparison.Ordinal));
        return line is not null && int.TryParse(line[5..], out var n) ? n : -1;
    }

    /// <summary>Removes <paramref name="containerPath"/> in the container - after a cancel or a failure too, so never cancelled; bounded.</summary>
    private async Task RemoveInContainerAsync(string containerPath)
    {
        using var limit = new CancellationTokenSource(CleanupWithin);
        try
        {
            await _proc.RunAsync("docker", ["exec", Container, "rm", "-f", containerPath], limit.Token, timeout: CleanupWithin);
        }
        catch (OperationCanceledException) { /* a container that doesn't answer keeps it - in its own backup folder */ }
    }

    /// <summary>
    /// The objectQualifier of DNN's tables in <paramref name="database"/> ("" for none, "dnn_") - found from its table
    /// <paramref name="table"/>, as <see cref="DnnTables.QualifierAsync"/> does; null when it has no such table.
    /// </summary>
    private async Task<Result<string?>> QualifierAsync(string database, string table, CancellationToken ct)
    {
        var sql = $@"SET NOCOUNT ON;
SELECT TOP 1 N'Q=' + LEFT(name, LEN(name) - LEN(N'{table}')) FROM sys.tables WHERE SCHEMA_NAME(schema_id) = 'dbo'
AND (name = N'{table}' OR name LIKE N'%[_]{table}') ORDER BY LEN(name);";
        var r = await SqlcmdAsync(database, sql, ct, plain: true);
        if (!r.Success) return Result<string?>.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
        var line = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Q=", StringComparison.Ordinal));
        if (line is null) return Result<string?>.Ok(null);
        var qualifier = line[2..];
        return DnnTables.IsQualifier(qualifier)
            ? Result<string?>.Ok(qualifier)
            : Result<string?>.Fail($"DNN's tables in [{database}] have a prefix that isn't DNN's ('{qualifier}').");
    }

    // Database names are not always ours: the local ones come from the project name, but the name a site actually uses
    // is read out of its web.config. Quoted properly (SqlText) instead of interpolated as raw text into T-SQL.
    private static string Quoted(string identifier) => SqlText.Identifier(identifier);
    private static string Literal(string value) => SqlText.Literal(value);
    private static string EscapeLike(string value) => SqlText.EscapeLike(value);
    private static string FileNamePart(string value) =>
        new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    public async Task<Result<bool>> DatabaseExistsAsync(string database, CancellationToken ct)
    {
        var q = $"SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(database)}') PRINT 'EXISTS'";
        var r = await SqlcmdAsync(null, q, ct);
        if (!r.Success) return Result<bool>.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
        return Result<bool>.Ok(r.StdOut.Contains("EXISTS", StringComparison.Ordinal));
    }

    public async Task<Result> CreateDatabaseAsync(DatabaseConfig db, CancellationToken ct)
    {
        // Create the database by name only. The DNN site (and this tool) connect as the container's
        // sa, so there is no per-project SQL login/user to provision. The collation can't be quoted -
        // only a plain name is let through (the settings check it too).
        if (!db.Collation.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return Result.Fail($"'{db.Collation}' isn't a collation name.");
        var sql = $@"
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}')
BEGIN CREATE DATABASE {Quoted(db.DatabaseName)} COLLATE {db.Collation}; END";
        var r = await SqlcmdAsync(null, sql, ct);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr);
    }

    /// <summary>How long a drop waits before it is tried again while the database is still in use.</summary>
    private static readonly TimeSpan[] DropRetries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    public async Task<Result> DropDatabaseAsync(string database, CancellationToken ct)
    {
        // A drop that fails puts the database back to everyone - left single-user, the site's next connection takes it.
        // Only an online database can be set single-user: one a cancelled RESTORE left RESTORING is dropped as it is.
        var sql = $@"
IF EXISTS (SELECT name FROM sys.databases WHERE name = N'{Literal(database)}')
BEGIN
  IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(database)}' AND state_desc = N'ONLINE')
    ALTER DATABASE {Quoted(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  BEGIN TRY
    DROP DATABASE {Quoted(database)};
  END TRY
  BEGIN CATCH
    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(database)}' AND state_desc = N'ONLINE')
      ALTER DATABASE {Quoted(database)} SET MULTI_USER;
    THROW;
  END CATCH
END";
        for (var attempt = 0; ; attempt++)
        {
            var r = await SqlcmdAsync(null, sql, ct);
            if (r.Success) return Result.Ok();
            // Still in use - a BACKUP or RESTORE that is just ending holds it: tried again, a little later each time.
            if (attempt >= DropRetries.Length || !IsInUse(r.StdErr + r.StdOut))
                return Result.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
            await Task.Delay(DropRetries[attempt], ct);
        }
    }

    /// <summary>
    /// Whether sqlcmd's error says the database is in use: 3702 (can't drop, in use), 3101 (exclusive access couldn't be
    /// had), 5061 (ALTER DATABASE couldn't lock it), 5052 (not while it is being restored).
    /// </summary>
    internal static bool IsInUse(string error) =>
        new[] { "Msg 3702,", "Msg 3101,", "Msg 5061,", "Msg 5052," }.Any(m => error.Contains(m, StringComparison.Ordinal)) ||
        error.Contains("currently in use", StringComparison.OrdinalIgnoreCase);

    public async Task<Result<string>> BackupDatabaseLocalAsync(string database, string backupFileName, CancellationToken ct)
    {
        var containerPath = $"{BackupDir}/{backupFileName}";
        // Not %TEMP%: the caller restores it later, and nothing unelevated may swap it meanwhile.
        var hostTmp = Path.Combine(PrivateTemp.Path, backupFileName);
        var handedOn = false;
        try
        {
            await _proc.RunAsync("docker", new[] { "exec", Container, "mkdir", "-p", BackupDir }, ct);

            var sql = $"BACKUP DATABASE {Quoted(database)} TO DISK = N'{Literal(containerPath)}' WITH INIT, FORMAT, COMPRESSION, STATS = 10;";
            var r = await StoppableSqlcmdAsync(sql, ct);
            if (!r.Success) return Result<string>.Fail(r.StdErr);

            // Copied out to a host temp file; the caller moves it into the project's backup folder.
            var cp = await _proc.RunAsync("docker", new[] { "cp", $"{Container}:{containerPath}", hostTmp }, ct);
            if (!cp.Success) return Result<string>.Fail(cp.StdErr);
            handedOn = true;
            return Result<string>.Ok(hostTmp);
        }
        finally
        {
            // A copy of the whole database: never kept in the container, and here only when it is handed on.
            await RemoveInContainerAsync(containerPath);
            if (!handedOn) TryDelete(hostTmp);
        }
    }

    public async Task<Result> RestoreDatabaseLocalAsync(DatabaseConfig db, string backupFilePath, CancellationToken ct)
    {
        var bakName = Path.GetFileName(backupFilePath);
        var containerPath = $"{BackupDir}/{bakName}";
        try
        {
            await _proc.RunAsync("docker", new[] { "exec", Container, "mkdir", "-p", BackupDir }, ct);
            var cp = await _proc.RunAsync("docker", new[] { "cp", backupFilePath, $"{Container}:{containerPath}" }, ct);
            if (!cp.Success) return Result.Fail(cp.StdErr);

            var listSql = $@"
SET NOCOUNT ON;
DECLARE @t TABLE (LogicalName nvarchar(128), PhysicalName nvarchar(260), Type char(1),
 FileGroupName nvarchar(128), Size numeric(20,0), MaxSize numeric(20,0), FileID bigint,
 CreateLSN numeric(25,0), DropLSN numeric(25,0), UniqueId uniqueidentifier,
 ReadOnlyLSN numeric(25,0), ReadWriteLSN numeric(25,0), BackupSizeInBytes bigint,
 SourceBlockSize int, FileGroupID int, LogGroupGUID uniqueidentifier,
 DifferentialBaseLSN numeric(25,0), DifferentialBaseGUID uniqueidentifier,
 IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32), SnapshotUrl nvarchar(360));
INSERT INTO @t EXEC('RESTORE FILELISTONLY FROM DISK = N''{Literal(Literal(containerPath))}''');
SELECT LogicalName + '|' + Type FROM @t;";
            var listR = await SqlcmdAsync(null, listSql, ct, plain: true);
            if (!listR.Success) return Result.Fail(listR.StdErr);

            var moves = new List<string>();
            foreach (var line in listR.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('|')))
            {
                var parts = line.Split('|');
                var logical = parts[0].Trim();
                var type = parts[1].Trim();
                var ext = type == "L" ? "_log.ldf" : ".mdf";
                // The logical names come from the .bak - someone else's file: quoted as text, and only safe characters in
                // the file name made from them.
                var file = FileNamePart($"{db.DatabaseName}_{logical}") + ext;
                moves.Add($"MOVE N'{Literal(logical)}' TO N'/var/opt/mssql/data/{file}'");
            }
            if (moves.Count == 0) return Result.Fail("Could not read backup file list.");

            // No per-project login to remap: the site connects as the container's sa (a sysadmin),
            // which can access the restored database regardless of the user mappings it carries.
            // Back to everyone whether the restore worked or not - a failed one mustn't leave the database single-user.
            // Only an online database can be set single-user (one a cancelled restore left RESTORING is replaced as it is).
            var restoreSql = $@"
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}' AND state_desc = N'ONLINE')
  ALTER DATABASE {Quoted(db.DatabaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
BEGIN TRY
  RESTORE DATABASE {Quoted(db.DatabaseName)} FROM DISK = N'{Literal(containerPath)}' WITH REPLACE, {string.Join(", ", moves)}, STATS = 10;
END TRY
BEGIN CATCH
  IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}' AND state_desc = N'ONLINE')
    ALTER DATABASE {Quoted(db.DatabaseName)} SET MULTI_USER;
  THROW;
END CATCH
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}')
  ALTER DATABASE {Quoted(db.DatabaseName)} SET MULTI_USER;";
            var rr = await StoppableSqlcmdAsync(restoreSql, ct);
            return rr.Success ? Result.Ok() : Result.Fail(rr.StdErr);
        }
        finally
        {
            // The backup's copy in the container - removed whatever happened, a cancel included.
            await RemoveInContainerAsync(containerPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left in DNN Manager's own temp folder */ }
    }

    public async Task<Result> RemapPortalAliasesAsync(string database, string hostnameSuffix, string newAlias, CancellationToken ct)
    {
        var qualifier = await QualifierAsync(database, "PortalAlias", ct);
        if (!qualifier.Success) return qualifier.WithoutValue();
        if (qualifier.Value is not { } q) return Result.Fail($"[{database}] has no DNN PortalAlias table.");
        var pa = DnnTables.Name(q, "PortalAlias");
        var paText = Literal(pa);

        // The database is an identifier; the suffix and alias are string literals. An alias of this suffix is the host
        // name alone, or with a port (shop.dnndev.me:8080).
        var db = Quoted(database);
        // In LIKE: the suffix's own % and _ are characters, not wildcards (dnn_dev.me mustn't match dnnxdev.me).
        var sfx = Literal(EscapeLike(hostnameSuffix));
        var hn = Literal(newAlias);

        var sql = $@"
USE {db};
SET NOCOUNT ON;

-- If the new alias is already present, just make sure portal 0 has only it.
IF NOT EXISTS (SELECT 1 FROM {pa} WHERE PortalID = 0 AND HTTPAlias = N'{hn}')
BEGIN
    -- Try to rewrite the first existing alias with the hostname suffix for portal 0 into the new one.
    DECLARE @existingId int = (
        SELECT TOP 1 PortalAliasID
        FROM {pa}
        WHERE PortalID = 0 AND (HTTPAlias LIKE N'%.{sfx}' OR HTTPAlias LIKE N'%.{sfx}:%')
        ORDER BY PortalAliasID
    );
    IF @existingId IS NOT NULL
        UPDATE {pa} SET HTTPAlias = N'{hn}' WHERE PortalAliasID = @existingId;
    ELSE
    BEGIN
        -- No matching alias to rewrite - insert one. Use a column list that's compatible
        -- with DNN 9.x schemas; rely on column defaults for anything else.
        IF COL_LENGTH(N'{paText}','BrowserType') IS NOT NULL AND COL_LENGTH(N'{paText}','IsPrimary') IS NOT NULL
            INSERT INTO {pa} (PortalID, HTTPAlias, CultureCode, Skin, BrowserType, IsPrimary, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate)
            VALUES (0, N'{hn}', NULL, NULL, 0, 1, -1, SYSUTCDATETIME(), -1, SYSUTCDATETIME());
        ELSE
            INSERT INTO {pa} (PortalID, HTTPAlias, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate)
            VALUES (0, N'{hn}', -1, SYSUTCDATETIME(), -1, SYSUTCDATETIME());
    END
END

-- Remove any leftover aliases with the hostname suffix for portal 0 that aren't the new one.
DELETE FROM {pa}
WHERE PortalID = 0 AND (HTTPAlias LIKE N'%.{sfx}' OR HTTPAlias LIKE N'%.{sfx}:%') AND HTTPAlias <> N'{hn}';

-- Make sure the new alias is marked primary if the column exists.
IF COL_LENGTH(N'{paText}','IsPrimary') IS NOT NULL
    UPDATE {pa} SET IsPrimary = CASE WHEN HTTPAlias = N'{hn}' THEN 1 ELSE 0 END
    WHERE PortalID = 0;
";
        var r = await SqlcmdAsync(null, sql, ct);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr);
    }

    public async Task<Result> GrantSiteLoginAsync(string database, string login, string password, CancellationToken ct)
    {
        var sql = $@"
SET NOCOUNT ON;
IF SUSER_ID(N'{Literal(login)}') IS NULL
  CREATE LOGIN {Quoted(login)} WITH PASSWORD = N'{Literal(password)}', CHECK_POLICY = OFF, DEFAULT_DATABASE = {Quoted(database)};
ELSE
  ALTER LOGIN {Quoted(login)} WITH PASSWORD = N'{Literal(password)}', DEFAULT_DATABASE = {Quoted(database)};
USE {Quoted(database)};
IF USER_ID(N'{Literal(login)}') IS NULL
  CREATE USER {Quoted(login)} FOR LOGIN {Quoted(login)};
ELSE
  ALTER USER {Quoted(login)} WITH LOGIN = {Quoted(login)};
ALTER ROLE db_owner ADD MEMBER {Quoted(login)};";
        // The site's password is in it: on sqlcmd's standard input, never on a command line.
        var r = await SqlcmdAsync(null, sql, ct, viaStdin: true);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
    }

    public async Task<Result> DropLoginAsync(string login, CancellationToken ct)
    {
        var sql = $"IF SUSER_ID(N'{Literal(login)}') IS NOT NULL DROP LOGIN {Quoted(login)};";
        var r = await SqlcmdAsync(null, sql, ct);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
    }

    public async Task<Result<int>> DisableSslAsync(string database, CancellationToken ct)
    {
        // DNN 10 keeps one SSLSetup portal setting (0 off, 1 on, 2 advanced); DNN 9 has SSLEnabled / SSLEnforced. Pages
        // can also be marked secure one by one. Each only if the schema has it - with DNN's objectQualifier, if any.
        var qualifier = await QualifierAsync(database, "PortalSettings", ct);
        if (!qualifier.Success) return Result<int>.Fail(qualifier.Error!);
        if (qualifier.Value is not { } q) return Result<int>.Fail($"[{database}] has no DNN PortalSettings table.");
        var settings = DnnTables.Name(q, "PortalSettings");
        var tabs = DnnTables.Name(q, "Tabs");
        var sql = $@"
USE {Quoted(database)};
SET NOCOUNT ON;
DECLARE @changed int = 0;
IF OBJECT_ID(N'{Literal(settings)}') IS NOT NULL
BEGIN
    UPDATE {settings} SET SettingValue = N'0'
    WHERE SettingName = N'SSLSetup' AND ISNULL(SettingValue, N'') <> N'0';
    SET @changed += @@ROWCOUNT;
    UPDATE {settings} SET SettingValue = N'False'
    WHERE SettingName IN (N'SSLEnabled', N'SSLEnforced') AND ISNULL(SettingValue, N'') NOT IN (N'False', N'false');
    SET @changed += @@ROWCOUNT;
END
IF COL_LENGTH(N'{Literal(tabs)}', N'IsSecure') IS NOT NULL
BEGIN
    UPDATE {tabs} SET IsSecure = 0 WHERE IsSecure = 1;
    SET @changed += @@ROWCOUNT;
END
PRINT 'CHANGED=' + CAST(@changed AS nvarchar(10));";
        var r = await SqlcmdAsync(null, sql, ct);
        if (!r.Success) return Result<int>.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
        var marker = r.StdOut.LastIndexOf("CHANGED=", StringComparison.Ordinal);
        var count = marker >= 0 && int.TryParse(r.StdOut[(marker + 8)..].Trim(), out var n) ? n : 0;
        return Result<int>.Ok(count);
    }
}
