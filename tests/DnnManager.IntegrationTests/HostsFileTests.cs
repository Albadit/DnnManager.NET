using System.Text;
using DnnManager.Application.Abstractions;
using DnnManager.Infrastructure.Hosts;
using DnnManager.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The sites' host names in the hosts file, so *.dnndev.me opens without internet and a custom domain opens at all - on a
/// hosts file of the test's own.
/// </summary>
[TestClass]
public sealed class HostsFileTests
{
    private const string WindowsHosts =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n# localhost name resolution is handled within DNS itself.\r\n#\t127.0.0.1       localhost\r\n#\t::1             localhost\r\n";

    private static IisSiteRuntime Site(params IisBinding[] bindings) => new(1, "Started", "shop", "Started", [], bindings, @"C:\DNN\shop");

    private static IisBinding Http(string host, int port = 80, string address = "*") => new("http", address, port, host, false);

    private static HostsEntry Local(string host) => new(HostsFile.Loopback, host);

    // ─── Which names, to which address ────────────────────────────────────

    [TestMethod]
    public void EntriesFor_TakesEveryHostNameOfASite_CustomDomainsToo()
    {
        var entries = HostsFile.EntriesFor(
        [
            Site(Http("Shop.DnnDev.Me"), new IisBinding("https", "*", 443, "shop.dnndev.me", true)),
            Site(Http("blog.dnndev.me", 8080), Http("klant.local"), Http("www.customer.com.")),
            Site(Http(""), Http("*.shop.test"), Http("localhost"), Http("192.168.1.20"), Http("bad name"),
                new IisBinding("net.tcp", "", null, "tcp.dnndev.me", false)),
        ]);

        CollectionAssert.AreEqual(new[] { Local("blog.dnndev.me"), Local("klant.local"), Local("shop.dnndev.me"), Local("www.customer.com") },
            entries.ToArray(), "One entry per name, lower case and sorted - not a site answering any name, a wildcard, localhost, " +
                               "an IP address, a name DNS can't have or a binding browsers don't use.");
    }

    [TestMethod]
    public void EntriesFor_PointsToTheAddressTheBindingListensOn()
    {
        var entries = HostsFile.EntriesFor(
        [
            Site(Http("lan.dnndev.me", address: "192.168.1.20")),
            Site(Http("six.dnndev.me", address: "[::1]")),
            Site(Http("both.dnndev.me", address: "192.168.1.20"), Http("both.dnndev.me", 8080)),
        ]);

        CollectionAssert.AreEqual(new[] { Local("both.dnndev.me"), new HostsEntry("192.168.1.20", "lan.dnndev.me"), new HostsEntry("::1", "six.dnndev.me") },
            entries.ToArray(), "A binding on every address is reached on loopback; that wins over a specific one.");
    }

    // ─── The block in the file ────────────────────────────────────────────

    [TestMethod]
    public void WithEntries_AddsTheBlockAtTheEnd_AndTakesItOutAgain()
    {
        var with = HostsFile.WithEntries(WindowsHosts, [Local("blog.dnndev.me"), Local("shop.dnndev.me")]);

        Assert.AreEqual(WindowsHosts + "\r\n" + HostsFile.Begin + "\r\n127.0.0.1       blog.dnndev.me\r\n127.0.0.1       shop.dnndev.me\r\n" +
                        HostsFile.End + "\r\n", with);
        Assert.AreEqual(with, HostsFile.WithEntries(with, [Local("blog.dnndev.me"), Local("shop.dnndev.me")]), "Written once, it stays as it is.");
        Assert.AreEqual(WindowsHosts, HostsFile.WithEntries(with, []), "Without sites the file is as it was before.");
        Assert.AreEqual(WindowsHosts, HostsFile.WithEntries(WindowsHosts, []));
    }

    [TestMethod]
    public void WithEntries_ReplacesTheBlockWhereItIs()
    {
        var file = HostsFile.WithEntries(WindowsHosts, [Local("old.dnndev.me")]) + "10.0.0.9 nas\r\n";

        var next = HostsFile.WithEntries(file, [Local("new.dnndev.me")]);

        Assert.AreEqual(WindowsHosts + "\r\n" + HostsFile.Begin + "\r\n127.0.0.1       new.dnndev.me\r\n" + HostsFile.End + "\r\n10.0.0.9 nas\r\n", next,
            "The lines after the block are the user's and stay after it.");
    }

