using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DnnManager.Application;
using DnnManager.Application.Deployment;
using DnnManager.Desktop;
using DnnManager.Desktop.Views;
using DnnManager.Domain;
using DnnManager.Infrastructure.Settings;
using DnnManager.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

[assembly: DoNotParallelize]

namespace DnnManager.Desktop.Tests
{

/// <summary>Avalonia without a screen, drawn with Skia - so a test can look at what the window shows.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[TestClass]
public sealed class SourceControlViewTests
{
    private static HeadlessUnitTestSession? _session;

    [ClassInitialize]
    public static void Start(TestContext _) => _session = HeadlessUnitTestSession.StartNew(typeof(TestApp));

    // Not disposed: HeadlessUnitTestSession.Dispose waits on its UI thread, which an Avalonia window's timers keep busy -
    // it hangs the test host. The process ends after the tests anyway.

    [TestMethod]
    public async Task Choosing_a_server_compares_it_and_a_file_shows_its_diff()
    {
        var run = Directory.CreateTempSubdirectory("dnnmanager-desktop-").FullName;
        try
        {
            var projects = Path.Combine(run, "projects");
            var project = Directory.CreateDirectory(Path.Combine(projects, "shop")).FullName;
            var server = new FolderServer(Path.Combine(run, "server"));
            void Both(string path, string content)
            {
                var file = Path.Combine(project, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, content);
                server.Write(path, content);
            }
            Both("Portals/0/site.css", "body {\n  color: black;\n}\n");
            Both("Controllers/HomeController.cs", "public class Home\n{\n    public string Hello() => \"hi\";\n}\n");
            Both("index.html", "<h1>shop</h1>\n");

            var services = Services(run, projects, server);
            var connections = services.GetRequiredService<DeployConnections>();
            var connection = new DeployConnection
            {
                Site = "shop", Name = "Live", Host = "shop.example.com", UserName = "deploy", RemoteRoot = "/var/www/shop",
                TrustedHostKey = "SHA256:test", IgnoreRules = IgnoreRules.Defaults
            };
            connections.Save(connection, new DeploySecrets("secret"));
            // A project outside the projects folder: its server's local folder says where.
            var elsewhere = Directory.CreateDirectory(Path.Combine(run, "elsewhere", "blog")).FullName;
            File.WriteAllText(Path.Combine(elsewhere, "only-here.txt"), "local");
            var blog = connection with { Id = Guid.NewGuid(), Site = "blog", Name = "Staging", Environment = DeployEnvironment.Staging, LocalRoot = elsewhere };
            connections.Save(blog, new DeploySecrets("secret"));
            CollectionAssert.AreEqual(new[] { "blog", "shop" }, connections.Sites().ToArray(), "Every project that has a server.");

            // In sync first (the baseline), then a change here, a hotfix there, a new file there, one changed in both.
            await services.GetRequiredService<SyncScanner>().ScanAsync(connection, project, new RecordingReporter(), CancellationToken.None);
            File.WriteAllText(Path.Combine(project, "index.html"), "<h1>shop</h1>\n<p>new in development</p>\n");
            server.Write("Controllers/HomeController.cs", "public class Home\n{\n    // hotfix on live\n    public string Hello() => \"hello\";\n}\n");
            server.Write("new-feature.js", "export function feature() { return 42; }\n");
            File.WriteAllText(Path.Combine(project, "Portals", "0", "site.css"), "body {\n  color: navy;\n}\n");
            server.Write("Portals/0/site.css", "body {\n  color: red;\n}\n");

            Exception? failed = null;
            // Returning a value, the async lambda is awaited by the session (Dispatch<TResult>(Func<Task<TResult>>)), its UI
            // thread running meanwhile - one returning nothing would be Dispatch<Task>, over at its first await.
            await _session!.Dispatch(async () =>
            {
              try
              {
                var window = new MainWindow { Width = 1280, Height = 800 };
                window.Show();
                // On Windows the window opens on Projects, as the Windows app: Source Control is chosen.
                window.Show("SourceControl");
                var view = (SourceControlView)window.FindControl<ContentControl>("ViewHost")!.Content!;
                var servers = view.FindControl<ListBox>("ServersList")!;
                var changes = view.FindControl<ListBox>("Changes")!;
                Assert.AreEqual(2, servers.ItemCount, "Each project's servers - the one in the projects folder and the one elsewhere.");
                Assert.AreEqual(0, servers.SelectedItems!.Count, "None chosen at first.");
                await Task.Delay(200);
                Assert.IsNull(changes.ItemsSource, "Nothing is read from a server until one is chosen.");
                StringAssert.StartsWith(view.FindControl<TextBlock>("ChangesMessage")!.Text, "Choose a server");
                var rows = servers.Items.Cast<ServerRow>().ToList();
                Assert.AreEqual(elsewhere, rows.Single(r => r.Site == "blog").Root, "Compared with its own local folder.");
                Save(window, "1-nothing-chosen");

                // Chosen: compared - as an operation, on the thread pool.
                servers.SelectedItem = rows.Single(r => r.Site == "shop");
                await Until(() => changes.ItemsSource is not null);
                List<ChangeRow> Rows() => changes.ItemsSource is IEnumerable<ChangeRow> shown ? [.. shown] : [];
                CollectionAssert.AreEqual(new[] { "Changes - to push 1", "Incoming - to pull 2", "Conflicts - changed in both 1" },
                    Rows().Where(r => r.IsHeader).Select(r => $"{r.Title} {r.Count}").ToArray());
                Assert.AreEqual("!", Rows().Single(r => r.Path == "Portals/0/site.css").Letter);
                Assert.AreEqual("A", Rows().Single(r => r.Path == "new-feature.js").Letter);
                StringAssert.Contains(rows.Single(r => r.Site == "shop").Name, "Live");

                // A file chosen: its diff in the preview tab - unified at first, as in the Windows app.
                changes.SelectedItem = Rows().Single(r => r.Path == "index.html");
                var diff = view.FindControl<DiffView>("Diff")!;
                await Until(() => diff.ShownRows.Count > 0);
                Assert.IsTrue(diff.ShownRows.Any(r => r.Unified.Text == "<p>new in development</p>" && r.Unified.IsAdded), "The added line, marked.");
                Assert.IsTrue(diff.ShownRows.Any(r => r.IsHunk), "Its hunk's header.");
                StringAssert.Contains(view.FindControl<TextBlock>("Breadcrumb")!.Text, "index.html");
                Assert.AreEqual(1, view.OpenTabs.Count);
                Assert.IsTrue(view.OpenTabs[0].IsPreview, "A click opens the preview tab.");
                Save(window, "2-compared-with-diff");

                // Side by side: old on the left, new on the right.
                diff.SetMode(split: true);
                await Until(() => diff.IsSplit && diff.ShownRows.Any(r => r.Right.Text == "<p>new in development</p>" && r.Right.IsAdded));
                // The code is selected and copied as in an editor.
                diff.SelectAll();
                StringAssert.Contains(diff.Selection, "<h1>shop</h1>");
                diff.SetMode(split: false);
                await Until(() => !diff.IsSplit && diff.ShownRows.Count > 0);

                // Another file: the preview tab shows it; a double-click keeps a tab - two tabs, moved with the keyboard's keys.
                var css = Rows().Single(r => r.Path == "Portals/0/site.css");
                changes.SelectedItem = css;
                Assert.AreEqual(1, view.OpenTabs.Count, "The preview tab is reused.");
                await Until(() => view.FindControl<Border>("ConflictBar")!.IsVisible);
                Assert.IsTrue(view.FindControl<Button>("MergeButton")!.IsEnabled, "Changed in both, as text: it can be merged.");
                view.OpenTab(Rows().Single(r => r.Path == "index.html").Item!, keep: true);
                Assert.AreEqual(2, view.OpenTabs.Count);
                Assert.AreEqual("index.html", view.OpenTabs[1].Path);
                view.MoveTab(view.OpenTabs[1], 0);
                Assert.AreEqual("index.html", view.OpenTabs[0].Path, "Moved to the front.");
                Assert.AreEqual(1, SourceControlView.TargetIndex(0, SourceControlView.DropGap(150, [(0, 100), (100, 100)])), "Dropped past the second tab's middle.");

                // Staged: Push counts it; a conflict can't be.
                var index = Rows().Single(r => r.Path == "index.html");
                Assert.IsTrue(index.CanStage);
                Assert.IsFalse(Rows().Single(r => r.Path == "Portals/0/site.css").CanStage, "A conflict is resolved, not staged.");
                view.FindControl<ListBox>("Changes")!.SelectedItem = index;
                var push = view.FindControl<Button>("PrimaryButton")!;
                Assert.IsFalse(push.IsEnabled, "Nothing staged, nothing to push.");
                changes.Focus();
                changes.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Space });
                Assert.AreEqual("Staged Changes 1", Rows().Where(r => r.IsHeader).Select(r => $"{r.Title} {r.Count}").First());
                Assert.IsTrue(push.IsEnabled);
                Assert.AreEqual("Push 1", view.FindControl<TextBlock>("PrimaryLabel")!.Text);

                // The Explorer: the project's files as a push sees them, a changed one with its letter; a folder opens.
                view.ShowView(SideView.Explorer);
                var tree = view.FindControl<ListBox>("ExplorerTree")!;
                await Until(() => tree.ItemCount > 0);
                var explorer = tree.Items.Cast<ExplorerRow>().ToList();
                Assert.AreEqual("M", explorer.Single(r => r.Path == "index.html").Letter);
                await view.ToggleFolderAsync(explorer.Single(r => r.Path == "Controllers"));
                Assert.IsTrue(tree.Items.Cast<ExplorerRow>().Any(r => r.Path == "Controllers/HomeController.cs" && r.Depth == 1));
                Save(window, "2b-explorer");

                // Search: every match in the project's files; a match opens the file there.
                view.ShowView(SideView.Search);
                view.FindControl<TextBox>("SearchText")!.Text = "navy";
                await view.SearchAsync();
                var match = view.SearchRows.Single(r => r.IsMatch);
                Assert.AreEqual("Portals/0/site.css", match.Path);
                view.OpenResult(match, keep: false);
                await Until(() => diff.SelectedLine == 2);
                Assert.IsTrue(view.OpenTabs.Any(t => t.IsFile && t.Path == "Portals/0/site.css"), "Opened as it is in the project.");
                Save(window, "2c-search");
                view.ShowView(SideView.SourceControl);

                // Both chosen: each compared; CHANGES says which one it shows.
                // The list is made anew after each comparison: its row now.
                servers.SelectedItems!.Add(servers.Items.Cast<ServerRow>().Single(r => r.Site == "blog"));
                await Until(() => view.FindControl<Button>("ChangesServer")!.IsVisible && Rows().Any(r => r.Path == "only-here.txt"));
                Assert.AreEqual("blog · Staging", view.FindControl<TextBlock>("ChangesServerText")!.Text, "The one chosen last is shown.");
                view.ShowServer(rows.Single(r => r.Site == "shop"));
                Assert.IsTrue(Rows().Any(r => r.Path == "index.html"), "Back to the first - compared already.");

                // On macOS and Linux, what only Windows has says so; on Windows, that it is in the Windows app.
                window.Show("Projects");
                var notHere = window.FindControl<ContentControl>("ViewHost")!.Content as Control;
                Assert.IsNotNull(notHere);
                Save(window, "3-projects-not-here");
                window.Show("Settings");
                Save(window, "4-settings");
                window.Close();
              }
              catch (Exception ex)
              {
                  // Thrown inside the session, a failure would leave it waiting: it is thrown outside.
                  failed = ex;
              }
              return true;
            }, CancellationToken.None);
            if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }
        finally
        {
            try { Directory.Delete(run, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task Adding_a_server_saves_it_with_the_folder_on_this_computer_it_is_compared_with()
    {
        var run = Directory.CreateTempSubdirectory("dnnmanager-dialog-").FullName;
        try
        {
            var projects = Directory.CreateDirectory(Path.Combine(run, "projects")).FullName;
            var elsewhere = Directory.CreateDirectory(Path.Combine(run, "work", "blog")).FullName;
            var services = Services(run, projects, new FolderServer(Path.Combine(run, "server")));
            var connections = services.GetRequiredService<DeployConnections>();
            Exception? failed = null;
            await _session!.Dispatch(() =>
            {
                try
                {
                    var dialog = new ServerDialog(connections, projects, [], null);
                    dialog.Show();
                    dialog.FindControl<AutoCompleteBox>("ProjectBox")!.Text = "blog";
                    dialog.FindControl<ComboBox>("ProtocolBox")!.SelectedIndex = 1;
                    Assert.IsTrue(dialog.FindControl<ComboBox>("EncryptionBox")!.IsVisible, "FTP asks for its encryption.");
                    Assert.IsFalse(dialog.FindControl<TextBlock>("KeyLabel")!.IsVisible, "FTP has no key file.");
                    var ok = dialog.FindControl<Button>("OkButton")!;
                    // Nothing filled in: each problem said, nothing saved.
                    ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    var problems = dialog.FindControl<TextBlock>("Problems")!;
                    Assert.IsTrue(problems.IsVisible);
                    StringAssert.Contains(problems.Text, "Host");
                    StringAssert.Contains(problems.Text, "there is no folder", "The project has no folder in the projects folder - Browse… for one.");
                    Assert.IsNull(dialog.Result);

                    dialog.FindControl<TextBox>("HostBox")!.Text = "ftp.example.com";
                    dialog.FindControl<TextBox>("UserBox")!.Text = "deploy";
                    dialog.FindControl<TextBox>("PasswordBox")!.Text = "secret";
                    dialog.FindControl<ComboBox>("EncryptionBox")!.SelectedIndex = (int)FtpEncryption.PlainOnly;
                    Assert.IsTrue(dialog.FindControl<Border>("PlainFtpWarning")!.IsVisible, "Plain FTP says what it means.");
                    dialog.FindControl<TextBox>("RootBox")!.Text = "/www";
                    dialog.FindControl<TextBox>("LocalBox")!.Text = elsewhere;
                    StringAssert.StartsWith(dialog.FindControl<TextBlock>("LocalHint")!.Text, "The folder on this computer");
                    ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    Assert.IsNotNull(dialog.Result, problems.Text);
                    Assert.AreEqual("blog", dialog.Result.Site);
                    Assert.AreEqual(elsewhere, dialog.Result.LocalRoot);
                    Assert.AreEqual(21, dialog.Result.Port, "Empty: FTP's usual port.");
                    Assert.AreEqual("secret", connections.Secrets(dialog.Result.Id).Password, "The password is kept - encrypted.");
                }
                catch (Exception ex)
                {
                    failed = ex;
                }
            }, CancellationToken.None);
            if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
        }
        finally
        {
            try { Directory.Delete(run, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task A_conflict_is_merged_by_hand_and_saved_once_no_marker_is_left()
    {
        Exception? failed = null;
        await _session!.Dispatch(() =>
        {
            try
            {
                var origin = TextContent.FromText("a\nb\nc\n");
                var local = TextContent.FromText("a\nproject\nc\n");
                var remote = TextContent.FromText("a\nserver\nc\n");
                var dialog = new MergeDialog("Portals/0/site.css", origin, local, remote);
                dialog.Show();
                var editor = dialog.FindControl<TextBox>("Editor")!;
                var ok = dialog.FindControl<Button>("OkButton")!;
                StringAssert.Contains(editor.Text, "<<<<<<<");
                Assert.IsFalse(ok.IsEnabled, "Markers left: not saved.");
                editor.Text = "a\nboth\nc\n";
                Assert.IsTrue(ok.IsEnabled);
                dialog.Ok_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
                Assert.AreEqual("a\nboth\nc\n", System.Text.Encoding.UTF8.GetString(dialog.Result!).Replace("\r\n", "\n"));
            }
            catch (Exception ex)
            {
                failed = ex;
            }
        }, CancellationToken.None);
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    }

    [TestMethod]
    public void Secrets_on_macOS_and_Linux_are_encrypted_with_a_key_only_the_user_reads()
    {
        var run = Directory.CreateTempSubdirectory("dnnmanager-cipher-").FullName;
        UserCipher.KeyFileOverride = Path.Combine(run, "config", "DnnManager", "user.key");
        UserCipher.AesOnWindows = true;
        try
        {
            var purpose = "DnnManager.test"u8.ToArray();
            var sealedValue = UserCipher.Protect("p@ss wörd"u8.ToArray(), purpose);
            StringAssert.StartsWith(sealedValue, UserCipher.AesPrefix);
            Assert.IsFalse(sealedValue.Contains("p@ss", StringComparison.Ordinal));
            Assert.AreEqual("p@ss wörd", System.Text.Encoding.UTF8.GetString(UserCipher.Unprotect(sealedValue, purpose)));
            Assert.AreEqual(32, new FileInfo(UserCipher.KeyFileOverride).Length);
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(UserCipher.KeyFileOverride), "Only the user reads the key.");
            // Another purpose, or a value changed, isn't read back.
            Assert.ThrowsExactly<System.Security.Cryptography.AuthenticationTagMismatchException>(() => UserCipher.Unprotect(sealedValue, "other"u8.ToArray()));
            // Without the key - another computer - it can't be read.
            File.Delete(UserCipher.KeyFileOverride);
            Assert.ThrowsExactly<System.Security.Cryptography.CryptographicException>(() => UserCipher.Unprotect(sealedValue, purpose));
        }
        finally
        {
            UserCipher.KeyFileOverride = null;
            UserCipher.AesOnWindows = false;
            Directory.Delete(run, true);
        }
    }

    [TestMethod]
    public void The_projects_folder_starts_where_the_system_keeps_a_user_s_files()
    {
        var folder = Platform.DefaultProjectsFolder;
        Assert.AreEqual(OperatingSystem.IsWindows() ? @"C:\DNN" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DNN"), folder);
        Assert.IsNull(Application.Configuration.SettingRules.ProjectsFolderProblem(folder), "The default is allowed.");
        if (!OperatingSystem.IsWindows())
        {
            StringAssert.Contains(Application.Configuration.SettingRules.ProjectsFolderProblem("/usr/local/dnn"), "system folder");
            StringAssert.Contains(Application.Configuration.SettingRules.ProjectsFolderProblem("/"), "root");
        }
        Assert.AreEqual(Platform.IsWindows, Platform.Supports(PlatformFeature.Iis));
        Assert.AreEqual(Platform.IsWindows, Platform.WhyNot(PlatformFeature.Iis) is null);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private static async Task Until(Func<bool> done, int seconds = 15)
    {
        for (var i = 0; i < seconds * 20 && !done(); i++) await Task.Delay(50);
        Assert.IsTrue(done(), "Waited in vain.");
    }

    // With DESKTOP_TEST_SCREENSHOTS set, what the window drew - to look at.
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DESKTOP_TEST_SCREENSHOTS") is not { Length: > 0 } folder) return;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(folder);
        var frame = window.CaptureRenderedFrame() ?? window.GetLastRenderedFrame();
#pragma warning disable CS0618 // The PNG encoder's defaults are all a screenshot needs.
        frame?.Save(Path.Combine(folder, $"{name}-{Environment.GetEnvironmentVariable("DESKTOP_TEST_THEME") ?? "light"}.png"));
#pragma warning restore CS0618
    }

    private static IServiceProvider Services(string run, string projects, FolderServer server)
    {
        // As the app starts: the activity log writes on Avalonia's UI thread.
        Presentation.Services.UiThread.Post = action => Dispatcher.UIThread.Post(action);
        var paths = new AppDataPaths(Path.Combine(run, "data"));
        paths.EnsureCreated();
        var store = new SettingsStore(paths);
        var loaded = store.Load();
        var options = loaded.Settings.ToAppOptions();
        options.BaseDirectory = projects;
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Program.AddServices(services, options, paths, store);
        services.RemoveAll<IRemoteFileSystemFactory>();
        services.AddSingleton(server.Factory);
        var provider = services.BuildServiceProvider();
        var theme = Environment.GetEnvironmentVariable("DESKTOP_TEST_THEME") ?? "light";
        loaded.Settings.Appearance.Theme = theme;
        Program.Use(provider, loaded.Settings, []);
        App.ApplyTheme(theme);
        return provider;
    }
}

}

namespace DnnManager.IntegrationTests.Support
{
/// <summary>Takes what an operation reports, and says nothing - the Windows tests' own is in TestDoubles.cs.</summary>
public sealed class RecordingReporter : Application.Abstractions.IProgressReporter
{
    public List<string> Lines { get; } = [];
    public void Step(string title) => Lines.Add(title);
    public void Info(string message) => Lines.Add(message);
    public void Success(string message) => Lines.Add(message);
    public void Fail(string message) => Lines.Add(message);
    public void Warn(string message) => Lines.Add(message);
    public void Progress(string message) { }
}
}
