using System.Text;
using DnnManager.Infrastructure.Settings;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Appends lines to <c>logs\dnnmanager-yyyyMMdd.log</c> in the user's DNN Manager folder, a new file each day,
/// deleting files older than <see cref="DaysKept"/> days - and those of the name before 1.7.4 (<c>dnnmanager-yyyy-MM-dd.log</c>).
/// A file deleted while it is written to (Troubleshoot → Clean
/// up data, or by hand) is started again with the next line - writing on would go into the deleted file, which
/// nobody sees. Logging is best effort: a write that fails is tried again a minute later, without disturbing the app.
/// The old files go at the start and at each new day (DNN Manager may run for weeks); a day's file stops at
/// <see cref="MaxBytes"/>, saying so - a message repeated without end mustn't fill the disk.
/// </summary>
public sealed class DailyLogFile : IDisposable
{
    private const int DaysKept = 30;

    /// <summary>How large a day's file may get.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    private readonly string _directory;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _path;
    private DateOnly _day;
    // The day's file reached MaxBytes: nothing more is written to it.
    private bool _full;
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
                var writer = Writer(DateOnly.FromDateTime(time));
                if (_full) return;
                if (writer.BaseStream.Length >= MaxBytes)
                {
                    _full = true;
                    writer.WriteLine($"{time:HH:mm:ss} --- the log reached {MaxBytes / 1048576} MB today: the rest of today's lines aren't kept ---");
                    return;
                }
                writer.WriteLine(text);
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
        // A new day: the files too old to keep go, as at the start.
        if (_writer is not null && day != _day) DeleteOldFiles();
        if (day != _day) _full = false;
        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, FileName(day));
        // Shared, so the log can be opened (or copied) - and deleted - while the app is running.
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _day = day;
        return _writer;
    }

    /// <summary>The day's file name: <c>dnnmanager-20261004.log</c>.</summary>
    public static string FileName(DateOnly day) => $"dnnmanager-{day:yyyyMMdd}.log";

    private void DeleteOldFiles()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var cutoff = DateTime.Now.AddDays(-DaysKept);
            foreach (var file in new DirectoryInfo(_directory).GetFiles("*.log"))
                if (file.LastWriteTime < cutoff || IsOldName(file.Name)) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // dnnmanager-2026-10-04.log - the name before 1.7.4.
    private static bool IsOldName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^dnnmanager-\d{4}-\d{2}-\d{2}\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
