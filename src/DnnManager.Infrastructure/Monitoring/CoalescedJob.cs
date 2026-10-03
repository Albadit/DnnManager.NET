namespace DnnManager.Infrastructure.Monitoring;

/// <summary>
/// A piece of background work that may be asked for many times in a row (a file watcher fires a burst of events) but
/// only needs to run once for all of them: requests while it runs are folded into one more run, and it never runs
/// twice at the same time - so what it reads and what it then applies can't overtake each other. A short wait before
/// each run lets a burst settle first.
/// </summary>
/// <param name="settle">How long to wait before a run starts, so requests arriving together share it.</param>
/// <param name="failed">Called when the work throws - the job carries on.</param>
internal sealed class CoalescedJob(Func<CancellationToken, Task> work, TimeSpan settle, Action<Exception> failed, CancellationToken stop)
{
    private readonly Func<CancellationToken, Task> _work = work;
    private readonly TimeSpan _settle = settle;
    private readonly Action<Exception> _failed = failed;
    private readonly CancellationToken _stop = stop;
    private readonly object _lock = new();
    private bool _running, _again, _atOnce;
    // Callers waiting for a run that starts after they asked.
    private List<TaskCompletionSource> _waiters = [];

    /// <summary>Asks for a run; returns at once.</summary>
    public void Request()
    {
        lock (_lock)
        {
            if (_running) { _again = true; return; }
            _running = true;
        }
        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Asks for a run - without the settling wait: someone is waiting for it - and completes when a run that started
    /// after this call has finished.
    /// </summary>
    public Task RunNowAsync()
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _waiters.Add(waiter);
            _atOnce = true;
            if (_running) { _again = true; return waiter.Task; }
            _running = true;
        }
        _ = Task.Run(RunAsync);
        return waiter.Task;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            bool atOnce;
            lock (_lock) atOnce = _atOnce;
            try
            {
                if (!atOnce && _settle > TimeSpan.Zero) await Task.Delay(_settle, _stop);
            }
            catch (OperationCanceledException) { }

            List<TaskCompletionSource> waiters;
            lock (_lock)
            {
                // Everything asked for up to here is covered by the run that starts now.
                _again = _atOnce = false;
                waiters = _waiters;
                _waiters = [];
            }

            try
            {
                if (!_stop.IsCancellationRequested) await _work(_stop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _failed(ex); }
            foreach (var waiter in waiters) waiter.TrySetResult();

            lock (_lock)
            {
                if (_again && !_stop.IsCancellationRequested) continue;
                _running = false;
                waiters = _waiters;
                _waiters = [];
            }
            // Only when stopping: nobody is left to run for them.
            foreach (var waiter in waiters) waiter.TrySetResult();
            return;
        }
    }
}
