using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Domain;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>
/// The Projects table's right-click menu: the row's start / stop actions and keep warm, then the less frequent ones - its
/// overview, open (site, folder, IDE, SSMS), the site's tools (clear its cache, its logs), export and remove. The
/// site's tools are also the row's ⋮ button and the overview's (<see cref="ShowSiteTools"/>).
/// </summary>
internal sealed class ProjectMenu
{
    private readonly IServiceProvider _services;
    private readonly OperationRunner _runner;
    private readonly Action<SiteAction, ProjectRow> _control;
    private readonly Action<ProjectRow> _remove;
    private readonly Action<ProjectRow> _open;
    private readonly Action<ProjectRow> _keepWarm;

    /// <param name="control">Starts, stops or restarts the row's site - the page's row actions.</param>
    /// <param name="remove">Removes the row's project - the page's row action.</param>
    /// <param name="open">Opens the row's overview (ProjectView).</param>
    /// <param name="keepWarm">Switches keep warm on or off for the row's site - the page's flame.</param>
    public ProjectMenu(IServiceProvider services, OperationRunner runner, Action<SiteAction, ProjectRow> control,
        Action<ProjectRow> remove, Action<ProjectRow> open, Action<ProjectRow> keepWarm)
    {
        _services = services; _runner = runner; _control = control; _remove = remove; _open = open; _keepWarm = keepWarm;
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
        var keepWarm = Item(row.KeepWarmAction, (_, _) => _keepWarm(row), row.CanToggleKeepWarm);
        keepWarm.ToolTip = row.KeepWarmOn ? row.KeepWarmText : row.KeepWarmTip;
        menu.Items.Add(keepWarm);
        menu.Items.Add(new Separator());

        menu.Items.Add(OpenWithMenu(row));
        menu.Items.Add(new Separator());

        var open = Item("Details…", (_, _) => _open(row));
        open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        menu.Items.Add(Item("Open site", (_, _) => Shell(row.Url), row.HasUrl));
        menu.Items.Add(Item("Open folder", (_, _) => OpenFolder(row)));
        // A shell in the project's folder, in the terminal panel - unless the terminal is switched off in the settings.
        var terminal = _services.GetRequiredService<TerminalService>();
        if (terminal.Settings.Enabled)
            menu.Items.Add(Item("Open in terminal", (_, _) => terminal.OpenIn(row.Path), Directory.Exists(row.Path)));
        menu.Items.Add(new Separator());
        AddSiteTools(menu.Items, _services, row);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Clone…", (_, _) => Clone(row), Directory.Exists(row.Path) && !_runner.IsBusy));
        menu.Items.Add(ExportMenu(row));
        menu.Items.Add(Item("Remove…", (_, _) => _remove(row), row.CanRemove));
    }

    /// <summary>
    /// "Open with…": the code editors and IDEs found on this PC for the project's folder, then SQL Server Management
    /// Studio for its database - nothing listed that isn't installed.
    /// </summary>
    private MenuItem OpenWithMenu(ProjectRow row)
    {
        var openWith = new MenuItem { Header = "Open with…" };
        var solution = IdeLocator.SolutionFor(row.Path);
        foreach (var ide in IdeLocator.Installed)
        {
            var header = ide.Name + (ide.OpensSolution && solution is not null ? $"  ({System.IO.Path.GetFileName(solution)})" : "");
            var item = Item(header, (_, _) => OpenInIde(ide, row));
            item.ToolTip = ide.ExePath;
            openWith.Items.Add(item);
        }

        var studios = IdeLocator.ManagementStudios;
        if (studios.Count > 0)
        {
            if (openWith.Items.Count > 0) openWith.Items.Add(new Separator());
            var projectDatabase = ProjectDatabaseName(row);
            foreach (var ssms in studios)
            {
                // Default: the local SQL Server as sa. Project: this project's database with its own login.
                // Whether SSMS remembers the password is the SsmsRememberPassword setting.
                var item = new MenuItem { Header = ssms.Name, ToolTip = ssms.ExePath };
                item.Items.Add(Item("Default  (local SQL Server, sa)", (_, _) => OpenDatabase(ssms, row, project: false)));
                item.Items.Add(Item(projectDatabase is null ? "Project database" : $"Project  ([{projectDatabase}])",
                    (_, _) => OpenDatabase(ssms, row, project: true)));
                openWith.Items.Add(item);
            }
        }

        if (openWith.Items.Count == 0)
        {
            openWith.IsEnabled = false;
            openWith.ToolTip = "No code editor, IDE or SQL Server Management Studio found on this PC.";
        }
        return openWith;
    }

