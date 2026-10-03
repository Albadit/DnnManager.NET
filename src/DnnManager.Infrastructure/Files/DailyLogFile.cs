using System.Text;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Appends lines to <c>logs\dnnmanager-yyyy-MM-dd.log</c> in the user's DNN Manager folder, a new file each day,
/// deleting files older than <see cref="DaysKept"/> days. A file deleted while it is written to (Troubleshoot → Clean
/// up data, or by hand) is started again with the next line - writing on would go into the deleted file, which
/// nobody sees. Logging is best effort: a write that fails is tried again a minute later, without disturbing the app.
/// </summary>
public sealed class DailyLogFile : IDisposable
{
    private const int DaysKept = 30;

    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _path;
    private DateOnly _day;
    // After a write failed: nothing is written before this.
    private DateTime _retryAt;

    public DailyLogFile(AppDataPaths paths)
    {
        _directory = paths.LogsDirectory;
        DeleteOldFiles();
    }

    /// <summary>
    /// A line, after its time: <c>11:57:09 IIS site and app pool 'test-web' removed.</c> - one a message, no headings:
    /// an operation's title and its stages are the Output tab's.
    /// </summary>
    public void Append(DateTime time, string line) => Write(time, $"{time:HH:mm:ss} {line}");

    private void Write(DateTime time, string text)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _retryAt) return;
            try
            {
                Writer(DateOnly.FromDateTime(time)).WriteLine(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _retryAt = DateTime.UtcNow.AddMinutes(1);
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    private StreamWriter Writer(DateOnly day)
    {
        // The same day's file - unless it was deleted meanwhile: then a new one.
        if (_writer is not null && day == _day && File.Exists(_path)) return _writer;
        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, $"dnnmanager-{day:yyyy-MM-dd}.log");
        // Shared, so the log can be opened (or copied) - and deleted - while the app is running.
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _day = day;
        return _writer;
    }

    private void DeleteOldFiles()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var cutoff = DateTime.Now.AddDays(-DaysKept);
            foreach (var file in new DirectoryInfo(_directory).GetFiles("*.log"))
                if (file.LastWriteTime < cutoff) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
