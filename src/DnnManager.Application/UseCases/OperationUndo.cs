using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// What an operation changed, and how to take it back - so a cancelled operation leaves the PC as it found it. One per
/// operation (scoped): each step that is about to create something (a folder, an IIS site, a database, a file) adds how
/// to remove it <b>before</b> it starts, so a step cut off half-way is taken back too; the removals don't mind what isn't
/// there. What can't be taken back (a database that was replaced, a site that was removed) is said so with
/// <see cref="CannotUndo"/>. The operation runner calls <see cref="RunAsync"/> when an operation was cancelled, and when it
/// failed: a failed operation leaves nothing half made behind, unless it called <see cref="Keep"/> to leave what it did
/// to look into (a DNN installation that failed). Each step has a time of its own (<see cref="StepLimit"/>) and the whole
/// undo one too (<see cref="TotalLimit"/>): a SQL Server or an IIS that doesn't answer can't keep it - or quitting - from
/// ever ending; what isn't done in time is said as not undone.
/// </summary>
public sealed class OperationUndo
{
    /// <summary>How long a step may take - a database dropped, a site removed, a login dropped.</summary>
    public static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);

    /// <summary>How long a step that only deletes or writes files may take.</summary>
    public static readonly TimeSpan FileStepLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long the whole undo may take - longer only for a step that has a longer time of its own.</summary>
    public static readonly TimeSpan TotalLimit = TimeSpan.FromMinutes(5);

    // How long a step whose time is up gets to end once its token is cancelled.
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    private readonly List<(string What, Func<CancellationToken, Task<Result>> Undo, TimeSpan Limit)> _steps = [];
    private readonly List<string> _lost = [];
    private readonly HashSet<string> _keptFiles = new(StringComparer.OrdinalIgnoreCase);
    // A file is kept in memory to be put back - a configuration file, not a backup.
    private const long MaxKeptFile = 10 * 1024 * 1024;
    // Undoing: what the undo steps themselves do (a backup put back) isn't one more thing to undo.
    private bool _running;

    /// <summary>Nothing was changed yet - or nothing that could be taken back.</summary>
    public bool IsEmpty => _steps.Count == 0 && _lost.Count == 0;

    /// <summary>
    /// What is left to undo now, each as it would be said - the steps, the last first, then what can't be taken back.
    /// Kept by the runner where the next start finds it, should DNN Manager end before the operation does.
    /// </summary>
    public IReadOnlyList<string> Pending =>
        [.. Enumerable.Range(0, _steps.Count).Select(i => _steps[_steps.Count - 1 - i].What), .. _lost.Select(l => $"Can't be put back: {l}")];

    /// <summary>What there is to undo changed (a step added, all kept, the undo done) - see <see cref="Pending"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// On undo: <paramref name="undo"/>, said as <paramref name="what"/> (e.g. "Drop database [shop]") - given a token that
    /// is cancelled after <paramref name="limit"/> (<see cref="StepLimit"/> when none), or when the undo is stopped.
    /// </summary>
    public void Add(string what, Func<CancellationToken, Task<Result>> undo, TimeSpan? limit = null)
    {
        if (_running) return;
        _steps.Add((what, undo, limit ?? StepLimit));
        Changed?.Invoke();
    }

    /// <summary>
    /// On undo: <paramref name="undo"/>, said as <paramref name="what"/> (e.g. "Remove the IIS site 'shop'"). It can't be
    /// cancelled: after <see cref="StepLimit"/> the undo goes on without it, and says so.
    /// </summary>
    public void Add(string what, Func<Task<Result>> undo) => Add(what, _ => undo());

    /// <summary>On undo: <paramref name="undo"/>, said as <paramref name="what"/> - on the thread pool, so it too has its time.</summary>
    public void Add(string what, Func<Result> undo) => Add(what, _ => Task.Run(undo));

    /// <summary>Something the operation changed that can't be taken back - said when undoing, so nobody counts on it.</summary>
    public void CannotUndo(string what)
    {
        if (_running) return;
        _lost.Add(what);
        Changed?.Invoke();
    }

    /// <summary>
    /// What was done so far stays, even when the operation is cancelled or fails later: the steps added until now are
    /// forgotten - e.g. a backup made before a change, which the change's own undo needs, or a failed installation left
    /// to look into.
    /// </summary>
    public void Keep()
    {
        _steps.Clear();
        _lost.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// On undo: <paramref name="folder"/> is deleted, with everything in it - for a folder this operation makes, called
    /// before it is made. A folder that is already there isn't the operation's to delete: nothing happens then.
    /// </summary>
    public void DeleteFolderOnUndo(string folder)
    {
        if (Directory.Exists(folder)) return;
        Add($"Delete {folder}", _ => DeleteFolderAsync(folder), FileStepLimit);
    }

    /// <summary>
    /// On undo: <paramref name="file"/> gets back what it holds now, or is deleted when it isn't there yet - called before
    /// the operation changes or writes it. Only the first call for a file counts: that is how it was before.
    /// </summary>
    public void RestoreFileOnUndo(string file)
    {
        if (!_keptFiles.Add(Path.GetFullPath(file))) return;
        if (!File.Exists(file))
        {
            Add($"Delete {file}", _ => Task.Run(() =>
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    return Result.Ok();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Result.Fail(ex.Message);
                }
            }), FileStepLimit);
            return;
        }
        if (new FileInfo(file).Length > MaxKeptFile)
        {
            CannotUndo($"{file} was overwritten.");
            return;
        }
        byte[] before;
        try { before = File.ReadAllBytes(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CannotUndo($"{file} (could not keep a copy of it: {ex.Message})");
            return;
        }
        Add($"Put {Path.GetFileName(file)} back as it was", _ => Task.Run(() =>
        {
            try
            {
                File.WriteAllBytes(file, before);
                return Result.Ok();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result.Fail(ex.Message);
            }
        }), FileStepLimit);
    }

    /// <summary>
    /// Takes back everything added, the last first - each step said in <paramref name="reporter"/>, a failed one not
    /// stopping the rest - then names what couldn't be. Not cancelled by the operation's cancel: it is what a cancel does.
    /// Each step has its time, the whole undo <see cref="TotalLimit"/>; <paramref name="skip"/> (a second Cancel, quitting
    /// now) stops it - the step under way is given up on, and the ones left are said as not undone. False when a step
    /// failed, wasn't done, or something couldn't be taken back.
    /// </summary>
    public async Task<bool> RunAsync(IProgressReporter reporter, CancellationToken skip = default)
    {
        var allDone = true;
        _running = true;
        var total = _steps.Count == 0 ? TotalLimit : TimeSpan.FromTicks(Math.Max(TotalLimit.Ticks, _steps.Max(s => s.Limit.Ticks)));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(skip);
        stop.CancelAfter(total);
        try
        {
            for (var i = _steps.Count - 1; i >= 0; i--)
            {
                var (what, undo, limit) = _steps[i];
                if (stop.IsCancellationRequested)
                {
                    allDone = false;
                    reporter.Warn($"Not undone: {what}");
                    continue;
                }
                var result = await RunStepAsync(undo, limit, stop.Token, skip, total);
                if (result.Success)
                {
                    reporter.Success(what);
                }
                else
                {
                    allDone = false;
                    reporter.Fail($"{what}: {result.Error}");
                }
            }
            foreach (var lost in _lost) reporter.Warn($"Can't be put back: {lost}");
            if (_lost.Count > 0) allDone = false;
        }
        finally
        {
            _running = false;
            _steps.Clear();
            _lost.Clear();
            Changed?.Invoke();
        }
        return allDone;
    }

    /// <summary>
    /// One step, given up on after <paramref name="limit"/> or once <paramref name="stop"/> is cancelled - also a step that
    /// ignores its token: it is left to end by itself, and what it does then isn't counted on.
    /// </summary>
    private static async Task<Result> RunStepAsync(Func<CancellationToken, Task<Result>> undo, TimeSpan limit, CancellationToken stop,
        CancellationToken skip, TimeSpan total)
    {
        using var step = CancellationTokenSource.CreateLinkedTokenSource(stop);
        step.CancelAfter(limit);
        Task<Result> work;
        try { work = undo(step.Token); }
        catch (Exception ex) { return Result.Fail(ex.Message); }

        var gaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (step.Token.Register(() => gaveUp.TrySetResult()))
        {
            if (await Task.WhenAny(work, gaveUp.Task) == work)
            {
                try { return await work; }
                catch (OperationCanceledException) { /* its time was up: said below, like a step that ignored it */ }
                catch (Exception ex) { return Result.Fail(ex.Message); }
            }
        }
        // A moment for one that honours its token to end (a program it ran ended with it) - not more: it may ignore it.
        await Task.WhenAny(work, Task.Delay(CancelGrace));
        // Left running: a failure it ends with later is nobody's to see - but not an unobserved task either.
        _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        if (skip.IsCancellationRequested) return Result.Fail("not done - the undo was stopped.");
        return Result.Fail(stop.IsCancellationRequested
            ? $"not done - the undo took longer than {Duration(total)} in all."
            : $"didn't finish within {Duration(limit)} - left as it is.");
    }

    private static string Duration(TimeSpan time) =>
        time.TotalSeconds < 120 ? $"{time.TotalSeconds:0} seconds" : $"{time.TotalMinutes:0} minutes";

    /// <summary>
    /// Deletes <paramref name="folder"/> and everything in it - read-only files too - trying again for a few seconds
    /// while something (a worker process that is just exiting, Windows Search) still holds a file.
    /// </summary>
    private static async Task<Result> DeleteFolderAsync(string folder)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return Result.Ok();
                // Not into a link or junction in it (an app pool can make one): with administrator rights the attributes
                // of whatever it points to would be changed. Directory.Delete only unlinks one.
                foreach (var file in Directory.EnumerateFiles(folder, "*", SafePath.Recursive))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, recursive: true);
                return Result.Ok();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10) return Result.Fail($"{ex.Message} Delete the folder yourself once nothing uses it.");
                await Task.Delay(500);
            }
        }
    }
}
