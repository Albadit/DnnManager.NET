using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;

namespace DnnManager.Infrastructure.Settings;

/// <summary>Something that happened while loading the settings, worth showing in the activity log.</summary>
public sealed record SettingsNotice(bool IsWarning, string Message);

public sealed record SettingsLoadResult(UserSettings Settings, IReadOnlyList<SettingsNotice> Notices);

/// <summary><c>settings.json</c> can't be used as it is: not JSON, a value of the wrong type or not allowed, or unreadable.</summary>
public sealed class SettingsException : Exception
{
    public SettingsException(string message, IReadOnlyList<string>? problems = null, Exception? inner = null)
        : base(message, inner) => Problems = problems ?? [];

    /// <summary>One line per bad value, e.g. <c>projects.sitePort must be a number between 1 and 65535.</c></summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Reads and writes the user's <c>settings.json</c> (see <see cref="AppDataPaths"/>). Loading creates the
/// file on first run (carrying over the <c>appsettings.json</c> of an older version when there is one),
/// migrates an older layout after backing the file up, and fills in any key that's missing with its
/// default. Saving keeps keys it doesn't know, and writes through a temporary file so a crash never
/// leaves half a file behind.
/// </summary>
public sealed class SettingsStore
{
    private const int BackupsKept = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // Keep paths and URLs readable ("C:\\DNN", not "C:\u005CDNN").
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly AppDataPaths _paths;
    private readonly string _legacyDirectory;
    private readonly ISecretStore _secrets;

    /// <param name="secrets">Where an upgrade moves secrets the settings name - the Windows Credential Manager unless given.</param>
    public SettingsStore(AppDataPaths paths, string? legacyDirectory = null, ISecretStore? secrets = null)
    {
        _paths = paths;
        _legacyDirectory = legacyDirectory ?? AppDataPaths.LegacyDirectory;
        _secrets = secrets ?? new WindowsCredentialStore();
    }

    public string FilePath => _paths.SettingsFile;

    /// <summary>
    /// Loads the settings at startup, creating, importing, migrating and completing the file as needed.
    /// Throws <see cref="SettingsException"/> when the file can't be used; nothing is written then.
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

        JsonObject root;
        var source = FilePath;
        var save = false;
        if (File.Exists(FilePath))
        {
            root = Parse(ReadText(FilePath), source);
        }
        else if (File.Exists(LegacyFile))
        {
            source = LegacyFile;
            root = Parse(ReadText(LegacyFile), source);
            save = true;
            notices.Add(new(false, $"Carried the settings over from {LegacyFile} to {FilePath}."));
        }
        else
        {
            root = Serialize(new UserSettings());
            save = true;
            notices.Add(new(false, $"Created {FilePath} with the default settings."));
        }

        var version = VersionOf(root, source);
        if (version < UserSettings.CurrentVersion)
        {
            // A file already in Documents is about to change layout - keep the original first. A legacy
            // appsettings.json isn't touched, so it needs no copy.
            if (source == FilePath)
            {
                var backup = Backup($"settings.v{version}");
                notices.Add(new(false, $"Upgraded the settings from version {version} to {UserSettings.CurrentVersion} - the previous file is in {backup}."));
            }
            SettingsMigrations.Apply(root, version, _secrets);
            save = true;
        }

        var added = new List<string>();
        AddMissing(root, Serialize(new UserSettings()), "", added);
        if (added.Count > 0 && source == FilePath)
        {
            save = true;
            notices.Add(new(false, $"Added settings missing from {FilePath} with their defaults: {string.Join(", ", added)}."));
        }

        // A password typed into the file by hand (or kept by an older version) is encrypted when the file is written.
        if (PlainSaPassword(root) is not null && source == FilePath)
        {
            save = true;
            notices.Add(new(false, $"Encrypted the SA password in {FilePath}."));
        }

        var settings = ToSettings(root, source);
        if (save)
        {
            try { Write(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The settings themselves are fine - run with them and say they weren't saved.
                notices.Add(new(true, $"Could not write {FilePath}: {ex.Message}{AccessHint(ex)} Using the settings without saving them."));
            }
        }
        return new SettingsLoadResult(settings, notices);
    }

