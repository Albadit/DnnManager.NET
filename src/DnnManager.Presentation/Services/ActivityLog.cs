using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DnnManager.Infrastructure.Files;

namespace DnnManager.Presentation.Services;

/// <summary>
/// The Output tab's contents: each operation as a <see cref="OutputRun"/> - its stages, their lines and times - and
/// between them the lines that belong to no operation (a site stopped in IIS Manager, SSMS signing in). Use cases run on
/// the thread pool, so every write is marshalled onto the UI thread, in order; the time of each is taken when it is
/// written. Each line also goes to the day's log file (in-place progress lines are left out), and so does background
/// activity (keep warm), which the tab shows elsewhere.
/// </summary>
/// <remarks>
/// The plain-text calls - Header, Step, Info… - are what operations have always used: a step whose title ends in
/// "complete" closes the run's stages, and "Step 3: " is left out of a stage's name.
/// </remarks>
public sealed partial class ActivityLog : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current.Dispatcher;
    private readonly DailyLogFile _file;

    public ActivityLog(DailyLogFile file) => _file = file;

    // The run going on now; null between runs.
    private OutputRun? _run;
    // When a cancel was asked for: the stage running then was cut short.
    private DateTime? _cancelAt;
    // The in-place progress line (e.g. a download percentage) and where it is, until the next line takes its place.
    private OutputLine? _progress;
    private IList<OutputLine>? _progressIn;

    /// <summary>The runs (<see cref="OutputRun"/>) and the lines outside them (<see cref="OutputLine"/>), oldest first.</summary>
    public ObservableCollection<OutputItem> Items { get; } = new();

    /// <summary>The newest run - going on, or the last one that ended. Null before the first, and after Clear.</summary>
    public OutputRun? Latest { get; private set; }

    /// <summary>A run ended - raised on the UI thread.</summary>
    public event Action<OutputRun>? RunEnded;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ─── Runs ─────────────────────────────────────────────────────────────

    /// <summary>An operation begins: what follows is its stages and lines.</summary>
    public void BeginRun(string title) => Post(now =>
    {
        if (_run is not null) EndRunNow(_run, now, RunStatus.Finished, null);
        _cancelAt = null;
        var run = new OutputRun(title, now);
        _run = run;
        Items.Add(run);
        DropOldest();
        SetLatest(run);
        _file.AppendHeading(now, $"# {title} · {now:yyyy-MM-dd HH:mm:ss}");
    });

    // The Output tab keeps the newest runs - over a long day it would only grow (it isn't virtualized); every run is in
    // the day's log file.
    private const int RunsKept = 50;

    /// <summary>Leaves the newest <see cref="RunsKept"/> runs, with the lines between them.</summary>
    private void DropOldest()
    {
        var runs = Items.Count(i => i is OutputRun);
        while (runs > RunsKept && Items.Count > 0)
        {
            if (Items[0] is OutputRun) runs--;
            Items.RemoveAt(0);
        }
    }

    /// <summary>The operation ended - finished, failed (with <paramref name="message"/> as its error) or cancelled.</summary>
    public void EndRun(RunStatus status, string? message) => Post(now =>
    {
        if (_run is { } run) EndRunNow(run, now, status, message);
        else if (message is not null) AddLine(now, status == RunStatus.Failed ? LineLevel.Error : LineLevel.Info, message);
    });

    /// <summary>A cancel was asked for: the stage running now is cut short.</summary>
    public void Cancelling() => Post(now =>
    {
        _cancelAt ??= now;
        AddLine(now, LineLevel.Info, "Cancelling…");
    });

    private void EndRunNow(OutputRun run, DateTime now, RunStatus status, string? message)
    {
        RemoveProgress();
        if (status == RunStatus.Failed && message is not null && !run.AllLines.Any(l => l.Level == LineLevel.Error && l.Text == message))
        {
            var (text, details, hint) = SplitError(message);
            AddLine(now, LineLevel.Error, text, details, hint);
        }
        // What runs after a cancel - putting things back - ends as it went; what ran when it came was cut short.
        if (status == RunStatus.Cancelled && run.Current is { StartedAt: { } started } current && _cancelAt is { } cancel && started >= cancel)
            current.End(now);
        run.End(now, status, message);
        _run = null;
        // How it ended, as the Output tab ends it: [success] with what it says it did, [error] with where it stopped.
        _file.Append(now, status switch
        {
            RunStatus.Finished => $"[success] {run.ClosingTitle ?? run.Title + " finished"} ({run.DurationText})",
            RunStatus.Cancelled => $"[cancelled] {message ?? run.Title + " was cancelled"} ({run.DurationText})",
            _ => $"[error] {(run.StoppedAt is { } at ? $"{run.Title} stopped at {at.Name}" : run.Title + " failed")} ({run.DurationText})"
        });
        RunEnded?.Invoke(run);
    }

    /// <summary>The newest run starts over: the old ones and the lines between them go; one going on stays.</summary>
    public void Clear() => Post(_ =>
    {
        RemoveProgress();
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(Items[i], _run)) Items.RemoveAt(i);
        SetLatest(_run);
    });

    // ─── Stages ───────────────────────────────────────────────────────────

    /// <summary>The run's stages, by their short names - pending until their step comes.</summary>
    public void Plan(IReadOnlyList<string> stages) => Post(_ =>
    {
        if (_run is not { } run) return;
        foreach (var name in stages)
            if (!run.Stages.Any(s => s.Name == name)) run.Stages.Add(new OutputStage(name, name));
        run.Changed();
    });

    public void Step(string title) => Step(title, NameOf(title));

    public void Step(string title, string name) => Post(now =>
    {
        // A stage is a heading - not the closing step ("Removal complete"), which is no stage.
        if (!IsClosing(title)) _file.AppendHeading(now, $"## {StripStepNumber(title)}");
        if (_run is not { } run)
        {
            AddLine(now, LineLevel.Info, title);
            return;
        }
        RemoveProgress();
        EndCurrent(run, now);
        // "Clone complete": no stage of its own - what follows is the run's closing notes.
        if (IsClosing(title))
        {
            run.Closing = true;
            run.ClosingTitle = title;
            run.Changed();
            return;
        }
        run.Closing = false;
        var stage = run.Stages.FirstOrDefault(s => s.Status == StageStatus.Pending && s.Name == name);
        var firstPending = run.Stages.FirstOrDefault(s => s.Status == StageStatus.Pending);
        if (stage is null)
        {
            // Not in the plan: it goes where the run is now - before the stages still to come.
            stage = new OutputStage(name, StripStepNumber(title));
            if (firstPending is null) run.Stages.Add(stage);
            else run.Stages.Insert(run.Stages.IndexOf(firstPending), stage);
        }
        else
        {
            stage.Title = StripStepNumber(title);
            // Planned stages before this one that never started: the operation went past them.
            foreach (var passed in run.Stages.TakeWhile(s => s != stage).Where(s => s.Status == StageStatus.Pending))
                passed.Status = StageStatus.Skipped;
        }
        stage.Start(now);
        run.Changed();
    });

    private void EndCurrent(OutputRun run, DateTime now)
    {
        if (run.Current is not { StartedAt: { } started } current) return;
        current.End(now, _cancelAt is { } cancel && started < cancel ? StageStatus.Cancelled : null);
    }

    // ─── Lines ────────────────────────────────────────────────────────────

    public void Header(string text) => BeginRun(text);
    public void Info(string text) => Line(LineLevel.Info, text);
    public void Success(string text) => Line(LineLevel.Success, text);
    public void Warn(string text) => Line(LineLevel.Warn, text);
    public void Fail(string text) => Fail(text, [], null);

    public void Fail(string text, IReadOnlyList<string> details, string? hint) => Post(now =>
    {
        if (details.Count == 0 && hint is null) (text, details, hint) = SplitError(text);
        AddLine(now, LineLevel.Error, text, details, hint);
    });

    private void Line(LineLevel level, string text) => Post(now => AddLine(now, level, text));

    public void Progress(string text) => Post(now =>
    {
        if (_progress is { } progress)
        {
            progress.Text = text;
            return;
        }
        _progress = new OutputLine(now, LineLevel.Progress, text);
        _progressIn = LinesForNewLine(now);
        if (_progressIn is null) Items.Add(_progress);
        else _progressIn.Add(_progress);
    });

    private void AddLine(DateTime now, LineLevel level, string text, IReadOnlyList<string>? details = null, string? hint = null)
    {
        var completes = level == LineLevel.Success && _progress is not null;
        RemoveProgress();
        var line = new OutputLine(now, level, text, details, hint, completes);
        // In the file: the line as it is - a warning or error marked [warning] / [error]; what lies behind an error,
        // and what to do, under it.
        _file.Append(now, level switch
        {
            LineLevel.Warn => $"[warning] {text}",
            LineLevel.Error => $"[error] {text}",
            _ => text
        });
        foreach (var detail in line.Details) _file.Append(now, $"        {detail}");
        if (hint is not null) _file.Append(now, $"        → {hint}");

        if (LinesForNewLine(now) is { } lines) lines.Add(line);
        else Items.Add(line);
        _run?.Changed();
    }

    /// <summary>
    /// Where a line goes now: the running stage, the run's closing notes - or, before its first step, a stage of its
    /// own. Null outside a run: the line stands on its own.
    /// </summary>
    private IList<OutputLine>? LinesForNewLine(DateTime now)
    {
        if (_run is not { } run) return null;
        if (run.Closing) return run.Notes;
        if (run.Current is { } current) return current.Lines;
        var start = new OutputStage("Start", "Starting");
        var firstPending = run.Stages.FirstOrDefault(s => s.Status == StageStatus.Pending);
        if (firstPending is null) run.Stages.Add(start);
        else run.Stages.Insert(run.Stages.IndexOf(firstPending), start);
        start.Start(now);
        return start.Lines;
    }

    private void RemoveProgress()
    {
        if (_progress is not { } progress) return;
        if (_progressIn is { } lines) lines.Remove(progress);
        else Items.Remove(progress);
        _progress = null;
        _progressIn = null;
    }

    // ─── About the run ────────────────────────────────────────────────────

    public void Context(string text) => Post(_ => _run?.AddContext(text));

    public void Fact(string name, string value) => Post(_ => _run?.SetFact(name, value));

    public void Link(string url) => Post(now =>
    {
        _file.Append(now, $"Open {url}");
        if (_run is { } run) run.Link = url;
    });

    /// <summary>Something going on in the background (keep warm): the log file has it - the tab shows its state elsewhere.</summary>
    public void Background(string text, bool isWarning) => Post(now => _file.Append(now, isWarning ? $"[warning] {text}" : text));

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void SetLatest(OutputRun? run)
    {
        if (ReferenceEquals(Latest, run)) return;
        Latest = run;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Latest)));
    }

    /// <summary>"Step 3: Testing database connection" - "Testing database connection".</summary>
    private static string StripStepNumber(string title) => StepNumber().Replace(title, "");

    private static string NameOf(string title) => StripStepNumber(title).TrimEnd('…', '.', ' ');

    /// <summary>"Clone complete", "Setup complete", "Done": the end of the stages, not one of them.</summary>
    private static bool IsClosing(string title) =>
        title.EndsWith(" complete", StringComparison.OrdinalIgnoreCase) || title.Equals("Done", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An error message of several lines: the first is the error, the others what lies behind it - and one starting
    /// with "→" or "Hint:" what to do about it.
    /// </summary>
    private static (string Text, IReadOnlyList<string> Details, string? Hint) SplitError(string message)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count <= 1) return (message.Trim(), [], null);
        string? hint = null;
        var details = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith('→')) hint = line.TrimStart('→', ' ');
            else if (line.StartsWith("Hint:", StringComparison.OrdinalIgnoreCase)) hint = line[5..].Trim();
            else details.Add(line);
        }
        return (lines[0], details, hint);
    }

    [GeneratedRegex(@"^Step \d+:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex StepNumber();

    /// <summary>What "now" is - another clock in tests.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>
    /// Queues a write for the UI thread - always, also from the UI thread itself: written at once there, it would come
    /// before writes of the operation's thread still in the queue (the end of a run before its last step).
    /// </summary>
    private void Post(Action<DateTime> action)
    {
        // The time it was written, not when the UI thread got to it.
        var now = Clock();
        _dispatcher.InvokeAsync(() => action(now));
    }
}
