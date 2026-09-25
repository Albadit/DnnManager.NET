using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DnnManager.Presentation.Services;

public enum LogKind { Header, Step, Info, Success, Fail, Progress }

public sealed class LogEntry : INotifyPropertyChanged
{
    private string _text;

    public LogEntry(LogKind kind, string text)
    {
        Kind = kind;
        _text = text;
        Time = DateTime.Now;
    }

    public LogKind Kind { get; }
    public DateTime Time { get; }

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); OnPropertyChanged(nameof(Display)); }
    }

    /// <summary>The line as shown in the log pane, with the same markers the TUI prints.</summary>
    public string Display => Kind switch
    {
        LogKind.Header  => $"▌ {Text}",
        LogKind.Step    => $"── {Text} ──",
        LogKind.Success => $"✓ {Text}",
        LogKind.Fail    => $"✗ {Text}",
        _               => $"• {Text}",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The activity pane's contents. Use cases run on the thread pool, so every write is marshalled onto
/// the UI thread; writes queue in order, so the log reads exactly as the use case reported it.
/// </summary>
public sealed class ActivityLog
{
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;

    // The in-place progress line (e.g. a download percentage) until the next regular message closes it.
    private LogEntry? _progress;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public void Header(string text)  => Add(LogKind.Header, text);
    public void Step(string text)    => Add(LogKind.Step, text);
    public void Info(string text)    => Add(LogKind.Info, text);
    public void Success(string text) => Add(LogKind.Success, text);
    public void Fail(string text)    => Add(LogKind.Fail, text);

    public void Progress(string text) => Post(() =>
    {
        if (_progress is null)
        {
            _progress = new LogEntry(LogKind.Progress, text);
            Entries.Add(_progress);
        }
        else
        {
            _progress.Text = text;
        }
    });

    public void Clear() => Post(() => { _progress = null; Entries.Clear(); });

    /// <summary>The whole log as plain text, for the clipboard.</summary>
    public string ToText() => string.Join(Environment.NewLine,
        Entries.Select(e => $"{e.Time:HH:mm:ss}  {e.Display}"));

    private void Add(LogKind kind, string text) => Post(() =>
    {
        _progress = null;
        Entries.Add(new LogEntry(kind, text));
    });

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.InvokeAsync(action);
    }
}
