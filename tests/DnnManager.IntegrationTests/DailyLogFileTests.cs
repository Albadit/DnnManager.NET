using DnnManager.Infrastructure.Files;
using DnnManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace DnnManager.IntegrationTests;

/// <summary>The day's log file: lines keep reaching it - also after it was deleted while DNN Manager runs.</summary>
[TestClass]
public sealed class DailyLogFileTests
{
    [TestMethod]
    public void ALogDeletedWhileWrittenTo_IsStartedAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            using var log = new DailyLogFile(paths);
            var now = DateTime.Now;
            var file = Path.Combine(paths.LogsDirectory, $"dnnmanager-{now:yyyyMMdd}.log");

            log.Append(now, "before");
            Assert.IsTrue(File.Exists(file));

            // Troubleshoot → Clean up data: the file goes while it is open - and so does the folder.
            File.Delete(file);
            Directory.Delete(paths.LogsDirectory);
            log.Append(now, "after");

            Assert.IsTrue(File.Exists(file), "The next line starts the file again.");
            var text = ReadShared(file);
            StringAssert.Contains(text, "after");
            Assert.IsFalse(text.Contains("before"), "Only what came after the clean-up.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* still open */ }
        }
    }

    [TestMethod]
    public void TheFileIsNamedByItsDay_AndTheOldNamesGoAtStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.LogsDirectory);
            var old = Path.Combine(paths.LogsDirectory, "dnnmanager-2026-10-03.log");
            var kept = Path.Combine(paths.LogsDirectory, "dnnmanager-20261003.log");
            File.WriteAllText(old, "12:00:00 the name before 1.7.4");
            File.WriteAllText(kept, "12:00:00 yesterday");

            using var log = new DailyLogFile(paths);
            log.Append(new DateTime(2026, 10, 4, 9, 30, 0), "today");

            Assert.IsFalse(File.Exists(old), "The old name is deleted.");
            Assert.IsTrue(File.Exists(kept), "Yesterday's file stays.");
            Assert.AreEqual("dnnmanager-20261004.log", DailyLogFile.FileName(new DateOnly(2026, 10, 4)));
            StringAssert.Contains(ReadShared(Path.Combine(paths.LogsDirectory, "dnnmanager-20261004.log")), "09:30:00 today");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* still open */ }
        }
    }

    [TestMethod]
    public void WarningsAndErrors_AreWrittenWithTheirStackTrace_AndARepeatOnlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            using var file = new DailyLogFile(paths);
            using var provider = new DailyLogFileLoggerProvider(file);
            var log = provider.CreateLogger("DnnManager.Infrastructure.Iis.IisManager");

            log.LogInformation("Not interesting");
            for (var i = 0; i < 3; i++) log.LogWarning(new InvalidOperationException("IIS is busy"), "Could not read IIS site states");
            log.LogError("Failed to write web.config");

            var text = ReadShared(Path.Combine(paths.LogsDirectory, $"dnnmanager-{DateTime.Now:yyyyMMdd}.log"));
            Assert.IsFalse(text.Contains("Not interesting"), "Information isn't written.");
            StringAssert.Contains(text, "[warning] IisManager: Could not read IIS site states");
            StringAssert.Contains(text, "    System.InvalidOperationException: IIS is busy");
            Assert.AreEqual(1, text.Split("Could not read IIS site states").Length - 1, "A repeat within ten minutes is left out.");
            StringAssert.Contains(text, "[error] IisManager: Failed to write web.config");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* still open */ }
        }
    }

    // The log stays open for writing: read it the way an editor would.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
