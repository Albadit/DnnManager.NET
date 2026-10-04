using DnnManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.State;

/// <summary>An area of what DNN Manager keeps between starts (where the user was, the window, unsaved form values…).</summary>
public interface IStateFile
{
    /// <summary>The area's name in the <c>state</c> table, e.g. <c>workspace</c>.</summary>
    static abstract string Area { get; }
}

/// <summary>
/// Reads and writes the <see cref="IStateFile"/>s - each an area of the <c>state</c> table in DNN Manager's database, one
/// row per value (<see cref="ValueRows"/>), so one that goes wrong takes nothing else with it. Never throws: a value
/// without a row, or one that can't be read, has its default, and one that can't be written is reported to the log. A
/// write is whole or not at all, and skipped when nothing changed since the last one.
/// </summary>
public sealed class StateStore(AppDatabase database, ILogger? log = null)
{
    private readonly AppDatabase _database = database;
    private readonly ILogger? _log = log;
    // What each area holds as this process last read or wrote it - an unchanged state isn't written again.
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>The state saved as <typeparamref name="T"/> - its defaults where nothing (usable) is saved.</summary>
    public T Load<T>() where T : class, IStateFile, new()
    {
        var area = T.Area;
        var state = new T();
        Dictionary<string, string> rows;
        try
        {
            using var connection = _database.Open();
            rows = AppDatabase.KeyValues(connection, "SELECT key, value FROM state WHERE area = $area", ("$area", area));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _log?.LogWarning(ex, "Could not read the {Area} state - starting from its defaults", area);
            return state;
        }

        var unreadable = ValueRows.Into(state, rows);
        if (unreadable.Count > 0)
            _log?.LogWarning("The {Area} state has values that can't be read - their defaults are used: {Keys}", area, string.Join(", ", unreadable));
        lock (_gate) _written[area] = Snapshot(rows);
        return state;
    }

    /// <summary>Writes <paramref name="state"/> - unless it is what the database already holds. False when nothing was written.</summary>
    public bool Save<T>(T state) where T : class, IStateFile
    {
        var area = T.Area;
        var rows = ValueRows.From(state);
        var snapshot = Snapshot(rows);
        lock (_gate)
        {
            if (_written.TryGetValue(area, out var last) && last == snapshot) return false;
            try
            {
                using var connection = _database.Open();
                AppDatabase.Execute(connection, "BEGIN IMMEDIATE");
                AppDatabase.Execute(connection, "DELETE FROM state WHERE area = $area", ("$area", area));
                AppDatabase.InsertAll(connection, "INSERT INTO state (area, key, value) VALUES ($area, $key, $value)", rows, ("$area", area));
                AppDatabase.Execute(connection, "COMMIT");
                _written[area] = snapshot;
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                _log?.LogWarning(ex, "Could not save the {Area} state", area);
                return false;
            }
        }
    }

    /// <summary>Removes <typeparamref name="T"/>'s state - it was used, and mustn't be used again.</summary>
    public void Delete<T>() where T : IStateFile => Remove("DELETE FROM state WHERE area = $area", T.Area);

    /// <summary>Removes every state - nothing of the workspace is left for the next start.</summary>
    public void Clear() => Remove("DELETE FROM state", null);

    private void Remove(string sql, string? area)
    {
        lock (_gate)
        {
            try
            {
                using var connection = _database.Open();
                AppDatabase.Execute(connection, sql, ("$area", area));
                if (area is null) _written.Clear();
                else _written.Remove(area);
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                _log?.LogWarning(ex, "Could not remove the {Area} state", area ?? "whole");
            }
        }
    }

    /// <summary>The rows in one string, in key order - to tell whether anything changed.</summary>
    private static string Snapshot(IReadOnlyDictionary<string, string> rows) =>
        string.Join('\u001e', rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Key + '\u001f' + r.Value));

    /// <summary>The database can't be opened, read or written - the folder or the file isn't usable.</summary>
    private static bool IsStorageError(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException;
}
