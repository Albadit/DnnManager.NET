using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Domain;
using DnnManager.Infrastructure.Processes;
using Microsoft.Extensions.Options;

namespace DnnManager.Infrastructure.Sql;

/// <summary>
/// Talks to SQL Server by running <c>sqlcmd</c> inside the shared container.
/// </summary>
public sealed class SqlServerService : ISqlServerService
{
    private readonly ProcessRunner _proc;
    private readonly AppOptions _opts;

    public SqlServerService(ProcessRunner proc, IOptions<AppOptions> opts)
    {
        _proc = proc; _opts = opts.Value;
    }

    private string Container => _opts.Docker.ContainerName;
    private string SaPassword => _opts.Docker.SaPassword;

    private async Task<ProcessResult> SqlcmdAsync(string? user, string? password, string? database, string query, CancellationToken ct)
    {
        var args = new List<string>
        {
            "exec", Container,
            "/opt/mssql-tools18/bin/sqlcmd",
            "-S", "localhost",
            "-U", user ?? "sa",
            "-P", password ?? SaPassword,
            "-C", "-No", "-b"
        };
        if (!string.IsNullOrEmpty(database)) { args.Add("-d"); args.Add(database); }
        args.Add("-Q"); args.Add(query);
        return await _proc.RunAsync("docker", args, ct);
    }

    // Database names are not always ours: the local ones come from the project name, but the name a
    // site actually uses is read out of its web.config. Quote them properly instead of interpolating
    // raw text into T-SQL.
    private static string Quoted(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    private static string Literal(string value) => value.Replace("'", "''");

    public async Task<Result> WaitReadyAsync(int timeoutSeconds, IProgressReporter reporter, CancellationToken ct)
    {
        reporter.Info($"Waiting for SQL Server (up to {timeoutSeconds}s)…");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = await SqlcmdAsync(null, null, null, "SELECT 1", ct);
            if (r.Success) { reporter.Success("SQL Server is ready."); return Result.Ok(); }
            await Task.Delay(2000, ct);
        }
        return Result.Fail("SQL Server did not become ready in time.");
    }

    public async Task<Result<bool>> DatabaseExistsAsync(string database, CancellationToken ct)
    {
        var q = $"SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(database)}') PRINT 'EXISTS'";
        var r = await SqlcmdAsync(null, null, null, q, ct);
        if (!r.Success) return Result<bool>.Fail(r.StdErr.Length > 0 ? r.StdErr : r.StdOut);
        return Result<bool>.Ok(r.StdOut.Contains("EXISTS", StringComparison.Ordinal));
    }

