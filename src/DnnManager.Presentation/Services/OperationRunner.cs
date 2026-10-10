using System.ComponentModel;
using System.Runtime.CompilerServices;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Runs one use case at a time on the thread pool (IIS, file copies and SqlPackage block), in its own
/// DI scope, with its output going to the activity log. Only one runs at a time - starting another
/// while one is running is refused - and <see cref="Cancel"/> backs the log's Cancel button. A cancelled or failed
/// operation is taken back: what it noted in its <see cref="OperationUndo"/> is undone, the last first. While it runs, what
/// it would undo is kept in DNN Manager's database (<see cref="UnfinishedOperation"/>): should DNN Manager end before it
/// does (a crash, Windows shutting down, Quit now), the next start says what was left half done.
/// </summary>
public sealed class OperationRunner : INotifyPropertyChanged
{
    private readonly IServiceProvider _services;
    private readonly ActivityLog _log;
    private readonly IProgressReporter _reporter;
    private readonly ILogger<OperationRunner> _logger;
    private readonly StateStore _journal;
    private CancellationTokenSource? _cts;
    // While an operation is being undone: cancelled to stop the undo (a second Cancel, Quit now).
    private CancellationTokenSource? _skipUndo;
    private string? _current;
    // The operation going on - from its start, also while it only asks whether to go ahead (Current waits for more).
    private string? _title;
    // Done when no operation runs - what quitting waits for, so an operation is never cut off half-way.
    private TaskCompletionSource _idle = Done();

    private static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    public OperationRunner(IServiceProvider services, ActivityLog log, IProgressReporter reporter, ILogger<OperationRunner> logger,
        AppDatabase database)
    {
        _services = services; _log = log; _reporter = reporter; _logger = logger;
        _journal = new StateStore(database, logger);
        // Under way (its first step or line): now the status bar and the pages see it.
        _log.RunShown += () => { if (_title is not null && _current is null) Current = _title; };
    }

    /// <summary>An operation is going on - also while it is still asking whether to go ahead: a second one waits.</summary>
    public bool IsBusy => _title is not null;

