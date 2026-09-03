using DnnManager.Application.Abstractions;
using DnnManager.Domain;
using FluentFTP;
using Microsoft.Extensions.Logging;

namespace DnnManager.Infrastructure.Files;

public sealed class ProjectFileCopier : IProjectFileCopier
{
    private readonly ILogger<ProjectFileCopier> _log;

    public ProjectFileCopier(ILogger<ProjectFileCopier> log) => _log = log;

    public async Task<Result> CopyAsync(CloneSource source, string destinationDirectory,
        IProgressReporter reporter, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);
        return source.Kind switch
        {
            CloneSourceKind.LocalFolder => CopyLocal(source, destinationDirectory, reporter, ct),
            CloneSourceKind.Ftp         => await CopyFtpAsync(source, destinationDirectory, reporter, ct),
            _ => Result.Fail($"Unknown clone source kind: {source.Kind}")
        };
    }

    private Result CopyLocal(CloneSource source, string dest, IProgressReporter reporter, CancellationToken ct)
    {
        var src = source.LocalPath;
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

    private async Task<Result> CopyFtpAsync(CloneSource source, string dest, IProgressReporter reporter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.FtpHost)) return Result.Fail("FTP host is empty.");
        var remotePath = string.IsNullOrWhiteSpace(source.FtpRemotePath) ? "/" : source.FtpRemotePath!;

        try
        {
            await using var client = new AsyncFtpClient(source.FtpHost, source.FtpUser ?? "", source.FtpPassword ?? "", source.FtpPort <= 0 ? 21 : source.FtpPort);
            // Azure App Service (and most modern hosts) require FTPS. Auto negotiates
            // explicit TLS and falls back to plain FTP when the server allows it.
            client.Config.EncryptionMode = FtpEncryptionMode.Auto;
            client.Config.ValidateAnyCertificate = true;
            await client.AutoConnect(ct);
            reporter.Info($"Connected to FTP {source.FtpHost}:{source.FtpPort} \u2014 {remotePath}");

            // 1. Build the file list ourselves so we can report progress while we
            //    scan. (DownloadDirectory does this listing internally and silently,
            //    which on a large site looks frozen for a long time.)
            var root = remotePath.TrimEnd('/');
            if (root.Length == 0) root = "/";
            var files = new List<FtpListItem>();
            var progress = new ProgressThrottle();
            await ScanAsync(client, root, files, reporter, progress, ct);
            reporter.Info($"Found {files.Count} files \u2014 downloading\u2026");

            if (files.Count == 0)
            {
                await client.Disconnect(ct);
                reporter.Success("No files to download.");
                return Result.Ok();
            }

            // 2. Download each file, updating one status line per file.
            var prefix = root == "/" ? "/" : root + "/";
            var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int ok = 0;
            long bytes = 0;
            var failed = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = files[i];
                var rel = item.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? item.FullName[prefix.Length..]
                    : item.FullName.TrimStart('/');
                var localPath = Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar));
                var localDir = Path.GetDirectoryName(localPath)!;
                if (createdDirs.Add(localDir)) Directory.CreateDirectory(localDir);

                if (progress.Due()) reporter.Progress($"{i + 1}/{files.Count}  {rel}");

                // A single locked/transient file on the server (e.g. an in-use
                // cache file) must not abort the whole clone - record and move on.
                try
                {
                    var status = await client.DownloadFile(localPath, item.FullName,
                        FtpLocalExists.Overwrite, FtpVerify.None, token: ct);
                    if (status == FtpStatus.Success || status == FtpStatus.Skipped)
                    {
                        ok++;
                        if (item.Size > 0) bytes += item.Size;
                    }
                    else
                    {
                        failed.Add(rel);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Skipping FTP file {Path}", item.FullName);
                    failed.Add(rel);
                    // The connection may have dropped; restore it so the rest of
                    // the files don't all fail in a cascade.
                    try { if (!client.IsConnected) await client.AutoConnect(ct); } catch { }
                }
            }

            await client.Disconnect(ct);

            reporter.Success($"Downloaded {ok} files ({bytes / 1024d / 1024d:N1} MB) from FTP.");

            if (failed.Count > 0)
            {
                // Show a few examples without flooding the screen.
                foreach (var f in failed.Take(10)) reporter.Info($"Skipped (locked/unavailable): {f}");
                if (failed.Count > 10) reporter.Info($"…and {failed.Count - 10} more skipped.");
            }

            // Only treat the copy as failed if nothing came through at all;
            // a few locked files (e.g. server cache) shouldn't block the clone.
            if (ok == 0 && failed.Count > 0)
                return Result.Fail($"All {failed.Count} FTP transfer(s) failed.");
            if (failed.Count > 0)
                reporter.Success($"Completed with {failed.Count} skipped file(s).");
            return Result.Ok();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FTP download failed");
            return Result.Fail($"FTP download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rate-limits status-line updates to roughly ten a second. Every
    /// <see cref="IProgressReporter.Progress"/> call repaints a full-width console line, which on a
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

    // Regenerable cache/temp folders that are routinely locked by the running site and
    // don't need to be cloned. Matched by directory name (case-insensitive).
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "imageflow_hybrid_cache",
        "_imagecache",
        "Cache",
        "Search",   // Lucene search index - rebuilt by DNN, often locked
        "Logs",
    };

    /// <summary>Recursively collects every file under <paramref name="path"/>, reporting scan progress.</summary>
    private async Task ScanAsync(
        AsyncFtpClient client, string path, List<FtpListItem> files, IProgressReporter reporter,
        ProgressThrottle progress, CancellationToken ct)
    {
        // Listing a single directory can fail transiently on Azure (the data
        // connection gets recycled, surfacing as a NullReferenceException). Don't
        // let one bad directory abort the whole scan \u2014 retry once, then skip it.
        FtpListItem[]? items = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                items = await client.GetListing(path, token: ct);
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    _log.LogWarning(ex, "Skipping unreadable FTP directory {Path}", path);
                    return;
                }
                // Reconnect if the control channel dropped, then retry.
                try { if (!client.IsConnected) await client.AutoConnect(ct); } catch { }
            }
        }

        foreach (var it in items ?? Array.Empty<FtpListItem>())
        {
            ct.ThrowIfCancellationRequested();
            if (it.Name is "." or "..") continue;

            if (it.Type == FtpObjectType.Directory)
            {
                if (SkipDirectories.Contains(it.Name))
                {
                    reporter.Progress($"Skipping cache folder {it.Name}/ …");
                    continue;
                }
                await ScanAsync(client, it.FullName, files, reporter, progress, ct);
            }
            else if (it.Type == FtpObjectType.File)
            {
                files.Add(it);
                if (progress.Due())
                    reporter.Progress($"Scanning remote files\u2026 {files.Count} found");
            }
        }
    }
}
