using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace DnnManager.Infrastructure.Settings;

/// <summary>Something that happened while loading the settings, worth showing in the activity log.</summary>
public sealed record SettingsNotice(bool IsWarning, string Message);

public sealed record SettingsLoadResult(UserSettings Settings, IReadOnlyList<SettingsNotice> Notices);

/// <summary>The settings can't be used as they are: a value that can't be read or isn't allowed, or a database that can't be read.</summary>
public sealed class SettingsException(string message, IReadOnlyList<string>? problems = null, Exception? inner = null) : Exception(message, inner)
{

    /// <summary>One line per bad value, e.g. <c>projects.sitePort must be a number between 1 and 65535.</c></summary>
    public IReadOnlyList<string> Problems { get; } = problems ?? [];
}

/// <summary>
/// Reads and writes the user's settings: the <c>settings</c> table of DNN Manager's database
/// (<c>Documents\DnnManager\dnnmanager.db</c>, see <see cref="AppDatabase"/>), one row per value -
/// <c>projects.sitePort</c> = <c>80</c> (<see cref="ValueRows"/>). A value without a row has its default; rows this version
/// doesn't know (a newer one's) are kept. The sa password is kept encrypted (<see cref="SecretProtector"/>).
/// </summary>
public sealed class SettingsStore(AppDataPaths paths)
{

    private const string SaPasswordKey = "sqlServer.saPassword";

    private readonly AppDataPaths _paths = paths;
    private readonly AppDatabase _database = new(paths);

    /// <summary>Where the settings are: the database file - for messages.</summary>
    public string Location => _database.Path;

    /// <summary>
    /// Loads the settings at startup - the defaults the first time, which are saved then. Throws
    /// <see cref="SettingsException"/> when they can't be used; nothing is written then.
    /// </summary>
    public SettingsLoadResult Load()
    {
        var notices = new List<SettingsNotice>();
        try
        {
            if (_paths.MoveFromOldName() is { } moved) notices.Add(new(false, moved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException(
                $"Could not rename {_paths.OldRoot} to {_paths.Root}: {ex.Message}",
                ["Close whatever has a file in it open (another DNN Manager, an editor, Explorer) and choose Try again."], ex);
        }

        try { _paths.EnsureCreated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"Could not create {_paths.Root}: {ex.Message}" + AccessHint(ex), inner: ex);
        }

        // A damaged file is put aside before anything reads it - back from the copy of the last update, or anew.
        try
        {
            if (_database.RecoverIfDamaged() is { } recovered) notices.Add(new(true, recovered));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It couldn't be moved (in use): reading it below says what is wrong.
        }

        var saved = ReadRows();
        var settings = ToSettings(saved);
        // The first start - or values added since: what is saved is complete again.
        if (saved.Count == 0 || !Rows(settings).All(r => saved.TryGetValue(r.Key, out var v) && (v == r.Value || r.Key == SaPasswordKey)))
        {
            if (saved.Count == 0) notices.Add(new(false, $"Created the settings in {Location} with their defaults."));
            try { Write(settings, saved); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The settings themselves are fine - run with them and say they weren't saved.
                notices.Add(new(true, $"Could not write {Location}: {ex.Message}{AccessHint(ex)} Using the settings without saving them."));
            }
        }
        return new SettingsLoadResult(settings, notices);
    }

    /// <summary>The settings as saved now, complete and checked, without writing anything. Throws <see cref="SettingsException"/>.</summary>
    public UserSettings Read() => ToSettings(ReadRows());

    /// <summary>
    /// Saves <paramref name="settings"/> after checking them. Rows this version doesn't know are kept. Throws
    /// <see cref="SettingsException"/> for values that aren't allowed, <see cref="IOException"/> when the database can't be written.
    /// </summary>
    public void Save(UserSettings settings)
    {
        ThrowIfInvalid(settings);
        IReadOnlyDictionary<string, string> saved;
        try { saved = ReadRows(); }
        catch (SettingsException) { saved = new Dictionary<string, string>(); } // unreadable - replaced by what's saved now
        Write(settings, saved);
    }

    /// <summary>Changes the saved settings: reads them, applies <paramref name="change"/> and saves them.</summary>
    public UserSettings Update(Action<UserSettings> change)
    {
        var settings = Read();
        change(settings);
        Save(settings);
        return settings;
    }

    /// <summary>Replaces the settings with the defaults - the current ones aren't kept, but for the container's sa password.</summary>
    public void ResetToDefaults()
    {
        _paths.EnsureCreated();
        try
        {
            // From the start-up dialog, the file may be what is wrong: a damaged one is put aside first.
            _database.RecoverIfDamaged();
            using var connection = _database.Open();
            AppDatabase.Execute(connection, "BEGIN IMMEDIATE");
            // The container was made with its sa password: a reset doesn't take it away (a new default wouldn't sign in).
            var sa = AppDatabase.KeyValues(connection, "SELECT key, value FROM settings WHERE key = $key", ("$key", SaPasswordKey));
            AppDatabase.Execute(connection, "DELETE FROM settings");
            AppDatabase.InsertAll(connection, "INSERT INTO settings (key, value) VALUES ($key, $value)", Rows(new UserSettings()));
            if (sa.TryGetValue(SaPasswordKey, out var kept))
                AppDatabase.Execute(connection, "UPDATE settings SET value = $value WHERE key = $key", ("$value", kept), ("$key", SaPasswordKey));
            AppDatabase.Execute(connection, "COMMIT");
        }
        catch (SqliteException ex) { throw new IOException(ex.Message, ex); }
    }

    /// <summary>The settings as saved - one value per key - empty when there are none yet. Throws <see cref="SettingsException"/>.</summary>
    public IReadOnlyDictionary<string, string> SavedValues() => ReadRows();

    /// <summary>The saved rows. Throws <see cref="SettingsException"/> when the database can't be read.</summary>
    private Dictionary<string, string> ReadRows()
    {
        try
        {
            using var connection = _database.Open();
            return AppDatabase.KeyValues(connection, "SELECT key, value FROM settings");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"Could not read the settings from {Location}: {ex.Message}" + AccessHint(ex), inner: ex);
        }
    }

