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
/// <see cref="MaxBytes"/>, saying so - a message repeated without end mustn't fill the disk - and all the files together
/// stay under <see cref="MaxTotalBytes"/>, the oldest going first.
/// Lines are written as they come but reach the disk in batches (<see cref="FlushEvery"/>, on a timer's thread): most
/// come from the UI thread, which mustn't wait for the disk line by line. A warning, an error and how an operation
/// ended reach it at once - what explains a crash is in the file when the process ends - and so does the first line of
/// a new file. The rest goes when DNN Manager closes (<see cref="Dispose"/>).
/// </summary>
public sealed class DailyLogFile : IDisposable
{
    private const int DaysKept = 30;

    /// <summary>How large a day's file may get.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>How large all the files in the logs folder may get together; the oldest go first.</summary>
    public const long MaxTotalBytes = 200L * 1024 * 1024;

    /// <summary>How long a line may wait in memory before it is written to the disk.</summary>
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    // Lines that go to the disk at once: the tags the activity log and the logger give warnings, errors and the end of
    // an operation (ActivityLog, DailyLogFileLoggerProvider).
    private static readonly string[] FlushedAtOnce = ["[warning]", "[error]", "[critical]", "[success]", "[cancelled]"];

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Timer _flusher;
    private StreamWriter? _writer;
    private string? _path;
    private DateOnly _day;
    // The day's file reached MaxBytes: nothing more is written to it.
    private bool _full;
    // Lines written since the last flush.
    private bool _pending;
    // After a write failed: nothing is written before this.
    private DateTime _retryAt;
    // Closed (the app is ending): a line that still comes is written at once - there is no timer to flush it.
    private bool _disposed;

    public DailyLogFile(AppDataPaths paths)
    {
        _directory = paths.LogsDirectory;
        DeleteOldFiles(null);
        _flusher = new Timer(_ => Flush(), null, FlushEvery, FlushEvery);
    }

    /// <summary>
    /// A line, after its time: <c>11:57:09 IIS site and app pool 'test-web' removed.</c> - one a message, no headings:
    /// an operation's title and its stages are the Output tab's.
    /// </summary>
    public void Append(DateTime time, string line) => Write(time, $"{time:HH:mm:ss} {line}", IsUrgent(line));

    /// <summary>Whether <paramref name="line"/> goes to the disk at once rather than with the next batch.</summary>
    internal static bool IsUrgent(string line)
    {
        foreach (var tag in FlushedAtOnce)
            if (line.StartsWith(tag, StringComparison.Ordinal)) return true;
        return false;
    }

    private void Write(DateTime time, string text, bool urgent)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _retryAt) return;
            try
            {
                var before = _writer;
                var writer = Writer(DateOnly.FromDateTime(time));
                // A file just (re)started shows at once what is in it - after Clean up data, say.
                var opened = !ReferenceEquals(before, writer);
                if (_full) return;
                if (writer.BaseStream.Length >= MaxBytes)
                {
                    _full = true;
                    writer.WriteLine($"{time:HH:mm:ss} --- the log reached {MaxBytes / 1048576} MB today: the rest of today's lines aren't kept ---");
                    writer.Flush();
                    return;
                }
                writer.WriteLine(text);
                if (urgent || opened || _disposed) writer.Flush();
                else _pending = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed();
            }
        }
    }

    /// <summary>Writes the lines waiting in memory to the disk - the timer's work, and the last thing before closing.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_pending || _writer is null) return;
            try
            {
                _writer.Flush();
                _pending = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed();
            }
        }
    }

    public void Dispose()
    {
        _flusher.Dispose();
        lock (_gate)
        {
            try { _writer?.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nowhere to say it */ }
            try { _writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _writer = null;
            _disposed = true;
        }
    }

    // A write or a flush failed: what is buffered is let go (it can't reach the file), and nothing is tried for a minute.
    private void Failed()
    {
        _retryAt = DateTime.UtcNow.AddMinutes(1);
        _pending = false;
        try { _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _writer = null;
    }

    private StreamWriter Writer(DateOnly day)
    {
        // The same day's file - unless it was deleted meanwhile: then a new one.
        if (_writer is not null && day == _day && File.Exists(_path)) return _writer;
        // A new day: the files too old to keep go, as at the start.
        if (_writer is not null && day != _day) DeleteOldFiles(Path.Combine(_directory, FileName(day)));
        if (day != _day) _full = false;
        try { _writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* its file is gone */ }
        _writer = null;
        _pending = false;
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, FileName(day));
        // Shared, so the log can be opened (or copied) - and deleted - while the app is running.
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 64 * 1024);
        _day = day;
        return _writer;
    }

    /// <summary>The day's file name: <c>dnnmanager-20261004.log</c>.</summary>
    public static string FileName(DateOnly day) => $"dnnmanager-{day:yyyyMMdd}.log";

    /// <summary>
    /// Deletes the files older than <see cref="DaysKept"/> days and those of the old name, then the oldest until all
    /// together are under <see cref="MaxTotalBytes"/> - never <paramref name="keep"/>, the file being written.
    /// </summary>
    private void DeleteOldFiles(string? keep)
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            var cutoff = DateTime.Now.AddDays(-DaysKept);
            var left = new List<FileInfo>();
            foreach (var file in new DirectoryInfo(_directory).GetFiles("*.log"))
            {
                if (file.LastWriteTime < cutoff || IsOldName(file.Name)) TryDelete(file);
                else left.Add(file);
            }
            var total = left.Sum(f => f.Length);
            foreach (var file in left.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaxTotalBytes) break;
                if (keep is not null && string.Equals(file.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                // Its length before it goes: a deleted FileInfo has none to read.
                var length = file.Length;
                if (TryDelete(file)) total -= length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // dnnmanager-2026-10-04.log - the name before 1.7.4.
    private static bool IsOldName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^dnnmanager-\d{4}-\d{2}-\d{2}\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
