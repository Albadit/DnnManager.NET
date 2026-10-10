using System.Diagnostics;
using System.Security.Cryptography;
using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The daily log's batches and its size cap, and the logs of a failed update - kept in the logs folder when the update's
/// own folder is cleaned up.
/// </summary>
[TestClass]
public sealed class OpsLogAndUpdateTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "ops-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a file may still be open */ }
    }

    // ─── The daily log ────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("[warning] IIS is busy", true)]
    [DataRow("[error] Install stopped at Database", true)]
    [DataRow("[critical] App: Unhandled fatal error", true)]
    [DataRow("[success] Installed test-web (12 s)", true)]
    [DataRow("[cancelled] Install was cancelled (3 s)", true)]
    [DataRow("Copying files…", false)]
    [DataRow("        → check the folder", false)]
    public void Warnings_errors_and_the_end_of_an_operation_reach_the_disk_at_once(string line, bool urgent) =>
        Assert.AreEqual(urgent, DailyLogFile.IsUrgent(line));

    [TestMethod]
    public void Plain_lines_wait_for_the_next_batch_and_a_warning_takes_them_along()
    {
        var paths = new AppDataPaths(_dir);
        var file = Path.Combine(paths.LogsDirectory, DailyLogFile.FileName(DateOnly.FromDateTime(DateTime.Now)));
        using var log = new DailyLogFile(paths);
        var now = DateTime.Now;

        log.Append(now, "first");
        Assert.IsTrue(ReadShared(file).Contains("first"), "A new file shows its first line at once.");
        log.Append(now, "second");
        // The timer flushes every FlushEvery; a line written now is (almost always) still in memory.
        log.Append(now, "[warning] something went wrong");
        var text = ReadShared(file);
        StringAssert.Contains(text, "second", "A warning flushes what came before it.");
        StringAssert.Contains(text, "[warning] something went wrong");
    }

    [TestMethod]
    public void What_is_still_in_memory_is_written_when_the_log_closes()
    {
        var paths = new AppDataPaths(_dir);
        var file = Path.Combine(paths.LogsDirectory, DailyLogFile.FileName(DateOnly.FromDateTime(DateTime.Now)));
        var log = new DailyLogFile(paths);
        var now = DateTime.Now;
        log.Append(now, "first");
        for (var i = 0; i < 50; i++) log.Append(now, $"line {i}");

        log.Dispose();

        StringAssert.Contains(ReadShared(file), "line 49");
        log.Append(now, "after closing");
        StringAssert.Contains(ReadShared(file), "after closing", "A line that comes after it closed is written at once.");
        log.Dispose();
    }

    [TestMethod]
    public void All_the_logs_together_stay_under_the_cap_the_oldest_going_first()
    {
        var paths = new AppDataPaths(_dir);
        Directory.CreateDirectory(paths.LogsDirectory);
        // Five days of 60 MB (sparse files - nothing is written): 300 MB, over the 200 MB cap.
        var files = Enumerable.Range(1, 5).Select(day =>
        {
            var path = Path.Combine(paths.LogsDirectory, DailyLogFile.FileName(DateOnly.FromDateTime(DateTime.Now.AddDays(-day))));
            using (var stream = new FileStream(path, FileMode.Create)) stream.SetLength(60L * 1024 * 1024);
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-day));
            return path;
        }).ToList();

        using var log = new DailyLogFile(paths);

        var left = files.Where(File.Exists).ToList();
        Assert.IsTrue(left.Sum(f => new FileInfo(f).Length) <= DailyLogFile.MaxTotalBytes, "Under the cap.");
        CollectionAssert.AreEqual(files.Take(left.Count).ToList(), left, "The newest are kept, the oldest went.");
        Assert.AreEqual(3, left.Count);
    }

    // ─── A failed update's logs ───────────────────────────────────────────

    [TestMethod]
    public void A_failed_updates_logs_are_kept_together_and_only_the_newest_two()
    {
        var logs = Path.Combine(_dir, "logs");
        var update = Path.Combine(_dir, "update.log");
        File.WriteAllText(update, "12:00:00 Setup ended with exit code 7.");
        File.WriteAllText(Path.ChangeExtension(update, ".setup.log"), "Setup log: something in use");
        Directory.CreateDirectory(logs);
        foreach (var (version, age) in new[] { ("1.9.0", 3), ("1.9.1", 2) })
        {
            var old = UpdateHelper.FailedLogPath(logs, version);
            File.WriteAllText(old, "older");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-age));
        }
        // One already there under the same name is replaced, not appended to.
        File.WriteAllText(UpdateHelper.FailedLogPath(logs, "1.9.2"), "stale");

        Assert.IsTrue(UpdateHelper.KeepFailedLog(update, Path.ChangeExtension(update, ".setup.log"), UpdateHelper.FailedLogPath(logs, "1.9.2"), out var problem), problem);

        var kept = File.ReadAllText(UpdateHelper.FailedLogPath(logs, "1.9.2"));
        StringAssert.Contains(kept, "Setup ended with exit code 7.");
        StringAssert.Contains(kept, "Setup log: something in use");
        Assert.IsFalse(kept.Contains("stale"));
        Assert.IsFalse(File.Exists(UpdateHelper.FailedLogPath(logs, "1.9.0")), "Only the newest two are kept.");
        Assert.IsTrue(File.Exists(UpdateHelper.FailedLogPath(logs, "1.9.1")));
    }

    [TestMethod]
    public void A_failed_update_points_Show_log_at_the_kept_copy()
    {
        var logs = Path.Combine(_dir, "logs");
        var package = Path.Combine(_dir, "DnnManager_Setup-1.9.0-x64.exe");
        var bytes = "new"u8.ToArray();
        File.WriteAllBytes(package, bytes);
        var plan = new UpdatePlan
        {
            Kind = UpdateKind.Installer, Package = package, AppExe = Path.Combine(_dir, "DnnManager.exe"),
            PackageSha256 = Convert.ToHexString(SHA256.HashData(bytes)), PackageSize = bytes.Length,
            WaitForProcessId = ExitedProcessId(), FromVersion = "1.8.2", ToVersion = "1.9.0",
            LogFile = Path.Combine(_dir, "update.log"), ResultFile = Path.Combine(_dir, "result.json"),
            FailedLogFile = UpdateHelper.FailedLogPath(logs, "1.9.0")
        };

        var code = UpdateHelper.Run(plan, info => info.FileName == package ? Exit(7) : null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, code);
        var result = UpdateResult.TryRead(plan.ResultFile);
        Assert.AreEqual(plan.FailedLogFile, result?.LogFile, "Show log opens the copy in the logs folder.");
        StringAssert.Contains(File.ReadAllText(plan.FailedLogFile), "Setup ended with exit code 7.");
    }

    [TestMethod]
    public void Cleaning_up_keeps_the_logs_of_a_failed_update_whose_helper_didnt()
    {
        var work = Path.Combine(_dir, "update");
        var logs = Path.Combine(_dir, "logs");
        var failed = Path.Combine(work, "1.9.0");
        var installed = Path.Combine(work, "1.9.1");
        Directory.CreateDirectory(failed);
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(failed, "update.log"), "The update failed: in use");
        new UpdateResult(false, "in use", Path.Combine(failed, "update.log")).Write(Path.Combine(failed, "result.json"));
        File.WriteAllText(Path.Combine(installed, "update.log"), "Done.");
        new UpdateResult(true, "installed", Path.Combine(installed, "update.log")).Write(Path.Combine(installed, "result.json"));

        AppUpdater.CleanUp(work, logs);

        Assert.IsFalse(Directory.Exists(failed));
        Assert.IsFalse(Directory.Exists(installed));
        StringAssert.Contains(File.ReadAllText(UpdateHelper.FailedLogPath(logs, "1.9.0")), "The update failed: in use");
        Assert.IsFalse(File.Exists(UpdateHelper.FailedLogPath(logs, "1.9.1")), "An update that worked leaves nothing.");
    }

    private static int ExitedProcessId()
    {
        using var process = Exit(0);
        process.WaitForExit();
        return process.Id;
    }

    private static Process Exit(int code) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c exit {code}") { CreateNoWindow = true, UseShellExecute = false })!;

    // The log stays open for writing: read it the way an editor would.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
