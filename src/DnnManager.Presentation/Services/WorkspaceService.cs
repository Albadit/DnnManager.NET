using System.Windows.Threading;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.State;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation.Services;

/// <summary>
/// Keeps the workspace (the states in WorkspaceStates.cs) between starts. The window tells it what to save for each one
/// (<see cref="Track{T}"/>) and when something changed (<see cref="Changed"/>); it saves a moment later - at most every
/// couple of seconds, and only the states whose content changed - so a crash loses little, and saves at once
/// (<see cref="SaveNow"/>) when DNN Manager closes, restarts or updates. Reading is <see cref="Load{T}"/>.
/// </summary>
public sealed class WorkspaceService
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private readonly ILogger<WorkspaceService> _log;
    private readonly List<(string Name, Action Save)> _parts = [];
    private readonly DispatcherTimer _timer;
    private bool _stopped;

    public WorkspaceService(AppDatabase database, ILogger<WorkspaceService> log)
    {
        _log = log;
        Store = new StateStore(database, log);
        _timer = new DispatcherTimer(Delay, DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Stop();
    }

    public StateStore Store { get; }

    public T Load<T>() where T : class, IStateFile, new() => Store.Load<T>();

    /// <summary>Saves <typeparamref name="T"/> with what <paramref name="capture"/> returns now - each time the workspace is saved.</summary>
    public void Track<T>(Func<T> capture) where T : class, IStateFile =>
        _parts.Add((T.Area, () => Store.Save(capture())));

    /// <summary>Something the workspace holds changed: it is saved in a moment (once for a burst of changes).</summary>
    public void Changed()
    {
        if (!_stopped && !_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Saves everything now. With <paramref name="last"/> (DNN Manager is closing) nothing is saved after it.</summary>
    public void SaveNow(bool last = false)
    {
        _timer.Stop();
        if (_stopped) return;
        _stopped = last;
        foreach (var (name, save) in _parts)
        {
            // One part that can't be captured doesn't keep the others from being saved.
            try { save(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not save the workspace's {File}", name); }
        }
    }

    /// <summary>
    /// Forgets the workspace - a factory reset: its files go, and nothing is saved again (not even as DNN Manager closes)
    /// unless <see cref="Resume"/> is called. The next start opens as a first one.
    /// </summary>
    public void Forget()
    {
        _timer.Stop();
        _stopped = true;
        Store.Clear();
    }

    /// <summary>Saves again after <see cref="Forget"/> - the restart that was to follow it didn't happen.</summary>
    public void Resume() => _stopped = false;

    private void Tick()
    {
        _timer.Stop();
        SaveNow();
    }
}
