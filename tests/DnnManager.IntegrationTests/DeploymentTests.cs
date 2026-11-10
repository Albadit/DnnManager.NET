using System.Text;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using DnnManager.Infrastructure.Data;
using DnnManager.IntegrationTests.Support;

namespace DnnManager.IntegrationTests;

/// <summary>
/// Deployments' rules on their own: where a file stands from the project, the server and the baseline; the ignore rules;
/// the paths a server may give; the line diff and the three-way merge.
/// </summary>
[TestClass]
public sealed class DeploymentRulesTests
{
    private static FileState F(string hash) => new(hash.Length, hash, null);
    private static BaselineEntry B(string hash) => new("a.txt", hash, hash.Length, hash.Length, null);

    [TestMethod]
    [DataRow("x2", "x1", "x1", SyncStatus.LocalModified, ConflictKind.None, DisplayName = "Modified here, unchanged there: push")]
    [DataRow("x1", "x2", "x1", SyncStatus.RemoteModified, ConflictKind.None, DisplayName = "Unchanged here, modified there: pull")]
    [DataRow("x2", "x3", "x1", SyncStatus.Conflict, ConflictKind.BothModified, DisplayName = "Modified in both: conflict")]
    [DataRow("x2", "x2", "x1", SyncStatus.Synchronized, ConflictKind.None, DisplayName = "Modified in both the same way: synchronized")]
    [DataRow("x1", null, null, SyncStatus.LocalAdded, ConflictKind.None, DisplayName = "New here, missing there: push")]
    [DataRow(null, "x1", null, SyncStatus.RemoteAdded, ConflictKind.None, DisplayName = "Missing here, new there: pull")]
    [DataRow(null, "x1", "x1", SyncStatus.LocalDeleted, ConflictKind.None, DisplayName = "Deleted here, unchanged there: push the deletion")]
    [DataRow("x1", null, "x1", SyncStatus.RemoteDeleted, ConflictKind.None, DisplayName = "Unchanged here, deleted there: pull the deletion")]
    [DataRow("x1", "x1", "x1", SyncStatus.Synchronized, ConflictKind.None, DisplayName = "Identical: synchronized")]
    [DataRow("x1", "x2", null, SyncStatus.Conflict, ConflictKind.BothAdded, DisplayName = "Added in both, differently: conflict")]
    [DataRow("x2", null, "x1", SyncStatus.Conflict, ConflictKind.LocalModifiedRemoteDeleted, DisplayName = "Modified here, deleted there: conflict")]
    [DataRow(null, "x2", "x1", SyncStatus.Conflict, ConflictKind.LocalDeletedRemoteModified, DisplayName = "Deleted here, modified there: conflict")]
    public void A_file_stands_where_its_three_states_say(string? local, string? remote, string? baseline, SyncStatus status, ConflictKind conflict)
    {
        var item = SyncComparer.Classify("a.txt", local is null ? null : F(local), remote is null ? null : F(remote), baseline is null ? null : B(baseline));
        Assert.AreEqual(status, item.Status);
        Assert.AreEqual(conflict, item.Conflict);
    }

    [TestMethod]
    public void A_newer_time_on_the_server_alone_is_not_a_change()
    {
        // The same content, listed later: content decides, never which side's clock is ahead.
        var item = SyncComparer.Classify("a.txt", new FileState(2, "x1", DateTime.UtcNow.AddDays(-9)), new FileState(2, "x1", DateTime.UtcNow), B("x1"));
        Assert.AreEqual(SyncStatus.Synchronized, item.Status);
    }

    [TestMethod]
    public void The_default_rules_leave_out_what_belongs_to_one_PC_or_server()
    {
        var rules = IgnoreRules.Parse(IgnoreRules.Defaults);
        Assert.IsTrue(rules.IsIgnored("web.config", false));
        Assert.IsFalse(rules.IsIgnored("DesktopModules/Blog/web.config", false), "A module's web.config is DNN's code.");
        Assert.IsTrue(rules.IsIgnored("App_Data/Database.mdf", false));
        Assert.IsTrue(rules.IsIgnored("Portals/0/Cache/a.resources", false));
        Assert.IsTrue(rules.IsIgnored("Portals/_default/Logs/2026.log.resources", false));
        Assert.IsTrue(rules.IsIgnored("node_modules/x/index.js", false));
        Assert.IsTrue(rules.IsIgnored("site.log", false));
        Assert.IsFalse(rules.IsIgnored("bin/DotNetNuke.dll", false));
        Assert.IsFalse(rules.IsIgnored("Portals/0/site.css", false));
    }

    [TestMethod]
    public void Ignore_rules_follow_gitignore()
    {
        var rules = IgnoreRules.Parse("""
            # a comment
            *.tmp
            !keep.tmp
            /root-only.txt
            build/
            docs/**/*.pdf
            Thumbs.DB
            """);
        Assert.IsTrue(rules.IsIgnored("a/b/c.tmp", false));
        Assert.IsFalse(rules.IsIgnored("a/keep.tmp", false), "! takes a file back in.");
        Assert.IsTrue(rules.IsIgnored("root-only.txt", false));
        Assert.IsFalse(rules.IsIgnored("sub/root-only.txt", false), "A leading / ties the pattern to the top.");
        Assert.IsTrue(rules.IsIgnored("src/build/out.dll", false), "Everything in an ignored folder is ignored.");
        Assert.IsFalse(rules.IsIgnored("build", false), "A trailing / matches only a folder.");
        Assert.IsTrue(rules.IsIgnored("docs/a/b/c.pdf", false));
        Assert.IsTrue(rules.IsIgnored("docs/c.pdf", false), "**/ matches no folder too.");
        Assert.IsTrue(rules.IsIgnored("thumbs.db", false), "Names are matched without case, as on Windows.");
        Assert.AreEqual(1, IgnoreRules.Parse("ok\n[broken").Problems.Count);
    }

