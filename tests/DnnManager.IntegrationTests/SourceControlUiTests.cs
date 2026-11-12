using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Deployment;
using DnnManager.Domain;
using DnnManager.Infrastructure;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using DnnManager.Presentation;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DnnManager.IntegrationTests;

/// <summary>
/// The Deploy page as the app makes it - its XAML, bindings and resources - offscreen, over a stand-in server: it compares
/// through the operation runner on its own when opened, lists what differs by direction, stages, and shows a file's diff.
/// With DEPLOY_TEST_SCREENSHOTS set to a folder it saves what it drew there (to look at, not to compare).
/// </summary>
[TestClass]
public sealed class SourceControlUiTests
{
    private static readonly List<ServiceProvider> Kept = [];

    [TestMethod]
    public async Task The_deploy_page_compares_on_opening_and_shows_each_change_with_its_diff()
    {
        var run = TestEnvironment.NewRunDirectory();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(run, "projects", "shop")).FullName;
            var server = new FolderServer(Path.Combine(run, "server"));
            void Both(string path, string content)
            {
                var file = Path.Combine(project, path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, content);
                server.Write(path, content);
            }
            Both("Portals/0/site.css", "body {\n  color: black;\n}\n");
            Both("Controllers/HomeController.cs", "public class Home\n{\n    public string Hello() => \"hi\";\n}\n");
            Both("index.html", "<h1>shop</h1>\n");

            var services = Services(run, server);
            var connections = services.GetRequiredService<DeployConnections>();
            var connection = new DeployConnection
            {
                Site = "shop", Name = "Live", Host = "shop.example.com", UserName = "deploy", RemoteRoot = "/var/www/shop",
                TrustedHostKey = "SHA256:test", IgnoreRules = IgnoreRules.Defaults
            };
            connections.Save(connection, new DeploySecrets("secret"));
            // A second server of the project, compared with a folder of its own on this PC (FileZilla's Default local
            // directory) - to choose it with the first.
            var staging = Directory.CreateDirectory(Path.Combine(run, "staging-copy")).FullName;
            File.WriteAllText(Path.Combine(staging, "only-here.txt"), "local");
            var second = connection with { Id = Guid.NewGuid(), Name = "Staging", Environment = DeployEnvironment.Staging, LocalRoot = staging };
            connections.Save(second, new DeploySecrets("secret"));
            Assert.AreEqual(staging, connections.Find(second.Id)!.LocalRoot, "The local folder is saved.");
            Assert.IsNull(connections.Find(connection.Id)!.LocalRoot, "None: the project's folder.");
            Assert.IsTrue(DeployConnections.Validate(second with { LocalRoot = Path.Combine(run, "missing") }, new DeploySecrets("x")).ContainsKey("LocalRoot"),
                "A local folder that isn't there is refused.");
            // In sync first: the baseline.
            await new SyncScanner(services.GetRequiredService<IDeployStore>(), services.GetRequiredService<ISnapshotStore>(), server.Factory,
                new LocalScanner(services.GetRequiredService<IDeployStore>())).ScanAsync(connection, project, new RecordingReporter(), CancellationToken.None);
            // Then a change here, a hotfix on the live site, a new file there, and one changed in both.
            File.WriteAllText(Path.Combine(project, "index.html"), "<h1>shop</h1>\n<p>new in development</p>\n");
            server.Write("Controllers/HomeController.cs", "public class Home\n{\n    // hotfix on live\n    public string Hello() => \"hello\";\n}\n");
            server.Write("new-feature.js", "export function feature() { return 42; }\n");
            File.WriteAllText(Path.Combine(project, "Portals", "0", "site.css"), "body {\n  color: navy;\n}\n");
            server.Write("Portals/0/site.css", "body {\n  color: red;\n}\n");