    /// <summary>
    /// "Clone…": a copy of the project - its files, its database restored from a backup of it, and an IIS website of its
    /// own - under the name asked for here.
    /// </summary>
    private async void Clone(ProjectRow row)
    {
        var projects = _services.GetRequiredService<IProjectRepository>();
        string? Problem(string name) =>
            ProjectName.Validate(name) is { Success: false } invalid ? invalid.Error
            : projects.ProjectExists(name) ? $"A project named '{name}' already exists."
            : null;
        // "shop_copy", or "shop_copy2"… when that is taken.
        var suggested = $"{row.Name}_copy";
        for (var n = 2; Problem(suggested) is not null && n < 100; n++) suggested = $"{row.Name}_copy{n}";

        if (InputDialog.Show($"Clone '{row.Name}' - its files, its database and an IIS website of its own - as a new project named:",
                suggested, "Clone", Problem) is not { } target)
            return;
        var request = new CloneProjectRequest
        {
            TargetProjectName = target,
            SourceDirectory = row.Path,
            SourceBackupServerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dnnmanager_clone_{target}_{DateTime.Now:yyyyMMddHHmmss}.bak"),
            CreateIisSite = true
        };
        await _runner.RunAsync($"Clone '{row.Name}' → '{target}'",
            (sp, reporter, ct) => sp.GetRequiredService<CloneProjectUseCase>().ExecuteAsync(request, reporter, ct));
    }

    // ─── The site's tools (⋮) ─────────────────────────────────────────────

    /// <summary>Opens the site's tools under <paramref name="button"/> - the ⋮ next to a row's actions, or the overview's.</summary>
    public static void ShowSiteTools(FrameworkElement button, IServiceProvider services, ProjectRow row)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddSiteTools(menu.Items, services, row);
        menu.IsOpen = true;
    }

    /// <summary>"Clear website cache…" and "View logs", with the site's logs grouped by where they come from.</summary>
    private static void AddSiteTools(ItemCollection items, IServiceProvider services, ProjectRow row)
    {
        var store = services.GetRequiredService<ServerStore>();
        var busy = services.GetRequiredService<OperationRunner>().IsBusy;
        var clear = Item("Clear website cache…", async (_, _) => await store.ClearCacheAsync(row), row.CanRemove && !busy);
        clear.ToolTip = row.IsDnn
            ? "Deletes DNN's cached files and bundled CSS / JavaScript, then recycles the app pool."
            : "Recycles the app pool, which empties what the site keeps in memory.";
        items.Add(clear);
        items.Add(LogsMenu(services, row));
    }

    /// <summary>"View logs": every log found for the site - opened on the panel's Logs tab, where it is followed live.</summary>
    private static MenuItem LogsMenu(IServiceProvider services, ProjectRow row)
    {
        var logs = new MenuItem { Header = "View logs" };
        var terminal = services.GetRequiredService<TerminalService>();
        var sources = services.GetRequiredService<SiteLogCatalog>().For(row.Name, row.IisSite.Id, row.Path, row.IisSite.AppPool);
        string? group = null;
        foreach (var source in sources)
        {
            if (source.Group != group)
            {
                if (group is not null) logs.Items.Add(new Separator());
                group = source.Group;
                logs.Items.Add(new MenuItem { Header = group, IsEnabled = false, FontSize = 11 });
            }
            var item = Item(source.Title, (_, _) => terminal.ShowLogs(row, source));
            item.ToolTip = source.FilePath is null ? source.Description : $"{source.Description}\n{source.FilePath}";
            logs.Items.Add(item);
        }
        if (sources.Count == 0) logs.Items.Add(new MenuItem { Header = "No logs found for this site", IsEnabled = false });
        return logs;
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


    public static void OpenFolder(ProjectRow row)
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
        var backups = _services.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path).BackupDirectory;
        export.Items.Add(Item("Open backups folder", (_, _) => Shell(backups), Directory.Exists(backups)));
        return export;
    }

    /// <summary>A dated backup: backups\&lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\ with &lt;project&gt;.zip and / or &lt;project&gt;.bacpac.</summary>
    private async void Backup(ProjectRow row, ExportParts parts)
    {
        var project = _services.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path);
        var folder = ProjectBackups.NewFolder(project, DateTime.Now);
        var request = new ExportProjectRequest
        {
            ProjectName = row.Name,
            ProjectDirectory = row.Path,
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
            if (row.Project.DatabaseName is null) return null;
            var project = scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path);
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
                    ? sql.ConnectionOf(scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path))
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
                             "Check the password in the site's web.config (or Settings → Database server for the local container).");
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
}
