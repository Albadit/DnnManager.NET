using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Sql;

public sealed class RemoteSqlBackupService : IRemoteSqlBackupService
{
    private readonly ILogger<RemoteSqlBackupService> _log;

    public RemoteSqlBackupService(ILogger<RemoteSqlBackupService> log) => _log = log;

    public async Task<Result<string>> BackupAsync(SiteSqlConnection source, string backupServerPath,
        IProgressReporter reporter, CancellationToken ct)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = source.Server,
                // Connect straight to the target database: Azure SQL logins are often
                // contained users that don't exist in [master], and a SQL-auth user may
                // only have rights to its own database.
                InitialCatalog = string.IsNullOrWhiteSpace(source.Database) ? "master" : source.Database,
                UserID = source.User,
                Password = source.Password,
                Encrypt = true,                  // Azure SQL requires TLS.
                TrustServerCertificate = true,
                ConnectTimeout = 30,
                CommandTimeout = 0
            };

            reporter.Info($"Connecting to source SQL Server {source.Server} as {source.User}\u2026");
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

            reporter.Info($"Backing up [{source.Database}] \u2192 {backupServerPath}");
            var sql = $"BACKUP DATABASE [{source.Database.Replace("]", "]]")}] TO DISK = N'{backupServerPath.Replace("'", "''")}' " +
                      "WITH INIT, FORMAT, COMPRESSION, STATS = 10;";
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 })
            {
                conn.InfoMessage += (_, e) =>
                {
                    foreach (SqlError err in e.Errors)
                    {
                        if (err.Class <= 10) reporter.Info(err.Message.Trim());
                    }
                };
                await cmd.ExecuteNonQueryAsync(ct);
            }
            reporter.Success("Source backup written.");

            // Best-effort: confirm the file is reachable from this host.
            if (!File.Exists(backupServerPath))
                return Result<string>.Fail(
                    $"Backup written on the SQL Server side but not visible at {backupServerPath} from this machine. " +
                    "If the source is remote, supply a UNC share path that both the SQL service and this host can access.");

            return Result<string>.Ok(backupServerPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Remote backup failed");
            return Result<string>.Fail(ex.Message);
        }
    }
}