    [TestMethod]
    public void Secrets_and_configuration_are_flagged()
    {
        Assert.IsTrue(IgnoreRules.IsSensitive("web.config"));
        Assert.IsTrue(IgnoreRules.IsSensitive("certs/site.pfx"));
        Assert.IsTrue(IgnoreRules.IsSensitive("appsettings.Production.json"));
        Assert.IsTrue(IgnoreRules.IsSensitive(".env"));
        Assert.IsFalse(IgnoreRules.IsSensitive("DesktopModules/Blog/web.config"));
        Assert.IsFalse(IgnoreRules.IsSensitive("Portals/0/site.css"));
    }

    [TestMethod]
    [DataRow("../etc/passwd")]
    [DataRow("a/../../b")]
    [DataRow("/etc/passwd")]
    [DataRow("C:/Windows/win.ini")]
    [DataRow("a/./b")]
    [DataRow("a/CON.txt")]
    [DataRow("a/b.")]
    [DataRow("a\u0001b")]
    [DataRow("a/b:stream")]
    public void A_path_that_could_leave_the_folder_is_refused(string path) => Assert.IsNull(DeployPath.Normalize(path));

    [TestMethod]
    public void The_usual_ports_and_logons_are_FileZillas()
    {
        Assert.AreEqual(22, DeployConnection.DefaultPort(TransferProtocol.Sftp));
        Assert.AreEqual(990, DeployConnection.DefaultPort(TransferProtocol.Ftp, FtpEncryption.RequireImplicit));
        Assert.AreEqual(21, DeployConnection.DefaultPort(TransferProtocol.Ftp, FtpEncryption.RequireExplicit));
        Assert.AreEqual(21, DeployConnection.DefaultPort(TransferProtocol.Ftp, FtpEncryption.PlainOnly));
        var ftp = new DeployConnection { Site = "s", Name = "Live", Protocol = TransferProtocol.Ftp, Host = "ftp.example.com", Port = 21, UserName = "", RemoteRoot = "/" };
        Assert.AreEqual(0, DeployConnections.Validate(ftp with { Auth = DeployAuth.Anonymous }, DeploySecrets.None).Count, "Anonymous needs no user or password.");
        Assert.IsTrue(DeployConnections.Validate(ftp with { Protocol = TransferProtocol.Sftp, Auth = DeployAuth.Anonymous }, DeploySecrets.None).ContainsKey("Auth"));
        Assert.IsTrue(DeployConnections.Validate(ftp with { UserName = "u" }, DeploySecrets.None).ContainsKey("Password"));
        StringAssert.StartsWith((ftp with { Encryption = FtpEncryption.RequireImplicit, Port = 990, UserName = "u" }).Address, "ftps://u@ftp.example.com:990");
    }

    [TestMethod]
    public void The_deploy_settings_are_saved_checked_and_given_to_a_new_server()
    {
        var root = Path.Combine(Path.GetTempPath(), "DnnManagerTests", "deploy-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DnnManager.Infrastructure.Settings.SettingsStore(new DnnManager.Infrastructure.Settings.AppDataPaths(root));
            store.Load();
            var defaults = store.Read().Deploy;
            Assert.IsTrue(defaults.CompareOnOpen);
            Assert.AreEqual(90, defaults.KeepVersionsDays);
            Assert.AreEqual(4, defaults.Parallel);
            Assert.AreEqual(IgnoreRules.Defaults, defaults.IgnoreRules);

            store.Update(s =>
            {
                s.Deploy.CompareOnOpen = false;
                s.Deploy.KeepVersionsDays = 0;
                s.Deploy.Parallel = 2;
                s.Deploy.VerifyUploads = false;
                s.Deploy.IgnoreRules = "/web.config\n*.bak";
            });
            var saved = store.SavedValues();
            Assert.AreEqual("false", saved["deploy.compareOnOpen"], "A row of its own.");
            var read = store.Read();
            Assert.AreEqual("/web.config\n*.bak", read.Deploy.IgnoreRules.Replace("\r\n", "\n"), "The rules come back line for line.");
            var options = read.ToAppOptions();
            Assert.AreEqual(0, options.Deploy.KeepVersionsDays);
            Assert.AreEqual("Deploy__Parallel", DnnManager.Application.Configuration.UserSettings.OptionNameOf("deploy.parallel"));

            var connections = new DeployConnections(new DeployHarness().Store, new FolderServer(Path.Combine(root, "s")).Factory,
                Microsoft.Extensions.Options.Options.Create(options));
            var server = connections.New("shop");
            Assert.AreEqual(2, server.Parallel);
            Assert.AreEqual(VerifyMode.Size, server.Verify);
            Assert.AreEqual("/web.config\n*.bak", server.IgnoreRules.Replace("\r\n", "\n"));
            Assert.IsFalse(connections.CompareOnOpen);

            read.Deploy.Parallel = 99;
            read.Deploy.KeepVersionsDays = -1;
            var problems = read.Validate().Select(p => p.Key).ToList();
            CollectionAssert.Contains(problems, "deploy.parallel");
            CollectionAssert.Contains(problems, "deploy.keepVersionsDays");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void Paths_are_made_relative_with_slashes()
    {
        Assert.AreEqual("Portals/0/site.css", DeployPath.Normalize(@"Portals\0\site.css"));
        Assert.AreEqual("/var/www/shop", DeployPath.NormalizeRemoteRoot(@"/var/www/shop/"));
        Assert.IsNull(DeployPath.NormalizeRemoteRoot("/var/www/../etc"));
        Assert.AreEqual("/var/www/shop/a/b.txt", DeployPath.Remote("/var/www/shop", "a/b.txt"));
        Assert.AreEqual("/a.txt", DeployPath.Remote("/", "a.txt"));
        Assert.AreEqual("a/b.txt", DeployPath.RelativeTo("/var/www/shop", "/var/www/shop/a/b.txt"));
        Assert.IsNull(DeployPath.RelativeTo("/var/www/shop", "/var/www/shopping/a.txt"), "A folder named like the root isn't in it.");
        Assert.IsTrue(DeployPath.IsOwnFile(DeployPath.PartName("a/b.txt", new string('a', 64))));
        Assert.IsTrue(DeployPath.IsOwnFile(DeployPath.LockFileName));
    }

    [TestMethod]
    public void The_diff_turns_the_old_text_into_the_new()
    {
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var old = Enumerable.Range(0, random.Next(0, 30)).Select(_ => "l" + random.Next(0, 6)).ToList();
            var @new = Enumerable.Range(0, random.Next(0, 30)).Select(_ => "l" + random.Next(0, 6)).ToList();
            var diff = TextDiff.Compute(old, @new);
            CollectionAssert.AreEqual(old, diff.Where(l => l.Kind != DiffKind.Added).Select(l => l.Text).ToList());
            CollectionAssert.AreEqual(@new, diff.Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text).ToList());
        }
    }