    /// <summary>
    /// Title of the running operation, or null when idle - and while it is still asking whether to go ahead (Remove,
    /// Stop IIS…): until it does something, nothing says it runs.
    /// </summary>
    public string? Current
    {
        get => _current;
        private set { _current = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); }
    }

    /// <summary>
    /// An operation failed - its title and what went wrong. Not raised when it was cancelled, or when the user said
    /// no to a question it asked. Raised on the thread <see cref="RunAsync"/> was called on (the UI thread).
    /// </summary>
    public event Action<string, string>? Failed;

    /// <summary>
    /// Completes when no operation runs any more - at once when none does; otherwise once the running one has finished,
    /// including what it undoes after a cancel or a failure.
    /// </summary>
    public Task WhenIdleAsync() => _idle.Task;

    /// <summary>The running operation is being undone (after a cancel or a failure) - <see cref="SkipUndo"/> stops that.</summary>
    public bool IsUndoing => _skipUndo is not null;

    /// <summary>
    /// The operation the last DNN Manager didn't see to its end, with what it had left to undo - once: it is forgotten as
    /// it is read. Null when every operation ended. Called at start, before an operation runs.
    /// </summary>
    public UnfinishedOperation? TakeUnfinished()
    {
        var record = _journal.Load<UnfinishedOperation>();
        if (record.Title is null) return null;
        _journal.Delete<UnfinishedOperation>();
        return record;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> and reports its outcome in the log.
    /// Returns true when it succeeded; false when it failed, was cancelled or another one is running.
    /// </summary>
    public async Task<bool> RunAsync(string title,
        Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result>> operation)
    {
        // Pages stay usable (scrolling, browsing) while an operation runs; only a second one is refused - said, not asked.
        if (IsBusy)
        {
            Toast.Show($"'{_title}' is still running - wait for it to finish, or cancel it first.", ToastKind.Warning);
            return false;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        _title = title;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OnPropertyChanged(nameof(IsBusy));
        _log.BeginRun(title);
        // Outside the operation, so what it noted to undo is still there when it was cancelled.
        using var scope = _services.CreateScope();
        // Kept as it changes, so a DNN Manager that ends before the operation does leaves word of it for the next start.
        var undo = scope.ServiceProvider.GetRequiredService<OperationUndo>();
        var startedUtc = DateTime.UtcNow;
        void Record() => _journal.Save(new UnfinishedOperation { Title = title, StartedUtc = startedUtc, Left = [.. undo.Pending] });
        Record();
        undo.Changed += Record;
        try
        {
            var result = await Task.Run(() => operation(scope.ServiceProvider, _reporter, cts.Token));

            // Cancelled - also when the operation turned the cancel into an ordinary failure.
            if (!result.Success && cts.IsCancellationRequested) return await UndoAsync(scope, title, null);
            if (result.Success)
            {
                _log.EndRun(RunStatus.Finished, null);
                return true;
            }
            // Said no to: it didn't happen - no error, and before it did anything no trace at all.
            if (result.IsAborted)
            {
                _log.DropRun();
                return false;
            }
            return await UndoAsync(scope, title, result.Error ?? $"{title} failed.");
        }
        catch (OperationCanceledException)
        {
            return await UndoAsync(scope, title, null);
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "{Title} ended with an error after it was cancelled", title);
            return await UndoAsync(scope, title, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action failed");
            return await UndoAsync(scope, title, $"Unexpected error: {ex.Message}");
        }
        finally
        {
            undo.Changed -= Record;
            // Seen to its end - undone or not, the Output tab has said so.
            _journal.Delete<UnfinishedOperation>();
            _cts = null;
            _title = null;
            Current = null;
            _idle.TrySetResult();
        }
    }

    /// <summary>
    /// After a cancel, or a failure (<paramref name="error"/>): takes back what the operation did
    /// (<see cref="OperationUndo"/>), each step in the log, so the PC is as it was before it started - nothing half made
    /// is left behind to get in the way of trying again. Always false - the operation didn't happen.
    /// </summary>
    private async Task<bool> UndoAsync(IServiceScope scope, string title, string? error)
    {
        var undo = scope.ServiceProvider.GetRequiredService<OperationUndo>();
        if (undo.IsEmpty)
        {
            if (error is null)
            {
                _log.EndRun(RunStatus.Cancelled, $"{title} - cancelled. Nothing had been changed yet.");
            }
            else
            {
                _log.EndRun(RunStatus.Failed, error);
                Failed?.Invoke(title, error);
            }
            return false;
        }
        Current = $"Undoing '{title}'";
        _reporter.Step(error is null ? "Cancelled - putting everything back as it was" : "Failed - putting back what it had done", "Undo");
        bool allUndone;
        using var skip = new CancellationTokenSource();
        _skipUndo = skip;
        try
        {
            allUndone = await Task.Run(() => undo.RunAsync(_reporter, skip.Token));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Undoing {Title} failed", title);
            allUndone = false;
        }
        finally
        {
            _skipUndo = null;
        }
        if (error is null)
        {
            _log.EndRun(RunStatus.Cancelled, allUndone
                ? $"{title} - cancelled. Everything it had done is undone."
                : $"{title} - cancelled. Not everything could be undone - see above.");
            return false;
        }
        var failed = allUndone
            ? $"{error} What it had done is undone - you can try again."
            : $"{error} Not everything it had done could be undone - see the Output tab.";
        _log.EndRun(RunStatus.Failed, failed);
        Failed?.Invoke(title, failed);
        return false;
    }

    /// <summary>Cancels the running operation - and, pressed again while it is being undone, stops the undo (<see cref="SkipUndo"/>).</summary>
    public void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            _log.Cancelling();
            cts.Cancel();
        }
        else if (IsUndoing)
        {
            SkipUndo();
        }
    }

    /// <summary>
    /// Stops undoing the running operation: the step under way is given up on, the ones left are said as not undone - for
    /// an undo that waits on something that doesn't answer, and for quitting now.
    /// </summary>
    public void SkipUndo()
    {
        if (_skipUndo is { IsCancellationRequested: false } skip)
        {
            _reporter.Warn("Stopping the undo - what isn't undone yet is left as it is, and said below.");
            skip.Cancel();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The operation running now, and what it would undo - written as it changes, deleted once it has ended. Still there at a
/// start, DNN Manager ended before the operation did (a crash, Windows shutting down, Quit now): what it lists may be half
/// made, and is said so. Not part of the workspace (WorkspaceStates.cs) - only the runner writes it.
/// </summary>
public sealed class UnfinishedOperation : IStateFile
{
    public static string Area => "operation";

    public string? Title { get; set; }
    public DateTime StartedUtc { get; set; }
    /// <summary>What it had left to undo, each as the Output tab would say it - the last made first.</summary>
    public List<string> Left { get; set; } = [];
}
