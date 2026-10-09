using DnnManager.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace DnnManager.Infrastructure.Data;

/// <summary>
/// DNN Manager's own data in one SQLite file, <c>Documents\DnnManager\dnnmanager.db</c> - every value in a column or a row
/// of its own:
/// <list type="bullet">
/// <item><c>settings</c> - the settings, one row per value: <c>projects.sitePort</c> = <c>80</c> (<see cref="ValueRows"/>,
/// <see cref="Settings.SettingsStore"/>);</item>

/// <item><c>state</c> - the workspace (window, page, forms, Logs tab, an update under way), by area, one row per value
/// (<see cref="State.StateStore"/>);</item>
/// <item><c>projects</c> - how DNN Manager installed the projects it set up;</item>
/// <item><c>keep_warm</c> - the sites kept warm, a row each (how, is the settings');</item>
/// <item><c>dnn_releases</c> - each repository's DNN versions as GitHub last listed them, for offline use.</item>
/// </list>
/// Each read or write opens the file for itself and closes it again (no pool): nothing holds it open between them, and
/// SQLite makes every write whole or not at all.
/// </summary>
public sealed class AppDatabase
{
    public const string FileName = "dnnmanager.db";

    /// <summary>
    /// What makes each version of the tables, from the one before - <c>PRAGMA user_version</c> holds how many have run. A
    /// change of the tables is a new step at the end; the ones before never change.
    /// </summary>
    private static readonly string[] Steps =
    [
        """
        CREATE TABLE IF NOT EXISTS documents (name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, content TEXT NOT NULL, updated_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS projects (
            site           TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            install_mode   TEXT NOT NULL,
            created_utc    TEXT NOT NULL,
            dnn_version    TEXT,
            host_user_name TEXT);
        CREATE TABLE IF NOT EXISTS keep_warm (
            site          TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
            enabled       INTEGER NOT NULL,
            ping_minutes  INTEGER,
            warm_up_path  TEXT,
            ping_path     TEXT);
        CREATE TABLE IF NOT EXISTS dnn_releases (
            api        TEXT NOT NULL COLLATE NOCASE,
            position   INTEGER NOT NULL,
            version    TEXT NOT NULL,
            tag        TEXT NOT NULL,
            url        TEXT NOT NULL,
            prerelease INTEGER NOT NULL,
            saved_utc  TEXT NOT NULL,
            PRIMARY KEY (api, position));
        """,
        // The settings and the workspace as rows of their own, not JSON text.
        """
        DROP TABLE IF EXISTS documents;
        CREATE TABLE settings (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL);
        CREATE TABLE settings_copies (
            copied_utc TEXT NOT NULL,
            reason     TEXT NOT NULL,
            key        TEXT NOT NULL,
            value      TEXT NOT NULL,
            PRIMARY KEY (copied_utc, key));
        CREATE TABLE state (
            area  TEXT NOT NULL COLLATE NOCASE,
            key   TEXT NOT NULL,
            value TEXT NOT NULL,
            PRIMARY KEY (area, key));
        """,
        // Keep warm is the same for every site (the settings): a site kept warm is a row, nothing more.
        """
        CREATE TABLE keep_warm_sites (site TEXT NOT NULL PRIMARY KEY COLLATE NOCASE);
        INSERT INTO keep_warm_sites (site) SELECT site FROM keep_warm WHERE enabled <> 0;
        DROP TABLE keep_warm;
        ALTER TABLE keep_warm_sites RENAME TO keep_warm;
        """,
        // A reset to the defaults keeps no copy of the settings.
        """
        DROP TABLE IF EXISTS settings_copies;
        """
    ];

    private readonly string _connectionString;

    public AppDatabase(string folder)
    {
        Folder = folder;
        Path = System.IO.Path.Combine(folder, FileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Opened for each read or write and closed after it - the file isn't kept open between them.
            Pooling = false,
            // Another write still busy (keep warm and the window save from different threads): wait for it.
            DefaultTimeout = 30
        }.ToString();
    }

    public AppDatabase(AppDataPaths paths) : this(paths.Root) { }

    public string Folder { get; }

    /// <summary>The database file, <c>dnnmanager.db</c>.</summary>
    public string Path { get; }

    /// <summary>
    /// <c>dnnmanager.backup.db</c>: the file as it was before its tables last changed (an update) - what a damaged file is
    /// put back from (<see cref="RecoverIfDamaged"/>).
    /// </summary>
    public string BackupPath => System.IO.Path.Combine(Folder, "dnnmanager.backup.db");

