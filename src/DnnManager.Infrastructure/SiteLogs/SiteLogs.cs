using System.Diagnostics.Eventing.Reader;
using System.Text;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.SiteLogs;

/// <summary>Which Windows event log entries a log shows: of these providers, in that log, mentioning that text.</summary>
public sealed record EventLogFilter(string LogName, IReadOnlyList<string> Providers, string? MessageContains);

/// <summary>
/// One log a website has - a file (DNN's log, IIS's request log…) or entries of a Windows event log.
/// </summary>
/// <param name="Group">What writes it: "DNN", "IIS", "Windows" - or "DNN Manager" for DNN Manager's own log.</param>
/// <param name="Title">What it is, as the menu and the Logs tab name it: "DNN log - 2026.10.01".</param>
/// <param name="Description">Where it is: the file's path with its size and time, or the event log and its filter.</param>
public sealed record SiteLogSource(string Group, string Title, string Description, string? FilePath, EventLogFilter? Events)
{
    /// <summary>Opens it: its newest lines, then new ones as they are written.</summary>
    public ILogTail Open() => FilePath is not null ? new FileLogTail(FilePath) : new EventLogTail(Events!);
}

/// <summary>
/// Finds a website's logs: DNN's own (Portals\_default\Logs), IIS's request logs for the site, HTTP.sys's errors,
/// and the Windows event log entries about it (ASP.NET errors, its app pool, worker process crashes) - and DNN
/// Manager's own log (<see cref="ForApp"/>).
/// </summary>
public sealed class SiteLogCatalog(IIisManager iis, AppDataPaths paths, ILogger<SiteLogCatalog> log)
{
    /// <summary>The group of DNN Manager's own log files.</summary>
    public const string AppGroup = "DNN Manager";

    private const int FilesPerKind = 8;

    private readonly IIisManager _iis = iis;
    private readonly AppDataPaths _paths = paths;
    private readonly ILogger<SiteLogCatalog> _log = log;

    /// <summary>DNN Manager's own log - a file a day, <c>logs\dnnmanager-yyyyMMdd.log</c> - newest first, named by its day.</summary>
    public IReadOnlyList<SiteLogSource> ForApp()
    {
        try
        {
            return Newest(_paths.LogsDirectory, "dnnmanager-*.log")
                .Select(file => (File: file, Day: Path.GetFileNameWithoutExtension(file)["dnnmanager-".Length..]))
                .Where(f => DateOnly.TryParseExact(f.Day, "yyyyMMdd", out _))
                .Take(FilesPerKind)
                .Select(f => FileSource(AppGroup, $"DNN Manager log - {DateOnly.ParseExact(f.Day, "yyyyMMdd"):yyyy-MM-dd}", f.File))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list DNN Manager's logs");
            return [];
        }
    }

