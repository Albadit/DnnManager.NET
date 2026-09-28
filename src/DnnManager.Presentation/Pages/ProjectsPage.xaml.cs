using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace DnnManager.Presentation.Pages;

/// <summary>"Show all projects info", plus quick actions on the selected project.</summary>
public partial class ProjectsPage : UserControl, IRefreshable
{
    private readonly IServiceProvider _services;
    private readonly OperationRunner _runner;
    private int _loadVersion;
    // Pages are rebuilt on every visit: the last list is kept so a revisit shows it at once while it refreshes.
    private static (IReadOnlyList<ProjectStatus> List, DateTime Loaded)? _last;
    // The row under the mouse at the last right-click (null: empty space) - what the context menu is for.
    private Row? _menuRow;

    public sealed record Row(ProjectStatus Status)
    {
        public string Name => Status.Name;
        public string Url => Status.SiteUrl;
        // "Live" when the site is started, "Offline" when it's stopped (or starting / stopping), "(none)" without one.
        public string Iis => !Status.IisSiteExists ? "(none)"
            : string.Equals(Status.IisSiteState, "Started", StringComparison.OrdinalIgnoreCase) ? "Live" : "Offline";
        // "Live" when the database is on the SQL Server, "Offline" when the server doesn't answer, "(none)" when the
        // server is up but the database doesn't exist.
        public string Sql => !Status.SqlReachable ? "Offline" : Status.DatabaseExists ? "Live" : "(none)";
        public string Dnn => Status.DnnVersion ?? "(none)";
        public string Database => Status.DatabaseName ?? "(unknown)";
        public string Size => $"{Status.DirectorySizeBytes / 1024d / 1024d:N1} MB";
        public string Path => Status.ProjectDirectory;
    }

    public ProjectsPage(IServiceProvider services, OperationRunner runner)
    {
        _services = services; _runner = runner;
        InitializeComponent();
        if (_last is { } last) Show(last.List, last.Loaded);
        Loaded += (_, _) => Refresh();
        // Look for installed IDEs now (vswhere takes a moment) so the first right-click opens at once.
        _ = Task.Run(() => (IdeLocator.Installed, IdeLocator.ManagementStudios));
    }

    private Row? Selected => ProjectsGrid.SelectedItem as Row;

    public async void Refresh()
    {
        // Sizing every site walks a lot of files; ignore a slower, older load that finishes late.
        var version = ++_loadVersion;
        SetLoading(true);
        try
        {
            var list = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ListProjectsUseCase>().ExecuteAsync(CancellationToken.None);
            });
            if (version != _loadVersion) return;