    /// <summary>
    /// The file, open - made, or brought up to the current tables, the first time. Throws <see cref="SqliteException"/>
    /// or <see cref="IOException"/> when it can't be opened or made - <see cref="NewerDatabaseException"/> when a newer
    /// DNN Manager wrote it.
    /// </summary>
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Folder);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // Asked each time (it costs nothing): a file deleted meanwhile is made again with its tables.
            var version = Scalar<long>(connection, "PRAGMA user_version");
            if (version > Steps.Length) throw new NewerDatabaseException(Path, version, Steps.Length);
            if (version < Steps.Length) Upgrade(connection, version);
            else if (BackupIsOld()) Backup(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Brings the tables up to this version's: a copy of what the user had first, then every step that hasn't run, and the
    /// count with them, in one transaction - all or nothing.
    /// </summary>
    private void Upgrade(SqliteConnection connection, long seen)
    {
        // A file with tables of an older version, not a new one: kept as it was.
        if (seen > 0) Backup(connection);
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            // Counted again under the write lock: another connection (keep warm, the workspace) may have brought it up since
            // it was read - running a step twice would set the count back and fail at the next one on every start.
            var version = Scalar<long>(connection, "PRAGMA user_version");
            if (version > Steps.Length) throw new NewerDatabaseException(Path, version, Steps.Length);
            for (var step = (int)version; step < Steps.Length; step++)
                Execute(connection, $"{Steps[step]} PRAGMA user_version = {step + 1};");
            Execute(connection, "COMMIT");
        }
        catch
        {
            try { Execute(connection, "ROLLBACK"); }
            catch (SqliteException) { /* already rolled back by the error */ }
            throw;
        }
    }

    // When the copy was last looked at by this process: once a day is enough to ask.
    private long _backupCheckedAt = long.MinValue;

    /// <summary>
    /// The copy is more than a day old (or there is none): made anew, so a damaged file is brought back as it was
    /// yesterday - not as it was at the last update, which may be months ago. Asked at most once a minute.
    /// </summary>
    private bool BackupIsOld()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _backupCheckedAt) < 60_000) return false;
        Interlocked.Exchange(ref _backupCheckedAt, now);
        try { return !File.Exists(BackupPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(BackupPath) > TimeSpan.FromDays(1); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// <see cref="BackupPath"/> made anew from the file as it is now - written beside it (VACUUM INTO, a consistent copy)
    /// and only then put in its place, so a copy is always whole.
    /// </summary>
    private void Backup(SqliteConnection connection)
    {
        var temp = BackupPath + ".tmp";
        try
        {
            File.Delete(temp);
            Execute(connection, "VACUUM INTO $file", ("$file", temp));
            File.Move(temp, BackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // No copy this time (a full disk, say): the tables still change safely, in one transaction - the copy is for
            // what can't be foreseen. The last copy, if any, stays.
            try { File.Delete(temp); }
            catch (Exception ignored) when (ignored is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Puts a damaged file aside - <c>dnnmanager.damaged-yyyyMMdd-HHmmss.db</c>, kept to look into - and brings back
    /// <see cref="BackupPath"/> when it is sound; otherwise the next <see cref="Open"/> starts a new file. Returns what
    /// happened, for the user - null when the file is sound or isn't there. Called at the start, before anything reads it.
    /// Throws <see cref="IOException"/> when the file can't be moved.
    /// </summary>
    public string? RecoverIfDamaged()
    {
        if (!File.Exists(Path) || IsSound(Path)) return null;
        var aside = System.IO.Path.Combine(Folder, $"dnnmanager.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        File.Move(Path, aside);
        // A journal left by an interrupted write belongs to the damaged file, not to what comes in its place.
        if (File.Exists(Path + "-journal")) File.Move(Path + "-journal", aside + "-journal");
        if (File.Exists(BackupPath) && IsSound(BackupPath))
        {
            File.Copy(BackupPath, Path);
            return $"{Path} was damaged - it is kept as {aside}. DNN Manager went back to the copy of " +
                   $"{File.GetLastWriteTime(BackupPath):g}, made before its last update.";
        }
        return $"{Path} was damaged - it is kept as {aside}. DNN Manager starts with its defaults.";
    }

    /// <summary>
    /// Whether SQLite finds <paramref name="file"/> sound (PRAGMA quick_check). Only a file it reports damaged, or not a
    /// database, isn't - one it can't open now (in use, no access) counts as sound: it isn't moved for that.
    /// </summary>
    private static bool IsSound(string file)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 30
            }.ToString());
            connection.Open();
            return Scalar<string>(connection, "PRAGMA quick_check") == "ok";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    private const int SqliteCorrupt = 11, SqliteNotADatabase = 26;

    // ─── Helpers for the tables ───────────────────────────────────────────

    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static T? Scalar<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    /// <summary>The key and value columns of <paramref name="sql"/>'s rows (its first two), as a dictionary.</summary>
    public static Dictionary<string, string> KeyValues(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    /// <summary>Writes <paramref name="rows"/> with <paramref name="insertSql"/> (taking <c>$key</c> and <c>$value</c>) - inside the caller's BEGIN … COMMIT.</summary>
    public static void InsertAll(SqliteConnection connection, string insertSql, IEnumerable<KeyValuePair<string, string>> rows,
        params (string Name, object? Value)[] extra)
    {
        using var command = Command(connection, insertSql, extra);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        foreach (var (k, v) in rows)
        {
            key.Value = k;
            value.Value = v;
            command.ExecuteNonQuery();
        }
    }
}

/// <summary>
/// <c>dnnmanager.db</c> has tables of a newer DNN Manager than this one (an older version started after a newer one) -
/// this version neither reads nor writes it, rather than change tables it doesn't know. An <see cref="IOException"/>, so
/// everything that handles a database it can't use handles this too.
/// </summary>
public sealed class NewerDatabaseException(string path, long found, int known) : IOException(
    $"{path} was written by a newer DNN Manager (its tables are at version {found}, this one knows {known}) - this version " +
    "leaves it as it is. Start the newer DNN Manager, or update this one.");