    public async Task<Result> CreateDatabaseAsync(DatabaseConfig db, CancellationToken ct)
    {
        // Create the database by name only. The DNN site (and this tool) connect as the container's
        // sa, so there is no per-project SQL login/user to provision.
        var sql = $@"
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}')
BEGIN CREATE DATABASE {Quoted(db.DatabaseName)} COLLATE {db.Collation}; END";
        var r = await SqlcmdAsync(null, null, null, sql, ct);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr);
    }

    public async Task<Result> DropDatabaseAsync(string database, CancellationToken ct)
    {
        var sql = $@"
IF EXISTS (SELECT name FROM sys.databases WHERE name = N'{Literal(database)}')
BEGIN
  ALTER DATABASE {Quoted(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  DROP DATABASE {Quoted(database)};
END";
        var r = await SqlcmdAsync(null, null, null, sql, ct);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr);
    }

    public async Task<Result<string>> BackupDatabaseLocalAsync(string database, string backupFileName, CancellationToken ct)
    {
        const string containerDir = "/var/opt/mssql/backup";
        var containerPath = $"{containerDir}/{backupFileName}";
        await _proc.RunAsync("docker", new[] { "exec", Container, "mkdir", "-p", containerDir }, ct);

        var sql = $"BACKUP DATABASE {Quoted(database)} TO DISK = N'{Literal(containerPath)}' WITH INIT, FORMAT, COMPRESSION, STATS = 10;";
        var r = await SqlcmdAsync(null, null, null, sql, ct);
        if (!r.Success) return Result<string>.Fail(r.StdErr);

        // copy out to a temp file we can return; caller can move it to backupDir.
        var hostTmp = Path.Combine(Path.GetTempPath(), backupFileName);
        var cp = await _proc.RunAsync("docker", new[] { "cp", $"{Container}:{containerPath}", hostTmp }, ct);
        if (!cp.Success) return Result<string>.Fail(cp.StdErr);
        await _proc.RunAsync("docker", new[] { "exec", Container, "rm", "-f", containerPath }, ct);
        return Result<string>.Ok(hostTmp);
    }

    public async Task<Result> RestoreDatabaseLocalAsync(DatabaseConfig db, string backupFilePath, CancellationToken ct)
    {
        const string containerDir = "/var/opt/mssql/backup";
        var bakName = Path.GetFileName(backupFilePath);
        var containerPath = $"{containerDir}/{bakName}";

        await _proc.RunAsync("docker", new[] { "exec", Container, "mkdir", "-p", containerDir }, ct);
        var cp = await _proc.RunAsync("docker", new[] { "cp", backupFilePath, $"{Container}:{containerPath}" }, ct);
        if (!cp.Success) return Result.Fail(cp.StdErr);

        // Enumerate logical files
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
        var args = new List<string>
        {
            "exec", Container, "/opt/mssql-tools18/bin/sqlcmd",
            "-S", "localhost", "-U", "sa", "-P", SaPassword, "-C", "-No", "-b",
            "-h", "-1", "-W", "-Q", listSql
        };
        var listR = await _proc.RunAsync("docker", args, ct);
        if (!listR.Success) return Result.Fail(listR.StdErr);

        var moves = new List<string>();
        foreach (var line in listR.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('|')))
        {
            var parts = line.Split('|');
            var logical = parts[0].Trim();
            var type = parts[1].Trim();
            var ext = type == "L" ? "_log.ldf" : ".mdf";
            moves.Add($"MOVE N'{logical}' TO N'/var/opt/mssql/data/{db.DatabaseName}_{logical}{ext}'");
        }
        if (moves.Count == 0) return Result.Fail("Could not read backup file list.");

        // No per-project login to remap: the site connects as the container's sa (a sysadmin),
        // which can access the restored database regardless of the user mappings it carries.
        var restoreSql = $@"
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}')
  ALTER DATABASE {Quoted(db.DatabaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE {Quoted(db.DatabaseName)} FROM DISK = N'{Literal(containerPath)}' WITH REPLACE, {string.Join(", ", moves)}, STATS = 10;
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{Literal(db.DatabaseName)}')
  ALTER DATABASE {Quoted(db.DatabaseName)} SET MULTI_USER;";
        var rr = await SqlcmdAsync(null, null, null, restoreSql, ct);
        await _proc.RunAsync("docker", new[] { "exec", Container, "rm", "-f", containerPath }, ct);
        return rr.Success ? Result.Ok() : Result.Fail(rr.StdErr);
    }

    public async Task<Result> RemapPortalAliasesAsync(string database, string hostnameSuffix, string newHostname, CancellationToken ct)
    {
        // The database is an identifier; the suffix and hostname are string literals.
        var db = Quoted(database);
        var sfx = Literal(hostnameSuffix);
        var hn = Literal(newHostname);

        var sql = $@"
USE {db};
SET NOCOUNT ON;

-- If the new alias is already present, just make sure portal 0 has only it.
IF NOT EXISTS (SELECT 1 FROM dbo.PortalAlias WHERE PortalID = 0 AND HTTPAlias = N'{hn}')
BEGIN
    -- Try to rewrite the first existing *.{sfx} alias for portal 0 into the new hostname.
    DECLARE @existingId int = (
        SELECT TOP 1 PortalAliasID
        FROM dbo.PortalAlias
        WHERE PortalID = 0 AND HTTPAlias LIKE N'%.{sfx}'
        ORDER BY PortalAliasID
    );
    IF @existingId IS NOT NULL
        UPDATE dbo.PortalAlias SET HTTPAlias = N'{hn}' WHERE PortalAliasID = @existingId;
    ELSE
    BEGIN
        -- No matching alias to rewrite - insert one. Use a column list that's compatible
        -- with DNN 9.x schemas; rely on column defaults for anything else.
        IF COL_LENGTH('dbo.PortalAlias','BrowserType') IS NOT NULL AND COL_LENGTH('dbo.PortalAlias','IsPrimary') IS NOT NULL
            INSERT INTO dbo.PortalAlias (PortalID, HTTPAlias, CultureCode, Skin, BrowserType, IsPrimary, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate)
            VALUES (0, N'{hn}', NULL, NULL, 0, 1, -1, SYSUTCDATETIME(), -1, SYSUTCDATETIME());
        ELSE
            INSERT INTO dbo.PortalAlias (PortalID, HTTPAlias, CreatedByUserID, CreatedOnDate, LastModifiedByUserID, LastModifiedOnDate)
            VALUES (0, N'{hn}', -1, SYSUTCDATETIME(), -1, SYSUTCDATETIME());
    END
END

-- Remove any leftover *.{sfx} aliases for portal 0 that aren't the new one.
DELETE FROM dbo.PortalAlias
WHERE PortalID = 0 AND HTTPAlias LIKE N'%.{sfx}' AND HTTPAlias <> N'{hn}';

-- Make sure the new alias is marked primary if the column exists.
IF COL_LENGTH('dbo.PortalAlias','IsPrimary') IS NOT NULL
    UPDATE dbo.PortalAlias SET IsPrimary = CASE WHEN HTTPAlias = N'{hn}' THEN 1 ELSE 0 END
    WHERE PortalID = 0;
";
        var r = await SqlcmdAsync(null, null, null, sql, ct);
        return r.Success ? Result.Ok() : Result.Fail(r.StdErr);
    }
}
