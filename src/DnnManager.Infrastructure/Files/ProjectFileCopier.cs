using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Infrastructure.Files;

public sealed class ProjectFileCopier : IProjectFileCopier
{
    public Task<Result> CopyAsync(string sourceDirectory, string destinationDirectory,
        IProgressReporter reporter, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);
        return Task.FromResult(CopyLocal(sourceDirectory, destinationDirectory, reporter, ct));
    }

    private static Result CopyLocal(string src, string dest, IProgressReporter reporter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(src)) return Result.Fail("Local source path is empty.");
        if (!Directory.Exists(src)) return Result.Fail($"Source folder does not exist: {src}");

        reporter.Info($"Copying files from {src}");
        // Enumerating FileInfo (rather than paths) carries each size over from the directory scan,
        // so the byte total costs no extra file-system calls.
        var files = new DirectoryInfo(src).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var total = files.Count;
        var progress = new ProgressThrottle();
        // One CreateDirectory per distinct destination folder instead of one per file.
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        var byteCount = 0L;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src, file.FullName);
            var destFile = Path.Combine(dest, rel);
            var destDir = Path.GetDirectoryName(destFile)!;
            if (createdDirs.Add(destDir)) Directory.CreateDirectory(destDir);
            file.CopyTo(destFile, overwrite: true);
            fileCount++;
            byteCount += file.Length;
            if (progress.Due()) reporter.Progress($"{fileCount}/{total}  {rel}");
        }
        reporter.Success($"Copied {fileCount} files ({byteCount / 1024d / 1024d:N1} MB).");
        return Result.Ok();
    }

    /// <summary>
    /// Rate-limits status-line updates to roughly ten a second. Every
    /// <see cref="IProgressReporter.Progress"/> call updates the activity log on the UI thread, which on a
    /// site with tens of thousands of small files costs considerably more than the copy itself.
    /// Check <see cref="Due"/> before building the message so skipped updates cost nothing at all.
    /// </summary>
    private sealed class ProgressThrottle
    {
        private const long IntervalMs = 100;
        private long _nextTicks;

        public bool Due()
        {
            var now = Environment.TickCount64;
            if (now < _nextTicks) return false;
            _nextTicks = now + IntervalMs;
            return true;
        }
    }
}