    [TestMethod]
    public void A_diff_has_hunks_and_two_columns()
    {
        var old = Enumerable.Range(1, 20).Select(i => $"line {i}").ToList();
        var @new = old.ToList();
        @new[4] = "line five";
        @new.Insert(15, "inserted");
        var diff = TextDiff.Compute(old, @new);
        var hunks = TextDiff.Hunks(diff);
        Assert.AreEqual(2, hunks.Count);
        Assert.AreEqual("@@ -2,7 +2,7 @@", hunks[0].Header);
        var rows = TextDiff.SideBySide(diff);
        var changed = rows.Single(r => r.Left?.Kind == DiffKind.Removed);
        Assert.AreEqual("line 5", changed.Left!.Text);
        Assert.AreEqual("line five", changed.Right!.Text);
        StringAssert.Contains(TextDiff.Unified(diff, "a", "b"), "+inserted");
    }

    [TestMethod]
    public void Changes_to_different_lines_merge_without_a_conflict()
    {
        string[] origin = ["a", "b", "c", "d", "e"];
        string[] local = ["a", "B", "c", "d", "e"];
        string[] remote = ["a", "b", "c", "d", "E", "f"];
        var merge = ThreeWayMerge.Merge(origin, local, remote);
        Assert.IsFalse(merge.HasConflicts);
        CollectionAssert.AreEqual(new[] { "a", "B", "c", "d", "E", "f" }, merge.WithMarkers().ToList());
    }

    [TestMethod]
    public void Changes_to_the_same_line_are_a_conflict_with_markers()
    {
        var merge = ThreeWayMerge.Merge(["a", "b", "c"], ["a", "local", "c"], ["a", "remote", "c"]);
        Assert.AreEqual(1, merge.ConflictCount);
        var lines = merge.WithMarkers();
        CollectionAssert.AreEqual(new[] { "a", MergeResult.MarkerLocal, "local", MergeResult.MarkerSeparator, "remote", MergeResult.MarkerRemote, "c" }, lines.ToList());
        Assert.IsTrue(MergeResult.HasMarkers(lines));
    }

    [TestMethod]
    public void Without_a_common_version_every_difference_is_a_conflict()
    {
        var merge = ThreeWayMerge.Merge(null, ["a", "x", "c"], ["a", "c"]);
        Assert.AreEqual(1, merge.ConflictCount, "Without the baseline there is no telling whether x was added or removed.");
    }

    [TestMethod]
    public void Text_keeps_how_it_was_written()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n' };
        var text = TextContent.From(bytes)!;
        CollectionAssert.AreEqual(new[] { "a", "b" }, text.Lines.ToList());
        Assert.AreEqual("\r\n", text.Newline);
        CollectionAssert.AreEqual(bytes, text.Encode(text.Lines));
        Assert.IsNull(TextContent.From([1, 0, 2]), "A NUL byte: binary.");
    }
}

/// <summary>
/// Push, pull, conflicts and rollback, end to end against a stand-in server (<see cref="FolderServer"/>): the project and
/// the server are folders of the test's own, DNN Manager's records a database of its own.
/// </summary>
[TestClass]
public sealed class DeploymentWorkflowTests
{
    private static async Task<DeployHarness> InSync(params (string Path, string Content)[] files)
    {
        var h = new DeployHarness();
        foreach (var (path, content) in files)
        {
            h.WriteLocal(path, content);
            h.Server.Write(path, content);
        }
        var scan = await h.ScanAsync();
        Assert.IsTrue(scan.Items.All(i => i.Status == SyncStatus.Synchronized), h.Reporter.Text);
        return h;
    }

