using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>DNN Manager's own log on the Logs tab: its daily files, newest first, and its warnings and errors coloured.</summary>
[TestClass]
public sealed class AppLogTests
{
    [TestMethod]
    public void TheDailyFiles_AreListedNewestFirst_ByTheirDay()
    {
        var root = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            foreach (var (day, age) in new[] { ("20261002", 2), ("20261004", 0), ("20261003", 1) })
            {
                var file = Path.Combine(paths.LogsDirectory, $"dnnmanager-{day}.log");
                File.WriteAllText(file, "12:00:00 Started\r\n");
                File.SetLastWriteTime(file, DateTime.Now.AddDays(-age));
            }
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "other.log"), "not DNN Manager's");

            var logs = new SiteLogCatalog(new UntouchedIis(), paths, NullLogger<SiteLogCatalog>.Instance).ForApp();

            CollectionAssert.AreEqual(new[] { "DNN Manager log - 2026-10-04", "DNN Manager log - 2026-10-03", "DNN Manager log - 2026-10-02" },
                logs.Select(l => l.Title).ToArray());
            Assert.IsTrue(logs.All(l => l.Group == SiteLogCatalog.AppGroup && l.FilePath is not null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void NoLogsFolder_IsNoLogs()
    {
        var paths = new AppDataPaths(Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N")));
        Assert.AreEqual(0, new SiteLogCatalog(new UntouchedIis(), paths, NullLogger<SiteLogCatalog>.Instance).ForApp().Count);
    }

    [TestMethod]
    [DataRow("14:03:22 [error] Set up existing project 'shop' stopped at Database (12s)", LogLineLevel.Error)]
    [DataRow("14:03:22 [critical] DnnManager.Presentation.App: Unhandled exception", LogLineLevel.Error)]
    [DataRow("14:03:22 [warning] Keep warm: shop doesn't answer", LogLineLevel.Warning)]
    [DataRow("14:03:22 [success] Start 'shop' finished (1s)", LogLineLevel.Normal)]
    public void DnnManagersWarningsAndErrors_AreColoured(string line, LogLineLevel level) =>
        Assert.AreEqual(level, new LogLine(line).Level);
}
