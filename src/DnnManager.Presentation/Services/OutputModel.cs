using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DnnManager.Presentation.Services;

// The Output tab's contents: what DNN Manager's operations did, as runs made of stages made of lines - built by
// ActivityLog on the UI thread, shown by PipelineView.

public enum RunStatus { Running, Finished, Failed, Cancelled }

public enum StageStatus { Pending, Running, Ok, Warning, Failed, Skipped, Cancelled }

public enum LineLevel { Info, Success, Warn, Error, Progress }

public abstract class OutputItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var name in names) Raise(name);
    }
}

/// <summary>One line: when it was written, how it went, and - for an error - what lies behind it and what to do.</summary>
/// <param name="completes">A success that took the place of a progress line - "Copied 4 487 files": shown in green.</param>
public sealed class OutputLine(DateTime time, LineLevel level, string text, IReadOnlyList<string>? details = null, string? hint = null,
    bool completes = false) : OutputItem
{
    public bool Completes { get; } = completes;

    private string _text = text;

    public DateTime Time { get; } = time;
    public LineLevel Level { get; } = level;
    public IReadOnlyList<string> Details { get; } = details ?? [];
    public string? Hint { get; } = hint;

    /// <summary>Changes only for a progress line, which is rewritten in place.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Raise();
        }
    }
}

/// <summary>A stage of a run: a short name for the stage list, a title for the log, its lines and its times.</summary>
public sealed class OutputStage(string name, string title) : OutputItem
{
    private string _title = title;
    private StageStatus _status = StageStatus.Pending;

    public string Name { get; } = name;

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            Raise();
        }
    }

    public StageStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status), nameof(DurationText), nameof(IsShownInLog), nameof(IsDone), nameof(IsRunning));
        }
    }

    public DateTime? StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }

    public ObservableCollection<OutputLine> Lines { get; } = new();

    public bool IsRunning => _status == StageStatus.Running;

    /// <summary>Has run, or was skipped: it has a place in the log. A stage still to come is only in the stage list.</summary>
    public bool IsShownInLog => _status != StageStatus.Pending;

    /// <summary>Done, one way or another - counted in "STAGES · done/total".</summary>
    public bool IsDone => _status is StageStatus.Ok or StageStatus.Warning;

    public TimeSpan? Duration => StartedAt is { } start ? (EndedAt ?? DateTime.Now) - start : null;

    /// <summary>"0.25s", "1m 02s"; "skipped", "—" for one still to come.</summary>
    public string DurationText => _status switch
    {
        StageStatus.Pending => "—",
        StageStatus.Skipped => "skipped",
        StageStatus.Cancelled => "cancelled",
        _ => Duration is { } d ? OutputFormat.Short(d) : ""
    };

    public void Start(DateTime at)
    {
        StartedAt = at;
        Status = StageStatus.Running;
    }

    /// <summary>Ends it: failed when one of its lines is an error, a warning when one is a warning, else done.</summary>
    public void End(DateTime at, StageStatus? status = null)
    {
        if (_status != StageStatus.Running) return;
        EndedAt = at;
        Status = status ?? (Lines.Any(l => l.Level == LineLevel.Error) ? StageStatus.Failed
            : Lines.Any(l => l.Level == LineLevel.Warn) ? StageStatus.Warning
            : StageStatus.Ok);
    }

    /// <summary>A line came in: a warning or error shows on the stage while it still runs.</summary>
    public void Add(OutputLine line) => Lines.Add(line);

    /// <summary>Time went on: the running stage's duration reads differently.</summary>
    public void Tick() => Raise(nameof(Duration), nameof(DurationText));
}

/// <summary>A figure in a run's summary, e.g. "Files copied" - "4 487 · 148,4 MB".</summary>
public sealed class OutputFact(string name, string value) : OutputItem
{
    private string _value = value;

    public string Name { get; } = name;

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Raise();
        }
    }
}

/// <summary>One operation - "Clone 'a' → 'b'" - from its start to its end.</summary>
public sealed class OutputRun(string title, DateTime startedAt) : OutputItem
{
    private RunStatus _status = RunStatus.Running;
    private string? _link;
    private DateTime? _endedAt;

    public string Title { get; } = title;
    public DateTime StartedAt { get; } = startedAt;

    public ObservableCollection<OutputStage> Stages { get; } = new();

    /// <summary>Lines after its closing step ("Setup complete"): shown after the stages, under no heading.</summary>
    public ObservableCollection<OutputLine> Notes { get; } = new();

    public ObservableCollection<OutputFact> Facts { get; } = new();

    private readonly List<string> _context = new();