    [TestMethod]
    public void WithEntries_LeavesOutANameTheUserMapsThemselves()
    {
        var file = WindowsHosts + "10.0.0.5   shop.dnndev.me   # the test server\r\n";
        var next = HostsFile.WithEntries(file, [Local("blog.dnndev.me"), Local("shop.dnndev.me")]);

        StringAssert.Contains(next, "127.0.0.1       blog.dnndev.me");
        Assert.IsFalse(next.Contains("127.0.0.1       shop.dnndev.me"), "The user's own line wins.");
        Assert.AreEqual(file, HostsFile.WithEntries(file, [Local("shop.dnndev.me")]), "Nothing left to add: no block at all.");
    }

    [TestMethod]
    public void WithEntries_KeepsTheFilesLineEndings()
    {
        var unix = "127.0.0.1 localhost\n";
        Assert.AreEqual("127.0.0.1 localhost\n\n" + HostsFile.Begin + "\n127.0.0.1       shop.dnndev.me\n" + HostsFile.End + "\n",
            HostsFile.WithEntries(unix, [Local("shop.dnndev.me")]));
        Assert.AreEqual(HostsFile.Begin + "\r\n127.0.0.1       shop.dnndev.me\r\n" + HostsFile.End + "\r\n",
            HostsFile.WithEntries("", [Local("shop.dnndev.me")]), "An empty file gets Windows' line breaks.");
    }

    [TestMethod]
    public void WithEntries_ABeginWithoutAnEnd_KeepsTheLinesAfterIt()
    {
        var file = WindowsHosts + HostsFile.Begin + "\r\n127.0.0.1       shop.dnndev.me\r\n10.0.0.9 nas\r\n";

        var next = HostsFile.WithEntries(file, [Local("shop.dnndev.me")]);

        Assert.AreEqual(WindowsHosts + "127.0.0.1       shop.dnndev.me\r\n10.0.0.9 nas\r\n", next,
            "Only the marker goes; the line left is then the user's, and nothing is added for it.");
    }

    [TestMethod]
    public void Write_KeepsTheRestOfTheFileByteForByte()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dnnmanager-hosts-{Guid.NewGuid():N}");
        try
        {
            // A BOM, a UTF-8 comment and a byte that isn't UTF-8 at all.
            byte[] original = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("# Café → host\r\n127.0.0.1 localhost # "), 0xE9, (byte)'\r', (byte)'\n'];
            File.WriteAllBytes(path, original);

            Assert.IsTrue(HostsFile.Write(path, [Local("shop.dnndev.me")]));
            Assert.IsFalse(HostsFile.Write(path, [Local("shop.dnndev.me")]), "Already there: not written again.");
            var bytes = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(original, bytes.Take(original.Length).ToArray());
            StringAssert.EndsWith(Encoding.ASCII.GetString(bytes), "127.0.0.1       shop.dnndev.me\r\n" + HostsFile.End + "\r\n");