            _last = (list, DateTime.Now);
            Show(list, DateTime.Now);
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            ShowOverlay($"Could not load projects: {ex.Message}");
        }
        finally
        {
            if (version == _loadVersion) SetLoading(false);
        }
    }

    private void Show(IReadOnlyList<ProjectStatus> list, DateTime loaded)
    {
        var selected = Selected?.Name;
        ProjectsGrid.ItemsSource = list.Select(p => new Row(p)).ToList();
        ProjectsGrid.SelectedItem = ProjectsGrid.Items.OfType<Row>().FirstOrDefault(r => r.Name == selected);
        Subtitle.Text = $"{list.Count} project folder{(list.Count == 1 ? "" : "s")} - their IIS site and database. " +
                        $"Updated {loaded:HH:mm:ss}.";
        ShowOverlay(list.Count == 0 ? "No projects found." : null);
    }

    /// <summary>
    /// Makes a (re)load visible: the button reads "Refreshing…", a bar runs along the table and the current
    /// rows fade until the new list arrives. The centred message is only for an empty table.
    /// </summary>
    private void SetLoading(bool loading)
    {
        RefreshButton.IsEnabled = !loading;
        RefreshButton.Content = loading ? "Refreshing…" : "Refresh";
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ProjectsGrid.Opacity = loading ? 0.5 : 1;
        if (loading && ProjectsGrid.Items.Count == 0) ShowOverlay("Loading projects…");
    }

    private void ShowOverlay(string? text)
    {
        OverlayText.Text = text ?? "";
        Overlay.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var any = Selected is not null;
        OpenSiteButton.IsEnabled = any;
        RemoveButton.IsEnabled = any;
    }

    // WPF has no sideways wheel scrolling: Shift + wheel scrolls the table horizontally.
    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || FindScrollViewer(ProjectsGrid) is not { } viewer) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) OpenSite_Click(sender, e);
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) Shell(row.Url);
    }

    // ─── Context menu ─────────────────────────────────────────────────────

    // A right-click selects the row under the mouse, like Explorer, so the menu acts on that project.
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not DataGridRow)
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

        _menuRow = (node as DataGridRow)?.Item as Row;
        if (_menuRow is not null) ProjectsGrid.SelectedItem = _menuRow;
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Opened from the keyboard (Menu key / Shift+F10) the cursor position is -1: use the selection.
        var row = e.CursorLeft < 0 ? Selected : _menuRow;
        if (row is null || ProjectsGrid.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        var details = NewMenuItem("Details…", (_, _) => ShowDetails(row));
        details.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(details);
        menu.Items.Add(NewMenuItem("Open site", (_, _) => Shell(row.Url)));
        menu.Items.Add(NewMenuItem("Open folder", (_, _) => OpenFolder(row)));
        menu.Items.Add(NewMenuItem("Copy path", (_, _) => CopyPath(row)));
        menu.Items.Add(new Separator());

        var ides = IdeLocator.Installed;
        if (ides.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No IDE found", IsEnabled = false });
        var solution = IdeLocator.SolutionFor(row.Path);
        foreach (var ide in ides)
        {
            var header = $"Open in {ide.Name}" +
                         (ide.OpensSolution && solution is not null ? $"  ({System.IO.Path.GetFileName(solution)})" : "");
            var item = NewMenuItem(header, (_, _) => OpenInIde(ide, row));
            item.ToolTip = ide.ExePath;
            menu.Items.Add(item);
        }
        if (IdeLocator.ManagementStudios.Count > 0)
        {
            var projectDatabase = ProjectDatabaseName(row);
            foreach (var ssms in IdeLocator.ManagementStudios)
            {
                // Default: the local SQL Server as sa. Project: this project's database with its own login.
                // Whether SSMS remembers the password is the SsmsRememberPassword setting.
                var item = new MenuItem { Header = $"Open in {ssms.Name}", ToolTip = ssms.ExePath };
                item.Items.Add(NewMenuItem("Default  (local SQL Server, sa)", (_, _) => OpenDatabase(ssms, row, project: false)));
                item.Items.Add(NewMenuItem(projectDatabase is null ? "Project database" : $"Project  ([{projectDatabase}])",
                    (_, _) => OpenDatabase(ssms, row, project: true)));
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(ExportMenu(row));
        menu.Items.Add(NewMenuItem("Remove…", (_, _) => Remove(row)));
    }

    private static void CopyPath(Row row)
    {
        try { Clipboard.SetText(row.Path); }
        catch (Exception ex) { Dialogs.Error($"Could not copy to the clipboard: {ex.Message}"); }
    }

    private enum ExportParts { Both, Site, Database }

    /// <summary>
    /// "Export": a backup into the project's 01_backup folder - site and database, or just one of them - or, with
    /// "Export to another folder…", a site .zip + .bacpac anywhere.
    /// </summary>
    private MenuItem ExportMenu(Row row)
    {
        var export = new MenuItem { Header = "Export" };
        export.Items.Add(NewMenuItem("Site and database  (.zip + .bacpac)", (_, _) => Backup(row, ExportParts.Both)));
        export.Items.Add(NewMenuItem("Site files  (.zip)", (_, _) => Backup(row, ExportParts.Site)));
        export.Items.Add(NewMenuItem("Database  (.bacpac)", (_, _) => Backup(row, ExportParts.Database)));
        export.Items.Add(new Separator());
        export.Items.Add(NewMenuItem("Export to another folder…", (_, _) => Export(row, ExportParts.Both)));
        var backups = System.IO.Path.Combine(row.Path, ProjectBackups.FolderName);
        var open = NewMenuItem($"Open {ProjectBackups.FolderName} folder", (_, _) => Shell(backups));
        open.IsEnabled = Directory.Exists(backups);
        export.Items.Add(open);
        return export;
    }

    /// <summary>A dated backup: 01_backup\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\ with &lt;project&gt;.zip and / or &lt;project&gt;.bacpac.</summary>
    private async void Backup(Row row, ExportParts parts)
    {
        var project = _services.GetRequiredService<IProjectRepository>().Build(row.Name);
        var folder = ProjectBackups.NewFolder(project, DateTime.Now);
        var request = new ExportProjectRequest
        {
            ProjectName = row.Name,
            ZipPath = parts == ExportParts.Database ? null : System.IO.Path.Combine(folder, ProjectBackups.SiteZipName(project)),
            BacpacPath = parts == ExportParts.Site ? null : System.IO.Path.Combine(folder, ProjectBackups.DatabaseName(project, ".bacpac"))
        };
        await _runner.RunAsync($"Back up '{row.Name}'",
            (sp, reporter, ct) => sp.GetRequiredService<ExportProjectUseCase>().ExecuteAsync(request, reporter, ct));
    }

    private async void Export(Row row, ExportParts parts)
    {
        var database = parts == ExportParts.Database;
        var dialog = new SaveFileDialog
        {
            Title = parts switch
            {
                ExportParts.Both => $"Export '{row.Name}' - the database is saved next to the .zip as a .bacpac",
                ExportParts.Site => $"Export the site files of '{row.Name}'",
                _ => $"Export the database of '{row.Name}'"
            },
            Filter = database ? "BACPAC files (*.bacpac)|*.bacpac" : "Zip files (*.zip)|*.zip",
            FileName = $"{row.Name}_{DateTime.Now:yyyyMMdd}{(database ? ".bacpac" : ".zip")}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
                ? System.IO.Path.Combine(home, "Downloads") : null
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var request = new ExportProjectRequest
        {
            ProjectName = row.Name,
            ZipPath = database ? null : dialog.FileName,
            BacpacPath = parts switch
            {
                ExportParts.Both => System.IO.Path.ChangeExtension(dialog.FileName, ".bacpac"),
                ExportParts.Database => dialog.FileName,
                _ => null
            }
        };
        await _runner.RunAsync($"Export '{row.Name}'",
            (sp, reporter, ct) => sp.GetRequiredService<ExportProjectUseCase>().ExecuteAsync(request, reporter, ct));
    }

    private static MenuItem NewMenuItem(string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        return item;
    }

    private static void OpenFolder(Row row)
    {
        if (Directory.Exists(row.Path)) Shell(row.Path);
        else Dialogs.Error($"The project folder no longer exists: {row.Path}");
    }

    private static void OpenInIde(Ide ide, Row row)
    {
        if (!Directory.Exists(row.Path))
        {
            Dialogs.Error($"The project folder no longer exists: {row.Path}");
            return;
        }
        try { IdeLocator.Open(ide, row.Path); }
        catch (Exception ex) { Dialogs.Error($"Could not start {ide.Name}: {ex.Message}"); }
    }

    /// <summary>The name of the database <paramref name="row"/>'s site uses, for the menu; null if it can't be read.</summary>
    private string? ProjectDatabaseName(Row row)
    {
        try
        {
            using var scope = _services.CreateScope();
            var project = scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name);
            var database = scope.ServiceProvider.GetRequiredService<LocalSqlContainer>().ConnectionOf(project).Database;
            return database.Length > 0 ? database : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens SSMS signed in to the local SQL Server as sa (<paramref name="project"/> false) or to the project's
    /// database - the one its web.config uses, or its local one. SSMS remembers the password when the
    /// SsmsRememberPassword setting is on.
    /// </summary>
    private async void OpenDatabase(Ide ssms, Row row, bool project)
    {
        try
        {
            SiteSqlConnection database;
            bool onThisMachine;
            using (var scope = _services.CreateScope())
            {
                var sql = scope.ServiceProvider.GetRequiredService<LocalSqlContainer>();
                database = project
                    ? sql.ConnectionOf(scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name))
                    : sql.DefaultConnection;
                onThisMachine = sql.IsOnThisMachine(database.Server);
            }

            // SQL Server reports a missing database as the same "Login failed for user" as a wrong password, so
            // check first: open the server when only the database is missing, and say which of the two it is.
            var log = _services.GetRequiredService<ActivityLog>();
            var tester = _services.GetRequiredService<ISqlConnectionTester>();
            if (database.Database.Length > 0 && !(await tester.TestAsync(database, CancellationToken.None, timeoutSeconds: 5)).Success)
            {
                var server = await tester.TestAsync(database with { Database = "master" }, CancellationToken.None, timeoutSeconds: 5);
                if (server.Success)
                {
                    log.Warn($"Database [{database.Database}] doesn't exist on {database.Server} - {ssms.Name} opens the server instead. " +
                             "Set it up with Host project (database) or restore a backup.");
                    database = database with { Database = "" };
                }
                else
                {
                    log.Warn($"Can't log in to {database.Server} as '{database.User}': {server.Error} " +
                             "Check the password in the site's web.config (or Settings → SQL Server for the local container).");
                }
            }

            var displayName = !project ? "Local SQL Server"
                : database.Database.Length > 0 ? $"{row.Name} - {database.Database}" : row.Name;
            var target = database.Database.Length > 0 ? $"[{database.Database}] on {database.Server}" : database.Server;
            log.Info($"{ssms.Name}: {target} as {(database.User.Length > 0 ? database.User : "Windows user")}.");

            // SSMS has no password switch, and any connection switch makes it try to connect at once - failing
            // with an error when a password is needed. So for a SQL login in SSMS 21+ start it bare and fill its
            // Connect dialog in; otherwise pass what the command line takes.
            var sqlLogin = database.User.Length > 0 && database.Password.Length > 0;
            if (!sqlLogin || ssms.MajorVersion < 21)
            {
                using var _ = IdeLocator.OpenDatabase(ssms, database, onThisMachine, displayName);
                if (sqlLogin) LeavePasswordOnClipboard(ssms, database, log);
                return;
            }

            var remember = _services.GetRequiredService<IOptions<AppOptions>>().Value.SsmsRememberPassword;

            // Add the connection to an SSMS that's already open rather than starting another one.
            var process = IdeLocator.FindRunning(ssms);
            if (process is not null && await SsmsConnectDialog.OpenInRunningAsync(process))
            {
                log.Info($"Adding the connection to the {ssms.Name} that's already open.");
            }
            else
            {
                process?.Dispose();
                process = IdeLocator.StartManagementStudio(ssms);
            }

            using var ownedProcess = process;
            if (process is not null &&
                await SsmsConnectDialog.SignInAsync(process, database, onThisMachine, displayName,
                    rememberPassword: remember, TimeSpan.FromSeconds(90)))
            {
                log.Success($"Signed in to {target} in {ssms.Name}" + (remember ? " (password remembered there)." : "."));
                return;
            }
            LeavePasswordOnClipboard(ssms, database, log);
        }
        catch (Exception ex)
        {
            Dialogs.Error($"Could not start {ssms.Name}: {ex.Message}");
        }
    }

    private static void LeavePasswordOnClipboard(Ide ssms, SiteSqlConnection database, ActivityLog log)
    {
        Clipboard.SetText(database.Password);
        log.Info($"Connect in {ssms.Name} to {database.Server} as '{database.User}' - the password is on the clipboard " +
                 "(tick 'Remember Password').");
    }

    /// <summary>
    /// The project's details in sections - project, website (IIS), database, web.config. Status values use the
    /// table's colours; things worth a look (a mismatched version or path, switched-off HTTPS rules) are amber.
    /// </summary>
    private async void ShowDetails(Row row)
    {
        var s = row.Status;
        var dir = s.ProjectDirectory;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;
            var project = sp.GetRequiredService<IProjectRepository>().Build(s.Name);
            var sql = sp.GetRequiredService<LocalSqlContainer>();
            var webConfigService = sp.GetRequiredService<IWebConfigService>();
            var webConfig = System.IO.Path.Combine(dir, "web.config");
            var hasWebConfig = File.Exists(webConfig);

            // The slow parts - IIS configuration and two SQL queries - off the UI thread.
            var database = sql.ConnectionOf(project);
            var site = await Task.Run(() => sp.GetRequiredService<IIisManager>().GetSiteInfo(s.Name));
            var facts = s.SqlReachable && s.DatabaseExists
                ? await sp.GetRequiredService<ISqlConnectionTester>().DescribeDatabaseAsync(database, CancellationToken.None, timeoutSeconds: 5)
                : null;

            var details = new List<DetailsDialog.Detail>();

            details.Add(DetailsDialog.Detail.Section("Project"));
            details.Add(new("Folder", dir));
            if (Directory.Exists(dir)) details.Add(new("Created", Directory.GetCreationTime(dir).ToString("g")));
            details.Add(new("Size", row.Size));
            details.Add(new("DNN version", s.DnnVersion ?? @"unknown (no bin\DotNetNuke.dll)"));
            if (GitBranch(dir) is { } branch) details.Add(new("Git branch", branch));
            if (Solutions(dir) is { Count: > 0 } solutions) details.Add(new("Solution", string.Join(", ", solutions)));
            var projectBackups = ProjectBackups.List(project);
            details.Add(new("Backups", projectBackups.Count == 0 ? $"none in {ProjectBackups.FolderName}"
                : $"{projectBackups.Count} in {project.BackupDirectory} - newest {projectBackups[0].Created:yyyy-MM-dd HH:mm}"));

            details.Add(DetailsDialog.Detail.Section("Website (IIS)"));
            details.Add(new("Status", row.Iis, row.Iis switch { "Live" => DetailKind.Good, "Offline" => DetailKind.Bad, _ => DetailKind.Normal }));
            details.Add(new("URL", s.SiteUrl));
            if (site is not null)
            {
                if (!string.Equals(site.State, "Started", StringComparison.OrdinalIgnoreCase))
                    details.Add(new("IIS state", site.State, DetailKind.Warning));
                details.Add(new("Bindings", string.Join(Environment.NewLine, site.Bindings)));
                var samePath = string.Equals(System.IO.Path.GetFullPath(site.PhysicalPath).TrimEnd('\\'),
                    System.IO.Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                details.Add(new("Physical path", samePath ? site.PhysicalPath : $"{site.PhysicalPath}  (not this project's folder)",
                    samePath ? DetailKind.Normal : DetailKind.Warning));
                details.Add(new("App pool", $"{site.AppPool}" + (site.AppPoolState is { } ps ? $" - {ps}" : ""),
                    string.Equals(site.AppPoolState, "Started", StringComparison.OrdinalIgnoreCase) ? DetailKind.Normal : DetailKind.Warning));
                details.Add(new(".NET / pipeline", $"{site.ClrVersion ?? "?"} / {site.PipelineMode ?? "?"}"));
                if (site.Identity is { } identity) details.Add(new("Identity", identity));
            }

            details.Add(DetailsDialog.Detail.Section("Database"));
            details.Add(new("SQL", row.Sql, row.Sql switch { "Live" => DetailKind.Good, "Offline" => DetailKind.Bad, _ => DetailKind.Warning }));
            details.Add(new("Database", s.DatabaseExists ? row.Database : $"{row.Database}  (doesn't exist on {database.Server})",
                s.DatabaseExists || !s.SqlReachable ? DetailKind.Normal : DetailKind.Warning));
            details.Add(new("Server", $"{database.Server} (user: {(database.User.Length == 0 ? "Windows" : database.User)})"));
            if (facts is { Success: true, Value: { } f })
            {
                details.Add(new("Size", $"{f.SizeMb:N1} MB"));
                if (f.DnnVersion is { } dbVersion)
                {
                    var matches = s.DnnVersion is null || s.DnnVersion == dbVersion;
                    details.Add(new("DNN version (database)", matches ? dbVersion : $"{dbVersion}  (the files are {s.DnnVersion})",
                        matches ? DetailKind.Normal : DetailKind.Warning));
                }
                if (f.Portals is { } portals) details.Add(new("Portals", portals.ToString()));
                if (f.PortalAliases.Count > 0) details.Add(new("Portal aliases", string.Join(Environment.NewLine, f.PortalAliases)));
            }
            else if (facts is { Success: false })
            {
                details.Add(new("Details", $"couldn't be read: {facts.Error}", DetailKind.Warning));
            }

            details.Add(DetailsDialog.Detail.Section("web.config"));
            if (!hasWebConfig)
            {
                details.Add(new("web.config", "not found", DetailKind.Warning));
            }
            else
            {
                var conn = webConfigService.ReadSiteSqlServer(webConfig);
                details.Add(new("Connection", conn is { Success: true, Value: { } c }
                    ? $"[{c.Database}] on {c.Server} (user: {(c.User.Length == 0 ? "integrated" : c.User)})"
                    : conn.Error ?? "no SiteSqlServer connection"));
                if (webConfigService.ReadFacts(webConfig) is { Success: true, Value: { } w })
                {
                    if (w.TargetFramework is { } tf) details.Add(new("Target framework", tf));
                    if (w.Debug is { } debug) details.Add(new("Debug", debug ? "on" : "off"));
                    if (w.CustomErrors is { } ce) details.Add(new("Custom errors", ce));
                    details.Add(w.DisabledHttpsRules.Count == 0
                        ? new("HTTPS redirects", "none switched off by DNN Manager")
                        : new("HTTPS redirects", $"switched off for local use: {string.Join(", ", w.DisabledHttpsRules)} - " +
                                                 "switch them back on before deploying", DetailKind.Warning));
                }
            }

            Mouse.OverrideCursor = null;
            DetailsDialog.Show(s.Name, details);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Dialogs.Error($"Could not read the details of '{s.Name}': {ex.Message}");
        }
    }

    private static IReadOnlyList<string> Solutions(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx"))
                .Select(f => System.IO.Path.GetFileName(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    /// <summary>The checked-out branch from <c>.git\HEAD</c>; null when the folder isn't a git repository.</summary>
    private static string? GitBranch(string dir)
    {
        var head = System.IO.Path.Combine(dir, ".git", "HEAD");
        if (!File.Exists(head)) return Directory.Exists(System.IO.Path.Combine(dir, ".git")) || File.Exists(System.IO.Path.Combine(dir, ".git")) ? "(git repository)" : null;
        try
        {
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..]
                : text.Length >= 7 ? $"(detached at {text[..7]})" : "(unknown)";
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) Remove(row);
    }

    private async void Remove(Row row)
    {
        // RemoveProjectUseCase asks about the database and for the final confirmation itself.
        await _runner.RunAsync($"Remove '{row.Name}'",
            (sp, reporter, ct) => sp.GetRequiredService<RemoveProjectUseCase>().ExecuteAsync(row.Name, reporter, ct));
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {target}: {ex.Message}"); }
    }
}