    /// <summary>The settings as saved now, complete and checked, without writing anything. Throws <see cref="SettingsException"/>.</summary>
    public UserSettings Read()
    {
        var root = File.Exists(FilePath) ? Parse(ReadText(FilePath), FilePath) : new JsonObject(NodeOptions);
        var version = VersionOf(root, FilePath);
        // Secrets are moved by Load, which saves the upgraded file - not by a read that leaves the file as it is.
        if (version < UserSettings.CurrentVersion) SettingsMigrations.Apply(root, version, secrets: null);
        AddMissing(root, Serialize(new UserSettings()), "", []);
        return ToSettings(root, FilePath);
    }

    /// <summary>
    /// Saves <paramref name="settings"/> after checking them. Keys in the file that <see cref="UserSettings"/>
    /// doesn't have are kept. Throws <see cref="SettingsException"/> for values that aren't allowed.
    /// </summary>
    public void Save(UserSettings settings)
    {
        ThrowIfInvalid(settings, FilePath);
        JsonObject root;
        try { root = File.Exists(FilePath) ? Parse(ReadText(FilePath), FilePath) : new JsonObject(NodeOptions); }
        catch (SettingsException) { root = new JsonObject(NodeOptions); } // unreadable - replaced by what's saved now
        Assign(root, Serialize(settings));
        Write(root);
    }

    /// <summary>Changes the saved settings: reads the file, applies <paramref name="change"/> and saves it.</summary>
    public UserSettings Update(Action<UserSettings> change)
    {
        var settings = Read();
        change(settings);
        Save(settings);
        return settings;
    }

    /// <summary>Replaces the file with the defaults, keeping the current one in the backups folder. Returns the backup's path, if one was made.</summary>
    public string? ResetToDefaults()
    {
        _paths.EnsureCreated();
        var backup = File.Exists(FilePath) ? Backup("settings.before-reset") : null;
        Write(Serialize(new UserSettings()));
        return backup;
    }

