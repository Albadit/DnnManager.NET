using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Data;
using DnnManager.Infrastructure.KeepWarm;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Infrastructure.Projects;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>Keep warm's parts that need no IIS: its rules, where requests go, its settings and records, and its requests.</summary>
[TestClass]
public sealed class KeepWarmTests
{
    private static IisSiteRuntime Site(TimeSpan? idleTimeout, params IisBinding[] bindings) =>
        new(1, "Started", "shop", "Started", [], bindings, @"C:\DNN\shop") { IdleTimeout = idleTimeout };

    private static IisBinding Http(string host, int port = 80, string address = "*") => new("http", address, port, host, false);

    // ─── When to send ─────────────────────────────────────────────────────

    [TestMethod]
    public void Interval_StaysWellWithinTheIdleTimeOut()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(5), KeepWarmRules.Interval(5, TimeSpan.FromMinutes(20)), "IIS's default leaves the setting as it is.");
        Assert.AreEqual(TimeSpan.FromMinutes(8), KeepWarmRules.Interval(30, TimeSpan.FromMinutes(20)), "Never more than 40% of the time-out.");
        Assert.AreEqual(TimeSpan.FromMinutes(2), KeepWarmRules.Interval(5, TimeSpan.FromMinutes(5)));
        Assert.AreEqual(KeepWarmRules.MinInterval, KeepWarmRules.Interval(5, TimeSpan.FromSeconds(20)), "Not more often than every 30 seconds.");
        Assert.AreEqual(TimeSpan.FromMinutes(5), KeepWarmRules.Interval(5, TimeSpan.Zero),
            "An app pool that never idles out still gets them: DNN itself restarts after a rebuild.");
        Assert.AreEqual(TimeSpan.FromMinutes(5), KeepWarmRules.Interval(5, null), "Unknown: the setting.");
    }

    [TestMethod]
    public void InUseSkip_OnlyWhenTheNextRequestStillComesInTime()
    {
        Assert.IsTrue(KeepWarmRules.MaySkipWhenInUse("shop", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)));
        Assert.IsTrue(KeepWarmRules.MaySkipWhenInUse("shop", TimeSpan.FromMinutes(5), null));
        Assert.IsTrue(KeepWarmRules.MaySkipWhenInUse("shop", TimeSpan.FromMinutes(5), TimeSpan.Zero));
        var interval = KeepWarmRules.Interval(5, TimeSpan.FromMinutes(1));
        Assert.IsFalse(KeepWarmRules.MaySkipWhenInUse("shop", interval, TimeSpan.FromMinutes(1)),
            "Two 30-second intervals don't fit in a 1-minute idle time-out: never skipped.");
    }

    [TestMethod]
    public void AWarmUpWaitsForTheServerTheSitesWebConfigNames()
    {
        Assert.AreEqual(("localhost", 1433), KeepWarmService.SqlEndpoint("localhost,1433"));
        Assert.AreEqual(("127.0.0.1", 1444), KeepWarmService.SqlEndpoint("tcp:127.0.0.1, 1444"));
        Assert.AreEqual(("sql.example.com", 1433), KeepWarmService.SqlEndpoint("sql.example.com"));
        Assert.AreEqual(("127.0.0.1", 1433), KeepWarmService.SqlEndpoint("(local)"));
        // Not reached over a TCP port of its own: nothing to wait for.
        Assert.IsNull(KeepWarmService.SqlEndpoint(@".\SQLEXPRESS"));
        Assert.IsNull(KeepWarmService.SqlEndpoint(@"(localdb)\MSSQLLocalDB"));
        Assert.IsNull(KeepWarmService.SqlEndpoint(@"np:\\.\pipe\sql\query"));
        Assert.IsNull(KeepWarmService.SqlEndpoint(null));
    }

    [TestMethod]
    public void Backoff_WaitsLongerAfterEachFailure()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), KeepWarmRules.Backoff(1));
        Assert.AreEqual(TimeSpan.FromMinutes(2), KeepWarmRules.Backoff(2));
        Assert.AreEqual(TimeSpan.FromMinutes(5), KeepWarmRules.Backoff(3));
        Assert.AreEqual(TimeSpan.FromMinutes(5), KeepWarmRules.Backoff(10));
    }

    [TestMethod]
    public void Jitter_IsTheSiteOwn_AndSmall()
    {
        var interval = TimeSpan.FromMinutes(5);
        Assert.AreEqual(KeepWarmRules.Jitter("shop", interval), KeepWarmRules.Jitter("SHOP", interval), "The same for a site every time.");
        foreach (var site in new[] { "shop", "blog", "intranet", "a", "dnn10" })
        {
            var jitter = KeepWarmRules.Jitter(site, interval);
            Assert.IsTrue(jitter >= TimeSpan.Zero && jitter < TimeSpan.FromSeconds(15), $"{site}: {jitter}");
        }
        Assert.IsTrue(KeepWarmRules.Jitter("shop", TimeSpan.FromSeconds(30)) < TimeSpan.FromSeconds(3), "At most a tenth of the interval.");
    }

    [TestMethod]
    public void Durations_ReadAsPeopleSayThem()
    {
        Assert.AreEqual("12 ms", KeepWarmRules.Duration(TimeSpan.FromMilliseconds(12.4)));
        // In the user's own way of writing numbers, like the app's other figures.
        Assert.AreEqual($"{3.4:0.0} s", KeepWarmRules.Duration(TimeSpan.FromMilliseconds(3420)));
        Assert.AreEqual("1 minute", KeepWarmRules.Span(TimeSpan.FromMinutes(1)));
        Assert.AreEqual("5 minutes", KeepWarmRules.Span(TimeSpan.FromMinutes(5)));
        Assert.AreEqual("30 seconds", KeepWarmRules.Span(TimeSpan.FromSeconds(30)));
        Assert.AreEqual($"{2.4:0.#} minutes", KeepWarmRules.Span(TimeSpan.FromSeconds(144)));
    }

    // ─── What an answer means ─────────────────────────────────────────────

    [TestMethod]
    public void Classify_AnyAnswerKeepsWarm_ButNotAnError()
    {
        var url = new Uri("http://shop.dnndev.me/KeepAlive.aspx");
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, KeepWarmRules.Classify(KeepWarmRequestKind.Ping, url, 200, null, "").Kind);
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, KeepWarmRules.Classify(KeepWarmRequestKind.Ping, url, 401, null, "").Kind, "Sign in first - but it runs.");
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, KeepWarmRules.Classify(KeepWarmRequestKind.Ping, url, 403, null, "").Kind);
        Assert.AreEqual(KeepWarmOutcomeKind.PingPathMissing, KeepWarmRules.Classify(KeepWarmRequestKind.Ping, url, 404, null, "").Kind);
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, url, 404, null, "").Kind,
            "A warm-up page that isn't there still had DNN answer.");
        Assert.AreEqual(KeepWarmOutcomeKind.Failed, KeepWarmRules.Classify(KeepWarmRequestKind.Ping, url, 500, null, "").Kind);
        Assert.AreEqual(KeepWarmOutcomeKind.NotAnswering, KeepWarmRules.Classify(KeepWarmRequestKind.Ping, url, 503, null, "").Kind);

        var wizard = KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, url, 200, null, "<form action=\"InstallWizard.aspx\">");
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, wizard.Kind);
        Assert.IsNotNull(wizard.Note, "Says that DNN's installer is waiting.");
    }

    [TestMethod]
    public void Classify_FollowsOnlyTheSameSite_AndNeverIntoTheInstaller()
    {
        var home = new Uri("http://shop.dnndev.me/");
        var follow = KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302, new Uri("/Home", UriKind.Relative), "");
        Assert.AreEqual("/Home", follow.FollowPath);
        Assert.IsNull(KeepWarmRules.Classify(KeepWarmRequestKind.Ping, home, 302, new Uri("/Home", UriKind.Relative), "").FollowPath,
            "A ping doesn't follow.");
        Assert.IsNull(KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 301, new Uri("https://shop.dnndev.me/"), "").FollowPath,
            "Another scheme is another site.");
        Assert.IsNull(KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302, new Uri("http://www.example.com/"), "").FollowPath);

        var installer = KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302, new Uri("/Install/Install.aspx?mode=upgrade", UriKind.Relative), "");
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, installer.Kind);
        Assert.IsNull(installer.FollowPath, "Requesting the installer can upgrade DNN.");

        Assert.IsNull(KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302, new Uri("/dnn/Install/Install.aspx?mode=upgrade", UriKind.Relative), "").FollowPath,
            "An installer in a child application isn't followed into either.");
        Assert.AreEqual("/Installation", KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302, new Uri("/Installation", UriKind.Relative), "").FollowPath,
            "A page that only starts like it is no installer.");
        // A double slash is still the site's path - not a host named Install.
        foreach (var location in new[] { new Uri("/\\Install/InstallWizard.aspx", UriKind.Relative), new Uri("/.//Install/InstallWizard.aspx", UriKind.Relative),
                     new Uri("http://shop.dnndev.me//Install/InstallWizard.aspx") })
            Assert.IsNull(KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302, location, "").FollowPath, location.OriginalString);
        Assert.IsTrue(KeepWarmSettings.IsInstallerPath("//Install/InstallWizard.aspx"));

        var error = KeepWarmRules.Classify(KeepWarmRequestKind.WarmUp, home, 302,
            new Uri("/ErrorPage.aspx?status=500&error=Connection+To+The+Database+Failed", UriKind.Relative), "");
        Assert.AreEqual(KeepWarmOutcomeKind.Failed, error.Kind);
        Assert.IsTrue(error.Reason!.Contains("Connection To The Database Failed"), error.Reason);
    }

    // ─── Where requests go ────────────────────────────────────────────────

    [TestMethod]
    public void Target_IsThisMachine_WithTheBindingsHostName()
    {
        var target = LocalSiteTarget.For(Site(null, Http("shop.dnndev.me")))!;
        Assert.AreEqual(new LocalSiteTarget("http", "127.0.0.1", 80, "shop.dnndev.me"), target);
        Assert.AreEqual(new Uri("http://127.0.0.1:80/KeepAlive.aspx"), target.UriFor("/KeepAlive.aspx"));
        Assert.AreEqual("http://shop.dnndev.me/KeepAlive.aspx", target.DisplayUrl("/KeepAlive.aspx"));

        Assert.AreEqual("localhost:8080", LocalSiteTarget.For(Site(null, Http("", 8080)))!.HostHeader, "No host name: localhost, with the port.");
        Assert.AreEqual("10.0.0.5", LocalSiteTarget.For(Site(null, Http("shop.dnndev.me", 80, "10.0.0.5")))!.ConnectHost);
        Assert.AreEqual("[::1]", LocalSiteTarget.For(Site(null, Http("shop.dnndev.me", 80, "[::1]")))!.ConnectHost);
    }

    [TestMethod]
    public void Target_PrefersWhatABrowserOpens_AndSkipsWhatCantBeRequested()
    {
        var https = new IisBinding("https", "*", 443, "shop.dnndev.me", HasCertificate: true);
        Assert.AreEqual(new LocalSiteTarget("https", "127.0.0.1", 443, "shop.dnndev.me"),
            LocalSiteTarget.For(Site(null, Http("shop.dnndev.me"), https)), "An https binding with a certificate first, as BrowseUrl.");
        Assert.AreEqual("shop.dnndev.me", LocalSiteTarget.For(Site(null, Http("*.dnndev.me"), Http("shop.dnndev.me")))!.HostHeader,
            "A wildcard host name is no Host header.");
        Assert.IsNull(LocalSiteTarget.For(Site(null, new IisBinding("net.tcp", "*", 808, "", false))), "No web binding: nothing to request.");
    }

    [TestMethod]
    public void Plan_TakesTheSettings_WithinTheIdleTimeout()
    {
        var site = Site(TimeSpan.FromMinutes(20), Http("shop.dnndev.me"));

        var plain = KeepWarmPlan.For(new KeepWarmSettings(), site);
        Assert.AreEqual(TimeSpan.FromMinutes(5), plain.Interval);
        Assert.AreEqual("/KeepAlive.aspx", plain.PingPath);
        Assert.AreEqual("/", plain.WarmUpPath);

        var changed = KeepWarmPlan.For(new KeepWarmSettings { PingMinutes = 30, WarmUpPath = "/Home", PingPath = "/Install/Install.aspx" }, site);
        Assert.AreEqual(TimeSpan.FromMinutes(8), changed.Interval, "30 minutes, kept within the 20-minute idle time-out.");
        Assert.AreEqual("/Home", changed.WarmUpPath);
        Assert.AreEqual("/KeepAlive.aspx", changed.PingPath, "A page of the installer is never requested - the built-in one instead.");
    }

    // ─── Settings and records ─────────────────────────────────────────────

    [TestMethod]
    public void Settings_RefusePagesThatAreNoPagesOfTheSite_OrOfTheInstaller()
    {
        foreach (var good in new[] { "/KeepAlive.aspx", "KeepAlive.aspx", "/", "/Home?keepwarm=1", " /Default.aspx " })
            Assert.IsNull(KeepWarmSettings.PathProblem(good), good);
        foreach (var bad in new[] { "", "  ", "http://shop.dnndev.me/", "//shop.dnndev.me/", "/a page", @"\KeepAlive.aspx", "/#top",
                     "/Install/Install.aspx", "/install", "/INSTALL/UpgradeWizard.aspx", "/Default.aspx?mode=upgrade",
                     // However it is written: dot segments, escapes (also escaped twice), trailing dots, a child application.
                     "/./Install/Install.aspx", "/x/../Install/Install.aspx", "../Install/Install.aspx", "/%49nstall/Install.aspx",
                     "/%2E/Install/Install.aspx", "/%2549nstall/Install.aspx", "/Install./Install.aspx", "/dnn/Install/Install.aspx" })
            Assert.IsNotNull(KeepWarmSettings.PathProblem(bad), bad);
        Assert.IsNull(KeepWarmSettings.PathProblem("/Installation"), "Only a folder named Install is the installer's.");
        Assert.AreEqual("/KeepAlive.aspx", KeepWarmSettings.NormalizePath(" KeepAlive.aspx "));

        var settings = new UserSettings();
        settings.Projects.KeepWarm.PingMinutes = 0;
        settings.Projects.KeepWarm.PingPath = "/Install/Install.aspx";
        var keys = settings.Validate().Select(p => p.Key).ToList();
        CollectionAssert.Contains(keys, "projects.keepWarm.pingMinutes");
        CollectionAssert.Contains(keys, "projects.keepWarm.pingPath");
        CollectionAssert.DoesNotContain(keys, "projects.keepWarm.warmUpPath");

        settings.Projects.KeepWarm = new KeepWarmSettings { PingPath = "KeepAlive.aspx", WarmUpPath = "Home" };
        var options = settings.ToAppOptions();
        Assert.AreEqual("/KeepAlive.aspx", options.KeepWarm.PingPath, "Requested with a leading /.");
        Assert.AreEqual("/Home", options.KeepWarm.WarmUpPath);
    }

    [TestMethod]
    public void Records_AreTheSitesKeptWarm()
    {
        var root = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        var database = new AppDatabase(paths);
        var records = new KeepWarmRecords(database, NullLogger<KeepWarmRecords>.Instance);
        try
        {
            Assert.IsNull(records.Find("shop"));
            Assert.AreEqual(0, records.List().Count, "No database yet: none.");

            records.Save(new KeepWarmRecord("shop", true));
            records.Save(new KeepWarmRecord("blog", true));
            Assert.AreEqual(new KeepWarmRecord("shop", true), records.Find("SHOP"), "Found whatever the case of its name.");
            Assert.AreEqual(2, records.List().Count);

            records.Save(new KeepWarmRecord("blog", false));
            Assert.IsNull(records.Find("blog"), "Switched off: no row.");
            Assert.AreEqual(1, records.List().Count);
            using (var connection = database.Open())
                CollectionAssert.AreEqual(new[] { "site" }, AppDatabase.KeyValues(connection, "SELECT name, type FROM pragma_table_info('keep_warm')").Keys.ToArray(),
                    "A site and nothing more: how it is kept warm is the settings'.");

            // Kept for the next start: another reader of the same database finds them.
            Assert.AreEqual(new KeepWarmRecord("shop", true), new KeepWarmRecords(new AppDatabase(paths), NullLogger<KeepWarmRecords>.Instance).Find("shop"));

            records.Remove("shop");
            Assert.IsNull(records.Find("shop"));

            var cleaner = new AppDataCleaner(paths);
            records.Save(new KeepWarmRecord("shop", true));
            var settings = new SettingsStore(paths);
            settings.Load();
            cleaner.Clean(AppDataKind.KeepWarmChoices);
            Assert.AreEqual(0, records.List().Count, "A factory reset forgets which sites are kept warm.");
            Assert.IsTrue(settings.SavedValues().Count > 0, "…and only that.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ─── Requests ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Requester_SendsTheHostName_AndKeepsEachSitesCookies()
    {
        await using var server = new TinyServer(path => path switch
        {
            "/KeepAlive.aspx" => new Answer(200, Body: "ok", SetCookie: ".ASPXANONYMOUS=abc; path=/; HttpOnly"),
            _ => new Answer(404)
        });
        using var requester = new KeepWarmRequester();
        var target = new LocalSiteTarget("http", "127.0.0.1", server.Port, $"shop.dnndev.me:{server.Port}");

        var first = await requester.SendAsync("shop", target, "/KeepAlive.aspx", KeepWarmRequestKind.Ping, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, first.Kind);
        Assert.AreEqual(1, first.Requests);
        await requester.SendAsync("shop", target, "/KeepAlive.aspx", KeepWarmRequestKind.Ping, CancellationToken.None);
        await requester.SendAsync("blog", target, "/KeepAlive.aspx", KeepWarmRequestKind.Ping, CancellationToken.None);

        var seen = server.Requests.ToList();
        Assert.AreEqual(3, seen.Count);
        Assert.AreEqual($"shop.dnndev.me:{server.Port}", seen[0].Host);
        Assert.IsTrue(seen[0].UserAgent?.Contains(KeepWarmRequester.UserAgent) == true, seen[0].UserAgent);
        Assert.IsNull(seen[0].Cookie, "Nothing to send the first time.");
        Assert.AreEqual(".ASPXANONYMOUS=abc", seen[1].Cookie, "The cookie DNN gave - no new anonymous visitor every time.");
        Assert.IsNull(seen[2].Cookie, "One site's cookies aren't another's.");

        requester.ForgetCookies("shop");
        await requester.SendAsync("shop", target, "/KeepAlive.aspx", KeepWarmRequestKind.Ping, CancellationToken.None);
        Assert.IsNull(server.Requests.Last().Cookie, "Gone with its worker process.");
    }

    [TestMethod]
    public async Task Requester_WarmUpFollowsTheSite_AndStopsAtTheInstallerOrAnError()
    {
        await using var server = new TinyServer(path => path switch
        {
            "/" => new Answer(302, Location: "/Home"),
            "/Home" => new Answer(200, Body: "<html>home</html>"),
            "/new" => new Answer(302, Location: "/Install/InstallWizard.aspx"),
            "/broken" => new Answer(302, Location: "/ErrorPage.aspx?status=500&error=Connection+To+The+Database+Failed"),
            "/down" => new Answer(503),
            _ => new Answer(404)
        });
        using var requester = new KeepWarmRequester();
        var target = new LocalSiteTarget("http", "127.0.0.1", server.Port, $"shop.dnndev.me:{server.Port}");

        var home = await requester.SendAsync("shop", target, "/", KeepWarmRequestKind.WarmUp, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, home.Kind);
        Assert.AreEqual(2, home.Requests, "The redirect to the same site was followed.");
        CollectionAssert.AreEqual(new[] { "/", "/Home" }, server.Requests.Select(r => r.Path).ToList());

        var installer = await requester.SendAsync("shop", target, "/new", KeepWarmRequestKind.WarmUp, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, installer.Kind);
        Assert.AreEqual(1, installer.Requests, "Never into the installer.");
        Assert.IsFalse(server.Requests.Any(r => r.Path.StartsWith("/Install", StringComparison.OrdinalIgnoreCase)));

        var broken = await requester.SendAsync("shop", target, "/broken", KeepWarmRequestKind.WarmUp, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.Failed, broken.Kind);
        Assert.IsTrue(broken.Reason!.Contains("Connection To The Database Failed"), broken.Reason);

        Assert.AreEqual(KeepWarmOutcomeKind.NotAnswering,
            (await requester.SendAsync("shop", target, "/down", KeepWarmRequestKind.Ping, CancellationToken.None)).Kind);
        Assert.AreEqual(KeepWarmOutcomeKind.PingPathMissing,
            (await requester.SendAsync("shop", target, "/KeepAlive.aspx", KeepWarmRequestKind.Ping, CancellationToken.None)).Kind);
    }

    [TestMethod]
    public async Task Requester_SaysWhenNothingAnswers()
    {
        // A port nothing listens on: one that was just free.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var requester = new KeepWarmRequester();
        var outcome = await requester.SendAsync("shop", new LocalSiteTarget("http", "127.0.0.1", port, "shop.dnndev.me"), "/KeepAlive.aspx",
            KeepWarmRequestKind.Ping, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.NotAnswering, outcome.Kind);
        Assert.IsFalse(await KeepWarmRequester.SqlAnswersAsync("127.0.0.1", port, CancellationToken.None), "Nor does a SQL Server there.");
    }

    [TestMethod]
    public async Task Requester_NeverRequestsTheInstaller_AndKnowsWhenIisItselfAnswers()
    {
        await using var server = new TinyServer(path => path switch
        {
            "/stopped" => new Answer(404, Server: "Microsoft-HTTPAPI/2.0"),
            _ => new Answer(200)
        });
        using var requester = new KeepWarmRequester();
        var target = new LocalSiteTarget("http", "127.0.0.1", server.Port, "shop.dnndev.me");

        var installer = await requester.SendAsync("shop", target, "/x/../Install/Install.aspx", KeepWarmRequestKind.WarmUp, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.Failed, installer.Kind);
        Assert.AreEqual(0, server.Requests.Count, "Not sent at all - whatever asked for it.");

        var stopped = await requester.SendAsync("shop", target, "/stopped", KeepWarmRequestKind.Ping, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.NotAnswering, stopped.Kind, "HTTP.sys's own 404 isn't the site saying it has no such page.");
    }

    [TestMethod]
    public async Task Requester_DoesntFollowARedirectIntoTheInstaller_HoweverItIsWritten()
    {
        await using var server = new TinyServer(path => path switch
        {
            "/" => new Answer(302, Location: "http://shop.dnndev.me//Install/InstallWizard.aspx"),
            _ => new Answer(200)
        });
        using var requester = new KeepWarmRequester();
        var outcome = await requester.SendAsync("shop", new LocalSiteTarget("http", "127.0.0.1", server.Port, "shop.dnndev.me"), "/",
            KeepWarmRequestKind.WarmUp, CancellationToken.None);
        Assert.AreEqual(KeepWarmOutcomeKind.Ok, outcome.Kind);
        CollectionAssert.AreEqual(new[] { "/" }, server.Requests.Select(r => r.Path).ToList());
    }

    [TestMethod]
    public async Task DebuggerPrograms_AreFound_AndLookedForAgain()
    {
        // A program named like VS Code's .NET debugger.
        var folder = Path.Combine(Path.GetTempPath(), "dnnmanager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var fake = Path.Combine(folder, "vsdbg.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
        var debugger = Process.Start(new ProcessStartInfo(fake, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var id = debugger.Id;
        try
        {
            KeepWarmRequester.ForgetDebuggers();
            CollectionAssert.Contains(KeepWarmRequester.RunningDebuggers().ToList(), id);
            debugger.Kill();
            debugger.WaitForExit();
            await Task.Delay(TimeSpan.FromSeconds(5.5));
            CollectionAssert.DoesNotContain(KeepWarmRequester.RunningDebuggers().ToList(), id, "Looked for again once the last look is 5 s old.");
        }
        finally
        {
            try { if (!debugger.HasExited) debugger.Kill(); } catch (Exception) { /* gone */ }
            debugger.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* still in use */ }
        }
    }

    [TestMethod]
    public void DebuggerCheck_SeesAProcessHeldOpenForWriting()
    {
        // This test process is the "debugger": starting a process leaves it a handle with every right to it.
        using var held = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
        int unheldId;
        using (var unheld = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!)
            unheldId = unheld.Id;
        try
        {
            Assert.IsTrue(KeepWarmRequester.HoldsForWriting(Environment.ProcessId, [held.Id]));
            Assert.IsFalse(KeepWarmRequester.HoldsForWriting(Environment.ProcessId, [unheldId]), "Its handle was closed.");
            Assert.IsFalse(KeepWarmRequester.DebuggerAttached([held.Id]), "Nothing debugs it - this process isn't a debugger program.");
        }
        finally
        {
            foreach (var id in new[] { held.Id, unheldId })
                try { using var p = Process.GetProcessById(id); p.Kill(entireProcessTree: true); } catch (Exception) { /* gone */ }
        }
    }

    // ─── The service ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task Service_KeepsASiteWarm_AndHoldsBackWhenItShould()
    {
        var keepAliveMissing = false;
        var failing = false;
        await using var server = new TinyServer(path => path switch
        {
            "/KeepAlive.aspx" when Volatile.Read(ref failing) => new Answer(500),
            "/KeepAlive.aspx" when !Volatile.Read(ref keepAliveMissing) => new Answer(200, Body: "ok"),
            "/" => new Answer(200, Body: "<html>home</html>"),
            _ => new Answer(404)
        });
        var feed = new FakeFeed();
        var records = new MemoryRecords();
        using var service = new KeepWarmService(feed, new UntouchedIis(), records, Options.Create(new AppOptions()),
            NullLogger<KeepWarmService>.Instance) { StartUpDelay = TimeSpan.Zero };
        var notices = new ConcurrentQueue<KeepWarmNotice>();
        service.Noticed += notices.Enqueue;
        service.Start();
        var worker = Environment.ProcessId;
        ProjectState Shop(string state, params int[] workers) => new()
        {
            Name = "shop",
            Directory = @"C:\DNN\shop",
            SiteUrl = $"http://shop.dnndev.me:{server.Port}",
            Site = new IisSiteRuntime(1, state, "shop", state, workers, [new IisBinding("http", "*", server.Port, "shop.dnndev.me", false)],
                @"C:\DNN\shop") { IdleTimeout = TimeSpan.FromMinutes(20) }
        };
        List<string> Paths() => server.Requests.Select(r => r.Path).ToList();

        feed.Raise(new RuntimeChanged(IisServerState.Running), new ProjectAdded(Shop("Started", worker)));
        await Task.Delay(300);
        Assert.AreEqual(KeepWarmState.Off, service.StatusOf("shop").State);
        Assert.AreEqual(0, server.Requests.Count, "Not kept warm: nothing is sent.");

        // Switched on: it has a worker process, so its keep-alive page - at once.
        service.SetEnabled("shop", true);
        await WaitForAsync(service, KeepWarmState.Warm);
        CollectionAssert.AreEqual(new[] { "/KeepAlive.aspx" }, Paths());
        Assert.AreEqual($"shop.dnndev.me:{server.Port}", server.Requests.Single().Host);
        Assert.IsTrue(records.Find("shop")?.Enabled == true, "Remembered.");

        // Stopped: paused, and nothing is sent.
        feed.Raise(new ProjectChanged(Shop("Stopped"), ProjectFacets.Site));
        await WaitForAsync(service, KeepWarmState.Paused);

        // Started again, without a worker process: warmed up with its home page - once: the worker process that started
        // isn't reported yet, so the sites are read again, and until then it isn't warmed up again.
        feed.Raise(new ProjectChanged(Shop("Started"), ProjectFacets.Site));
        await WaitForAsync(service, KeepWarmState.Warm, after: 1);
        await Task.Delay(500);
        CollectionAssert.AreEqual(new[] { "/KeepAlive.aspx", "/" }, Paths());
        await WaitUntilAsync(() => Volatile.Read(ref feed.Syncs) == 1, "the sites to be read again");
        feed.Raise(new ProjectChanged(Shop("Started", worker), ProjectFacets.Site));
        await Task.Delay(300);
        Assert.AreEqual(2, server.Requests.Count, "The worker process the warm-up started is its own - no check of it.");

        // An operation on the site holds it back. One that never began (its question was answered no) changed nothing:
        // nothing to check afterwards.
        service.Pause(KeepWarmPause.For(["shop"]));
        await WaitForAsync(service, KeepWarmState.Paused);
        service.Resume();
        await WaitForAsync(service, KeepWarmState.Warm);
        await Task.Delay(300);
        Assert.AreEqual(2, server.Requests.Count);
        // One that ran gets it checked again once it has ended - not even Check now goes before.
        service.Pause(KeepWarmPause.For(["shop"]));
        await WaitForAsync(service, KeepWarmState.Paused);
        service.CheckNow("shop");
        await Task.Delay(300);
        Assert.AreEqual(2, server.Requests.Count, "Not even Check now while the operation runs.");
        service.Recheck(["shop"]);
        service.Resume();
        await WaitUntilAsync(() => server.Requests.Count == 3);
        Assert.AreEqual("/KeepAlive.aspx", Paths()[2]);

        // Its keep-alive page goes missing: one 404 only brings a warm-up; a second one in a row means it isn't there.
        Volatile.Write(ref keepAliveMissing, true);
        service.CheckNow("shop");
        await WaitUntilAsync(() => server.Requests.Count == 5);
        CollectionAssert.AreEqual(new[] { "/KeepAlive.aspx", "/" }, Paths().Skip(3).ToList());
        Assert.IsFalse(notices.Any(n => n.Message.Contains("has no")), "Not after one 404.");
        feed.Raise(new ProjectChanged(Shop("Started", worker, 4), ProjectFacets.Site));
        await WaitUntilAsync(() => server.Requests.Count == 7);
        Assert.IsTrue(notices.Any(n => n.Message.Contains("has no /KeepAlive.aspx")), string.Join(" | ", notices.Select(n => n.Message)));

        // It fails while someone else's operation runs: not counted - tried again once that has ended, not meanwhile.
        Volatile.Write(ref keepAliveMissing, false);
        Volatile.Write(ref failing, true);
        service.Pause(KeepWarmPause.WarmUps);
        var sent = server.Requests.Count;
        service.CheckNow("shop");
        await WaitForAsync(service, KeepWarmState.Waiting);
        await Task.Delay(500);
        Assert.AreEqual(sent + 1, server.Requests.Count, "Not again while the operation runs.");
        Assert.IsFalse(notices.Any(n => n.IsWarning), "Not counted as a failure: " + string.Join(" | ", notices.Select(n => n.Message)));
        Volatile.Write(ref failing, false);
        service.Resume();
        await WaitForAsync(service, KeepWarmState.Warm);
        Assert.AreEqual(sent + 2, server.Requests.Count);

        // Gone from IIS: its record goes with it - a new site of the same name starts cold.
        feed.Raise(new ProjectRemoved("shop"));
        await WaitUntilAsync(() => records.Find("shop") is null);
        Assert.AreEqual(KeepWarmState.Off, service.StatusOf("shop").State);
    }

    [TestMethod]
    public async Task Service_ForgetsSitesRemovedFromIisWhileItWasClosed()
    {
        var feed = new FakeFeed();
        var records = new MemoryRecords();
        records.Save(new KeepWarmRecord("shop", true));
        records.Save(new KeepWarmRecord("gone", true));
        using var service = new KeepWarmService(feed, new UntouchedIis(), records, Options.Create(new AppOptions()),
            NullLogger<KeepWarmService>.Instance);
        service.Start();
        Assert.AreEqual(KeepWarmState.Waiting, service.StatusOf("gone").State, "Kept warm, as far as it knows yet.");

        // The first complete read of IIS: 'gone' isn't there.
        var shop = new ProjectState
        {
            Name = "shop", Directory = @"C:\DNN\shop", SiteUrl = "",
            Site = new IisSiteRuntime(1, "Started", "shop", "Started", [], [], @"C:\DNN\shop")
        };
        feed.Raise(new RuntimeChanged(IisServerState.Running), new ProjectAdded(shop), new ConnectionChanged(MonitorConnection.Live, null));
        await WaitUntilAsync(() => records.Find("gone") is null, "the record of the site gone from IIS to go");
        Assert.IsNotNull(records.Find("shop"), "A site that is there keeps its record.");
        Assert.AreEqual(KeepWarmState.Off, service.StatusOf("gone").State);
    }

    [TestMethod]
    public async Task Service_KeepsEachSiteOnItsOwn_OnceEach_AndAgainAfterARestart()
    {
        var down = false;
        await using var server = new TinyServer(path => path switch
        {
            "/KeepAlive.aspx" when Volatile.Read(ref down) => new Answer(503),
            "/KeepAlive.aspx" => new Answer(200, Body: "ok"),
            _ => new Answer(404)
        });
        var feed = new FakeFeed();
        var records = new MemoryRecords();
        var worker = Environment.ProcessId;
        ProjectState Site(string name) => new()
        {
            Name = name,
            Directory = $@"C:\DNN\{name}",
            SiteUrl = $"http://{name}.dnndev.me:{server.Port}",
            Site = new IisSiteRuntime(1, "Started", name, "Started", [worker], [new IisBinding("http", "*", server.Port, $"{name}.dnndev.me", false)],
                $@"C:\DNN\{name}") { IdleTimeout = TimeSpan.FromMinutes(20) }
        };
        int SentTo(string name) => server.Requests.Count(r => r.Host == $"{name}.dnndev.me:{server.Port}");
        KeepWarmService NewService() => new(feed, new UntouchedIis(), records, Options.Create(new AppOptions()),
            NullLogger<KeepWarmService>.Instance) { StartUpDelay = TimeSpan.Zero };
        MonitorEvent[] Sites() =>
            [new RuntimeChanged(IisServerState.Running), new ProjectAdded(Site("a")), new ProjectAdded(Site("b")), new ProjectAdded(Site("c"))];

        var service = NewService();
        try
        {
            service.Start();
            feed.Raise(Sites());

            // A and C on - A switched on three times over: still one site, one request, no second timer.
            service.SetEnabled("a", true);
            service.SetEnabled("a", true);
            service.SetEnabled("c", true);
            service.SetEnabled("a", true);
            await WaitUntilAsync(() => service.StatusOf("a").State == KeepWarmState.Warm && service.StatusOf("c").State == KeepWarmState.Warm,
                "A and C to be warm");
            await Task.Delay(1000);
            Assert.AreEqual(1, SentTo("a"), "Switched on three times: one request, then the interval (minutes).");
            Assert.AreEqual(1, SentTo("c"));
            Assert.AreEqual(0, SentTo("b"), "B isn't kept warm.");
            Assert.AreEqual(KeepWarmState.Off, service.StatusOf("b").State);

            // A goes down for a while: failing, not given up on - and warm again once it answers.
            Volatile.Write(ref down, true);
            service.CheckNow("a");
            await WaitUntilAsync(() => service.StatusOf("a").State == KeepWarmState.Failing, "A to be failing", () => service.StatusOf("a").Text);
            Volatile.Write(ref down, false);
            service.CheckNow("a");
            await WaitUntilAsync(() => service.StatusOf("a").State == KeepWarmState.Warm, "A to be warm again", () => service.StatusOf("a").Text);
            Assert.AreEqual(KeepWarmState.Warm, service.StatusOf("c").State, "C goes on as before.");

            // C off: nothing more is sent to it, not even when asked.
            service.SetEnabled("c", false);
            await WaitUntilAsync(() => service.StatusOf("c").State == KeepWarmState.Off, "C to be off");
            var sentToC = SentTo("c");
            service.CheckNow("c");
            await Task.Delay(500);
            Assert.AreEqual(sentToC, SentTo("c"), "Switched off: nothing is sent.");
            Assert.IsTrue(records.Find("c") is null, "Off with nothing of its own: no record left.");
        }
        finally
        {
            service.Dispose();
        }

        // DNN Manager closed and opened again: A is kept warm again, B and C aren't.
        var before = (a: SentTo("a"), b: SentTo("b"), c: SentTo("c"));
        using var restarted = NewService();
        restarted.Start();
        Assert.AreEqual(KeepWarmState.Waiting, restarted.StatusOf("a").State, "Remembered before IIS was even read.");
        feed.Raise(Sites());
        await WaitUntilAsync(() => SentTo("a") > before.a, "A to be requested after the restart", () => restarted.StatusOf("a").Text);
        await WaitUntilAsync(() => restarted.StatusOf("a").State == KeepWarmState.Warm, "A to be warm after the restart");
        await Task.Delay(500);
        Assert.AreEqual(before.b, SentTo("b"));
        Assert.AreEqual(before.c, SentTo("c"));
        Assert.AreEqual(KeepWarmState.Off, restarted.StatusOf("c").State);
    }

    private static async Task WaitForAsync(KeepWarmService service, KeepWarmState state, int after = 0)
    {
        if (after > 0) await Task.Delay(50);
        await WaitUntilAsync(() => service.StatusOf("shop").State == state, $"the status to be {state}", () => service.StatusOf("shop").Text);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what = "the condition", Func<string>? describe = null)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail($"Timed out waiting for {what}. {describe?.Invoke()}");
            await Task.Delay(20);
        }
    }

    /// <summary>The monitor, played by the test: what it reports, and what a read of the sites then finds.</summary>
    private sealed class FakeFeed : IServerStateFeed
    {
        public event Action<IReadOnlyList<MonitorEvent>>? Changed;
        public event Action? Resumed;

        public Func<MonitorEvent[]>? OnSync { get; set; }

        public int Syncs;

        public void Raise(params MonitorEvent[] events) => Changed?.Invoke(events);

        public void Wake() => Resumed?.Invoke();

        public Task SyncSitesAsync()
        {
            Interlocked.Increment(ref Syncs);
            if (OnSync?.Invoke() is { Length: > 0 } events) Raise(events);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryRecords : IKeepWarmRecords
    {
        private readonly ConcurrentDictionary<string, KeepWarmRecord> _records = new(StringComparer.OrdinalIgnoreCase);

        public KeepWarmRecord? Find(string site) => _records.GetValueOrDefault(site);

        public IReadOnlyList<KeepWarmRecord> List() => _records.Values.ToList();

        public void Save(KeepWarmRecord record)
        {
            if (record.IsEmpty) _records.TryRemove(record.Site, out _);
            else _records[record.Site] = record;
        }

        public void Remove(string site) => _records.TryRemove(site, out _);
    }

    // ─── A tiny web server ────────────────────────────────────────────────

    private sealed record Answer(int Status, string? Location = null, string Body = "", string? SetCookie = null, string? Server = null);

    private sealed record Seen(string Path, string? Host, string? Cookie, string? UserAgent);

    /// <summary>HTTP/1.1 on 127.0.0.1 with an answer per path - and what each request was, in order.</summary>
    private sealed class TinyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, Answer> _answer;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public TinyServer(Func<string, Answer> answer)
        {
            _answer = answer;
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<Seen> Requests { get; } = new();

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine is null) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync() is { Length: > 0 } line)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                var path = requestLine.Split(' ')[1];
                Requests.Enqueue(new Seen(path, headers.GetValueOrDefault("Host"), headers.GetValueOrDefault("Cookie"),
                    headers.GetValueOrDefault("User-Agent")));

                var answer = _answer(path);
                var body = Encoding.UTF8.GetBytes(answer.Body);
                var head = new StringBuilder($"HTTP/1.1 {answer.Status} Answer\r\nContent-Length: {body.Length}\r\nConnection: close\r\n");
                if (answer.Location is { } location) head.Append($"Location: {location}\r\n");
                if (answer.SetCookie is { } cookie) head.Append($"Set-Cookie: {cookie}\r\n");
                if (answer.Server is { } software) head.Append($"Server: {software}\r\n");
                head.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
        }
    }
}
