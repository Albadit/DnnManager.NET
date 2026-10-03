using System.ComponentModel;
using System.Runtime.CompilerServices;
using DnnManager.Application.Abstractions;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Runs one use case at a time on the thread pool (IIS, file copies and SqlPackage block), in its own
/// DI scope, with its output going to the activity log. Only one runs at a time - starting another
/// while one is running is refused - and <see cref="Cancel"/> backs the log's Cancel button. A cancelled
/// operation is taken back: what it noted in its <see cref="OperationUndo"/> is undone, the last first.
/// </summary>
public sealed class OperationRunner : INotifyPropertyChanged
{
    private readonly IServiceProvider _services;
    private readonly ActivityLog _log;
    private readonly IProgressReporter _reporter;
    private readonly ILogger<OperationRunner> _logger;
    private CancellationTokenSource? _cts;
    private string? _current;
    // The operation going on - from its start, also while it only asks whether to go ahead (Current waits for more).
    private string? _title;

    public OperationRunner(IServiceProvider services, ActivityLog log, IProgressReporter reporter, ILogger<OperationRunner> logger)
    {
        _services = services; _log = log; _reporter = reporter; _logger = logger;
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
        OnPropertyChanged(nameof(IsBusy));
        _log.BeginRun(title);
        // Outside the operation, so what it noted to undo is still there when it was cancelled.
        using var scope = _services.CreateScope();
        try
        {
            var result = await Task.Run(() => operation(scope.ServiceProvider, _reporter, cts.Token));

            // Cancelled - also when the operation turned the cancel into an ordinary failure.
            if (!result.Success && cts.IsCancellationRequested) return await UndoAsync(scope, title);
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
            var error = result.Error ?? $"{title} failed.";
            _log.EndRun(RunStatus.Failed, error);
            Failed?.Invoke(title, error);
            return false;
        }
        catch (OperationCanceledException)
        {
            return await UndoAsync(scope, title);
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "{Title} ended with an error after it was cancelled", title);
            return await UndoAsync(scope, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action failed");
            _log.EndRun(RunStatus.Failed, $"Unexpected error: {ex.Message}");
            Failed?.Invoke(title, ex.Message);
            return false;
        }
        finally
        {
            _cts = null;
            _title = null;
            Current = null;
        }
    }

    /// <summary>
    /// After a cancel: takes back what the operation did (<see cref="OperationUndo"/>), each step in the log, so the PC is
    /// as it was before it started. Always false - the operation didn't happen.
    /// </summary>
    private async Task<bool> UndoAsync(IServiceScope scope, string title)
    {
        var undo = scope.ServiceProvider.GetRequiredService<OperationUndo>();
        if (undo.IsEmpty)
        {
            _log.EndRun(RunStatus.Cancelled, $"{title} - cancelled. Nothing had been changed yet.");
            return false;
        }
        Current = $"Undoing '{title}'";
        _reporter.Step("Cancelled - putting everything back as it was", "Undo");
        var allUndone = await Task.Run(() => undo.RunAsync(_reporter));
        _log.EndRun(RunStatus.Cancelled, allUndone
            ? $"{title} - cancelled. Everything it had done is undone."
            : $"{title} - cancelled. Not everything could be undone - see above.");
        return false;
    }

    public void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            _log.Cancelling();
            cts.Cancel();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
