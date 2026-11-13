using Avalonia.Threading;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.State;
using Microsoft.Extensions.Logging;

namespace DnnManager.Desktop.Services;

/// <summary>
/// Keeps the workspace (Presentation/Services/WorkspaceStates.cs - the window, the side bars, the panel, the page) between
/// starts, in the same database table as the Windows app's (src/DnnManager.Presentation/Services/WorkspaceService.cs). The
/// window says what to save (<see cref="Track{T}"/>) and when something changed (<see cref="Changed"/>); it is saved a
/// moment later, and at once as DNN Manager closes, restarts or updates (<see cref="SaveNow"/>).
/// </summary>
public sealed class WorkspaceService
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private readonly ILogger<WorkspaceService> _log;
    private readonly List<(string Name, Action Save)> _parts = [];
    private DispatcherTimer? _timer;
    private bool _stopped;

    public WorkspaceService(AppDatabase database, ILogger<WorkspaceService> log)
    {
        _log = log;
        Store = new StateStore(database, log);
    }

    public StateStore Store { get; }

    public T Load<T>() where T : class, IStateFile, new() => Store.Load<T>();

    public void Track<T>(Func<T> capture) where T : class, IStateFile => _parts.Add((T.Area, () => Store.Save(capture())));

    /// <summary>Something the workspace holds changed: saved in a moment (once for a burst of changes).</summary>
    public void Changed()
    {
        if (_stopped) return;
        _timer ??= new DispatcherTimer(Delay, DispatcherPriority.Background, (_, _) => SaveNow());
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Saves everything now; with <paramref name="last"/> (closing) nothing is saved after it.</summary>
    public void SaveNow(bool last = false)
    {
        _timer?.Stop();
        if (_stopped) return;
        _stopped = last;
        foreach (var (name, save) in _parts)
        {
            try { save(); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not save the workspace's {File}", name); }
        }
    }

    /// <summary>A factory reset: the workspace goes, nothing is saved again until <see cref="Resume"/>.</summary>
    public void Forget()
    {
        _timer?.Stop();
        _stopped = true;
        Store.Clear();
    }

    public void Resume() => _stopped = false;
}
