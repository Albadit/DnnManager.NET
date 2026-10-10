using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Sql;

public sealed class RemoteSqlBackupService(ILogger<RemoteSqlBackupService> log) : IRemoteSqlBackupService
{
    private readonly ILogger<RemoteSqlBackupService> _log = log;

    public async Task<Result<string>> BackupAsync(SiteSqlConnection source, string backupServerPath,
        IProgressReporter reporter, CancellationToken ct)
    {
        // From the BACKUP on, what is at backupServerPath is this backup's - before, it may be anybody's file.
        var started = false;
        try
        {
            // Connect straight to the target database: Azure SQL logins are often contained users that don't exist in
            // [master], and a SQL-auth user may only have rights to its own database. A site that signs in with Windows
            // authentication has no login in its web.config: signed in as you.
            var builder = ConnectionStrings.For(source.Server,
                string.IsNullOrWhiteSpace(source.Database) ? "master" : source.Database, source.User, source.Password, 30);
            builder.CommandTimeout = 0;
            var windows = source.User.Length == 0;

            reporter.Info($"Connecting to source SQL Server {source.Server} as {(windows ? "you (Windows authentication)" : source.User)}\u2026");
            using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            reporter.Success($"Connected. SQL Server version: {conn.ServerVersion}");

            // Azure SQL Database (EngineEdition 5) does not support BACKUP DATABASE ... TO DISK.
            var edition = 0;
            using (var edCmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", conn))
                edition = (int)(await edCmd.ExecuteScalarAsync(ct) ?? 0);
            if (edition == 5)
                return Result<string>.Fail(
                    "Source is Azure SQL Database, which does not support BACKUP DATABASE TO DISK. " +
                    "Cloning from Azure SQL needs a BACPAC export (SqlPackage) instead \u2014 not yet supported.");

            using (var existsCmd = new SqlCommand(
                "SELECT COUNT(*) FROM sys.databases WHERE name = @n", conn))
            {
                existsCmd.Parameters.AddWithValue("@n", source.Database);
                var n = (int)(await existsCmd.ExecuteScalarAsync(ct) ?? 0);
                if (n == 0) return Result<string>.Fail($"Database [{source.Database}] not found on {source.Server}.");
            }

            // A server on this PC writes where its service account may: its own backup folder, not your %TEMP%. Only a
            // folder on this PC, by its full path: the server says where it is, and DNN Manager reads the file from
            // there and deletes it afterwards - with its administrator rights, never on a share or a relative path.
            if (SqlServerAddress.IsOnThisMachine(source.Server))
            {
                using var pathCmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(260))", conn);
                if (await pathCmd.ExecuteScalarAsync(ct) is string folder && folder.Length > 0)
                {
                    if (!IsLocalFolder(folder))
                        return Result<string>.Fail($"{source.Server} reports its backup folder as '{folder}' - not a folder on this PC. " +
                                                   "DNN Manager only reads a backup from a local folder given by its full path.");
                    backupServerPath = Path.Combine(folder, Path.GetFileName(backupServerPath));
                }
            }

            // Express and LocalDB (EngineEdition 4) can't compress a backup.
            reporter.Info($"Backing up [{source.Database}] \u2192 {backupServerPath}");
            var sql = $"BACKUP DATABASE {SqlText.Identifier(source.Database)} TO DISK = N'{SqlText.Literal(backupServerPath)}' " +
                      $"WITH INIT, FORMAT,{(edition == 4 ? "" : " COMPRESSION,")} STATS = 10;";
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 })
            {
                conn.InfoMessage += (_, e) =>
                {
                    foreach (SqlError err in e.Errors)
                    {
                        if (err.Class <= 10) reporter.Info(err.Message.Trim());
                    }
                };
                started = true;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            reporter.Success("Source backup written.");

            // Best-effort: confirm the file is reachable from this host.
            if (!File.Exists(backupServerPath))
            {
                // A full copy of the source left on its server is nobody's to keep: removed there if the login may.
                var removed = false;
                try
                {
                    using var delete = new SqlCommand("EXEC master.dbo.xp_delete_file 0, @path", conn);
                    delete.Parameters.AddWithValue("@path", backupServerPath);
                    await delete.ExecuteNonQueryAsync(ct);
                    removed = true;
                }
                catch (SqlException ex)
                {
                    _log.LogInformation(ex, "Could not delete the backup {Path} on {Server}", backupServerPath, source.Server);
                }
                return Result<string>.Fail(
                    $"Backup written on the SQL Server side but not visible at {backupServerPath} from this machine. " +
                    "If the source is remote, supply a UNC share path that both the SQL service and this host can access." +
                    (removed ? "" : $" The backup is still on the server at {backupServerPath} - delete it there."));
            }

            return Result<string>.Ok(backupServerPath);
        }
        catch (OperationCanceledException)
        {
            // Cancelled, not failed: the runner says so and undoes what was done. A backup cut off (or finished just
            // before) is a copy of the source's whole database - not left where it was written.
            _log.LogInformation("Remote backup of [{Database}] on {Server} cancelled", source.Database, source.Server);
            if (started) await RemoveBackupAsync(source, backupServerPath);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Remote backup failed");
            return Result<string>.Fail(ConnectionStrings.Explained(ex.Message));
        }
    }

    /// <summary>
    /// Deletes the backup at <paramref name="path"/> after a cancel - from here when this PC sees it, otherwise on the
    /// server if the login may. Best effort, and bounded: a cancel mustn't wait long for it.
    /// </summary>
    private async Task RemoveBackupAsync(SiteSqlConnection source, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var builder = ConnectionStrings.For(source.Server,
                string.IsNullOrWhiteSpace(source.Database) ? "master" : source.Database, source.User, source.Password, 10);
            using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(limit.Token);
            using var delete = new SqlCommand("EXEC master.dbo.xp_delete_file 0, @path", conn) { CommandTimeout = 10 };
            delete.Parameters.AddWithValue("@path", path);
            await delete.ExecuteNonQueryAsync(limit.Token);
        }
        catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            _log.LogInformation(ex, "Could not delete the cancelled backup {Path} on {Server}", path, source.Server);
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is a folder on this PC given by its full path - <c>D:\SQL\Backup</c>, not a
    /// share (<c>\\server\…</c>), a device path (<c>\\?\…</c>) or a path relative to wherever DNN Manager runs.
    /// </summary>
    internal static bool IsLocalFolder(string folder) =>
        folder.Length >= 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && folder[2] is '\\' or '/' &&
        Path.IsPathFullyQualified(folder) && !folder.Contains("..", StringComparison.Ordinal);
}
