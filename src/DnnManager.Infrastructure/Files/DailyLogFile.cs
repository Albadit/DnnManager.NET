using System.Text;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Appends lines to <c>logs\dnnmanager-yyyy-MM-dd.log</c> in the user's DNN Manager folder, a new file each day,
/// deleting files older than <see cref="DaysKept"/> days. Logging is best effort: once a write fails, the
/// file is left alone for the rest of the run instead of disturbing the app.
/// </summary>
public sealed class DailyLogFile : IDisposable
{
    private const int DaysKept = 30;

    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private bool _failed;

    public DailyLogFile(AppDataPaths paths)
    {
        _directory = paths.LogsDirectory;
        DeleteOldFiles();
    }

    public void Append(DateTime time, string line)
    {
        lock (_gate)
        {
            if (_failed) return;
            try
            {
                Writer(DateOnly.FromDateTime(time)).WriteLine($"{time:HH:mm:ss}  {line}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failed = true;
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
        if (_writer is not null && day == _day) return _writer;
        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        // Shared, so the log can be opened (or copied) while the app is running.
        var stream = new FileStream(Path.Combine(_directory, $"dnnmanager-{day:yyyy-MM-dd}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
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
