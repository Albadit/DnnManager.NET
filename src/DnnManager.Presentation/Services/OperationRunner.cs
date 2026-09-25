using System.ComponentModel;
using System.Runtime.CompilerServices;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Runs one use case at a time on the thread pool (IIS, file copies and SqlPackage block), in its own
/// DI scope, with its output going to the activity log. Only one runs at a time - starting another
/// while one is running is refused - and <see cref="Cancel"/> backs the log's Cancel button.
/// </summary>
public sealed class OperationRunner : INotifyPropertyChanged
{
    private readonly IServiceProvider _services;
    private readonly ActivityLog _log;
    private readonly IProgressReporter _reporter;
    private readonly ILogger<OperationRunner> _logger;
    private CancellationTokenSource? _cts;
    private string? _current;

    public OperationRunner(IServiceProvider services, ActivityLog log, IProgressReporter reporter, ILogger<OperationRunner> logger)
    {
        _services = services; _log = log; _reporter = reporter; _logger = logger;
    }

    public bool IsBusy => _current is not null;

    /// <summary>Title of the running operation, or null when idle.</summary>
    public string? Current
    {
        get => _current;
        private set { _current = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> and reports its outcome in the log.
    /// Returns true when it succeeded; false when it failed, was cancelled or another one is running.
    /// </summary>
    public async Task<bool> RunAsync(string title,
        Func<IServiceProvider, IProgressReporter, CancellationToken, Task<Result>> operation)
    {
        // Pages stay usable (scrolling, browsing) while an operation runs; only a second one is refused.
        if (IsBusy)
        {
            Dialogs.Error($"'{Current}' is still running - wait for it to finish, or cancel it first.");
            return false;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        Current = title;
        _log.Header(title);
        try
        {
            var result = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                return await operation(scope.ServiceProvider, _reporter, cts.Token);
            });

            if (result.Success) _log.Success($"{title} - finished.");
            else _log.Fail(result.Error ?? $"{title} failed.");
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            _log.Fail($"{title} - cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Action failed");
            _log.Fail($"Unexpected error: {ex.Message}");
            return false;
        }
        finally
        {
            _cts = null;
            Current = null;
        }
    }

    public void Cancel()
    {
        if (_cts is { IsCancellationRequested: false } cts)
        {
            _log.Info("Cancelling…");
            cts.Cancel();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