    [TestMethod]
    public async Task The_first_scan_makes_the_baseline_and_the_next_reads_nothing()
    {
        using var h = await InSync(("a.txt", "one"), ("b/c.txt", "two"));
        h.Server.CanHash = false;
        var before = h.Server.Downloads.Count;
        var scan = await h.ScanAsync();
        Assert.AreEqual(2, scan.Count(SyncDirection.None));
        Assert.AreEqual(before, h.Server.Downloads.Count, "An unchanged server is compared by its listing and the baseline, not by reading it.");
        Assert.AreEqual(2, h.Store.Baseline(h.Connection.Id).Count);
    }

    [TestMethod]
    public async Task The_first_scan_without_server_hashes_reads_only_files_of_the_same_size()
    {
        using var h = new DeployHarness();
        h.Server.CanHash = false;
        h.WriteLocal("same.txt", "same");
        h.Server.Write("same.txt", "same");
        h.WriteLocal("sized.txt", "short");
        h.Server.Write("sized.txt", "much longer");
        var scan = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.Synchronized, scan.Find("same.txt")!.Status);
        Assert.AreEqual(SyncStatus.Conflict, scan.Find("sized.txt")!.Status, "Different on both sides without a baseline.");
        CollectionAssert.AreEqual(new[] { "same.txt" }, h.Server.Downloads.ToArray(), "A file whose size already differs isn't read.");
    }

    [TestMethod]
    public async Task A_local_change_is_pushed_alone_and_recorded()
    {
        using var h = await InSync(("a.txt", "one"), ("b.txt", "two"), ("c.txt", "three"));
        h.WriteLocal("b.txt", "two, changed");
        var scan = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.LocalModified, scan.Find("b.txt")!.Status);

        var pushed = await h.PushAsync(scan, "b.txt");
        Assert.IsTrue(pushed.Success, pushed.Error + h.Reporter.Text);
        Assert.AreEqual("two, changed", h.Server.Read("b.txt"));
        Assert.AreEqual(1, h.Server.Uploads.Count(u => u.Path.Contains("b.txt")), "Only the changed file is sent.");
        Assert.AreEqual(0, h.Server.Uploads.Count(u => !u.Path.Contains("b.txt") && !u.Path.Contains(DeployPath.LockFileName)));
        Assert.IsNull(h.Server.Read(DeployPath.LockFileName), "The lock is let go of.");

        var history = h.Store.Operations(h.Connection.Id);
        Assert.AreEqual(DeployOperationKind.Push, history[0].Kind);
        Assert.AreEqual(DeployOperationStatus.Completed, history[0].Status);
        Assert.AreEqual("Test push", history[0].Message);
        var file = h.Store.Files(history[0].Id).Single();
        Assert.AreEqual(FileAction.Modified, file.Action);
        Assert.AreEqual(Hashing.OfBytes(Encoding.UTF8.GetBytes("two")), file.BeforeHash);
        Assert.AreEqual(Hashing.OfBytes(Encoding.UTF8.GetBytes("two, changed")), file.AfterHash);

        Assert.IsTrue((await h.ScanAsync()).Items.All(i => i.Status == SyncStatus.Synchronized));
    }

    [TestMethod]
    public async Task Changes_made_on_the_live_server_are_found_and_pulled_selectively()
    {
        // The request's own example: production edited directly, development untouched.
        using var h = await InSync(("config.json", "{ }"), ("Controllers/HomeController.cs", "class Home {}"), ("old-file.css", "a {}"),
            ("Program.cs", "Main()"), ("README.md", "readme"));
        h.Server.Write("config.json", "{ \"live\": true }");
        h.Server.Write("Controllers/HomeController.cs", "class Home { /* hotfix */ }");
        h.Server.Write("new-feature.js", "feature()");
        File.Delete(h.Server.PathOf("old-file.css"));
        h.Server.Write("Program.cs", "Main() // live");
        h.WriteLocal("Program.cs", "Main() // dev");

        var scan = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.RemoteModified, scan.Find("config.json")!.Status);
        Assert.AreEqual(SyncStatus.RemoteModified, scan.Find("Controllers/HomeController.cs")!.Status);
        Assert.AreEqual(SyncStatus.RemoteAdded, scan.Find("new-feature.js")!.Status);
        Assert.AreEqual(SyncStatus.RemoteDeleted, scan.Find("old-file.css")!.Status);
        Assert.AreEqual(SyncStatus.Conflict, scan.Find("Program.cs")!.Status);
        Assert.AreEqual(SyncStatus.Synchronized, scan.Find("README.md")!.Status);

        var pulled = await h.PullAsync(scan, "Controllers/HomeController.cs", "new-feature.js");
        Assert.IsTrue(pulled.Success, pulled.Error + h.Reporter.Text);
        Assert.AreEqual("class Home { /* hotfix */ }", h.ReadLocal("Controllers/HomeController.cs"));
        Assert.AreEqual("feature()", h.ReadLocal("new-feature.js"));
        Assert.AreEqual("{ }", h.ReadLocal("config.json"), "A file not chosen isn't pulled.");
        Assert.AreEqual("a {}", h.ReadLocal("old-file.css"), "A deletion not chosen isn't applied.");
        Assert.AreEqual("Main() // dev", h.ReadLocal("Program.cs"), "The project's own work is never overwritten by a newer server.");

        var after = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.Synchronized, after.Find("Controllers/HomeController.cs")!.Status);
        Assert.AreEqual(SyncStatus.Synchronized, after.Find("new-feature.js")!.Status);
        Assert.AreEqual(SyncStatus.RemoteModified, after.Find("config.json")!.Status);
    }

    [TestMethod]
    public async Task A_pull_never_overwrites_a_file_changed_in_the_project_after_the_scan()
    {
        using var h = await InSync(("a.txt", "one"), ("b.txt", "two"));
        h.Server.Write("a.txt", "one, live");
        h.Server.Write("b.txt", "two, live");
        var scan = await h.ScanAsync();
        h.WriteLocal("b.txt", "two, my unsaved work");

        var pulled = await h.PullAsync(scan, "a.txt", "b.txt");
        Assert.IsFalse(pulled.Success);
        StringAssert.Contains(pulled.Error, "b.txt was changed in the project since the scan");
        Assert.AreEqual("two, my unsaved work", h.ReadLocal("b.txt"));
        Assert.AreEqual("one", h.ReadLocal("a.txt"), "Nothing of the pull is applied when one file can't be.");
    }

    [TestMethod]
    public async Task A_push_never_overwrites_a_file_changed_on_the_server_after_the_scan()
    {
        using var h = await InSync(("a.txt", "one"));
        h.WriteLocal("a.txt", "one, dev");
        var scan = await h.ScanAsync();
        h.Server.Write("a.txt", "one, edited live meanwhile");

        var pushed = await h.PushAsync(scan, "a.txt");
        Assert.IsFalse(pushed.Success);
        StringAssert.Contains(pushed.Error, "changed on the server since the scan");
        Assert.AreEqual("one, edited live meanwhile", h.Server.Read("a.txt"));
    }

    [TestMethod]
    public async Task A_conflict_is_refused_until_it_is_resolved()
    {
        using var h = await InSync(("a.txt", "base"));
        h.WriteLocal("a.txt", "dev");
        h.Server.Write("a.txt", "live");
        var scan = await h.ScanAsync();
        var item = scan.Find("a.txt")!;
        Assert.AreEqual(SyncStatus.Conflict, item.Status);
        var push = await h.PushAsync(scan, "a.txt");
        Assert.IsFalse(push.Success);
        StringAssert.Contains(push.Error, "resolve the conflict first");
        Assert.AreEqual("live", h.Server.Read("a.txt"));

        // Keep the project's: the server's change is set aside, and pushing it is an ordinary push.
        var kept = await h.ResolveAsync(scan, "a.txt", ConflictChoice.KeepLocal);
        Assert.IsTrue(kept.Success, kept.Error);
        Assert.AreEqual("live", h.Server.Read("a.txt"), "Resolving never changes the server.");
        var next = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.LocalModified, next.Find("a.txt")!.Status);
        Assert.IsTrue((await h.PushAsync(next, "a.txt")).Success);
        Assert.AreEqual("dev", h.Server.Read("a.txt"));
    }

    [TestMethod]
    public async Task A_conflict_can_take_the_servers_version_or_a_merge()
    {
        using var h = await InSync(("a.txt", "base"), ("m.txt", "1\n2\n3\n"));
        h.WriteLocal("a.txt", "dev");
        h.Server.Write("a.txt", "live");
        h.WriteLocal("m.txt", "1 dev\n2\n3\n");
        h.Server.Write("m.txt", "1\n2\n3 live\n");
        var scan = await h.ScanAsync();

        Assert.IsTrue((await h.ResolveAsync(scan, "a.txt", ConflictChoice.TakeRemote)).Success);
        Assert.AreEqual("live", h.ReadLocal("a.txt"));

        // The baseline content was kept by the first scan? No - only what DNN Manager sent; m.txt's merge is two-way here.
        var merged = Encoding.UTF8.GetBytes("1 dev\n2\n3 live\n");
        Assert.IsTrue((await h.ResolveAsync(scan, "m.txt", ConflictChoice.Merged, merged)).Success, h.Reporter.Text);
        Assert.AreEqual("1 dev\n2\n3 live\n", h.ReadLocal("m.txt"));

        var after = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.Synchronized, after.Find("a.txt")!.Status);
        Assert.AreEqual(SyncStatus.LocalModified, after.Find("m.txt")!.Status, "A merge is pushed like any change.");
        Assert.IsTrue((await h.PushAsync(after, "m.txt")).Success);
        Assert.AreEqual("1 dev\n2\n3 live\n", h.Server.Read("m.txt"));
        Assert.AreEqual(2, h.Store.Operations(h.Connection.Id).Count(o => o.Kind == DeployOperationKind.Resolve));
    }

    [TestMethod]
    public async Task Deletions_go_both_ways_only_when_chosen()
    {
        using var h = await InSync(("gone-here.txt", "1"), ("gone-there.txt", "2"), ("folder/only.txt", "3"));
        h.DeleteLocal("gone-here.txt");
        h.DeleteLocal("folder/only.txt");
        File.Delete(h.Server.PathOf("gone-there.txt"));
        var scan = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.LocalDeleted, scan.Find("gone-here.txt")!.Status);
        Assert.AreEqual(SyncStatus.RemoteDeleted, scan.Find("gone-there.txt")!.Status);

        Assert.IsTrue((await h.PushAsync(scan, "gone-here.txt", "folder/only.txt")).Success, h.Reporter.Text);
        Assert.IsNull(h.Server.Read("gone-here.txt"));
        Assert.IsFalse(Directory.Exists(h.Server.PathOf("folder")), "A folder the deletion left empty goes too.");
        Assert.IsTrue((await h.PullAsync(scan, "gone-there.txt")).Success, h.Reporter.Text);
        Assert.IsNull(h.ReadLocal("gone-there.txt"));
        Assert.AreEqual(0, (await h.ScanAsync()).Items.Count);
    }

    [TestMethod]
    public async Task A_transfer_that_fails_on_the_network_is_tried_again()
    {
        using var h = await InSync(("a.txt", "one"));
        h.WriteLocal("a.txt", "one, dev");
        h.Server.FailTransient["a.txt"] = 2;
        var scan = await h.ScanAsync();
        var pushed = await h.PushAsync(scan, "a.txt");
        Assert.IsTrue(pushed.Success, pushed.Error);
        Assert.AreEqual("one, dev", h.Server.Read("a.txt"));
        Assert.AreEqual(2, pushed.Value!.Retries);
    }

    [TestMethod]
    public async Task An_interrupted_upload_is_resumed_not_sent_again()
    {
        using var h = await InSync(("big.bin", "x"));
        var content = new string('z', 200_000);
        h.WriteLocal("big.bin", content);
        h.Server.CutOnce["big.bin"] = true;
        var scan = await h.ScanAsync();
        var pushed = await h.PushAsync(scan, "big.bin");
        Assert.IsTrue(pushed.Success, pushed.Error + h.Reporter.Text);
        Assert.AreEqual(content, h.Server.Read("big.bin"));
        Assert.IsTrue(h.Server.Uploads.Any(u => u.Offset > 0), "The second try goes on from where the first stopped.");
        Assert.IsFalse(Directory.EnumerateFiles(h.Server.Root).Any(f => f.EndsWith(DeployPath.PartSuffix)), "No part is left.");
    }

    [TestMethod]
    public async Task A_push_that_fails_puts_back_what_it_sent()
    {
        using var h = await InSync(("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"));
        h.WriteLocal("a.txt", "1 new");
        h.WriteLocal("b.txt", "2 new");
        h.WriteLocal("c.txt", "3 new");
        h.WriteLocal("d.txt", "4 added");
        h.Change(c => c with { Parallel = 1 });
        h.Server.Refuse["c.txt"] = true;
        var scan = await h.ScanAsync();
        var pushed = await h.PushAsync(scan, "a.txt", "b.txt", "c.txt", "d.txt");
        Assert.IsFalse(pushed.Success);
        StringAssert.Contains(pushed.Error, "permission denied");
        Assert.AreEqual("1", h.Server.Read("a.txt"), h.Reporter.Text);
        Assert.AreEqual("2", h.Server.Read("b.txt"));
        Assert.AreEqual("3", h.Server.Read("c.txt"));
        Assert.IsNull(h.Server.Read("d.txt"), "A file the push added is deleted again.");
        Assert.IsNull(h.Server.Read(DeployPath.LockFileName), "The undo lets go of the lock.");
        Assert.AreEqual(DeployOperationStatus.Failed, h.Store.Operations(h.Connection.Id)[0].Status);
        var again = await h.ScanAsync();
        Assert.AreEqual(4, again.Count(SyncDirection.Push), "Nothing became the baseline.");
    }

    [TestMethod]
    public async Task A_push_is_rolled_back_from_its_snapshots()
    {
        using var h = await InSync(("a.txt", "v1"), ("b.txt", "keep"));
        h.WriteLocal("a.txt", "v2");
        h.WriteLocal("new.txt", "added");
        var scan = await h.ScanAsync();
        var pushed = await h.PushAsync(scan, "a.txt", "new.txt");
        Assert.IsTrue(pushed.Success, pushed.Error);

        var rolledBack = await h.RollbackAsync(pushed.Value!.Id);
        Assert.IsTrue(rolledBack.Success, rolledBack.Error + h.Reporter.Text);
        Assert.AreEqual("v1", h.Server.Read("a.txt"));
        Assert.IsNull(h.Server.Read("new.txt"));
        Assert.AreEqual(DeployOperationKind.Rollback, rolledBack.Value!.Kind);
        Assert.AreEqual(rolledBack.Value.Id, h.Store.Operation(pushed.Value.Id)!.RolledBackBy);

        var after = await h.ScanAsync();
        Assert.AreEqual(SyncStatus.LocalModified, after.Find("a.txt")!.Status, "The project still has v2: it is a change to push again.");
        Assert.AreEqual(SyncStatus.LocalAdded, after.Find("new.txt")!.Status);
        Assert.IsFalse((await h.RollbackAsync(pushed.Value.Id)).Success, "An operation is rolled back once.");
    }

    [TestMethod]
    public async Task A_rollback_never_overwrites_a_newer_change()
    {
        using var h = await InSync(("a.txt", "v1"));
        h.WriteLocal("a.txt", "v2");
        var pushed = await h.PushAsync(await h.ScanAsync(), "a.txt");
        h.Server.Write("a.txt", "v3, a hotfix on the server");
        var rolledBack = await h.RollbackAsync(pushed.Value!.Id);
        Assert.IsFalse(rolledBack.Success);
        Assert.AreEqual("v3, a hotfix on the server", h.Server.Read("a.txt"));
    }

    [TestMethod]
    public async Task A_pull_is_rolled_back_in_the_project()
    {
        using var h = await InSync(("a.txt", "dev"));
        h.Server.Write("a.txt", "live");
        var pulled = await h.PullAsync(await h.ScanAsync(), "a.txt");
        Assert.AreEqual("live", h.ReadLocal("a.txt"));
        var rolledBack = await h.RollbackAsync(pulled.Value!.Id);
        Assert.IsTrue(rolledBack.Success, rolledBack.Error);
        Assert.AreEqual("dev", h.ReadLocal("a.txt"));
        Assert.AreEqual(SyncStatus.RemoteModified, (await h.ScanAsync()).Find("a.txt")!.Status, "The server's change is one to pull again.");
    }

    [TestMethod]
    public async Task A_review_pull_changes_nothing_in_the_project()
    {
        using var h = await InSync(("a.txt", "dev"));
        h.Server.Write("a.txt", "live");
        h.Server.Write("b.txt", "new");
        var scan = await h.ScanAsync();
        var review = Path.Combine(h.Folder, "review");
        var pulled = await h.RunAsync((engine, undo) => new PullChangesUseCase(h.Store, h.Snapshots, h.Server.Factory, engine, undo)
            .ExecuteAsync(new PullRequest(scan, ["a.txt", "b.txt"], "look", ReviewFolder: review), h.Reporter, CancellationToken.None));
        Assert.IsTrue(pulled.Success, pulled.Error);
        Assert.AreEqual("live", File.ReadAllText(Path.Combine(review, "a.txt")));
        Assert.AreEqual("dev", h.ReadLocal("a.txt"));
        Assert.IsNull(h.ReadLocal("b.txt"));
        Assert.AreEqual(review, h.Store.Operation(pulled.Value!.Id)!.ReviewFolder);
        Assert.AreEqual(2, (await h.ScanAsync()).Count(SyncDirection.Pull), "The baseline didn't move.");
    }

    [TestMethod]
    public async Task Ignored_files_are_neither_compared_nor_sent()
    {
        using var h = await InSync(("index.html", "x"));
        h.WriteLocal("web.config", "<local connection string>");
        h.Server.Write("web.config", "<live connection string>");
        h.WriteLocal("App_Data/Database.mdf", "data");
        var scan = await h.ScanAsync();
        Assert.IsNull(scan.Find("web.config"));
        Assert.IsNull(scan.Find("App_Data/Database.mdf"));
    }

    [TestMethod]
    public async Task A_secret_is_sent_only_when_agreed_to()
    {
        using var h = await InSync(("index.html", "x"));
        h.WriteLocal("certs/site.pfx", "secret");
        var scan = await h.ScanAsync();
        Assert.IsTrue(scan.Find("certs/site.pfx")!.Sensitive);
        var refused = await h.PushAsync(scan, "certs/site.pfx");
        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Error, "secrets");
        var agreed = await h.RunAsync((engine, undo) => new PushChangesUseCase(h.Store, h.Snapshots, h.Server.Factory, engine, undo)
            .ExecuteAsync(new PushRequest(scan, ["certs/site.pfx"], "cert", AllowSensitive: true), h.Reporter, CancellationToken.None));
        Assert.IsTrue(agreed.Success, agreed.Error);
    }

    [TestMethod]
    public async Task Another_deployment_holding_the_lock_is_waited_for_but_a_stale_lock_is_taken_over()
    {
        using var h = await InSync(("a.txt", "1"));
        h.WriteLocal("a.txt", "2");
        h.Server.Write(DeployPath.LockFileName, $"COLLEAGUE\\pat on PC2\n{DateTime.UtcNow:o}\nx\n");
        var scan = await h.ScanAsync();
        Assert.IsNull(scan.Find(DeployPath.LockFileName), "The lock isn't a file of the project.");
        var refused = await h.PushAsync(scan, "a.txt");
        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Error, "COLLEAGUE\\pat on PC2 is deploying");

        // Not renewed for two hours, by the server's clock.
        h.Server.Write(DeployPath.LockFileName, $"COLLEAGUE\\pat on PC2\n{DateTime.UtcNow.AddHours(-2):o}\nx\n", DateTime.UtcNow.AddHours(-2));
        Assert.IsTrue((await h.PushAsync(scan, "a.txt")).Success, h.Reporter.Text);
    }

    [TestMethod]
    public async Task A_path_outside_the_project_is_never_written()
    {
        using var h = await InSync(("a.txt", "1"));
        var undo = new DnnManager.Application.UseCases.OperationUndo();
        var engine = new SyncEngine(h.Snapshots, h.Server.Factory, h.Store, undo);
        var hash = await h.Snapshots.AddAsync(new MemoryStream("evil"u8.ToArray()), CancellationToken.None);
        var result = await engine.ApplyLocalAsync(h.Local, true, [new FileChange("../outside.txt", hash, 4, ExpectedState.Absent)], h.Reporter, null, CancellationToken.None);
        Assert.IsFalse(result.Success);
        Assert.IsFalse(File.Exists(Path.Combine(h.Folder, "outside.txt")));
    }

    [TestMethod]
    public void An_asked_password_is_never_saved()
    {
        using var h = new DeployHarness();
        var connections = new DeployConnections(h.Store, h.Server.Factory);
        var asked = h.Connection with { Auth = DeployAuth.AskForPassword };
        connections.Save(asked, new DeploySecrets("typed for the test"));
        using (var db = h.Database.Open())
            Assert.IsNull(AppDatabase.Scalar<string>(db, "SELECT password FROM deploy_connections"), "Not in the database.");
        Assert.AreEqual("typed for the test", h.Store.ReadSecrets(asked.Id).Password, "Kept for the session.");
        Assert.IsFalse(connections.NeedsPassword(asked));
        connections.Delete(asked.Id);
        Assert.IsTrue(connections.NeedsPassword(asked), "Forgotten with the connection.");
    }

    [TestMethod]
    public async Task Secrets_are_kept_encrypted()
    {
        using var h = new DeployHarness();
        h.Store.SaveSecrets(h.Connection.Id, new DeploySecrets("p@ssw0rd!", "-----BEGIN OPENSSH PRIVATE KEY-----", "phrase"));
        using var db = h.Database.Open();
        var raw = AppDatabase.Scalar<string>(db, "SELECT password || private_key || passphrase FROM deploy_connections");
        Assert.IsFalse(raw!.Contains("p@ssw0rd!") || raw.Contains("OPENSSH") || raw.Contains("phrase"));
        Assert.AreEqual("p@ssw0rd!", h.Store.ReadSecrets(h.Connection.Id).Password);
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task A_file_changed_on_the_server_while_a_push_runs_is_never_overwritten()
    {
        using var h = await InSync(("a.txt", "1"), ("b.txt", "2"));
        h.WriteLocal("a.txt", "1 new");
        h.WriteLocal("b.txt", "2 new");
        h.Change(c => c with { Parallel = 1 });
        var scan = await h.ScanAsync();
        // Checked before the transfers began - edited on the server while they ran.
        h.Server.BeforeUpload = path =>
        {
            if (path == "b.txt") h.Server.Write("b.txt", "2, a hotfix made during the push");
        };
        var pushed = await h.PushAsync(scan, "a.txt", "b.txt");
        Assert.IsFalse(pushed.Success);
        StringAssert.Contains(pushed.Error, "changed on the server while this operation ran");
        Assert.AreEqual("2, a hotfix made during the push", h.Server.Read("b.txt"));
        Assert.AreEqual("1", h.Server.Read("a.txt"), "What was sent is put back.");
    }

    [TestMethod]
    public async Task A_file_that_cant_be_read_is_never_taken_for_deleted()
    {
        using var h = await InSync(("locked.txt", "1"), ("a.txt", "2"));
        h.WriteLocal("locked.txt", "1 changed");
        // Open with no sharing (another program has it): the scan can't hash it.
        using (new FileStream(Path.Combine(h.Local, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var scan = await h.ScanAsync();
            Assert.IsNull(scan.Find("locked.txt"), "Not known - neither a change nor a deletion.");
            StringAssert.Contains(string.Join("\n", scan.Warnings), "locked.txt");
        }
        Assert.AreEqual(1, h.Store.Baseline(h.Connection.Id).Keys.Count(k => k == "locked.txt"), "Its baseline stays.");
        Assert.AreEqual(SyncStatus.LocalModified, (await h.ScanAsync()).Find("locked.txt")!.Status);
    }

    [TestMethod]
    public async Task A_server_folder_that_cant_be_listed_is_never_taken_for_deleted()
    {
        using var h = await InSync(("private/a.txt", "1"), ("b.txt", "2"));
        h.Server.Unlistable["private"] = true;
        var scan = await h.ScanAsync();
        Assert.IsNull(scan.Find("private/a.txt"));
        Assert.AreEqual(0, scan.Changes.Count());
    }

    [TestMethod]
    public async Task Names_that_differ_only_in_case_are_left_out()
    {
        using var h = await InSync(("a.txt", "1"));
        h.WriteLocal("Readme.md", "here");
        h.Server.Write("README.md", "there");
        var scan = await h.ScanAsync();
        Assert.IsNull(scan.Find("Readme.md"));
        StringAssert.Contains(string.Join("\n", scan.Warnings), "spells it README.md");
    }

    [TestMethod]
    public async Task A_resolution_that_kept_the_projects_version_has_nothing_to_roll_back()
    {
        using var h = await InSync(("a.txt", "base"));
        h.WriteLocal("a.txt", "dev");
        h.Server.Write("a.txt", "live");
        var kept = await h.ResolveAsync(await h.ScanAsync(), "a.txt", ConflictChoice.KeepLocal);
        var rolledBack = await h.RollbackAsync(kept.Value!.Id);
        Assert.IsFalse(rolledBack.Success);
        StringAssert.Contains(rolledBack.Error, "changed no file");
    }

    [TestMethod]
    public async Task A_cancelled_push_leaves_the_server_as_it_was()
    {
        using var h = await InSync(("a.txt", "1"), ("b.txt", "2"));
        h.WriteLocal("a.txt", "1 new");
        h.WriteLocal("b.txt", "2 new");
        h.Change(c => c with { Parallel = 1 });
        var scan = await h.ScanAsync();
        using var cts = new CancellationTokenSource();
        // Cancelled while the second file is tried again: the first is on the server by then.
        h.Server.FailTransient["b.txt"] = 3;
        var push = h.RunAsync((engine, undo) => new PushChangesUseCase(h.Store, h.Snapshots, h.Server.Factory, engine, undo)
            .ExecuteAsync(new PushRequest(scan, ["a.txt", "b.txt"], "x"), h.Reporter, cts.Token));
        await Task.Delay(500);
        await cts.CancelAsync();
        var result = await push;
        Assert.IsFalse(result.Success);
        Assert.AreEqual("1", h.Server.Read("a.txt"), h.Reporter.Text);
        Assert.AreEqual("2", h.Server.Read("b.txt"));
        Assert.AreEqual(DeployOperationStatus.Cancelled, h.Store.Operations(h.Connection.Id)[0].Status);
    }
}