            await WpfUi.RunAsync(async () =>
            {
                // Made as the window makes it - before IIS's projects came in: the servers follow them once they do.
                var page = ActivatorUtilities.CreateInstance<SourceControlPage>(services);
                var host = Host(page);
                Assert.AreEqual(0, ((ListBox)page.FindName("ServersList")).Items.Count);
                services.GetRequiredService<ServerStore>().Projects.Add(new ProjectRow(new ProjectState { Name = "shop", Directory = project, SiteUrl = "", Site = new DnnManager.Application.Abstractions.IisSiteRuntime(1, "Started", "shop", "Started", [], [], project) }));
                page.Activated();
                var servers = (ListBox)page.FindName("ServersList");
                Assert.AreEqual(2, servers.Items.Count, "The project's servers show once the project is known.");
                var changes = (ListBox)page.FindName("Changes");
                // Nothing is read from a server until one is chosen.
                Assert.AreEqual(0, servers.SelectedItems.Count);
                await Task.Delay(300);
                Assert.IsNull(changes.ItemsSource, "Not compared before a server is chosen.");
                StringAssert.StartsWith(((TextBlock)page.FindName("ChangesMessage")).Text, "Choose a server");
                var live = servers.Items.Cast<ServerRow>().Single(r => r.Name == "Live");
                var stagingRow = servers.Items.Cast<ServerRow>().Single(r => r.Name == "Staging");
                Assert.AreEqual(project, live.Root);
                Assert.AreEqual(staging, stagingRow.Root, "Compared with its own local folder.");
                // Chosen: compared - as an operation, on the thread pool.
                servers.SelectedItem = live;
                for (var i = 0; i < 100 && changes.ItemsSource is null; i++) await Task.Delay(100);
                List<ChangeRow> Rows() => changes.ItemsSource is IEnumerable<ChangeRow> shown ? [.. shown] : [];
                var headers = Rows().Where(r => r.IsHeader).Select(r => $"{r.Title} {r.Count}").ToList();
                CollectionAssert.AreEqual(new[] { "Changes - to push 1", "Incoming - to pull 2", "Conflicts - changed in both 1" }, headers);
                var css = Rows().Single(r => r.Path == "Portals/0/site.css");
                Assert.AreEqual("!", css.Letter);
                Assert.IsFalse(css.CanStage, "A conflict is resolved, not staged.");
                Assert.AreEqual("A", Rows().Single(r => r.Path == "new-feature.js").Letter);
                Assert.AreEqual("M", Rows().Single(r => r.Path == "index.html").Letter);
                StringAssert.Contains(servers.Items.Cast<ServerRow>().Single(r => r.Name == "Live").Counts, "↑1 ↓2 !1");

                // A second server chosen with Ctrl+click: compared too, after the first, with its own folder; its changes
                // shown, and CHANGES' header switches back to the first's.
                servers.SelectedItems.Add(servers.Items.Cast<ServerRow>().Single(r => r.Name == "Staging"));
                for (var i = 0; i < 100 && !Rows().Any(r => r.Path == "only-here.txt"); i++) await Task.Delay(100);
                Assert.IsTrue(Rows().Any(r => r.Path == "only-here.txt"), "The Staging server's own local folder is compared.");
                Assert.AreEqual(2, servers.SelectedItems.Count);
                Assert.AreEqual(Visibility.Visible, ((Button)page.FindName("ChangesServer")).Visibility, "Two chosen: the header says whose changes these are.");
                page.ShowServer(servers.Items.Cast<ServerRow>().Single(r => r.Name == "Live"));
                Assert.AreEqual("shop · Live", ((TextBlock)page.FindName("ChangesServerText")).Text);
                Assert.IsTrue(Rows().Any(r => r.Path == "index.html") && !Rows().Any(r => r.Path == "only-here.txt"), "Live's changes again.");
                Assert.AreEqual(2, servers.SelectedItems.Count, "Both stay chosen.");
                servers.SelectedItem = servers.Items.Cast<ServerRow>().Single(r => r.Name == "Live");

                // Tabs, as VS Code's: a click opens the preview tab, the next click's file replaces it; a double-click keeps it.
                var tabs = (ListBox)page.FindName("Tabs");
                List<EditorTab> Open() => [.. tabs.Items.Cast<EditorTab>()];
                changes.SelectedItem = Rows().Single(r => r.Path == "index.html");
                Assert.AreEqual(1, Open().Count);
                Assert.IsTrue(Open()[0].IsPreview, "A click: the preview tab.");
                changes.SelectedItem = Rows().Single(r => r.Path == "new-feature.js");
                Assert.AreEqual("new-feature.js", Open().Single().Path, "The next click's file replaces the preview.");
                page.GetType().GetMethod("Changes_DoubleClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(page, [changes, null]);
                Assert.IsFalse(Open().Single().IsPreview, "A double-click keeps it.");
                changes.SelectedItem = Rows().Single(r => r.Path == "index.html");
                CollectionAssert.AreEqual(new[] { "new-feature.js", "index.html" }, Open().Select(t => t.Path).ToList(), "Kept, another opens beside it.");
                Assert.AreEqual("index.html", ((EditorTab)tabs.SelectedItem).Path);
                Assert.IsTrue(page.HasOpenTab);
                // Dragged to another place (or Ctrl+Shift+PageUp): the tabs swap, the chosen one still chosen.
                // A move doesn't choose the tab again - that would load its file again.
                var changed = 0;
                SelectionChangedEventHandler count = (_, _) => changed++;
                tabs.SelectionChanged += count;
                page.MoveTab(Open()[1], 0);
                tabs.SelectionChanged -= count;
                Assert.AreEqual(0, changed, "A move changed the chosen tab.");
                CollectionAssert.AreEqual(new[] { "index.html", "new-feature.js" }, Open().Select(t => t.Path).ToList(), "Moved to the front.");
                Assert.AreEqual("index.html", ((EditorTab)tabs.SelectedItem).Path, "Still the chosen tab.");
                page.MoveTab(Open()[0], 1);
                page.CloseActiveTab();
                Assert.AreEqual("new-feature.js", ((EditorTab)tabs.SelectedItem).Path, "Closed: the tab beside it is chosen.");
                page.CloseActiveTab();
                Assert.IsFalse(page.HasOpenTab);

                // Staged, a file moves to Staged Changes, and Push says how many.
                var primary = (Button)page.FindName("PrimaryButton");
                Assert.IsFalse(primary.IsEnabled);
                page.GetType().GetMethod("Stage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(page, [new[] { Rows().Single(r => r.Path == "index.html").Item! }, true]);
                Assert.AreEqual("Staged Changes 1", Rows().Where(r => r.IsHeader).Select(r => $"{r.Title} {r.Count}").First());
                Assert.IsTrue(primary.IsEnabled);
                Assert.AreEqual("Push 1", ((TextBlock)page.FindName("PrimaryLabel")).Text);

                // The diff of a server's hotfix: the project's version, then the server's.
                var diff = (DiffView)page.FindName("Diff");
                changes.SelectedItem = Rows().Single(r => r.Path == "Controllers/HomeController.cs");
                var rows = (ListBox)diff.FindName("Rows");
                for (var i = 0; i < 50 && rows.ItemsSource is null; i++) await Task.Delay(100);
                var diffRows = ((IEnumerable<DiffRow>)rows.ItemsSource!).ToList();
                Assert.IsTrue(diffRows.Any(r => r.Unified is { Kind: "Added", Text: "    // hotfix on live" }), "The server's added line is shown as added.");
                Assert.IsTrue(diffRows.Any(r => r.Unified.Kind == "Removed" && r.Unified.Text.Contains("\"hi\"")));
                StringAssert.Contains(((TextBlock)diff.FindName("Titles")).Text, "In the project → On the server");
                ((RadioButton)diff.FindName("SplitMode")).IsChecked = true;
                Save(host, "deploy-pull-diff.png");

                // A conflict shows its choices above its diff.
                changes.SelectedItem = Rows().Single(r => r.Path == "Portals/0/site.css");
                for (var i = 0; i < 50 && ((ListBox)diff.FindName("Rows")).ItemsSource is null; i++) await Task.Delay(100);
                Assert.AreEqual(Visibility.Visible, ((Border)page.FindName("ConflictBar")).Visibility);
                Save(host, "deploy-conflict-side-by-side.png");

                // The Explorer: the project's files, folders first, a changed file with its letter; a folder opens with a
                // click, a file opens as it is - one column of numbers, no diff - and shows in Open Editors.
                const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                page.ShowView(SideView.Explorer);
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)page.FindName("ExplorerView")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)page.FindName("ScmView")).Visibility);
                var tree = (ListBox)page.FindName("ExplorerTree");
                for (var i = 0; i < 50 && tree.Items.Count == 0; i++) await Task.Delay(100);
                List<ExplorerRow> Tree() => [.. tree.Items.Cast<ExplorerRow>()];
                Assert.AreEqual("M", Tree().Single(r => r.Path == "index.html").Letter);
                var controllers = Tree().Single(r => r.Path == "Controllers");
                Assert.IsTrue(controllers.IsFolder && Tree().IndexOf(controllers) < Tree().FindIndex(r => r.Path == "index.html"), "Folders first.");
                await (Task)page.GetType().GetMethod("ToggleFolderAsync", Private)!.Invoke(page, [controllers])!;
                Assert.IsTrue(Tree().Any(r => r.Path == "Controllers/HomeController.cs" && r.Depth == 1));
                page.OpenFile("Controllers/HomeController.cs", keep: false);
                Assert.IsTrue(((EditorTab)tabs.SelectedItem).IsFile);
                for (var i = 0; i < 50 && !(rows.ItemsSource is IEnumerable<DiffRow> shown && shown.Any(r => r.Unified.Text.Contains("\"hi\"")) && shown.All(r => r.Unified.Kind == "Same")); i++)
                    await Task.Delay(100);
                var fileRows = ((IEnumerable<DiffRow>)rows.ItemsSource!).ToList();
                Assert.AreEqual(4, fileRows.Count, "Every line, no hunk headers.");
                Assert.IsTrue(fileRows.All(r => r.Unified.Kind == "Same"));
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)diff.FindName("Modes")).Visibility, "No diff modes for a file as it is.");
                Assert.IsTrue(((ListBox)page.FindName("OpenEditors")).Items.Cast<EditorTab>().Any(t => t.Path == "Controllers/HomeController.cs"));
                Save(host, "deploy-explorer.png");

                // Search: each match by file; Replace shows what it would write; a match opens its file there, selected.
                page.ShowView(SideView.Search);
                ((TextBox)page.FindName("SearchText")).Text = "hello";
                await page.SearchAsync();
                var results = ((ListBox)page.FindName("SearchResults")).Items.Cast<SearchRow>().ToList();
                Assert.AreEqual("Controllers/HomeController.cs", results.Single(r => r.IsFile).Path);
                var match = results.Single(r => !r.IsFile);
                Assert.AreEqual((3, "Hello"), (match.Match!.Line, match.Text));
                StringAssert.StartsWith(((TextBlock)page.FindName("SearchSummary")).Text, "1 result in 1 file");
                var replaceToggle = (System.Windows.Controls.Primitives.ToggleButton)page.FindName("ReplaceToggle");
                replaceToggle.IsChecked = true;
                page.GetType().GetMethod("ReplaceToggle_Click", Private)!.Invoke(page, [replaceToggle, new RoutedEventArgs()]);
                ((TextBox)page.FindName("ReplaceText")).Text = "Greet";
                match = ((ListBox)page.FindName("SearchResults")).Items.Cast<SearchRow>().Single(r => !r.IsFile);
                Assert.AreEqual("Greet", match.Replacement);
                Assert.IsNotNull(match.Strike, "The match struck through, the replacement after it.");
                Assert.IsTrue(((Button)page.FindName("ReplaceAllButton")).IsEnabled);
                page.GetType().GetMethod("OpenResult", Private)!.Invoke(page, [match, false]);
                for (var i = 0; i < 50 && diff.SelectedLine != 3; i++) await Task.Delay(100);
                Assert.AreEqual(3, diff.SelectedLine, "Opened at the match's line.");
                Assert.AreEqual("Hello", diff.Selection, "The match selected.");
                // The selection drawn under the match: where its text is, at the code font's width - not one measured before
                // the font was set (it sat two columns early).
                var line3 = FindAll<HighlightedText>(rows).Single(c => c.Line?.Text.Contains("Hello") == true);
                var drawn = (LinearGradientBrush)line3.Background;
                var hello = line3.ContentStart.GetPositionAtOffset(0)!;
                var width = new FormattedText("0000000000", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(line3.FontFamily, line3.FontStyle, line3.FontWeight, line3.FontStretch), line3.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace / 10;
                Assert.AreEqual(line3.Padding.Left + 18 * width, drawn.GradientStops[1].Offset * drawn.EndPoint.X, 0.5, "The highlight starts at the H of Hello.");
                Save(host, "deploy-search.png");
                page.ShowView(SideView.SourceControl);

                // A push, as the Push button runs it - then the history shows it, its file and that file's diff.
                var store = services.GetRequiredService<IDeployStore>();
                var scan = await new SyncScanner(store, services.GetRequiredService<ISnapshotStore>(), server.Factory, new LocalScanner(store))
                    .ScanAsync(connection, project, new RecordingReporter(), CancellationToken.None);
                var pushed = await services.GetRequiredService<OperationRunner>().RunAsync("Push", async (sp, reporter, ct) =>
                    (await sp.GetRequiredService<PushChangesUseCase>().ExecuteAsync(new PushRequest(scan, ["index.html"], "Add the development paragraph"), reporter, ct))
                    .WithoutValue());
                Assert.IsTrue(pushed);
                page.Refresh();
                page.GetType().GetMethod("LoadHistory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(page, null);
                var history = (ListBox)page.FindName("HistoryList");
                Assert.AreEqual(1, history.Items.Count);
                history.SelectedIndex = 0;
                Assert.IsTrue(((Button)page.FindName("RollbackButton")).IsEnabled, "A completed push can be rolled back.");
                var historyRows = (ListBox)((DiffView)page.FindName("HistoryDiff")).FindName("Rows");
                for (var i = 0; i < 50 && historyRows.ItemsSource is null; i++) await Task.Delay(100);
                Assert.IsTrue(((IEnumerable<DiffRow>)historyRows.ItemsSource!).Any(r => r.Unified is { Kind: "Added", Text: "<p>new in development</p>" }));
                Save(host, "deploy-history.png");
            });
        }
        finally
        {
            TestEnvironment.DeleteDirectory(run);
        }
    }

    [TestMethod]
    public void A_dragged_tab_goes_where_the_line_shows_and_nothing_moves_before_the_drop()
    {
        // Three tabs: 0-60, 60-260 (a wide one), 260-320. The gap is before the first tab whose middle is right of the pointer.
        (double, double)[] tabs = [(0, 60), (60, 200), (260, 60)];
        Assert.AreEqual(0, SourceControlPage.DropGap(10, tabs));
        Assert.AreEqual(1, SourceControlPage.DropGap(40, tabs), "Past the first tab's middle: after it.");
        Assert.AreEqual(1, SourceControlPage.DropGap(150, tabs), "Over the wide tab's left half: before it.");
        Assert.AreEqual(2, SourceControlPage.DropGap(170, tabs));
        Assert.AreEqual(3, SourceControlPage.DropGap(400, tabs), "Past the last tab: at the end.");
        // The first tab dropped after the wide one (gap 2) is the second tab; dropped at the end, the third.
        Assert.AreEqual(1, SourceControlPage.TargetIndex(from: 0, gap: 2));
        Assert.AreEqual(2, SourceControlPage.TargetIndex(from: 0, gap: 3));
        // The last tab dropped before the first: the first.
        Assert.AreEqual(0, SourceControlPage.TargetIndex(from: 2, gap: 0));
        // Dropped in the gap on either side of itself: it stays.
        Assert.AreEqual(1, SourceControlPage.TargetIndex(from: 1, gap: 1));
        Assert.AreEqual(1, SourceControlPage.TargetIndex(from: 1, gap: 2));
    }

    [TestMethod]
    public async Task The_diff_text_is_selected_and_copied_as_in_an_editor() => await WpfUi.RunAsync(async () =>
    {
        var diff = new DnnManager.Presentation.Controls.DiffView();
        var host = Host(diff);
        await diff.ShowDiffAsync("a.cs", System.Text.Encoding.UTF8.GetBytes("a\tb\nkeep\nold line\n"),
            System.Text.Encoding.UTF8.GetBytes("a\tb\nkeep\nnew line\n"), "On the server", "In the project");
        Layout(host);
        var rows = (ListBox)diff.FindName("Rows");
        var all = rows.Items.Cast<DnnManager.Presentation.Controls.DiffRow>().ToList();
        var nl = Environment.NewLine;

        // The icon toggles say what they are - the radio buttons and check box underneath, still.
        foreach (var name in new[] { "UnifiedMode", "SplitMode", "WholeFile" })
        {
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)diff.FindName(name);
            Assert.IsInstanceOfType<Viewbox>(toggle.Content, $"{name} is an icon.");
            Assert.IsFalse(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(toggle)), $"{name} has a spoken name.");
        }

        // Ctrl+A: every line - the hunk header not, the tab as it is in the file.
        diff.SelectAll();
        Assert.AreEqual($"a\tb{nl}keep{nl}old line{nl}new line", diff.Selection);

        // Part of a line, by the columns shown: "a" and the tab (4 columns).
        var first = all.First(r => r.Hunk is null).Index;
        diff.Select(0, first, 0, first, 5);
        Assert.AreEqual("a\t", diff.Selection);
        Layout(host);
        var codes = FindAll<DnnManager.Presentation.Controls.HighlightedText>(rows).ToList();
        Assert.IsInstanceOfType<LinearGradientBrush>(codes.Single(t => t.Line?.Row == first).Background, "The selected part marked under the text.");
        Assert.IsTrue(codes.Where(t => t.Line?.Row != first).All(t => t.Background is null), "Nothing else marked.");
        diff.Select(0, first, 2, first + 2, 3);
        Layout(host);
        Save(host, "deploy-diff-selection.png");

        // Side by side: one side at a time - the right one, the new version.
        ((RadioButton)diff.FindName("SplitMode")).IsChecked = true;
        for (var i = 0; i < 100 && rows.ItemTemplate != diff.Resources["SplitRow"]; i++) await Task.Delay(20);
        Assert.AreEqual("", diff.Selection, "A new view starts with nothing selected.");
        var split = rows.Items.Cast<DnnManager.Presentation.Controls.DiffRow>().ToList();
        diff.Select(1, 0, 0, split.Count - 1, int.MaxValue);
        Assert.AreEqual($"a\tb{nl}keep{nl}new line", diff.Selection);
        diff.Select(0, 0, 0, split.Count - 1, int.MaxValue);
        Assert.AreEqual($"a\tb{nl}keep{nl}old line", diff.Selection);
    });

    [TestMethod]
    public async Task A_minified_line_shows_its_start_and_show_more_in_both_views() => await WpfUi.RunAsync(async () =>
    {
        // A minified script: one line of 250 KB.
        var minified = "/*! license */\n!function(e,t){" + string.Concat(Enumerable.Repeat("var a=\"x\";return e(t);", 12_000)) + "}();\n";
        var diff = new DnnManager.Presentation.Controls.DiffView();
        var host = Host(diff);
        await diff.ShowDiffAsync("js/formio.full.min.js", null, System.Text.Encoding.UTF8.GetBytes(minified), "On the server", "In the project");
        var rows = (ListBox)diff.FindName("Rows");
        var line = rows.Items.Cast<DnnManager.Presentation.Controls.DiffRow>().Single(r => r.Hunk is null && r.Unified.Number == "2").Unified;
        Assert.AreEqual(DnnManager.Presentation.Controls.DiffSide.MaxShown, line.Text.Length, "Its start only.");
        StringAssert.StartsWith(line.MoreText, "Show more (");
        var width = (double)diff.Resources["CodeWidth"];
        Assert.IsTrue(width < 20_000, $"As wide as its start, not the whole line: {width}");
        Layout(host);
        var shown = FindAll<DnnManager.Presentation.Controls.HighlightedText>(rows).First(t => t.Line?.Full is not null);
        var plain = FindAll<DnnManager.Presentation.Controls.HighlightedText>(rows).First(t => t.Line?.Full is null);
        Assert.IsTrue(shown.ActualHeight <= plain.ActualHeight + 1,
            $"Show more no taller than a line - the line's text level with its number: {shown.ActualHeight} against {plain.ActualHeight}.");
        Save(host, "deploy-minified-unified.png");

        // Side by side: each side half the width - the new one on the screen, one scroll bar for both.
        ((RadioButton)diff.FindName("SplitMode")).IsChecked = true;
        for (var i = 0; i < 100 && rows.ItemTemplate != diff.Resources["SplitRow"]; i++) await Task.Delay(20);
        Layout(host);
        var code = FindAll<DnnManager.Presentation.Controls.HighlightedText>(rows).Where(t => t.Line?.Full is not null).ToList();
        Assert.AreEqual(1, code.Count);
        var at = code[0].TranslatePoint(new Point(), host);
        Assert.IsTrue(at.X > 300 && at.X < host.ActualWidth - 100, $"Side by side, the new side is on the screen: at {at.X}.");
        Assert.AreEqual(Visibility.Visible, ((System.Windows.Controls.Primitives.ScrollBar)diff.FindName("SplitScroll")).Visibility, "One scroll bar for both sides.");
        Save(host, "deploy-minified-split.png");

        // Scrolled sideways: the code moves under its mark and number, cut off at the code's own edge - never over them.
        var scroll = (System.Windows.Controls.Primitives.ScrollBar)diff.FindName("SplitScroll");
        scroll.Value = 300;
        Layout(host);
        var clip = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(code[0]));
        Assert.IsTrue(clip.ClipToBounds, "The code clipped at its own edge.");
        Assert.IsTrue(code[0].ActualWidth > clip.ActualWidth + 300, $"The code as wide as its line, not the side: {code[0].ActualWidth} in {clip.ActualWidth}.");
        var mark = FindAll<TextBlock>((DependencyObject)VisualTreeHelper.GetParent(clip)).First(t => t.Text == "+");
        Assert.IsTrue(clip.TranslatePoint(new Point(), host).X >= mark.TranslatePoint(new Point(mark.ActualWidth, 0), host).X - 0.5,
            "The code's clip starts after the mark.");
        Save(host, "deploy-minified-split-scrolled.png");
        scroll.Value = 0;

        // Show more, clicked: the whole line, wrapped at the side's width - the row as tall as it.
        var more = (Button)((InlineUIContainer)code[0].Inlines.Last()).Child;
        StringAssert.StartsWith((string)more.Content, "Show more (");
        var before = code[0].ActualHeight;
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(more)).Invoke();
        await Task.Delay(50);
        Layout(host);
        Assert.AreEqual(TextWrapping.Wrap, code[0].TextWrapping);
        Assert.IsTrue(code[0].ActualHeight > before * 10, $"The row grew with the whole line: {code[0].ActualHeight}.");
        Assert.IsTrue(code[0].ActualWidth <= host.ActualWidth / 2, $"Wrapped at a side's width: {code[0].ActualWidth}.");
        Save(host, "deploy-minified-more.png");
    });

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    [TestMethod]
    public void Without_a_server_the_page_says_how_to_add_one() => WpfUi.Run(() =>
    {
        var run = TestEnvironment.NewRunDirectory();
        try
        {
            var services = Services(run, new FolderServer(Path.Combine(run, "server")));
            var page = ActivatorUtilities.CreateInstance<SourceControlPage>(services);
            var host = Host(page);
            Assert.AreEqual(Visibility.Visible, ((Border)page.FindName("EmptyState")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)page.FindName("FileView")).Visibility);
            Save(host, "deploy-empty.png");

            // The history folds to its title, giving its room to the changes - and opens at the height it had.
            var history = (System.Windows.Controls.Primitives.ToggleButton)page.FindName("HistoryToggle");
            var row = (RowDefinition)page.FindName("HistoryRow");
            history.IsChecked = false;
            page.GetType().GetMethod("HistoryToggle_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(page, [history, new RoutedEventArgs()]);
            Assert.IsTrue(row.Height.IsAuto);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)page.FindName("HistoryBody")).Visibility);
            history.IsChecked = true;
            page.GetType().GetMethod("HistoryToggle_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(page, [history, new RoutedEventArgs()]);
            Assert.AreEqual(220, row.Height.Value);

            // The servers, changes and history are the window's primary side bar's - not in the page.
            Assert.IsFalse(((Grid)page.FindName("Root")).Children.Contains(page.SideBar));
        }
        finally
        {
            TestEnvironment.DeleteDirectory(run);
        }
    });

    [TestMethod]
    public void The_preview_of_a_production_push_asks_for_each_risk_before_it_runs() => WpfUi.Run(() =>
    {
        var connection = new DeployConnection { Site = "shop", Name = "Live", Host = "shop.example.com", UserName = "deploy", RemoteRoot = "/var/www" };
        SyncItem Item(string path, SyncStatus status) => new(path, status, ConflictKind.None, new FileState(10, "a", null), new FileState(9, "b", null), null,
            IgnoreRules.IsSensitive(path));
        var dialog = new DeployPreviewDialog(true, connection,
            [Item("index.html", SyncStatus.LocalModified), Item("web.config", SyncStatus.LocalModified), Item("old.css", SyncStatus.LocalDeleted)], "Release 2");
        var host = new Border { Child = WithoutWindow(dialog), Width = 640, Height = 560 };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        host.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        Layout(host);
        var ok = dialog.OkButton;
        var production = dialog.ProductionBox;
        var sensitive = dialog.SensitiveBox;
        Assert.AreEqual(Visibility.Visible, production.Visibility);
        Assert.AreEqual(Visibility.Visible, sensitive.Visibility, "web.config looks like configuration.");
        Assert.IsFalse(ok.IsEnabled, "Nothing runs before the changes to Production are agreed to.");
        production.IsChecked = true;
        Assert.IsFalse(ok.IsEnabled, "… and the secret-looking file too.");
        sensitive.IsChecked = true;
        Assert.IsTrue(ok.IsEnabled);
        Assert.AreEqual(3, dialog.Plan.Items.Count);
        Save(host, "deploy-preview.png");
    });

    [TestMethod]
    public void The_connection_dialog_and_the_merge_dialog_load() => WpfUi.Run(() =>
    {
        var run = TestEnvironment.NewRunDirectory();
        try
        {
            var services = Services(run, new FolderServer(Path.Combine(run, "server")));
            var dialog = new DeployConnectionDialog(services.GetRequiredService<DeployConnections>(), "shop", null, @"C:\DNN\shop");
            var host = new Border { Child = WithoutWindow(dialog), Width = 620, Height = 720 };
            host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
            host.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
            Layout(host);
            // As in FileZilla: FTP, implicit TLS, a normal logon - the port left empty for its usual 990.
            Assert.AreEqual("FTP - File Transfer Protocol", ((IEnumerable<string>)dialog.ProtocolBox.ItemsSource).First(), "FTP first, as in FileZilla.");
            dialog.ProtocolBox.SelectedIndex = 0;
            dialog.EncryptionBox.SelectedIndex = 2;
            dialog.HostBox.Text = "waws-prod-am2-363.ftp.azurewebsites.windows.net";
            dialog.UserBox.Text = @"dzp\$dzp";
            dialog.RootBox.Text = "/site/wwwroot";
            Layout(host);
            Assert.AreEqual(Visibility.Visible, dialog.EncryptionRow.Visibility);
            Assert.AreEqual(Visibility.Collapsed, dialog.KeyRow.Visibility, "FTP has no key file.");
            CollectionAssert.AreEqual(new[] { "Anonymous", "Normal", "Ask for password" }, ((IEnumerable<string>)dialog.LogonBox.ItemsSource).ToList());
            // The local folder - FileZilla's Default local directory: empty, the project's folder, as the hint says.
            StringAssert.Contains(dialog.LocalHint.Text, @"C:\DNN\shop");
            dialog.LocalBox.Text = run;
            StringAssert.StartsWith(dialog.LocalHint.Text, "Compared with");
            Save(host, "deploy-connection.png");
            dialog.LocalBox.Text = "";
            dialog.EncryptionBox.SelectedIndex = 3;
            Assert.AreEqual(Visibility.Visible, dialog.PlainFtpWarning.Visibility, "Plain FTP is warned about.");
            dialog.ProtocolBox.SelectedIndex = 1;
            Assert.AreEqual(Visibility.Collapsed, dialog.EncryptionRow.Visibility, "SFTP has no FTP encryption.");
            CollectionAssert.AreEqual(new[] { "Normal", "Ask for password", "Key file" }, ((IEnumerable<string>)dialog.LogonBox.ItemsSource).ToList());

            var merge = new MergeDialog("Portals/0/site.css", TextContent.FromText("a\nb\nc\n"), TextContent.FromText("a\nB\nc\n"),
                TextContent.FromText("a\nb!\nc\n"));
            var mergeHost = new Border { Child = WithoutWindow(merge), Width = 900, Height = 600 };
            mergeHost.SetResourceReference(Border.BackgroundProperty, "WindowBg");
            mergeHost.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
            Layout(mergeHost);
            Assert.IsFalse(merge.OkButton.IsEnabled, "A merge with a marker left isn't saved.");
            Save(mergeHost, "deploy-merge.png");
        }
        finally
        {
            TestEnvironment.DeleteDirectory(run);
        }
    });

    /// <summary>A dialog's content, taken out of the window it was made for - laid out and drawn without showing it.</summary>
    private static FrameworkElement WithoutWindow(Window dialog)
    {
        var content = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        // Its names and resources stay its own.
        content.Resources.MergedDictionaries.Add(dialog.Resources);
        return content;
    }

    // Laid out on its own, not in a window: the first window shown would become the test app's main window, and closing it
    // would shut the app down for every test after.
    private static Border Host(FrameworkElement content)
    {
        // A page with a side bar view: the two side by side, as the window's primary side bar and page show them.
        if (content is ISideBarPage { SideBar: { Parent: null } side })
        {
            var both = new Grid();
            both.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            both.ColumnDefinitions.Add(new ColumnDefinition());
            both.Children.Add(side);
            Grid.SetColumn(content, 1);
            both.Children.Add(content);
            content = both;
        }
        var host = new Border { Child = content, Width = 1280, Height = 780 };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        host.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        Layout(host);
        return host;
    }

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
    }

    private static void Save(Border host, string name)
    {
        Layout(host);
        if (Environment.GetEnvironmentVariable("DEPLOY_TEST_SCREENSHOTS") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            // Outside a window a theme swap reaches nothing: the palette goes into the host's own resources instead.
            ResourceDictionary palette = theme == AppTheme.Dark ? new DnnManager.Presentation.Themes.DarkTheme() : new DnnManager.Presentation.Themes.LightTheme();
            host.Resources.MergedDictionaries.Clear();
            host.Resources.MergedDictionaries.Add(palette);
            Layout(host);
            var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, theme == AppTheme.Dark ? name.Replace(".png", "-dark.png") : name));
            encoder.Save(file);
        }
        host.Resources.MergedDictionaries.Clear();
    }

    private static ServiceProvider Services(string run, FolderServer server)
    {
        var paths = new AppDataPaths(Path.Combine(run, "data"));
        var store = new SettingsStore(paths);
        var options = store.Load().Settings.ToAppOptions();
        options.BaseDirectory = Path.Combine(run, "projects");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(options));
        services.AddApplication();
        services.AddInfrastructure(paths, store);
        services.RemoveAll<IIisManager>();
        services.AddSingleton<IIisManager>(new UntouchedIis());
        services.RemoveAll<IRemoteFileSystemFactory>();
        services.AddSingleton(server.Factory);
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<GuiProgressReporter>();
        services.AddSingleton<IProgressReporter>(sp => sp.GetRequiredService<GuiProgressReporter>());
        services.AddSingleton<GuiUserPrompt>();
        services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<GuiUserPrompt>());
        services.AddSingleton<OperationRunner>();
        services.AddSingleton<ServerStore>();
        var provider = services.BuildServiceProvider();
        Kept.Add(provider);
        return provider;
    }
}
