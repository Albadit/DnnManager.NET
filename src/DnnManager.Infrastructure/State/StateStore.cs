using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.State;

/// <summary>
/// A file of state kept between starts (where the user was, the window, unsaved form values…): its own file, its own format
/// number. A file of an older format is brought up to date by <see cref="Upgrade"/> as it is read.
/// </summary>
public interface IStateFile
{
    /// <summary>The file's name in the state folder, e.g. <c>workspace.json</c>.</summary>
    static abstract string FileName { get; }

    /// <summary>The format this version writes. Raise it when a change needs <see cref="Upgrade"/> to read older files.</summary>
    static abstract int CurrentFormat { get; }

    /// <summary>The format the file was written in.</summary>
    int Format { get; set; }

    /// <summary>
    /// An older file (format <paramref name="from"/>) as the current format reads it. By default as it is: a value added
    /// since starts at its default, one removed is ignored.
    /// </summary>
    static virtual JsonObject Upgrade(JsonObject file, int from) => file;
}

/// <summary>
/// Reads and writes the <see cref="IStateFile"/>s in one folder (<c>Documents\DnnManager\state</c>), each on its own, so
/// one that goes wrong takes nothing else with it. Never throws: a file that can't be read is set aside as
/// <c>&lt;name&gt;.bad</c> and its defaults are used, one written by a newer DNN Manager is left alone and ignored, and
/// one that can't be written is reported to the log. A write is whole or not at all (written beside it, then moved over
/// it), and skipped when nothing changed since the last one.
/// </summary>
public sealed class StateStore(string folder, ILogger? log = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger? _log = log;
    // What each file holds as this process last read or wrote it - an unchanged state isn't written again.
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public string Folder { get; } = folder;

    public string PathOf<T>() where T : IStateFile => Path.Combine(Folder, T.FileName);

    /// <summary>The state saved in <typeparamref name="T"/>'s file - its defaults when there is none, or it can't be used.</summary>
    public T Load<T>() where T : class, IStateFile, new()
    {
        var path = PathOf<T>();
        string text;
        try
        {
            if (!File.Exists(path)) return new T();
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not read {File} - starting from its defaults", path);
            return new T();
        }

        try
        {
            var file = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("it isn't a JSON object");
            var format = file["format"]?.GetValue<int>() ?? 0;
            if (format > T.CurrentFormat)
            {
                // A newer DNN Manager's: left as it is, for that version.
                _log?.LogWarning("{File} is of format {Format}, newer than {Current} - starting from its defaults", path, format, T.CurrentFormat);
                return new T();
            }
            if (format < T.CurrentFormat) file = T.Upgrade(file, format);
            var state = file.Deserialize<T>(Json) ?? throw new JsonException("it is empty");
            state.Format = T.CurrentFormat;
            lock (_gate) _written[path] = text;
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            SetAside(path, ex);
            return new T();
        }
    }

    /// <summary>Writes <paramref name="state"/> - unless it is what the file already holds. False when nothing was written.</summary>
    public bool Save<T>(T state) where T : class, IStateFile
    {
        var path = PathOf<T>();
        state.Format = T.CurrentFormat;
        string text;
        try { text = JsonSerializer.Serialize(state, Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _log?.LogWarning(ex, "Could not save {File}", path);
            return false;
        }

        lock (_gate)
        {
            if (_written.TryGetValue(path, out var last) && last == text) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                WriteWhole(path, text);
                _written[path] = text;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning(ex, "Could not save {File}", path);
                return false;
            }
        }
    }

    /// <summary>Removes <typeparamref name="T"/>'s file - it was used, and mustn't be used again.</summary>
    public void Delete<T>() where T : IStateFile
    {
        var path = PathOf<T>();
        try
        {
            File.Delete(path);
            lock (_gate) _written.Remove(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not delete {File}", path);
        }
    }

    /// <summary>Removes a state file no version uses any more, e.g. <c>terminals.json</c>.</summary>
    public void DeleteFile(string fileName)
    {
        var path = Path.Combine(Folder, fileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not delete {File}", path);
        }
    }

    /// <summary>Removes every state file (and any set aside) - nothing of the workspace is left for the next start.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _written.Clear();
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log?.LogWarning(ex, "Could not delete {File}", file); }
            }
        }
    }

    // Beside the file, flushed to the disk, then moved over it in one step: a crash leaves the old file or the new one.
    private static void WriteWhole(string path, string text)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            writer.Write(text);
        File.Move(temporary, path, overwrite: true);
    }

    private void SetAside(string path, Exception problem)
    {
        _log?.LogWarning(problem, "{File} can't be used - set aside as .bad, starting from its defaults", path);
        try { File.Move(path, path + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* it is ignored all the same */ }
    }
}
