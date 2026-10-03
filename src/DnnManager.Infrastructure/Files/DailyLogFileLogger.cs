using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Files;

/// <summary>
/// Sends the app's own warnings and errors (<see cref="ILogger"/>) to the daily log file, next to what the operations
/// write there - with the exception's stack trace, for whoever has to find out what went wrong:
/// <code>
/// 14:58:19  [warning] IisManager: Could not read IIS site states
///     System.UnauthorizedAccessException: …
///        at …
/// </code>
/// The same message from the same place is written once per <see cref="RepeatAfter"/>: a background read that fails on
/// every tick doesn't fill the file.
/// </summary>
public sealed class DailyLogFileLoggerProvider(DailyLogFile file) : ILoggerProvider
{
    internal static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTime> _lastWritten = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose() { }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTime.Now;
        lock (_lastWritten)
        {
            var key = $"{category}|{message}|{exception?.GetType().FullName}";
            if (_lastWritten.TryGetValue(key, out var last) && now - last < RepeatAfter) return;
            // Bounded: messages naming a site or a file can be many over a long run.
            if (_lastWritten.Count > 500) _lastWritten.Clear();
            _lastWritten[key] = now;
        }
        var tag = level switch { LogLevel.Warning => "warning", LogLevel.Critical => "critical", _ => "error" };
        var text = $"[{tag}] {category}: {message}";
        // Indented: a code block in the Markdown the file reads as.
        if (exception is not null) text += Environment.NewLine + "    " + exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");
        file.Append(now, text);
    }

    private sealed class Logger(DailyLogFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