    /// <summary>Writes <paramref name="settings"/> whole, keeping the rows of <paramref name="saved"/> this version doesn't know.</summary>
    private void Write(UserSettings settings, IReadOnlyDictionary<string, string> saved)
    {
        var rows = Rows(settings);
        try
        {
            using var connection = _database.Open();
            AppDatabase.Execute(connection, "BEGIN IMMEDIATE");
            // What this version writes replaces what it wrote before - an item of its lists and dictionaries that is gone
            // goes; a newer version's own rows stay.
            foreach (var key in saved.Keys.Where(k => !rows.ContainsKey(k) && IsItemOf(k, rows)))
                AppDatabase.Execute(connection, "DELETE FROM settings WHERE key = $key", ("$key", key));
            AppDatabase.InsertAll(connection, "INSERT OR REPLACE INTO settings (key, value) VALUES ($key, $value)", rows);
            AppDatabase.Execute(connection, "COMMIT");
        }
        catch (SqliteException ex) { throw new IOException(ex.Message, ex); }
    }

    /// <summary>The rows of <paramref name="settings"/>, the sa password encrypted.</summary>
    private static Dictionary<string, string> Rows(UserSettings settings)
    {
        var rows = ValueRows.From(settings);
        if (rows.TryGetValue(SaPasswordKey, out var password) && !SecretProtector.IsProtected(password))
            rows[SaPasswordKey] = SecretProtector.Protect(password);
        return rows;
    }

    /// <summary>
    /// <paramref name="key"/> is an item of one of this version's lists or dictionaries (<c>keyboard.shortcuts{…}</c>,
    /// <c>iis.requiredFeatures[3].name</c>) - the list itself is one of <paramref name="rows"/>.
    /// </summary>
    private static bool IsItemOf(string key, IReadOnlyDictionary<string, string> rows)
    {
        var bracket = key.IndexOfAny(['[', '{']);
        return bracket > 0 && rows.ContainsKey(key[..bracket]);
    }

    private UserSettings ToSettings(IReadOnlyDictionary<string, string> saved)
    {
        if (saved.TryGetValue("version", out var text) && int.TryParse(text, out var version) && version > UserSettings.CurrentVersion)
            throw new SettingsException(
                $"The settings in {Location} were saved by a newer version of DNN Manager (settings version {version}; this version reads up to {UserSettings.CurrentVersion}).",
                ["Update DNN Manager, or reset the settings to this version's defaults."]);

        var rows = new Dictionary<string, string>(saved, StringComparer.Ordinal);
        if (rows.TryGetValue(SaPasswordKey, out var password) && SecretProtector.IsProtected(password))
        {
            try { rows[SaPasswordKey] = SecretProtector.Unprotect(password); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                throw new SettingsException($"The settings in {Location} have an SA password that can't be decrypted.",
                    ["sqlServer.saPassword was encrypted by another Windows user or on another PC. Reset the settings, then enter " +
                     "the password again in Settings → Database server."], ex);
            }
        }

        var settings = new UserSettings();
        var unreadable = ValueRows.Into(settings, rows);
        if (unreadable.Count > 0)
            throw new SettingsException($"The settings in {Location} have values that can't be read.",
                unreadable.Select(key => $"{key} has a value of the wrong type.").ToList());
        settings.Version = UserSettings.CurrentVersion;
        ThrowIfInvalid(settings);
        return settings;
    }

    private void ThrowIfInvalid(UserSettings settings)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
            throw new SettingsException($"The settings in {Location} have values that aren't allowed.", problems.Select(p => p.ToString()).ToList());
    }

    // Windows Security's "Controlled folder access" blocks apps it doesn't know from writing to Documents.
    private static string AccessHint(Exception ex) => ex is UnauthorizedAccessException
        ? " If Windows Security's Controlled folder access is on, allow DnnManager.exe through it."
        : "";
}