            Assert.IsTrue(HostsFile.Write(path, []));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(HostsFile.BackupPath(path));
        }
    }

    [TestMethod]
    public void Write_KeepsTheFileAsItWasBeforeBesideIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dnnmanager-hosts-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, WindowsHosts);
            Assert.IsTrue(HostsFile.Write(path, [Local("shop.dnndev.me")]));
            Assert.AreEqual(WindowsHosts, File.ReadAllText(HostsFile.BackupPath(path)));
            Assert.IsTrue(HostsFile.Write(path, [Local("blog.dnndev.me")]));
            StringAssert.Contains(File.ReadAllText(HostsFile.BackupPath(path)), "shop.dnndev.me", "The copy is of the file as it was just before.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(HostsFile.BackupPath(path));
        }
    }

    [TestMethod]
    public void WithEntries_FindsItsBlockAfterAByteOrderMark()
    {
        // An editor saved the file as UTF-8 with a BOM - the block on line 1 is still DNN Manager's, not the user's lines.
        var bom = Encoding.Latin1.GetString(Encoding.UTF8.GetPreamble());
        var text = bom + HostsFile.Begin + "\r\n127.0.0.1       old.dnndev.me\r\n" + HostsFile.End + "\r\n";
        var next = HostsFile.WithEntries(text, [Local("shop.dnndev.me")]);
        Assert.IsFalse(next.Contains("old.dnndev.me", StringComparison.Ordinal), next);
        Assert.AreEqual(1, next.Split(HostsFile.End).Length - 1, "One block: " + next);
    }

    [TestMethod]
    public void EntriesFor_WritesAnInternationalNameInPunycode() =>
        CollectionAssert.AreEqual(new[] { Local("xn--caf-dma.test") }, HostsFile.EntriesFor([Site(Http("café.test"))]).ToArray());

    // ─── Following the sites ──────────────────────────────────────────────

    [TestMethod]
    public async Task Service_WritesTheSitesOnceIisIsReadCompletely_AndFollowsThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dnnmanager-hosts-{Guid.NewGuid():N}");
        File.WriteAllText(path, WindowsHosts);
        var feed = new FakeFeed();
        var notices = new List<(string Message, bool Warning)>();
        using var service = new HostsFileService(feed, NullLogger<HostsFileService>.Instance)
            { FilePath = path, Settle = TimeSpan.Zero };
        service.Noticed += (m, w) => { lock (notices) notices.Add((m, w)); };
        try
        {
            service.Start();
            feed.Raise(new ProjectAdded(Project("shop", Http("shop.dnndev.me"))), new ProjectAdded(Project("blog", Http("blog.local.test"))));
            await service.SyncNowAsync();
            Assert.AreEqual(WindowsHosts, File.ReadAllText(path), "Not before IIS has been read completely - it may be only some of the sites.");

            feed.Raise(new ConnectionChanged(MonitorConnection.Live, null));
            await service.SyncNowAsync();
            StringAssert.Contains(File.ReadAllText(path), "127.0.0.1       blog.local.test\r\n127.0.0.1       shop.dnndev.me\r\n");

            feed.Raise(new ProjectChanged(Project("shop", Http("store.dnndev.me")), ProjectFacets.Site), new ProjectRemoved("blog"));
            await service.SyncNowAsync();
            var text = File.ReadAllText(path);
            StringAssert.Contains(text, HostsFile.Begin + "\r\n127.0.0.1       store.dnndev.me\r\n" + HostsFile.End);
            Assert.IsFalse(text.Contains("shop.dnndev.me") || text.Contains("blog.local.test"));

            feed.Raise(new ProjectRemoved("shop"));
            await service.SyncNowAsync();
            Assert.AreEqual(WindowsHosts, File.ReadAllText(path));
            lock (notices)
                CollectionAssert.AreEqual(new[]
                {
                    ("Hosts file: blog.local.test, shop.dnndev.me - these sites open without internet.", false),
                    ("Hosts file: store.dnndev.me added; blog.local.test, shop.dnndev.me removed.", false),
                    ("Hosts file: store.dnndev.me removed.", false),
                }, notices);
        }
        finally
        {
            File.Delete(path);
            File.Delete(HostsFile.BackupPath(path));
        }
    }

    [TestMethod]
    public async Task Service_AFileItCantWrite_IsSaidOnce_AndTriedAgainAtTheNextChange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dnnmanager-hosts-{Guid.NewGuid():N}");
        File.WriteAllText(path, WindowsHosts);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var feed = new FakeFeed();
        var notices = new List<(string Message, bool Warning)>();
        using var service = new HostsFileService(feed, NullLogger<HostsFileService>.Instance)
            { FilePath = path, Settle = TimeSpan.Zero };
        service.Noticed += (m, w) => { lock (notices) notices.Add((m, w)); };
        try
        {
            service.Start();
            feed.Raise(new ProjectAdded(Project("shop", Http("shop.dnndev.me"))), new ConnectionChanged(MonitorConnection.Live, null));
            await service.SyncNowAsync();
            feed.Raise(new ProjectChanged(Project("shop", Http("shop.dnndev.me"), Http("shop.dnndev.me", 8080)), ProjectFacets.Site));
            await service.SyncNowAsync();
            lock (notices)
            {
                Assert.AreEqual(1, notices.Count, "Said once, not at every change.");
                Assert.IsTrue(notices[0].Warning);
                StringAssert.Contains(notices[0].Message, "only open while this PC has internet");
            }

            File.SetAttributes(path, FileAttributes.Normal);
            feed.Raise(new ProjectChanged(Project("shop", Http("shop.dnndev.me")), ProjectFacets.Site));
            await service.SyncNowAsync();
            StringAssert.Contains(File.ReadAllText(path), "127.0.0.1       shop.dnndev.me");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            File.Delete(HostsFile.BackupPath(path));
        }
    }

    private static ProjectState Project(string name, params IisBinding[] bindings) => new()
    {
        Name = name,
        Directory = $@"C:\DNN\{name}",
        SiteUrl = "",
        Site = new IisSiteRuntime(1, "Started", name, "Started", [], bindings, $@"C:\DNN\{name}"),
    };

    private sealed class FakeFeed : IServerStateFeed
    {
        public event Action<IReadOnlyList<MonitorEvent>>? Changed;
        public event Action? Resumed { add { } remove { } }

        public void Raise(params MonitorEvent[] events) => Changed?.Invoke(events);

        public Task SyncSitesAsync() => Task.CompletedTask;
    }
}
