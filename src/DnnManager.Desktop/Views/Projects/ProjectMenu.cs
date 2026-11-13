using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Desktop.Services;
using DnnManager.Domain;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>
/// The Projects table's right-click menu (the Windows app's ProjectMenu): the row's start / stop actions and keep warm,
/// then the less frequent ones - its overview, open (site, folder, IDE, SSMS), the site's tools (clear its cache, its
/// logs), export and remove. The site's tools are also the overview's ⋮ button (<see cref="ShowSiteTools"/>).
/// </summary>
internal sealed class ProjectMenu(IServiceProvider services, OperationRunner runner, Action<SiteAction, ProjectRow> control,
    Action<ProjectRow> remove, Action<ProjectRow> open, Action<ProjectRow> keepWarm)
{
    /// <summary>The menu for <paramref name="row"/>; the IDE entries depend on what is installed.</summary>
    public ContextMenu Build(ProjectRow row)
    {
        var menu = new ContextMenu();
        var items = menu.Items;
        if (row.ShowStart) items.Add(Item("Start", () => control(SiteAction.Start, row), row.CanStart));
        if (row.ShowStop)
        {
            items.Add(Item("Stop", () => control(SiteAction.Stop, row), row.CanStop));
            items.Add(Item("Restart", () => control(SiteAction.Restart, row), row.CanRestart));
        }
        var warm = Item(row.KeepWarmAction, () => keepWarm(row), row.CanToggleKeepWarm);
        Tip(warm, row.KeepWarmOn ? row.KeepWarmText : row.KeepWarmUnavailable);
        items.Add(warm);
        items.Add(new Separator());

        items.Add(OpenWithMenu(row));
        items.Add(new Separator());

        var details = Item("Details…", () => open(row));
        details.FontWeight = FontWeight.SemiBold;
        items.Add(details);
        items.Add(Item("Open site", () => ProjectShell.Open(row.Url), row.HasUrl));
        items.Add(Item("Open folder", () => OpenFolder(row)));
        // A shell in the project's folder - a terminal window (the Windows app opens it in its terminal panel).
        items.Add(Item("Open in terminal", () => ProjectShell.OpenTerminal(row.Path), Directory.Exists(row.Path)));
        items.Add(new Separator());
        AddSiteTools(items, services, row);
        items.Add(new Separator());
        items.Add(Item("Rename…", () => ProjectEdits.Rename(services, row), ProjectEdits.CanEdit(services, row)));
        items.Add(Item("Clone…", () => Clone(row), Directory.Exists(row.Path) && !runner.IsBusy));
        if (row.IsDnn) items.Add(UpgradeItem(services, row));
        items.Add(ExportMenu(services, row));
        items.Add(RestoreMenu(services, row));
        items.Add(Item("Remove…", () => remove(row), row.CanRemove));
        return menu;
    }

    /// <summary>
    /// "Open with…": the code editors and IDEs found on this PC for the project's folder, then SQL Server Management
    /// Studio for its database - nothing listed that isn't installed.
    /// </summary>
    private MenuItem OpenWithMenu(ProjectRow row)
    {
        var installed = IdeLocator.Installed.Count + IdeLocator.ManagementStudios.Count > 0;
        var openWith = Lazy("Open with…", menu => FillOpenWith(menu, row));
        if (!installed)
        {
            openWith.IsEnabled = false;
            Tip(openWith, "No code editor, IDE or SQL Server Management Studio found on this PC.");
        }
        return openWith;
    }

    private void FillOpenWith(MenuItem openWith, ProjectRow row)
    {
        var solution = IdeLocator.SolutionFor(row.Path);
        foreach (var ide in IdeLocator.Installed)
        {
            var header = ide.Name + (ide.OpensSolution && solution is not null ? $"  ({Path.GetFileName(solution)})" : "");
            var item = Item(header, () => OpenInIde(ide, row));
            Tip(item, ide.ExePath);
            openWith.Items.Add(item);
        }

        var studios = IdeLocator.ManagementStudios;
        if (studios.Count == 0) return;
        if (openWith.Items.Count > 0) openWith.Items.Add(new Separator());
        var projectDatabase = ProjectDatabaseName(row);
        foreach (var ssms in studios)
        {
            var item = new MenuItem { Header = ssms.Name };
            Tip(item, ssms.ExePath);
            // A copy DNN Manager doesn't start as Administrator (others could change it): greyed, saying why.
            if (IdeLocator.Refusal(ssms) is { } refusal)
            {
                item.IsEnabled = false;
                Tip(item, refusal);
                openWith.Items.Add(item);
                continue;
            }
            item.Items.Add(Item($"Default  (local SQL Server, {services.GetRequiredService<IOptions<AppOptions>>().Value.Docker.SqlUser})",
                () => OpenDatabase(ssms, row, project: false)));
            var projectItem = Item(projectDatabase is null ? "Project database" : $"Project  ([{projectDatabase}])",
                () => OpenDatabase(ssms, row, project: true), projectDatabase is not null);
            if (projectDatabase is null) Tip(projectItem, row.DatabaseTip ?? "The site's web.config names no database.");
            item.Items.Add(projectItem);
            openWith.Items.Add(item);
        }
    }

    /// <summary>
    /// A submenu filled the first time it opens - what it lists is read from disk or IIS, and most menus close without
    /// it. Until then it holds one placeholder, so it shows as a submenu.
    /// </summary>
    private static MenuItem Lazy(string header, Action<MenuItem> fill)
    {
        var menu = new MenuItem { Header = header };
        menu.Items.Add(new MenuItem { Header = "Reading…", IsEnabled = false });
        var filled = false;
        menu.SubmenuOpened += (_, e) =>
        {
            if (filled || !ReferenceEquals(e.Source, menu)) return;
            filled = true;
            menu.Items.Clear();
            fill(menu);
        };
        return menu;
    }

    /// <summary>"Clone…": a copy of the project - its files, its database and an IIS website of its own.</summary>
    private async void Clone(ProjectRow row)
    {
        var projects = services.GetRequiredService<IProjectRepository>();
        var store = ProjectsServices.Store;
        string? Problem(string name) =>
            ProjectName.Validate(name) is { Success: false } invalid ? invalid.Error
            : projects.ProjectExists(name) ? $"A project named '{name}' already exists."
            : store.Projects.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? $"IIS already has a site named '{name}'."
            : null;
        // "shop_copy", or "shop_copy2"… when that is taken.
        var suggested = $"{row.Name}_copy";
        for (var n = 2; Problem(suggested) is not null && n < 100; n++) suggested = $"{row.Name}_copy{n}";

        if (await InputDialog.ShowAsync($"Clone '{row.Name}' - its files, its database and an IIS website of its own - as a new project named:",
                suggested, "Clone", Problem) is not { } target)
            return;
        var request = new CloneProjectRequest
        {
            TargetProjectName = target,
            SourceDirectory = row.Path,
            SourceBackupServerPath = Path.Combine(Path.GetTempPath(), $"dnnmanager_clone_{target}_{DateTime.Now:yyyyMMddHHmmss}.bak"),
            CreateIisSite = true
        };
        if (await runner.RunAsync($"Clone '{row.Name}' → '{target}'",
                (sp, reporter, ct) => sp.GetRequiredService<CloneProjectUseCase>().ExecuteAsync(request, reporter, ct)))
            ProjectNotice.Show($"'{target}' is ready - a copy of '{row.Name}' with its own database and IIS site.", NoticeKind.Success);
    }

    // ─── The site's tools (⋮) ─────────────────────────────────────────────

    /// <summary>Opens the site's tools under <paramref name="button"/> - the overview's ⋮ - and Upgrade, Export and Restore.</summary>
    public static void ShowSiteTools(Control button, IServiceProvider services, ProjectRow row)
    {
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        AddSiteTools(menu.Items, services, row);
        menu.Items.Add(new Separator());
        if (row.IsDnn) menu.Items.Add(UpgradeItem(services, row));
        menu.Items.Add(ExportMenu(services, row));
        menu.Items.Add(RestoreMenu(services, row));
        menu.Open(button);
    }

    /// <summary>"Upgrade DNN…": a newer DNN release, after a backup it can go back to.</summary>
    private static MenuItem UpgradeItem(IServiceProvider services, ProjectRow row)
    {
        var upgrade = Item("Upgrade DNN…", () => ProjectEdits.UpgradeDnn(services, row), ProjectEdits.CanEdit(services, row));
        Tip(upgrade, $"From DNN {row.Dnn}, with a backup before each step.");
        return upgrade;
    }

    /// <summary>"Restore backup": the project's complete backups (site and database), newest first - read when it opens.</summary>
    private static MenuItem RestoreMenu(IServiceProvider services, ProjectRow row)
    {
        var restore = Lazy("Restore backup", menu =>
        {
            var backups = ProjectEdits.Backups(services, row);
            var canEdit = ProjectEdits.CanEdit(services, row);
            foreach (var backup in backups)
            {
                var item = Item($"{backup.Created:yyyy-MM-dd HH:mm:ss}" + (backup.Note is { } note ? $"  -  {note}" : ""),
                    () => ProjectEdits.RestoreBackup(services, row, backup), canEdit);
                Tip(item, backup.Folder);
                menu.Items.Add(item);
            }
            if (backups.Count == 0) menu.Items.Add(new MenuItem { Header = "No backup with the site and its database yet", IsEnabled = false });
        });
        Tip(restore, "Puts the site and its database back from a backup.");
        restore.IsEnabled = Directory.Exists(row.Path);
        return restore;
    }

    /// <summary>"Clear website cache…" and "View logs", with the site's logs grouped by where they come from.</summary>
    private static void AddSiteTools(ItemCollection items, IServiceProvider services, ProjectRow row)
    {
        var busy = services.GetRequiredService<OperationRunner>().IsBusy;
        var clear = Item("Clear website cache…", async () => await ProjectsServices.Store.ClearCacheAsync(row), row.CanRemove && !busy);
        Tip(clear, row.IsDnn
            ? "Deletes DNN's cached files and bundled CSS / JavaScript, then recycles the app pool."
            : "Recycles the app pool, which empties what the site keeps in memory.");
        items.Add(clear);
        items.Add(LogsMenu(services, row));
    }

    /// <summary>"View logs": every log found for the site - opened in the Logs window, where it is followed live.</summary>
    private static MenuItem LogsMenu(IServiceProvider services, ProjectRow row) =>
        // Found when it opens, off the UI thread: IIS's configuration and log folders of thousands of files are read.
        Lazy("View logs", async logs =>
        {
            var catalog = services.GetRequiredService<SiteLogCatalog>();
            var (name, id, path, pool) = (row.Name, row.IisSite.Id, row.Path, row.IisSite.AppPool);
            logs.Items.Add(new MenuItem { Header = "Reading…", IsEnabled = false });
            var sources = await Task.Run(() => catalog.For(name, id, path, pool));
            logs.Items.Clear();
            string? group = null;
            foreach (var source in sources)
            {
                if (source.Group != group)
                {
                    if (group is not null) logs.Items.Add(new Separator());
                    group = source.Group;
                    logs.Items.Add(new MenuItem { Header = group, IsEnabled = false, FontSize = 11 });
                }
                var item = Item(source.Title, () => LogsWindow.Show(row, source));
                Tip(item, source.FilePath is null ? source.Description : $"{source.Description}\n{source.FilePath}");
                logs.Items.Add(item);
            }
            if (sources.Count == 0) logs.Items.Add(new MenuItem { Header = "No logs found for this site", IsEnabled = false });
        });

    private static MenuItem Item(string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    private static void Tip(Control item, string? tip)
    {
        ToolTip.SetTip(item, tip);
        ToolTip.SetShowOnDisabled(item, true);
    }

    public static void OpenFolder(ProjectRow row)
    {
        if (Directory.Exists(row.Path)) ProjectShell.Open(row.Path);
        else ProjectShell.Error($"The project folder no longer exists: {row.Path}");
    }

    private static void OpenInIde(Ide ide, ProjectRow row)
    {
        if (!Directory.Exists(row.Path))
        {
            ProjectShell.Error($"The project folder no longer exists: {row.Path}");
            return;
        }
        try { IdeLocator.Open(ide, row.Path); }
        catch (Exception ex) { ProjectShell.Error($"Could not start {ide.Name}: {ex.Message}"); }
    }

    // ─── Export ───────────────────────────────────────────────────────────

    private enum ExportParts { Both, Site, Database }

    /// <summary>"Export": a package for the live server, or a backup into the project's backups folder.</summary>
    private static MenuItem ExportMenu(IServiceProvider services, ProjectRow row)
    {
        var export = new MenuItem { Header = "Export" };
        var busy = services.GetRequiredService<OperationRunner>().IsBusy;
        var deploy = Item("For deployment…", () => ProjectEdits.ExportForDeployment(services, row), ProjectEdits.CanEdit(services, row));
        Tip(deploy, "A package for the live server: the site's files with web.config ready for it, the database with the live domain, and DEPLOY.txt.");
        export.Items.Add(deploy);
        export.Items.Add(new Separator());
        export.Items.Add(Item("Site and database  (.zip + .bacpac)", () => Backup(services, row, ExportParts.Both), !busy));
        export.Items.Add(Item("Site files  (.zip)", () => Backup(services, row, ExportParts.Site), !busy));
        export.Items.Add(Item("Database  (.bacpac)", () => Backup(services, row, ExportParts.Database), !busy));
        export.Items.Add(new Separator());
        var backups = services.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path).BackupDirectory;
        export.Items.Add(Item("Open backups folder", () => ProjectShell.Open(backups), Directory.Exists(backups)));
        return export;
    }

    /// <summary>A dated backup: backups\&lt;project&gt;\&lt;project&gt;_&lt;yyyyMMdd_HHmmss&gt;\ with &lt;project&gt;.zip and / or &lt;project&gt;.bacpac.</summary>
    private static async void Backup(IServiceProvider services, ProjectRow row, ExportParts parts)
    {
        var project = services.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path);
        var folder = ProjectBackups.NewFolder(project, DateTime.Now);
        var request = new ExportProjectRequest
        {
            ProjectName = row.Name,
            ProjectDirectory = row.Path,
            ZipPath = parts == ExportParts.Database ? null : Path.Combine(folder, ProjectBackups.SiteZipName(project)),
            BacpacPath = parts == ExportParts.Site ? null : Path.Combine(folder, ProjectBackups.DatabaseName(project, ".bacpac"))
        };
        if (await services.GetRequiredService<OperationRunner>().RunAsync($"Back up '{row.Name}'",
                (sp, reporter, ct) => sp.GetRequiredService<ExportProjectUseCase>().ExecuteAsync(request, reporter, ct)))
            ProjectNotice.Show($"'{row.Name}' is backed up in {folder}.", NoticeKind.Success, "Open folder", () => ProjectShell.Open(folder));
    }

    // ─── SQL Server Management Studio ─────────────────────────────────────

    /// <summary>The name of the database <paramref name="row"/>'s site uses, for the menu; null if it can't be read.</summary>
    private string? ProjectDatabaseName(ProjectRow row)
    {
        try
        {
            using var scope = services.CreateScope();
            if (row.Project.DatabaseName is null) return null;
            var project = scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path);
            var database = scope.ServiceProvider.GetRequiredService<LocalSqlContainer>().SiteConnectionOf(project)?.Database;
            return database is { Length: > 0 } ? database : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens SSMS on the local SQL Server as the container's user (<paramref name="project"/> false) or on the project's
    /// database - the one its web.config uses. SSMS has no password switch: for a SQL login it opens with the server and
    /// user filled in, and the password is typed there (the Windows app also fills SSMS 21's Connect dialog in itself).
    /// </summary>
    private async void OpenDatabase(Ide ssms, ProjectRow row, bool project)
    {
        try
        {
            SiteSqlConnection database;
            bool onThisMachine;
            using (var scope = services.CreateScope())
            {
                var sql = scope.ServiceProvider.GetRequiredService<LocalSqlContainer>();
                if (!project) database = sql.DefaultConnection;
                else if (sql.SiteConnectionOf(scope.ServiceProvider.GetRequiredService<IProjectRepository>().Build(row.Name, row.Path)) is { } site)
                    database = site;
                else
                {
                    services.GetRequiredService<ActivityLog>().Warn(
                        $"'{row.Name}' has no database to open: {row.DatabaseTip ?? "its web.config names none"}.");
                    return;
                }
                onThisMachine = sql.IsContainerHost(database.Server);
            }

            // Windows authentication to a server web.config names - not on this PC, not the one in Settings - hands it your
            // Windows sign-in: asked once, naming it.
            if (database.User.Length == 0 &&
                !SqlServerAddress.MaySignInAsUser(database.Server, services.GetRequiredService<IOptions<AppOptions>>().Value.DatabaseServer) &&
                !await services.GetRequiredService<IUserPrompt>().ConfirmAsync(SqlServerAddress.SignInQuestion(database.Server, $"Opening it in {ssms.Name}"),
                    $"Sign in to {database.Server}", "Cancel"))
                return;

            // SQL Server reports a missing database as the same "Login failed for user" as a wrong password, so check
            // first: open the server when only the database is missing, and say which of the two it is.
            var log = services.GetRequiredService<ActivityLog>();
            var tester = services.GetRequiredService<ISqlConnectionTester>();
            if (database.Database.Length > 0 && !(await tester.TestAsync(database, CancellationToken.None, timeoutSeconds: 5)).Success)
            {
                var server = await tester.TestAsync(database with { Database = "master" }, CancellationToken.None, timeoutSeconds: 5);
                if (server.Success)
                {
                    log.Warn($"Database [{database.Database}] doesn't exist on {database.Server} - {ssms.Name} opens the server instead. " +
                             "Restore a backup to put it back.");
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
            // Any connection switch makes SSMS 21+ connect at once - failing when a password is needed: it starts bare then.
            var sqlLogin = database.User.Length > 0 && database.Password.Length > 0;
            using var _ = sqlLogin && ssms.MajorVersion >= 21
                ? IdeLocator.FindRunning(ssms) ?? IdeLocator.StartManagementStudio(ssms)
                : IdeLocator.OpenDatabase(ssms, database, onThisMachine, displayName);
            if (sqlLogin)
                log.Info($"Connect in {ssms.Name} to {database.Server} as '{database.User}' - type the password the site's web.config has " +
                         "(DNN Manager doesn't put it on the clipboard here).");
        }
        catch (Exception ex)
        {
            ProjectShell.Error($"Could not start {ssms.Name}: {ex.Message}");
        }
    }
}
