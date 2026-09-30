using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>
/// The Projects table's right-click menu: the row's start / stop actions, then the less frequent ones - details,
/// open (site, folder, IDE, SSMS), export and remove.
/// </summary>
internal sealed class ProjectMenu
{
    private readonly IServiceProvider _services;
    private readonly OperationRunner _runner;
    private readonly Action<SiteAction, ProjectRow> _control;
    private readonly Action<ProjectRow> _remove;

    /// <param name="control">Starts, stops or restarts the row's site - the page's row actions.</param>
    /// <param name="remove">Removes the row's project - the page's row action.</param>
    public ProjectMenu(IServiceProvider services, OperationRunner runner, Action<SiteAction, ProjectRow> control, Action<ProjectRow> remove)
    {
        _services = services; _runner = runner; _control = control; _remove = remove;
    }

    /// <summary>Fills <paramref name="menu"/> for <paramref name="row"/>; the IDE entries depend on what is installed.</summary>
    public void Fill(ContextMenu menu, ProjectRow row)
    {
        menu.Items.Clear();
        if (row.ShowStart) menu.Items.Add(Item("Start", (_, _) => _control(SiteAction.Start, row), row.CanStart));
        if (row.ShowStop)
        {
            menu.Items.Add(Item("Stop", (_, _) => _control(SiteAction.Stop, row), row.CanStop));
            menu.Items.Add(Item("Restart", (_, _) => _control(SiteAction.Restart, row), row.CanRestart));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var details = Item("Details…", (_, _) => ShowDetails(row));
        details.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(details);
        menu.Items.Add(Item("Open site", (_, _) => Shell(row.Url)));
        menu.Items.Add(Item("Open folder", (_, _) => OpenFolder(row)));
        // A shell in the project's folder, in the terminal panel - unless the terminal is switched off in the settings.
        var terminal = _services.GetRequiredService<TerminalService>();
        if (terminal.Settings.Enabled)
            menu.Items.Add(Item("Open in terminal", (_, _) => terminal.OpenIn(row.Path), Directory.Exists(row.Path)));
        menu.Items.Add(new Separator());

        // One submenu with the editors found on this PC - nothing listed that isn't installed.
        var ides = IdeLocator.Installed;
        var openWith = new MenuItem { Header = "Open with" };
        if (ides.Count == 0)
        {
            openWith.IsEnabled = false;
            openWith.ToolTip = "No code editor or IDE found on this PC.";
        }
        var solution = IdeLocator.SolutionFor(row.Path);
        foreach (var ide in ides)
        {
            var header = ide.Name + (ide.OpensSolution && solution is not null ? $"  ({System.IO.Path.GetFileName(solution)})" : "");
            var item = Item(header, (_, _) => OpenInIde(ide, row));
            item.ToolTip = ide.ExePath;
            openWith.Items.Add(item);
        }
        menu.Items.Add(openWith);
        if (IdeLocator.ManagementStudios.Count > 0)
        {
            var projectDatabase = ProjectDatabaseName(row);
            foreach (var ssms in IdeLocator.ManagementStudios)
            {
                // Default: the local SQL Server as sa. Project: this project's database with its own login.
                // Whether SSMS remembers the password is the SsmsRememberPassword setting.
                var item = new MenuItem { Header = $"Open with {ssms.Name}", ToolTip = ssms.ExePath };
                item.Items.Add(Item("Default  (local SQL Server, sa)", (_, _) => OpenDatabase(ssms, row, project: false)));
                item.Items.Add(Item(projectDatabase is null ? "Project database" : $"Project  ([{projectDatabase}])",
                    (_, _) => OpenDatabase(ssms, row, project: true)));
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(ExportMenu(row));
        menu.Items.Add(Item("Remove…", (_, _) => _remove(row), row.CanRemove));
    }

    private static MenuItem Item(string header, RoutedEventHandler click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += click;
        return item;
    }

    public static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error($"Could not open {target}: {ex.Message}"); }
    }


    private static void OpenFolder(ProjectRow row)
    {
        if (Directory.Exists(row.Path)) Shell(row.Path);
        else Dialogs.Error($"The project folder no longer exists: {row.Path}");
    }

    private static void OpenInIde(Ide ide, ProjectRow row)
    {
        if (!Directory.Exists(row.Path))
        {
            Dialogs.Error($"The project folder no longer exists: {row.Path}");
            return;
        }
        try { IdeLocator.Open(ide, row.Path); }
        catch (Exception ex) { Dialogs.Error($"Could not start {ide.Name}: {ex.Message}"); }
    }

    // ─── Export ───────────────────────────────────────────────────────────

    private enum ExportParts { Both, Site, Database }

    /// <summary>
    /// "Export": a backup into the project's backups folder (Documents\DnnManager\backups\&lt;project&gt;) - site and
    /// database, or just one of them.
    /// </summary>
    private MenuItem ExportMenu(ProjectRow row)
    {
        var export = new MenuItem { Header = "Export" };
        export.Items.Add(Item("Site and database  (.zip + .bacpac)", (_, _) => Backup(row, ExportParts.Both)));
        export.Items.Add(Item("Site files  (.zip)", (_, _) => Backup(row, ExportParts.Site)));
        export.Items.Add(Item("Database  (.bacpac)", (_, _) => Backup(row, ExportParts.Database)));
        export.Items.Add(new Separator());
        var backups = _services.GetRequiredService<IProjectRepository>().Build(row.Name).BackupDirectory;
        export.Items.Add(Item("Open backups folder", (_, _) => Shell(backups), Directory.Exists(backups)));
        return export;
    }

    /// <summary>A dated backup: backups\&lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\ with &lt;project&gt;.zip and / or &lt;project&gt;.bacpac.</summary>
    private async void Backup(ProjectRow row, ExportParts parts)
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

    // ─── SQL Server Management Studio ─────────────────────────────────────

    /// <summary>The name of the database <paramref name="row"/>'s site uses, for the menu; null if it can't be read.</summary>
    private string? ProjectDatabaseName(ProjectRow row)
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
    private async void OpenDatabase(Ide ssms, ProjectRow row, bool project)
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

    // ─── Details ──────────────────────────────────────────────────────────

    /// <summary>
    /// The project's details in sections - project, website (IIS), database, web.config. Status values use the
    /// table's colours; things worth a look (a mismatched version or path, switched-off HTTPS rules) are amber.
    /// </summary>
    private async void ShowDetails(ProjectRow row)
    {
        var s = row.Project;
        var dir = s.Directory;
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
            var facts = s.SqlReachable == true && s.DatabaseExists
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
            details.Add(new("Backups", projectBackups.Count == 0 ? $"none in {project.BackupDirectory}"
                : $"{projectBackups.Count} in {project.BackupDirectory} - newest {projectBackups[0].Created:yyyy-MM-dd HH:mm}"));

            details.Add(DetailsDialog.Detail.Section("Website (IIS)"));
            details.Add(new("Status", row.StateText, row.State switch
            {
                SiteRunState.Running => DetailKind.Good,
                SiteRunState.NoSite => DetailKind.Normal,
                SiteRunState.Stopped => DetailKind.Bad,
                _ => DetailKind.Warning
            }));
            details.Add(new("URL", s.SiteUrl));
            if (site is not null)
            {
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
                s.DatabaseExists || s.SqlReachable != true ? DetailKind.Normal : DetailKind.Warning));
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
}
