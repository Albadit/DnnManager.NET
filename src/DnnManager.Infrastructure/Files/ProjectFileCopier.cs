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
        reporter.Success($"Copied {fileCount:N0} files ({byteCount / 1024d / 1024d:N1} MB).");
        reporter.Fact("Files copied", $"{fileCount:N0} · {byteCount / 1024d / 1024d:N1} MB");
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
            reporter.Success($"Extracted {count:N0} files ({bytes / 1024d / 1024d:N1} MB).");
            reporter.Fact("Files extracted", $"{count:N0} · {bytes / 1024d / 1024d:N1} MB");
            return Result.Ok();
        }
    }

    public Task<Result<int>> RemoveFilesNotInZipAsync(string zipPath, string directory, IReadOnlyCollection<string> excludedPaths,
        IProgressReporter reporter, CancellationToken ct)
        => Task.Run(() => RemoveFilesNotInZip(zipPath, directory, excludedPaths, reporter, ct), ct);

    private static Result<int> RemoveFilesNotInZip(string zipPath, string directory, IReadOnlyCollection<string> excludedPaths,
        IProgressReporter reporter, CancellationToken ct)
    {
        HashSet<string> inZip;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            static string Normalized(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');
            var files = zip.Entries.Where(e => !Normalized(e).EndsWith('/')).ToList();
            // The site root in the zip, as ExtractZip finds it.
            var webConfig = files.Where(e => e.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => Normalized(e).Count(c => c == '/')).FirstOrDefault();
            var root = webConfig is null ? "" : Normalized(webConfig)[..^webConfig.Name.Length];
            inZip = files.Select(Normalized).Where(n => n.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Select(n => n[root.Length..].Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidDataException) { return Result<int>.Fail($"Not a valid .zip file: {zipPath}"); }

        // Only what came after the zip: a file the backup skipped (in use while it was made) is older, and stays.
        var madeAt = File.GetLastWriteTimeUtc(zipPath);
        var excluded = new HashSet<string>(excludedPaths.Select(p => p.Replace('/', '\\').Trim('\\')), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        var failed = new List<string>();
        foreach (var (file, rel) in FilesToZip(directory, excluded, ct).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (inZip.Contains(rel) || file.CreationTimeUtc <= madeAt) continue;
            try
            {
                file.Attributes = FileAttributes.Normal;
                file.Delete();
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(rel);
            }
        }
        if (failed.Count > 0)
            reporter.Warn($"Could not delete {failed.Count} file(s) added since the backup: {string.Join(", ", failed.Take(5))}{(failed.Count > 5 ? ", …" : "")}");
        return Result<int>.Ok(removed);
    }

    public Task<Result> CreateZipAsync(string sourceDirectory, string zipPath, IReadOnlyCollection<string> excludedPaths,
        IProgressReporter reporter, CancellationToken ct)
        => Task.Run(() => CreateZip(sourceDirectory, zipPath, excludedPaths, reporter, ct), ct);

    private static Result CreateZip(string src, string zipPath, IReadOnlyCollection<string> excludedPaths,
        IProgressReporter reporter, CancellationToken ct)
    {
        if (!Directory.Exists(src)) return Result.Fail($"Folder not found: {src}");

        var excluded = new HashSet<string>(excludedPaths.Select(p => p.Replace('/', '\\').Trim('\\')),
            StringComparer.OrdinalIgnoreCase);
        var tmp = zipPath + ".tmp";
        var files = FilesToZip(src, excluded, ct)
            // Never zip the zip itself when it is being written inside the folder.
            .Where(f => !f.File.FullName.Equals(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase)
                     && !f.File.FullName.Equals(Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var progress = new ProgressThrottle();
        var skipped = new List<string>();
        var count = 0;
        var bytes = 0L;
        try
        {
            // Written beside the target and moved into place, so a failed or cancelled export leaves no half zip.
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var (file, rel) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Shared read: the running site keeps some files (logs, caches) open.
                        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal);
                        entry.LastWriteTime = file.LastWriteTime;
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped.Add(rel);
                        continue;
                    }
                    count++;
                    bytes += file.Length;
                    if (progress.Due()) reporter.Progress($"{count}/{files.Count}  {rel}");
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }

        if (skipped.Count > 0)
            reporter.Warn($"Skipped {skipped.Count} file(s) that couldn't be read: " +
                          string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? ", …" : ""));
        reporter.Success($"Zipped {count:N0} files ({bytes / 1024d / 1024d:N1} MB) into {zipPath}");
        reporter.Fact("Files zipped", $"{count:N0} · {bytes / 1024d / 1024d:N1} MB");
        return Result.Ok();
    }

    /// <summary>
    /// Every file under <paramref name="root"/> with its path relative to it, leaving out the <paramref name="excluded"/>
    /// relative paths. An excluded folder isn't walked at all, so a large cache folder costs nothing.
    /// </summary>
    private static IEnumerable<(FileInfo File, string Rel)> FilesToZip(string root, HashSet<string> excluded, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // a junction would zip its target or loop
        };
        var folders = new Stack<DirectoryInfo>();
        folders.Push(new DirectoryInfo(root));
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            foreach (var file in folder.EnumerateFiles("*", options))
            {
                var rel = Path.GetRelativePath(root, file.FullName);
                if (!excluded.Contains(rel)) yield return (file, rel);
            }
            foreach (var sub in folder.EnumerateDirectories("*", options))
                if (!excluded.Contains(Path.GetRelativePath(root, sub.FullName))) folders.Push(sub);
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
