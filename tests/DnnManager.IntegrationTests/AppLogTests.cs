using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>
/// DNN Manager's own log on the Logs tab: its daily files, newest first, and its warnings and errors coloured - and the
/// hosts file, shown whole and again when it is rewritten.
/// </summary>
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

            var logs = Catalog(paths).ForApp();

            CollectionAssert.AreEqual(new[] { "DNN Manager log - 2026-10-04", "DNN Manager log - 2026-10-03", "DNN Manager log - 2026-10-02" },
                logs.Select(l => l.Title).ToArray());
            Assert.IsTrue(logs.All(l => l.Group == SiteLogCatalog.AppGroup && l.FilePath is not null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // No hosts file: the logs alone.
    private static SiteLogCatalog Catalog(AppDataPaths paths, string? hosts = null) =>
        new(new UntouchedIis(), paths, NullLogger<SiteLogCatalog>.Instance) { HostsFilePath = hosts ?? Path.Combine(paths.Root, "no-hosts") };

    [TestMethod]
    public void TheHostsFile_IsListedFirst_InASectionOfItsOwn_ShownWhole()
    {
        var root = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        try
        {
            Directory.CreateDirectory(paths.LogsDirectory);
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "dnnmanager-20261007.log"), "12:00:00 Started\r\n");
            var hosts = Path.Combine(root, "hosts");
            File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");

            var logs = Catalog(paths, hosts).ForApp();

            CollectionAssert.AreEqual(new[] { (SiteLogCatalog.HostsGroup, SiteLogCatalog.HostsTitle), (SiteLogCatalog.AppGroup, "DNN Manager log - 2026-10-07") },
                logs.Select(l => (l.Group, l.Title)).ToArray());
            Assert.IsTrue(logs[0].Whole && logs[0].FilePath == hosts);
            Assert.IsFalse(logs[1].Whole, "The logs are followed from their end.");
            Assert.IsTrue(logs.All(l => SiteLogCatalog.IsAppLog(l.Group, l.Title)), "Both are DNN Manager's, not a site's.");
            Assert.IsFalse(SiteLogCatalog.IsAppLog("Windows", "ASP.NET errors and warnings"), "A site's Windows section isn't.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TheHostsFile_RewrittenInTheMiddle_IsShownAnew()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dnnmanager-hosts-{Guid.NewGuid():N}");
        File.WriteAllText(file, "127.0.0.1 localhost\r\n# begin\r\n127.0.0.1 shop.dnndev.me\r\n# end\r\n");
        var source = new SiteLogSource(SiteLogCatalog.AppGroup, SiteLogCatalog.HostsTitle, "", file, null) { Whole = true };
        var batches = new System.Collections.Concurrent.BlockingCollection<IReadOnlyList<string>>();
        using var tail = source.Open();
        tail.Replaced += () => batches.Add(["(replaced)"]);
        tail.Lines += batches.Add;
        try
        {
            tail.Start(10);
            Assert.IsTrue(batches.TryTake(out var first, 5000));
            CollectionAssert.AreEqual(new[] { "127.0.0.1 localhost", "# begin", "127.0.0.1 shop.dnndev.me", "# end" }, first.ToArray(), "All of it.");

            // A line added inside the block, not at the end - what tailing the end would get wrong.
            await Task.Delay(50);
            File.WriteAllText(file, "127.0.0.1 localhost\r\n# begin\r\n127.0.0.1 blog.dnndev.me\r\n127.0.0.1 shop.dnndev.me\r\n# end\r\n");
            Assert.IsTrue(batches.TryTake(out var replaced, 10000) && replaced[0] == "(replaced)", "The view is cleared first.");
            Assert.IsTrue(batches.TryTake(out var again, 5000));
            StringAssert.StartsWith(again[0], "--- changed at ");
            CollectionAssert.AreEqual(new[] { "127.0.0.1 localhost", "# begin", "127.0.0.1 blog.dnndev.me", "127.0.0.1 shop.dnndev.me", "# end" },
                again.Skip(1).ToArray());
        }
        finally
        {
            tail.Dispose();
            File.Delete(file);
        }
    }

    [TestMethod]
    public void NoLogsFolder_IsNoLogs()
    {
        var paths = new AppDataPaths(Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N")));
        Assert.AreEqual(0, Catalog(paths).ForApp().Count);
    }

    [TestMethod]
    [DataRow("14:03:22 [error] Set up existing project 'shop' stopped at Database (12s)", LogLineLevel.Error)]
    [DataRow("14:03:22 [critical] DnnManager.Presentation.App: Unhandled exception", LogLineLevel.Error)]
    [DataRow("14:03:22 [warning] Keep warm: shop doesn't answer", LogLineLevel.Warning)]
    [DataRow("14:03:22 [success] Start 'shop' finished (1s)", LogLineLevel.Normal)]
    public void DnnManagersWarningsAndErrors_AreColoured(string line, LogLineLevel level) =>
        Assert.AreEqual(level, new LogLine(line).Level);
}
