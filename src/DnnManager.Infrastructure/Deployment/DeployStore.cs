using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using DnnManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace DnnManager.Infrastructure.Deployment;

/// <summary>
/// The deployments' records in <c>dnnmanager.db</c> (<see cref="AppDatabase"/>): the connections in
/// <c>deploy_connections</c> - their password, private key and passphrase encrypted with DPAPI for this Windows user, in
/// columns of their own -, the baselines in <c>deploy_baseline</c>, the projects' file hashes in <c>deploy_local_hashes</c>
/// and the history in <c>deploy_operations</c> and <c>deploy_operation_files</c>.
/// </summary>
public sealed class DeployStore(AppDatabase database) : IDeployStore
{
    private const string Prefix = "dpapi:";

    // Ties the encrypted values to DNN Manager's deployments: another program's DPAPI data can't be passed off as them.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DnnManager.deploy.v1");

    private readonly object _write = new();

    // ─── Connections ───────────────────────────────────────────────────────

    private const string ConnectionColumns =
        "id, site, name, environment, protocol, host, port, user_name, auth, remote_root, trusted_host_key, ignore_rules, parallel, " +
        "keep_backups, verify, created_utc, last_checked_utc, last_error, last_sync_utc, ftp_encryption";

    public IReadOnlyList<DeployConnection> Connections(string site)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection,
            $"SELECT {ConnectionColumns} FROM deploy_connections WHERE site = $site ORDER BY name COLLATE NOCASE", ("$site", site));
        return ReadConnections(command);
    }

    public DeployConnection? Connection(Guid id)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection, $"SELECT {ConnectionColumns} FROM deploy_connections WHERE id = $id", ("$id", id.ToString()));
        return ReadConnections(command).FirstOrDefault();
    }

    private static List<DeployConnection> ReadConnections(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<DeployConnection>();
        while (reader.Read())
            list.Add(new DeployConnection
            {
                Id = Guid.Parse(reader.GetString(0)),
                Site = reader.GetString(1),
                Name = reader.GetString(2),
                Environment = Enum.TryParse<DeployEnvironment>(reader.GetString(3), out var env) ? env : DeployEnvironment.Production,
                Protocol = Enum.TryParse<TransferProtocol>(reader.GetString(4), out var protocol) ? protocol : TransferProtocol.Sftp,
                Host = reader.GetString(5),
                Port = reader.GetInt32(6),
                UserName = reader.GetString(7),
                Auth = Enum.TryParse<DeployAuth>(reader.GetString(8), out var auth) ? auth : DeployAuth.Password,
                RemoteRoot = reader.GetString(9),
                TrustedHostKey = reader.IsDBNull(10) ? null : reader.GetString(10),
                IgnoreRules = reader.GetString(11),
                Parallel = reader.GetInt32(12),
                KeepBackups = reader.GetInt64(13) != 0,
                Verify = Enum.TryParse<VerifyMode>(reader.GetString(14), out var verify) ? verify : VerifyMode.Checksum,
                CreatedUtc = Time(reader.GetString(15)),
                LastCheckedUtc = reader.IsDBNull(16) ? null : Time(reader.GetString(16)),
                LastError = reader.IsDBNull(17) ? null : reader.GetString(17),
                LastSyncUtc = reader.IsDBNull(18) ? null : Time(reader.GetString(18)),
                Encryption = !reader.IsDBNull(19) && Enum.TryParse<FtpEncryption>(reader.GetString(19), out var encryption) ? encryption : FtpEncryption.RequireExplicit
            });
        return list;
    }

    public void Save(DeployConnection c)
    {
        lock (_write)
        {
            using var connection = database.Open();
            AppDatabase.Execute(connection,
                """
                INSERT INTO deploy_connections (id, site, name, environment, protocol, host, port, user_name, auth, remote_root, trusted_host_key,
                    ignore_rules, parallel, keep_backups, verify, created_utc, last_checked_utc, last_error, last_sync_utc, ftp_encryption)
                VALUES ($id, $site, $name, $env, $protocol, $host, $port, $user, $auth, $root, $key, $rules, $parallel, $backups, $verify, $created,
                    $checked, $error, $sync, $encryption)
                ON CONFLICT (id) DO UPDATE SET site = $site, name = $name, environment = $env, protocol = $protocol, host = $host, port = $port,
                    user_name = $user, auth = $auth, remote_root = $root, trusted_host_key = $key, ignore_rules = $rules, parallel = $parallel,
                    keep_backups = $backups, verify = $verify, ftp_encryption = $encryption
                """,
                ("$id", c.Id.ToString()), ("$site", c.Site), ("$name", c.Name), ("$env", c.Environment.ToString()), ("$protocol", c.Protocol.ToString()),
                ("$host", c.Host), ("$port", c.Port), ("$user", c.UserName), ("$auth", c.Auth.ToString()), ("$root", c.RemoteRoot),
                ("$key", c.TrustedHostKey), ("$rules", c.IgnoreRules), ("$parallel", c.Parallel), ("$backups", c.KeepBackups ? 1 : 0),
                ("$verify", c.Verify.ToString()), ("$created", Text(c.CreatedUtc)), ("$checked", Text(c.LastCheckedUtc)), ("$error", c.LastError),
                ("$sync", Text(c.LastSyncUtc)), ("$encryption", c.Encryption.ToString()));
        }
    }

    public void Delete(Guid id)
    {
        SessionPasswords.TryRemove(id, out _);
        lock (_write)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            var key = ("$id", (object?)id.ToString());
            AppDatabase.Execute(connection, "DELETE FROM deploy_operation_files WHERE operation_id IN (SELECT id FROM deploy_operations WHERE connection_id = $id)", key);
            AppDatabase.Execute(connection, "DELETE FROM deploy_operations WHERE connection_id = $id", key);
            AppDatabase.Execute(connection, "DELETE FROM deploy_baseline WHERE connection_id = $id", key);
            AppDatabase.Execute(connection, "DELETE FROM deploy_connections WHERE id = $id", key);
            transaction.Commit();
        }
    }

    public void RecordCheck(Guid id, DateTime atUtc, string? error) =>
        Write("UPDATE deploy_connections SET last_checked_utc = $at, last_error = $error WHERE id = $id",
            ("$id", id.ToString()), ("$at", Text(atUtc)), ("$error", error));

    public void RecordSync(Guid id, DateTime atUtc) =>
        Write("UPDATE deploy_connections SET last_sync_utc = $at WHERE id = $id", ("$id", id.ToString()), ("$at", Text(atUtc)));

    // ─── Secrets ───────────────────────────────────────────────────────────

    public DeploySecrets ReadSecrets(Guid id)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection, "SELECT password, private_key, passphrase, auth FROM deploy_connections WHERE id = $id",
            ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return DeploySecrets.None;
        if (reader.GetString(3) == nameof(DeployAuth.AskForPassword)) return new DeploySecrets(SessionPasswords.GetValueOrDefault(id));
        return new DeploySecrets(Unprotect(reader, 0), Unprotect(reader, 1), Unprotect(reader, 2));
    }

    // "Ask for password": what was typed, for as long as DNN Manager runs.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> SessionPasswords = new();

    public void RememberForSession(Guid id, string password) => SessionPasswords[id] = password;

    public bool HasSessionPassword(Guid id) => SessionPasswords.ContainsKey(id);

    public void SaveSecrets(Guid id, DeploySecrets secrets) =>
        Write("UPDATE deploy_connections SET password = $password, private_key = $key, passphrase = $passphrase WHERE id = $id",
            ("$id", id.ToString()), ("$password", Protect(secrets.Password)), ("$key", Protect(secrets.PrivateKey)),
            ("$passphrase", Protect(secrets.Passphrase)));

    private static string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain)
            ? null
            : Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The secret in the column - null when there is none, or it was encrypted by another Windows user or PC.</summary>
    private static string? Unprotect(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column)) return null;
        var value = reader.GetString(column);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    // ─── Baseline ──────────────────────────────────────────────────────────

    public IReadOnlyDictionary<string, BaselineEntry> Baseline(Guid id)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection,
            "SELECT path, hash, size, remote_size, remote_modified_utc FROM deploy_baseline WHERE connection_id = $id", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        var entries = new Dictionary<string, BaselineEntry>(DeployPath.Comparer);
        while (reader.Read())
            entries[reader.GetString(0)] = new BaselineEntry(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : Time(reader.GetString(4)));
        return entries;
    }

    public void UpdateBaseline(Guid id, IEnumerable<BaselineEntry> set, IEnumerable<string> remove)
    {
        lock (_write)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            using (var upsert = AppDatabase.Command(connection,
                       """
                       INSERT INTO deploy_baseline (connection_id, path, hash, size, remote_size, remote_modified_utc)
                       VALUES ($id, $path, $hash, $size, $remoteSize, $remoteModified)
                       ON CONFLICT (connection_id, path) DO UPDATE SET path = $path, hash = $hash, size = $size, remote_size = $remoteSize,
                           remote_modified_utc = $remoteModified
                       """, ("$id", id.ToString())))
            {
                var path = upsert.Parameters.Add("$path", SqliteType.Text);
                var hash = upsert.Parameters.Add("$hash", SqliteType.Text);
                var size = upsert.Parameters.Add("$size", SqliteType.Integer);
                var remoteSize = upsert.Parameters.Add("$remoteSize", SqliteType.Integer);
                var remoteModified = upsert.Parameters.Add("$remoteModified", SqliteType.Text);
                foreach (var entry in set)
                {
                    path.Value = entry.Path;
                    hash.Value = entry.Hash;
                    size.Value = entry.Size;
                    remoteSize.Value = (object?)entry.RemoteSize ?? DBNull.Value;
                    remoteModified.Value = (object?)Text(entry.RemoteModifiedUtc) ?? DBNull.Value;
                    upsert.ExecuteNonQuery();
                }
            }
            using (var delete = AppDatabase.Command(connection, "DELETE FROM deploy_baseline WHERE connection_id = $id AND path = $path", ("$id", id.ToString())))
            {
                var path = delete.Parameters.Add("$path", SqliteType.Text);
                foreach (var gone in remove)
                {
                    path.Value = gone;
                    delete.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
    }

    // ─── The projects' file hashes ─────────────────────────────────────────

    public IReadOnlyDictionary<string, CachedHash> LocalHashes(string root)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection, "SELECT path, size, modified_ticks, hash FROM deploy_local_hashes WHERE root = $root",
            ("$root", Root(root)));
        using var reader = command.ExecuteReader();
        var hashes = new Dictionary<string, CachedHash>(DeployPath.Comparer);
        while (reader.Read()) hashes[reader.GetString(0)] = new CachedHash(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
        return hashes;
    }

    public void SaveLocalHashes(string root, IReadOnlyDictionary<string, CachedHash> files)
    {
        lock (_write)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            AppDatabase.Execute(connection, "DELETE FROM deploy_local_hashes WHERE root = $root", ("$root", Root(root)));
            using var insert = AppDatabase.Command(connection,
                "INSERT INTO deploy_local_hashes (root, path, size, modified_ticks, hash) VALUES ($root, $path, $size, $ticks, $hash)", ("$root", Root(root)));
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
            var hash = insert.Parameters.Add("$hash", SqliteType.Text);
            foreach (var (file, cached) in files)
            {
                path.Value = file;
                size.Value = cached.Size;
                ticks.Value = cached.ModifiedTicks;
                hash.Value = cached.Hash;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static string Root(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    // ─── History ───────────────────────────────────────────────────────────

    public long Begin(DeployOperation operation)
    {
        lock (_write)
        {
            using var connection = database.Open();
            // One still "running" of this connection is one DNN Manager didn't get to finish - it ended first.
            AppDatabase.Execute(connection,
                "UPDATE deploy_operations SET status = $failed, ended_utc = $now, error = $why WHERE connection_id = $id AND status = $running",
                ("$failed", DeployOperationStatus.Failed.ToString()), ("$now", Text(DateTime.UtcNow)), ("$id", operation.ConnectionId.ToString()),
                ("$running", DeployOperationStatus.Running.ToString()), ("$why", "DNN Manager ended before it was done."));
            AppDatabase.Execute(connection,
                """
                INSERT INTO deploy_operations (connection_id, site, connection_name, environment, kind, started_utc, user_name, message, status, retries, rollback_of)
                VALUES ($id, $site, $name, $env, $kind, $started, $user, $message, $status, 0, $of)
                """,
                ("$id", operation.ConnectionId.ToString()), ("$site", operation.Site), ("$name", operation.ConnectionName),
                ("$env", operation.Environment.ToString()), ("$kind", operation.Kind.ToString()), ("$started", Text(operation.StartedUtc)),
                ("$user", operation.User), ("$message", operation.Message), ("$status", operation.Status.ToString()), ("$of", operation.RollbackOf));
            return AppDatabase.Scalar<long>(connection, "SELECT last_insert_rowid()");
        }
    }

    public void AddFiles(long operationId, IEnumerable<DeployOperationFile> files)
    {
        lock (_write)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = AppDatabase.Command(connection,
                """
                INSERT OR REPLACE INTO deploy_operation_files (operation_id, path, side, action, before_hash, after_hash, base_before, base_before_size,
                    conflict, resolution, error, attempts)
                VALUES ($op, $path, $side, $action, $before, $after, $base, $baseSize, $conflict, $resolution, $error, $attempts)
                """, ("$op", operationId));
            var p = new[] { "$path", "$side", "$action", "$before", "$after", "$base", "$baseSize", "$conflict", "$resolution", "$error", "$attempts" }
                .ToDictionary(n => n, n => insert.Parameters.Add(n, SqliteType.Text));
            foreach (var file in files)
            {
                p["$path"].Value = file.Path;
                p["$side"].Value = file.Side.ToString();
                p["$action"].Value = file.Action.ToString();
                p["$before"].Value = (object?)file.BeforeHash ?? DBNull.Value;
                p["$after"].Value = (object?)file.AfterHash ?? DBNull.Value;
                p["$base"].Value = (object?)file.BaseBefore ?? DBNull.Value;
                p["$baseSize"].Value = (object?)file.BaseBeforeSize ?? DBNull.Value;
                p["$conflict"].Value = (object?)file.Conflict ?? DBNull.Value;
                p["$resolution"].Value = (object?)file.Resolution ?? DBNull.Value;
                p["$error"].Value = (object?)file.Error ?? DBNull.Value;
                p["$attempts"].Value = file.Attempts;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void End(long operationId, DeployOperationStatus status, string? error, int retries) =>
        Write("UPDATE deploy_operations SET status = $status, error = $error, retries = $retries, ended_utc = $now WHERE id = $op",
            ("$op", operationId), ("$status", status.ToString()), ("$error", error), ("$retries", retries), ("$now", Text(DateTime.UtcNow)));

    public void SetReviewFolder(long operationId, string folder) =>
        Write("UPDATE deploy_operations SET review_folder = $folder WHERE id = $op", ("$op", operationId), ("$folder", folder));

    public void MarkRolledBack(long operationId, long rollbackId) =>
        Write("UPDATE deploy_operations SET rolled_back_by = $by WHERE id = $op", ("$op", operationId), ("$by", rollbackId));

    private const string OperationColumns =
        "o.id, o.connection_id, o.site, o.connection_name, o.environment, o.kind, o.started_utc, o.ended_utc, o.user_name, o.message, o.status, " +
        "o.error, o.retries, o.rollback_of, o.rolled_back_by, o.review_folder, (SELECT COUNT(*) FROM deploy_operation_files f WHERE f.operation_id = o.id)";

    public IReadOnlyList<DeployOperation> Operations(Guid connectionId, int limit = 200)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection,
            $"SELECT {OperationColumns} FROM deploy_operations o WHERE o.connection_id = $id ORDER BY o.id DESC LIMIT $limit",
            ("$id", connectionId.ToString()), ("$limit", limit));
        return ReadOperations(command);
    }

    public DeployOperation? Operation(long id)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection, $"SELECT {OperationColumns} FROM deploy_operations o WHERE o.id = $id", ("$id", id));
        return ReadOperations(command).FirstOrDefault();
    }

    private static List<DeployOperation> ReadOperations(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<DeployOperation>();
        while (reader.Read())
            list.Add(new DeployOperation
            {
                Id = reader.GetInt64(0),
                ConnectionId = Guid.Parse(reader.GetString(1)),
                Site = reader.GetString(2),
                ConnectionName = reader.GetString(3),
                Environment = Enum.TryParse<DeployEnvironment>(reader.GetString(4), out var env) ? env : DeployEnvironment.Production,
                Kind = Enum.TryParse<DeployOperationKind>(reader.GetString(5), out var kind) ? kind : DeployOperationKind.Push,
                StartedUtc = Time(reader.GetString(6)),
                EndedUtc = reader.IsDBNull(7) ? null : Time(reader.GetString(7)),
                User = reader.GetString(8),
                Message = reader.GetString(9),
                Status = Enum.TryParse<DeployOperationStatus>(reader.GetString(10), out var status) ? status : DeployOperationStatus.Failed,
                Error = reader.IsDBNull(11) ? null : reader.GetString(11),
                Retries = reader.GetInt32(12),
                RollbackOf = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                RolledBackBy = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                ReviewFolder = reader.IsDBNull(15) ? null : reader.GetString(15),
                FileCount = reader.GetInt32(16)
            });
        return list;
    }

    public IReadOnlyList<DeployOperationFile> Files(long operationId)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection,
            """
            SELECT path, side, action, before_hash, after_hash, base_before, base_before_size, conflict, resolution, error, attempts
            FROM deploy_operation_files WHERE operation_id = $op ORDER BY path COLLATE NOCASE
            """, ("$op", operationId));
        using var reader = command.ExecuteReader();
        var files = new List<DeployOperationFile>();
        string? Nullable(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (reader.Read())
            files.Add(new DeployOperationFile
            {
                OperationId = operationId,
                Path = reader.GetString(0),
                Side = Enum.TryParse<SyncSide>(reader.GetString(1), out var side) ? side : SyncSide.Remote,
                Action = Enum.TryParse<FileAction>(reader.GetString(2), out var action) ? action : FileAction.Modified,
                BeforeHash = Nullable(3),
                AfterHash = Nullable(4),
                BaseBefore = Nullable(5),
                BaseBeforeSize = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                Conflict = Nullable(7),
                Resolution = Nullable(8),
                Error = Nullable(9),
                Attempts = reader.GetInt32(10)
            });
        return files;
    }

    public IReadOnlySet<string> ReferencedHashes(DateTime historySinceUtc)
    {
        using var connection = database.Open();
        using var command = AppDatabase.Command(connection,
            """
            SELECT before_hash FROM deploy_operation_files f JOIN deploy_operations o ON o.id = f.operation_id WHERE o.started_utc >= $since AND before_hash IS NOT NULL
            UNION SELECT after_hash FROM deploy_operation_files f JOIN deploy_operations o ON o.id = f.operation_id WHERE o.started_utc >= $since AND after_hash IS NOT NULL
            UNION SELECT base_before FROM deploy_operation_files f JOIN deploy_operations o ON o.id = f.operation_id WHERE o.started_utc >= $since AND base_before IS NOT NULL
            UNION SELECT hash FROM deploy_baseline
            """, ("$since", Text(historySinceUtc)));
        using var reader = command.ExecuteReader();
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) hashes.Add(reader.GetString(0));
        return hashes;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private void Write(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_write)
        {
            using var connection = database.Open();
            AppDatabase.Execute(connection, sql, parameters);
        }
    }

    // A time of no kind is UTC already (a server's listing) - never shifted by this PC's time zone.
    private static string? Text(DateTime? utc) => utc is not { } value
        ? null
        : (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
            .ToString("o", CultureInfo.InvariantCulture);

    private static DateTime Time(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