    /// <summary>The logs of site <paramref name="siteName"/> (ID <paramref name="siteId"/>), newest files first within each kind.</summary>
    public IReadOnlyList<SiteLogSource> For(string siteName, long siteId, string directory, string appPool)
    {
        var logs = new List<SiteLogSource>();
        try
        {
            // DNN 9 writes log4net files named by day ("2026.10.01.log.resources"); older versions plain ".log".
            var dnnLogs = Path.Combine(directory, "Portals", "_default", "Logs");
            foreach (var file in Newest(dnnLogs, "*.log.resources").Concat(Newest(dnnLogs, "*.log")).Take(FilesPerKind))
                logs.Add(FileSource("DNN", $"DNN log - {LogName(file)}", file));

            if (_iis.GetLogDirectory(siteName) is { } iisLogs)
                foreach (var file in Newest(iisLogs, "*.log").Take(FilesPerKind))
                    logs.Add(FileSource("IIS", $"IIS requests - {LogName(file)}", file));

            var httpErr = Path.Combine(Environment.SystemDirectory, "LogFiles", "HTTPERR");
            foreach (var file in Newest(httpErr, "httperr*.log").Take(1))
                logs.Add(FileSource("IIS", "HTTP.sys errors (all sites)", file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list the logs of {Site}", siteName);
        }

        // ASP.NET's errors name the application as /LM/W3SVC/<id>/ROOT; WAS names the app pool in quotes.
        logs.Add(new SiteLogSource("Windows", "ASP.NET errors and warnings", "Application event log - ASP.NET, this site",
            null, new EventLogFilter("Application", ["ASP.NET 4.0.30319.0", "ASP.NET 2.0.50727.0"], $"/LM/W3SVC/{siteId}/")));
        if (appPool.Length > 0)
            logs.Add(new SiteLogSource("Windows", "App pool events", $"System event log - IIS (WAS), app pool '{appPool}'",
                null, new EventLogFilter("System", ["Microsoft-Windows-WAS"], $"'{appPool}'")));
        logs.Add(new SiteLogSource("Windows", "Worker process crashes", "Application event log - .NET Runtime and Application Error, w3wp.exe",
            null, new EventLogFilter("Application", [".NET Runtime", "Application Error"], "w3wp.exe")));
        return logs;
    }

    private static IEnumerable<string> Newest(string folder, string pattern) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName)
            : [];

    private static string LogName(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".log.resources", StringComparison.OrdinalIgnoreCase) ? name[..^".log.resources".Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    private static SiteLogSource FileSource(string group, string title, string file)
    {
        var info = new FileInfo(file);
        return new SiteLogSource(group, title, $"{file}  ({Size(info.Length)}, {info.LastWriteTime:g})", file, null);
    }

    private static string Size(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / 1024d / 1024d:0.#} MB";
}

/// <summary>
/// A log being shown: its newest lines first (<see cref="Start"/>), then each new batch of lines as it is written -
/// raised on a background thread. A large file is never read whole: only its end, then what is added.
/// </summary>
public interface ILogTail : IDisposable
{
    /// <summary>New lines, in order.</summary>
    event Action<IReadOnlyList<string>>? Lines;

    /// <summary>Starts reading: the last <paramref name="lines"/> lines, then following.</summary>
    void Start(int lines);

    /// <summary>
    /// While true it stops looking for new lines - nobody reads them (the window is minimized). Set back to false it
    /// looks at once and reads on from where it was, so no line is lost. Set before <see cref="Start"/>, the last
    /// lines are still read and following them begins once it is false. A log that Windows pushes (an event log)
    /// isn't polled - its lines keep coming.
    /// </summary>
    bool Paused { get; set; }
}

/// <summary>The end of a text file, then what is appended to it - looked at every second, the file shared with its writer.</summary>
internal sealed class FileLogTail(string path) : ILogTail
{
    private const int PollEvery = 1000;

    private readonly string _path = path;
    // Also the lock for the timer and the flags below.
    private readonly StringBuilder _partial = new();
    private Timer? _timer;
    private long _position;
    private int _busy;
    private bool _disposed, _paused;

    public event Action<IReadOnlyList<string>>? Lines;

    public bool Paused
    {
        get { lock (_partial) return _paused; }
        set
        {
            lock (_partial)
            {
                if (_paused == value) return;
                _paused = value;
                // Going on: looked at right away (it reads on from _position), then every second again.
                _timer?.Change(value ? Timeout.Infinite : 0, value ? Timeout.Infinite : PollEvery);
            }
        }
    }

    public void Start(int lines) => Task.Run(() =>
    {
        Interlocked.Exchange(ref _busy, 1);
        try
        {
            using var stream = Open();
            ReadFrom(stream, StartOfLastLines(stream, lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Lines?.Invoke([$"Can't read {_path}: {ex.Message}"]);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
        lock (_partial)
        {
            var every = _paused ? Timeout.Infinite : PollEvery;
            if (!_disposed) _timer = new Timer(_ => Poll(), null, every, every);
        }
    });

    private FileStream Open() => new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // Back from the end in blocks until enough line breaks are found - a large file is not read whole.
    private static long StartOfLastLines(FileStream stream, int lines)
    {
        const int block = 64 * 1024;
        var buffer = new byte[block];
        var found = 0;
        var position = stream.Length;
        while (position > 0)
        {
            var size = (int)Math.Min(block, position);
            position -= size;
            stream.Position = position;
            stream.ReadExactly(buffer, 0, size);
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != '\n' || position + i == stream.Length - 1) continue;
                if (++found >= lines) return position + i + 1;
            }
        }
        return 0;
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            using var stream = Open();
            if (stream.Length < _position)
            {
                // Rewritten from the start (rolled over): read it anew.
                _partial.Clear();
                Lines?.Invoke(["--- the file was emptied or replaced; reading it from the start ---"]);
                ReadFrom(stream, 0);
            }
            else if (stream.Length > _position)
            {
                ReadFrom(stream, _position);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being rotated or locked for a moment - looked at again in a second.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void ReadFrom(FileStream stream, long position)
    {
        stream.Position = position;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var text = reader.ReadToEnd();
        // Where the reading ended - not the length now, which may already include lines written meanwhile.
        _position = stream.Position;
        if (text.Length == 0) return;

        // A line still being written stays until its line break arrives.
        _partial.Append(text);
        var all = _partial.ToString();
        var last = all.LastIndexOf('\n');
        if (last < 0) return;
        _partial.Clear().Append(all, last + 1, all.Length - last - 1);
        var lines = all[..last].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0) Lines?.Invoke(lines);
    }

    public void Dispose()
    {
        lock (_partial)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>Windows event log entries that match a filter: the newest ones, then each new one as Windows writes it.</summary>
internal sealed class EventLogTail(EventLogFilter filter) : ILogTail
{
    private readonly EventLogFilter _filter = filter;
    private readonly object _lock = new();
    private EventLogWatcher? _watcher;
    private bool _disposed;

    private const int MaxScanned = 5000;

    public event Action<IReadOnlyList<string>>? Lines;

    // Windows pushes each new entry - there is no polling to pause.
    public bool Paused { get; set; }

    private string Query => "*[System[Provider[" + string.Join(" or ", _filter.Providers.Select(p => $"@Name='{p}'")) + "]]]";

    public void Start(int lines) => Task.Run(() =>
    {
        var events = new List<EventRecord>();
        try
        {
            using (var reader = new EventLogReader(new EventLogQuery(_filter.LogName, PathType.LogName, Query) { ReverseDirection = true }))
            {
                // Newest first, as many as are wanted (each entry is a few lines), then in the order they happened -
                // looking through so many at most, as a busy log can hold many of these providers' entries about others.
                var budget = Math.Max(20, lines / 6);
                var scanned = 0;
                for (var record = reader.ReadEvent(); record is not null && events.Count < budget; record = reader.ReadEvent())
                {
                    if (++scanned > MaxScanned)
                    {
                        record.Dispose();
                        break;
                    }
                    if (Matches(record)) events.Add(record);
                    else record.Dispose();
                }
            }
            events.Reverse();
            var text = events.SelectMany(Format).ToList();
            Lines?.Invoke(text.Count > 0 ? text
                : [$"No entries yet - {_filter.LogName} event log, {string.Join(", ", _filter.Providers)}. New ones show up here."]);

            var watcher = new EventLogWatcher(new EventLogQuery(_filter.LogName, PathType.LogName, Query));
            watcher.EventRecordWritten += (_, e) =>
            {
                using var record = e.EventRecord;
                if (record is not null && Matches(record)) Lines?.Invoke(Format(record).ToList());
            };
            lock (_lock)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }
                watcher.Enabled = true;
                _watcher = watcher;
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            Lines?.Invoke([$"Can't read the {_filter.LogName} event log: {ex.Message}"]);
        }
        finally
        {
            foreach (var record in events) record.Dispose();
        }
    });

    private bool Matches(EventRecord record) =>
        _filter.MessageContains is not { } text || Message(record).Contains(text, StringComparison.OrdinalIgnoreCase);

    private static string Message(EventRecord record)
    {
        try { return record.FormatDescription() ?? string.Join(" ", record.Properties.Select(p => p.Value)); }
        catch (EventLogException) { return ""; }
    }

    // "2026-10-01 14:03:22  ERROR  ASP.NET 4.0.30319.0 (1309)", then the message, indented.
    private static IEnumerable<string> Format(EventRecord record)
    {
        string level;
        try { level = record.LevelDisplayName ?? $"Level {record.Level}"; } catch (EventLogException) { level = $"Level {record.Level}"; }
        yield return $"{record.TimeCreated:yyyy-MM-dd HH:mm:ss}  {level.ToUpperInvariant()}  {record.ProviderName} ({record.Id})";
        foreach (var line in Message(record).Split('\n'))
            if (line.TrimEnd('\r').Length > 0) yield return "    " + line.TrimEnd('\r');
        yield return "";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
