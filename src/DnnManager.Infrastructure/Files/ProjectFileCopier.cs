using System.IO.Compression;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Domain;

namespace DnnManager.Infrastructure.Files;

public sealed class ProjectFileCopier : IProjectFileCopier
{
    // The times a zip entry can have (DOS date and time).
    private static readonly DateTime ZipFirstTime = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
    private static readonly DateTime ZipLastTime = new(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);

    public Task<Result> CopyAsync(string sourceDirectory, string destinationDirectory,
        IProgressReporter reporter, CancellationToken ct)
    {
        // Not into a link or junction either: with administrator rights the copy would land wherever it points.
        if (DestinationLink(destinationDirectory) is { } link) return Task.FromResult(Result.Fail(link));
        Directory.CreateDirectory(destinationDirectory);
        return Task.FromResult(CopyLocal(sourceDirectory, destinationDirectory, reporter, ct));
    }

    /// <summary>Why <paramref name="dest"/> can't be copied into - it, or a folder on the way to it, is a link or junction; null when it can.</summary>
    private static string? DestinationLink(string dest)
    {
        var full = Path.GetFullPath(dest);
        return SafePath.HasLink(Path.GetPathRoot(full)!, full)
            ? $"{dest} is a link or junction, or reached through one - DNN Manager doesn't copy into those.{Environment.NewLine}{SafePath.LinkHint}"
            : null;
    }

    private static Result CopyLocal(string src, string dest, IProgressReporter reporter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(src)) return Result.Fail("Local source path is empty.");
        if (!Directory.Exists(src)) return Result.Fail($"Source folder does not exist: {src}");

        reporter.Info($"Copying files from {src}");
        // Not through a link or junction: with administrator rights, the copy would read wherever one points (files only
        // an administrator may read) into a folder the user can read.
        if (SafePath.IsLink(src))
            return Result.Fail($"{src} is a link or junction - DNN Manager copies a site's own folder, not what a link points to.{Environment.NewLine}{SafePath.LinkHint}");
        if (DestinationLink(dest) is { } link) return Result.Fail(link);
        var (files, links) = Walk(src);
        // Said, not left out silently: the copy lacks what they point to.
        if (links.Count > 0)
            reporter.Warn($"Not copied: {links.Count} link(s) or junction(s) - DNN Manager doesn't copy what they point to with its administrator rights: " +
                          string.Join(", ", links.Take(10)) + (links.Count > 10 ? ", …" : "") +
                          $"{Environment.NewLine}Hint: copy what they point to into the new project yourself, or recreate the links there.");
        var total = files.Count;
        var progress = new ProgressThrottle();
        // Every destination folder first, once each - then the files, a few at a time: a DNN site is tens of thousands
        // of small files, and copying them one after the other waits on each file's own round trip, not the disk.
        var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in files.Select(f => Path.GetDirectoryName(Path.Combine(dest, Path.GetRelativePath(src, f.FullName)))!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SafePath.EnsureNoLink(dest, folder, checkedFolders);
            Directory.CreateDirectory(folder);
        }
        var fileCount = 0;
        var byteCount = 0L;
        try
        {
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, file =>
            {
                var rel = Path.GetRelativePath(src, file.FullName);
                var target = Path.Combine(dest, rel);
                // A file already there that is a link, or has other names, is replaced - not written through.
                if (SafePath.IsLink(target) || SafePath.IsHardLinked(target)) File.Delete(target);
                file.CopyTo(target, overwrite: true);
                var copied = Interlocked.Increment(ref fileCount);
                Interlocked.Add(ref byteCount, file.Length);
                lock (progress)
                    if (progress.Due()) reporter.Progress($"{copied}/{total}  {rel}");
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // What went wrong with the first file that failed - as a copy one at a time would have said it.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
        reporter.Success($"Copied {fileCount:N0} files ({byteCount / 1024d / 1024d:N1} MB).");
        reporter.Fact("Files copied", $"{fileCount:N0} · {byteCount / 1024d / 1024d:N1} MB");
        return Result.Ok();
    }

    /// <summary>
    /// Every file under <paramref name="src"/> (hidden and system ones too), and - relative to it - the links and junctions
    /// in it, which aren't gone into.
    /// </summary>
    internal static (List<FileInfo> Files, List<string> Links) Walk(string src)
    {
        var files = new List<FileInfo>();
        var links = new List<string>();
        var options = new EnumerationOptions { AttributesToSkip = 0 };
        var folders = new Stack<DirectoryInfo>();
        folders.Push(new DirectoryInfo(src));
        while (folders.Count > 0)
        {
            foreach (var entry in folders.Pop().EnumerateFileSystemInfos("*", options))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) links.Add(Path.GetRelativePath(src, entry.FullName));
                else if (entry is DirectoryInfo folder) folders.Push(folder);
                else if (entry is FileInfo file) files.Add(file);
            }
        }
        return (files, links);
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
            var files = zip.Entries.Where(e => !SafeZip.IsFolder(e)).ToList();
            if (files.Count == 0) return Result.Fail("The zip is empty.");

            var webConfig = files
                .Where(e => e.Name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => SafeZip.Name(e).Count(c => c == '/'))
                .FirstOrDefault();
            var root = webConfig is null ? "" : SafeZip.Name(webConfig)[..^webConfig.Name.Length];
            if (webConfig is null)
                reporter.Info("No web.config in the zip - extracting it as it is. It may not be a DNN site.");
            else if (root.Length > 0)
                reporter.Info($"Site root in the zip: {root}");

            var inRoot = files.Where(e => SafeZip.Name(e).StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList();
            var skipped = files.Count - inRoot.Count;
            var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var progress = new ProgressThrottle();
            var count = 0;
            var bytes = 0L;
            try
            {
                // Too big for the disk (a zip bomb): refused before the first file.
                SafeZip.EnsureFits(inRoot, dest);
                Directory.CreateDirectory(dest);
                foreach (var entry in inRoot)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = SafeZip.Name(entry);
                    // Strictly inside the project ("zip slip"), on no other file's stream (':'), and through no link or junction
                    // in the project (its app pool can make one): with administrator rights the file would be written wherever
                    // it points.
                    SafeZip.ExtractEntry(dest, entry, checkedFolders, name[root.Length..]);
                    count++;
                    bytes += entry.Length;
                    if (progress.Due()) reporter.Progress($"{count}/{inRoot.Count}  {name}");
                }
            }
            // A file in use is thrown, not returned: the caller may end what holds it and try again (Restore backup does).
            catch (IOException ex) when (!FileInUse.Is(ex))
            {
                return Result.Fail(ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return Result.Fail($"A file in the zip is damaged: {ex.Message}");
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
                        // Fastest: a site's DLLs, images and packages hardly get smaller - Optimal only takes longer.
                        var entry = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Fastest);
                        // A zip keeps times from 1980 to 2107 only: one outside is kept at that end, rather than failing the export.
                        entry.LastWriteTime = file.LastWriteTime < ZipFirstTime ? ZipFirstTime
                            : file.LastWriteTime > ZipLastTime ? ZipLastTime : file.LastWriteTime;
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
        // Four times a second: the line is rewritten in the Output tab's document each time (laid out again, and said to
        // UI Automation listeners) - more often only costs, and nobody reads it faster.
        private const long IntervalMs = 250;
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