    /// <summary>What it works with: "localhost,1433 · SQL Server 16.0.4255.1 · IIS · mysite.dnndev.me".</summary>
    public string Meta => string.Join(" · ", _context);

    public RunStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status), nameof(IsRunning));
        }
    }

    public bool IsRunning => _status == RunStatus.Running;

    public DateTime? EndedAt
    {
        get => _endedAt;
        private set
        {
            _endedAt = value;
            Raise(nameof(EndedAt), nameof(TimeRange), nameof(DurationText));
        }
    }

    /// <summary>Where the result opens - e.g. the new site.</summary>
    public string? Link
    {
        get => _link;
        set
        {
            if (_link == value) return;
            _link = value;
            Raise();
        }
    }

    /// <summary>The message it ended with - why it failed, or that it was cancelled.</summary>
    public string? Outcome { get; private set; }

    public int Warnings => AllLines.Count(l => l.Level == LineLevel.Warn);
    public int Errors => AllLines.Count(l => l.Level == LineLevel.Error);

    /// <summary>"1 error", "2 warnings" - errors only when there are any; null when there is nothing to say.</summary>
    public string? IssueText => Errors > 0 ? Count(Errors, "error") : Warnings > 0 ? Count(Warnings, "warning") : null;

    public bool HasErrors => Errors > 0;

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
    public int Done => Stages.Count(s => s.IsDone);
    public int Total => Stages.Count;
    public int Skipped => Stages.Count(s => s.Status == StageStatus.Skipped);

    /// <summary>The stage it stopped at - failed, or running when it was cancelled.</summary>
    public OutputStage? StoppedAt => Stages.LastOrDefault(s => s.Status is StageStatus.Failed or StageStatus.Cancelled);

    public IEnumerable<OutputLine> AllLines => Stages.SelectMany(s => s.Lines).Concat(Notes);

    public TimeSpan Duration => (_endedAt ?? DateTime.Now) - StartedAt;

    public string DurationText => OutputFormat.Long(Duration);

    public string TimeRange => _endedAt is { } end ? $"{StartedAt:HH:mm:ss} → {end:HH:mm:ss}" : $"{StartedAt:HH:mm:ss} →";

    /// <summary>The stage running now; null between stages.</summary>
    public OutputStage? Current => Stages.FirstOrDefault(s => s.Status == StageStatus.Running);

    /// <summary>After its closing step ("Clone complete"): what follows is a note, not a stage of its own.</summary>
    public bool Closing { get; set; }

    /// <summary>Its closing step's words - "Removal complete" - for its end; null when it had none.</summary>
    public string? ClosingTitle { get; set; }

    public void AddContext(string text)
    {
        if (text.Length == 0 || _context.Contains(text)) return;
        _context.Add(text);
        Raise(nameof(Meta));
    }

    public void SetFact(string name, string value)
    {
        if (Facts.FirstOrDefault(f => f.Name == name) is { } fact) fact.Value = value;
        else Facts.Add(new OutputFact(name, value));
    }

    /// <summary>
    /// Ends it: the running stage ends (failed, or cancelled), and the stages it never got to are skipped.
    /// </summary>
    public void End(DateTime at, RunStatus status, string? outcome)
    {
        Outcome = outcome;
        Current?.End(at, status switch
        {
            RunStatus.Failed => StageStatus.Failed,
            RunStatus.Cancelled => StageStatus.Cancelled,
            _ => null
        });
        foreach (var stage in Stages.Where(s => s.Status == StageStatus.Pending)) stage.Status = StageStatus.Skipped;
        EndedAt = at;
        Status = status;
        Changed();
    }

    /// <summary>A stage or line changed: the counts read differently.</summary>
    public void Changed() => Raise(nameof(Warnings), nameof(Errors), nameof(IssueText), nameof(HasErrors), nameof(Done), nameof(Total),
        nameof(Skipped), nameof(StoppedAt));

    /// <summary>Time went on: a running run's duration (and its running stage's) reads differently.</summary>
    public void Tick()
    {
        Raise(nameof(Duration), nameof(DurationText));
        Current?.Tick();
    }
}

/// <summary>Durations as the Output tab writes them.</summary>
public static class OutputFormat
{
    /// <summary>"0.25s", "42.21s", "1m 02s" - a stage.</summary>
    public static string Short(TimeSpan d) =>
        d < TimeSpan.FromMinutes(1) ? d.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s" : Long(d);

    /// <summary>"57s", "1m 02s", "1h 03m" - a whole run.</summary>
    public static string Long(TimeSpan d) =>
        d < TimeSpan.FromMinutes(1) ? $"{(int)d.TotalSeconds}s"
        : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s"
        : $"{(int)d.TotalHours}h {d.Minutes:00}m";
}
