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
public sealed class DeployUiTests
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
                var page = ActivatorUtilities.CreateInstance<DeployPage>(services);
                var host = Host(page);
                Assert.AreEqual(0, ((ListBox)page.FindName("ServersList")).Items.Count);
                services.GetRequiredService<ServerStore>().Projects.Add(new ProjectRow(new ProjectState { Name = "shop", Directory = project, SiteUrl = "", Site = new DnnManager.Application.Abstractions.IisSiteRuntime(1, "Started", "shop", "Started", [], [], project) }));
                page.Activated();
                Assert.AreEqual(1, ((ListBox)page.FindName("ServersList")).Items.Count, "The project's server shows once the project is known.");
                // The comparison runs as an operation, on the thread pool.
                var changes = (ListBox)page.FindName("Changes");
                for (var i = 0; i < 100 && changes.ItemsSource is null; i++) await Task.Delay(100);
                List<ChangeRow> Rows() => [.. (IEnumerable<ChangeRow>)changes.ItemsSource!];
                var headers = Rows().Where(r => r.IsHeader).Select(r => $"{r.Title} {r.Count}").ToList();
                CollectionAssert.AreEqual(new[] { "Changes - to push 1", "Incoming - to pull 2", "Conflicts - changed in both 1" }, headers);
                var css = Rows().Single(r => r.Path == "Portals/0/site.css");
                Assert.AreEqual("!", css.Letter);
                Assert.IsFalse(css.CanStage, "A conflict is resolved, not staged.");
                Assert.AreEqual("A", Rows().Single(r => r.Path == "new-feature.js").Letter);
                Assert.AreEqual("M", Rows().Single(r => r.Path == "index.html").Letter);
                StringAssert.Contains(((ListBox)page.FindName("ServersList")).Items.Cast<ServerRow>().Single().Counts, "↑1 ↓2 !1");

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
            var page = ActivatorUtilities.CreateInstance<DeployPage>(services);
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
            var dialog = new DeployConnectionDialog(services.GetRequiredService<DeployConnections>(), "shop", null);
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
            Save(host, "deploy-connection.png");
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
