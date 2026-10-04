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
    /// The file, open - made, or brought up to the current tables, the first time. Throws <see cref="SqliteException"/>
    /// or <see cref="IOException"/> when it can't be opened or made.
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
            for (var step = (int)version; step < Steps.Length; step++)
                Execute(connection, $"BEGIN IMMEDIATE; {Steps[step]} PRAGMA user_version = {step + 1}; COMMIT;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

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
