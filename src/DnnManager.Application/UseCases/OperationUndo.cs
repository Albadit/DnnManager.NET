using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Application.UseCases;

/// <summary>
/// What an operation changed, and how to take it back - so a cancelled operation leaves the PC as it found it. One per
/// operation (scoped): each step that is about to create something (a folder, an IIS site, a database, a file) adds how
/// to remove it <b>before</b> it starts, so a step cut off half-way is taken back too; the removals don't mind what isn't
/// there. What can't be taken back (a database that was replaced, a site that was removed) is said so with
/// <see cref="CannotUndo"/>. The operation runner calls <see cref="RunAsync"/> when an operation was cancelled.
/// </summary>
public sealed class OperationUndo
{
    private readonly List<(string What, Func<Task<Result>> Undo)> _steps = [];
    private readonly List<string> _lost = [];
    private readonly HashSet<string> _keptFiles = new(StringComparer.OrdinalIgnoreCase);
    // A file is kept in memory to be put back - a configuration file, not a backup.
    private const long MaxKeptFile = 10 * 1024 * 1024;

    /// <summary>Nothing was changed yet - or nothing that could be taken back.</summary>
    public bool IsEmpty => _steps.Count == 0 && _lost.Count == 0;

    /// <summary>On undo: <paramref name="undo"/>, said as <paramref name="what"/> (e.g. "Remove the IIS site 'shop'").</summary>
    public void Add(string what, Func<Task<Result>> undo) => _steps.Add((what, undo));

    /// <summary>On undo: <paramref name="undo"/>, said as <paramref name="what"/>.</summary>
    public void Add(string what, Func<Result> undo) => _steps.Add((what, () => Task.FromResult(undo())));

    /// <summary>Something the operation changed that can't be taken back - said when undoing, so nobody counts on it.</summary>
    public void CannotUndo(string what) => _lost.Add(what);

    /// <summary>
    /// What was done so far stays, even when the operation is cancelled later: the steps added until now are forgotten -
    /// e.g. a backup made before a change, which the change's own undo needs.
    /// </summary>
    public void Keep()
    {
        _steps.Clear();
        _lost.Clear();
    }

    /// <summary>
    /// On undo: <paramref name="folder"/> is deleted, with everything in it - for a folder this operation makes, called
    /// before it is made. A folder that is already there isn't the operation's to delete: nothing happens then.
    /// </summary>
    public void DeleteFolderOnUndo(string folder)
    {
        if (Directory.Exists(folder)) return;
        Add($"Delete {folder}", () => DeleteFolderAsync(folder));
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
            Add($"Delete {file}", () =>
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
            });
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
        Add($"Put {Path.GetFileName(file)} back as it was", () =>
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
        });
    }

    /// <summary>
    /// Takes back everything added, the last first - each step said in <paramref name="reporter"/>, a failed one not
    /// stopping the rest - then names what couldn't be. Not cancellable: it is what a cancel does. False when a step failed.
    /// </summary>
    public async Task<bool> RunAsync(IProgressReporter reporter)
    {
        var allDone = true;
        for (var i = _steps.Count - 1; i >= 0; i--)
        {
            var (what, undo) = _steps[i];
            Result result;
            try { result = await undo(); }
            catch (Exception ex) { result = Result.Fail(ex.Message); }
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
        _steps.Clear();
        _lost.Clear();
        return allDone;
    }

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
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
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
