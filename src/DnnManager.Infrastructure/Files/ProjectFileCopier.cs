using System.IO.Compression;
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

    public Task<Result> ExtractZipAsync(string zipPath, string destinationDirectory, IProgressReporter reporter, CancellationToken ct)
        => Task.Run(() => ExtractZip(zipPath, destinationDirectory, reporter, ct), ct);

    private static Result ExtractZip(string zipPath, string dest, IProgressReporter reporter, CancellationToken ct)
    {
        if (!File.Exists(zipPath)) return Result.Fail($"Zip file not found: {zipPath}");

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { return Result.Fail($"Not a valid .zip file: {zipPath}"); }

        using (zip)
        {
            // Some zip tools write '\' separators; directory entries end with a separator and hold nothing.
            static string Normalized(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');
            var files = zip.Entries.Where(e => !Normalized(e).EndsWith('/')).ToList();
            if (files.Count == 0) return Result.Fail("The zip is empty.");

            var webConfig = files
                .Where(e => e.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => Normalized(e).Count(c => c == '/'))
                .FirstOrDefault();
            var root = webConfig is null ? "" : Normalized(webConfig)[..^webConfig.Name.Length];
            if (webConfig is null)
                reporter.Info("No web.config in the zip - extracting it as it is. It may not be a DNN site.");
            else if (root.Length > 0)
                reporter.Info($"Site root in the zip: {root}");

            var destRoot = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var progress = new ProgressThrottle();
            int count = 0, skipped = 0;
            var bytes = 0L;

            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = Normalized(entry);

                if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                // An entry like "../../evil.dll" must not land outside the project ("zip slip").
                var target = Path.GetFullPath(Path.Combine(dest, name[root.Length..]));
                if (!target.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                    return Result.Fail($"The zip contains an unsafe path: {entry.FullName}");

                var dir = Path.GetDirectoryName(target)!;
                if (createdDirs.Add(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(target, overwrite: true);
                count++;
                bytes += entry.Length;
                if (progress.Due()) reporter.Progress($"{count}/{files.Count}  {name}");
            }

            if (skipped > 0) reporter.Info($"Skipped {skipped} file(s) outside the site root.");
            reporter.Success($"Extracted {count} files ({bytes / 1024d / 1024d:N1} MB).");
            return Result.Ok();
        }
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
