using DnnManager.Application.Configuration;
using DnnManager.Domain;

namespace DnnManager.Application.Abstractions;

/// <summary>Finds and closes the programs that keep a folder from being deleted.</summary>
public interface IFileLockService
{
    /// <summary>
    /// Processes with files under <paramref name="directory"/> open, or with their working folder inside it.
    /// A helper process is reported as the app that owns it (e.g. a VS Code helper as VS Code).
    /// </summary>
    IReadOnlyList<LockingProcess> FindLockers(string directory);

    /// <summary>
    /// Asks each process to close, then force-closes it (with its child processes) if it doesn't within a few
    /// seconds. Returns the ones that are still running.
    /// </summary>
    Task<IReadOnlyList<LockingProcess>> CloseAsync(IReadOnlyList<LockingProcess> processes, CancellationToken ct);

    /// <summary>Has Windows delete whatever is left of <paramref name="directory"/> at the next restart.</summary>
    Result ScheduleDeleteOnRestart(string directory);

    /// <summary>
    /// Ends the programs that run from an exe inside <paramref name="directory"/> - e.g. the C# compiler ASP.NET starts
    /// from a site's bin\roslyn, which keeps running after the site stopped and holds its files. The names of those ended.
    /// </summary>
    Task<IReadOnlyList<string>> StopProgramsRunningFromAsync(string directory);
}