    private string LegacyFile => Path.Combine(_legacyDirectory, "appsettings.json");

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"Could not read {path}: {ex.Message}", inner: ex);
        }
    }

    private static JsonObject Parse(string text, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, NodeOptions, DocumentOptions);
            Materialize(node); // objects are filled in lazily - a duplicate key would only throw later
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1}, position {ex.BytePositionInLine + 1})" : "";
            throw new SettingsException($"{path} is not valid JSON{where}.", [ex.Message], ex);
        }
        catch (ArgumentException ex) // the same key twice (keys are matched ignoring case)
        {
            throw new SettingsException($"{path} is not valid JSON.", [ex.Message], ex);
        }
        return node as JsonObject
            ?? throw new SettingsException($"{path} must contain a JSON object ({{ ... }}).");
    }

    // ─── The sa password: encrypted in the file, plain in memory ────────────────

    /// <summary>The sa password when it's in the file as plain text, else null.</summary>
    private static string? PlainSaPassword(JsonObject root) =>
        root["sqlServer"] is JsonObject sql && sql["saPassword"] is JsonValue value && value.TryGetValue<string>(out var text)
        && !SecretProtector.IsProtected(text) ? text : null;

    /// <summary>Encrypts a plain sa password in <paramref name="root"/> - everything written to settings.json goes through here.</summary>
    private static void EncryptSaPassword(JsonObject root)
    {
        if (PlainSaPassword(root) is { } plain) root["sqlServer"]!["saPassword"] = SecretProtector.Protect(plain);
    }

    /// <summary>Replaces an encrypted sa password in <paramref name="root"/> with the plain one the app works with.</summary>
    private static void DecryptSaPassword(JsonObject root, string path)
    {
        if (root["sqlServer"] is not JsonObject sql || sql["saPassword"] is not JsonValue value ||
            !value.TryGetValue<string>(out var text) || !SecretProtector.IsProtected(text))
            return;
        try
        {
            sql["saPassword"] = SecretProtector.Unprotect(text);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new SettingsException($"{path} has an SA password that can't be decrypted.",
                ["sqlServer.saPassword was encrypted by another Windows user or on another PC. Replace it with the " +
                 "password as plain text - it is encrypted again on the next start."], ex);
        }
    }

    private static void Materialize(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var (_, child) in obj) Materialize(child);
        else if (node is JsonArray array) foreach (var child in array) Materialize(child);
    }

    private static int VersionOf(JsonObject root, string path)
    {
        var node = root["version"];
        if (node is null) return 0; // the appsettings.json of 2.0 and earlier had no version
        if (node is not JsonValue value || !value.TryGetValue<int>(out var version) || version < 0)
            throw new SettingsException($"{path} has an invalid version.", [$"version must be a whole number, found {node.ToJsonString()}."]);
        if (version > UserSettings.CurrentVersion)
            throw new SettingsException(
                $"{path} was saved by a newer version of DNN Manager (settings version {version}; this version reads up to {UserSettings.CurrentVersion}).",
                ["Update DNN Manager, or reset the settings to this version's defaults."]);
        return version;
    }

    private static UserSettings ToSettings(JsonObject root, string path)
    {
        DecryptSaPassword(root, path);
        UserSettings? settings;
        try { settings = root.Deserialize<UserSettings>(JsonOptions); }
        catch (JsonException ex)
        {
            var key = ex.Path is { Length: > 2 } p ? p[2..] : "a value"; // "$.projects.sitePort" -> "projects.sitePort"
            throw new SettingsException($"{path} has a value of the wrong type.", [$"{key} has the wrong type ({FirstSentence(ex.Message)})"], ex);
        }
        if (settings is null) throw new SettingsException($"{path} is empty.");
        ThrowIfInvalid(settings, path);
        return settings;
    }

    private static void ThrowIfInvalid(UserSettings settings, string path)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
            throw new SettingsException($"{path} has values that aren't allowed.", problems.Select(p => p.ToString()).ToList());
    }

    private static JsonObject Serialize(UserSettings settings)
        => (JsonObject)JsonSerializer.SerializeToNode(settings, JsonOptions)!;

    /// <summary>Adds every key of <paramref name="defaults"/> that <paramref name="target"/> lacks (or has as null), recursing into objects.</summary>
    private static void AddMissing(JsonObject target, JsonObject defaults, string prefix, List<string> added)
    {
        foreach (var (key, value) in defaults)
        {
            var existing = target[key];
            if (existing is null)
            {
                target[key] = value?.DeepClone();
                added.Add(prefix + key);
            }
            else if (existing is JsonObject child && value is JsonObject childDefaults)
            {
                AddMissing(child, childDefaults, prefix + key + ".", added);
            }
        }
    }

    /// <summary>Overwrites the keys of <paramref name="target"/> with <paramref name="source"/>'s, recursing into objects; other keys stay.</summary>
    private static void Assign(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject child && target[key] is JsonObject existing) Assign(existing, child);
            else target[key] = value?.DeepClone();
        }
    }

    private void Write(JsonObject root)
    {
        EncryptSaPassword(root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(JsonOptions) + Environment.NewLine);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Copies the settings file to <c>backups\{name}.{timestamp}.json</c> and prunes the oldest copies.</summary>
    private string Backup(string name)
    {
        Directory.CreateDirectory(_paths.BackupsDirectory);
        var path = Path.Combine(_paths.BackupsDirectory, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Copy(FilePath, path, overwrite: true);

        foreach (var old in new DirectoryInfo(_paths.BackupsDirectory).GetFiles("settings.*.json")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupsKept))
        {
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return path;
    }

    // Windows Security's "Controlled folder access" blocks apps it doesn't know from writing to Documents.
    private static string AccessHint(Exception ex) => ex is UnauthorizedAccessException
        ? " If Windows Security's Controlled folder access is on, allow DnnManager.exe through it."
        : "";

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        return (end > 0 ? message[..end] : message).TrimEnd('.');
    }
}
