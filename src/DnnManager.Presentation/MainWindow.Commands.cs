using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DnnManager.Application.UseCases;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;
using DnnManager.Presentation.Terminal;

namespace DnnManager.Presentation;

// The keyboard: every command DNN Manager has (AppCommands), the shortcuts that run them, and the command palette -
// like VS Code. A command makes sense only when it can do something now: its palette entry shows only then, and its
// shortcut does nothing otherwise.
public partial class MainWindow
{
    private void RegisterCommands()
    {
        void Add(string id, string title, string area, string? shortcut, Action run, Func<bool>? available = null,
            string keywords = "", bool inTerminal = false, bool inPalette = true) =>
            _commands.Add(new AppCommand
            {
                Id = id, Title = title, Area = area, DefaultShortcut = shortcut, Run = run, IsAvailable = available ?? (() => true),
                Keywords = keywords, InTerminal = inTerminal, InPalette = inPalette
            });

        void AddForProject(string id, string title, string? shortcut, Func<ProjectRow, bool> fits, Action<ProjectRow> run, string keywords = "") =>
            _commands.Add(new AppCommand
            {
                Id = id, Title = title, Area = "Project", DefaultShortcut = shortcut, ForProject = r => r.IsDnn && fits(r), RunOn = run,
                Keywords = keywords
            });

        // ── Application ──
        // Only its shortcut: in the palette it would open what is already open.
        Add("workbench.commandPalette", "Show all commands", "Application", "Ctrl+Shift+P", () => ShowPalette(commands: true),
            keywords: "command palette actions", inTerminal: true, inPalette: false);
        Add("view.search", "Search in this view", "Application", "Ctrl+F", SearchHere, () => SearchTarget() is not null,
            "find filter output log terminal", inTerminal: true);
        Add("app.checkUpdates", "Check for updates", "Application", null, () => _ = CheckForUpdatesAsync(), keywords: "version release github");
        Add("app.whatsNew", "What's new in this version", "Application", null, ShowWhatsNew, () => !IsNewUser,
            "release notes changes changelog version");
        Add("app.installUpdate", "Install the update", "Application", null, () => _ = _updater.UpdateAsync(),
            () => _updater.CanInstall && !_runner.IsBusy, "upgrade new version");
        Add("app.restart", "Restart DNN Manager", "Application", null, () => AppRestart.Restart(), () => !_runner.IsBusy, "reload");
        Add("app.quit", "Quit DNN Manager", "Application", null, Quit, keywords: "exit close stop background notification area tray");
        Add("app.colorTheme", "Color theme…", "Application", null, ShowThemePick, keywords: "dark light modern system appearance");

        // ── Help (MainWindow.Help) ──
        Add("help.page", "Help for this page", "Help", "F1", ShowPageHelp, keywords: "explain what is this question how");
        Add("help.gettingStarted", "Getting started guide", "Help", null, ShowGuide,
            keywords: "welcome onboarding tutorial introduction beginner first start how to");
        Add("help.tour", "Take the tour", "Help", null, StartTour, keywords: "tutorial walkthrough show around highlight window");
        Add("help.userGuide", "User guide on GitHub", "Help", null, OpenUserGuide, keywords: "documentation docs manual read");

        // ── Projects ──
        Add("projects.quickOpen", "Go to project…", "Projects", "Ctrl+P", () => ShowPalette(commands: false),
            keywords: "open find quick", inTerminal: true);
        Add("projects.search", "Search projects", "Projects", "Ctrl+Shift+F", SearchProjects, keywords: "find filter table");

        // ── Project (the selected one, or the one chosen) ──
        AddForProject("project.open", "Open project details", null, _ => true, OpenDetails, "overview inspect show");
        AddForProject("project.start", "Start project", "F5", r => r.CanStart, r => _ = _store.ControlSitesAsync(SiteAction.Start, [r]), "run iis site");
        AddForProject("project.stop", "Stop project", "Shift+F5", r => r.CanStop, r => _ = _store.ControlSitesAsync(SiteAction.Stop, [r]), "iis site");
        AddForProject("project.restart", "Restart project", "Ctrl+Shift+R", r => r.CanRestart,
            r => _ = _store.ControlSitesAsync(SiteAction.Restart, [r]), "recycle iis site");
        AddForProject("project.openWebsite", "Open website", null, r => r.HasUrl, r => Shell.Open(r.Url), "browser url");
        AddForProject("project.openFolder", "Open project folder", null, r => Directory.Exists(r.Path), r => Shell.Open(r.Path), "explorer files");
        AddForProject("project.logs", "Show project logs", null, _ => true, r =>
        {
            OpenPanel();
            TerminalPanel.ShowLogs(r, null);
        }, "dnn iis event log");
        AddForProject("project.terminal", "Open terminal in project folder", null, r => Directory.Exists(r.Path), r =>
        {
            OpenPanel();
            TerminalPanel.NewTerminal(directory: r.Path);
        }, "shell powershell command prompt");
        AddForProject("project.keepWarm", "Keep website warm", null, r => !r.KeepWarmOn && r.CanToggleKeepWarm, _store.ToggleKeepWarm, "ping alive flame");
        AddForProject("project.stopKeepWarm", "Stop keeping website warm", null, r => r.KeepWarmOn, _store.ToggleKeepWarm, "ping alive flame");
        AddForProject("project.rename", "Rename project…", null, r => ProjectEdits.CanEdit(_services, r), r => ProjectEdits.Rename(_services, r),
            "name folder iis site app pool");
        AddForProject("project.editBindings", "Edit host names…", null, r => ProjectEdits.CanEdit(_services, r), r => ProjectEdits.EditBindings(_services, r),
            "bindings port hostname domain alias iis");
        AddForProject("project.editAppPool", "Edit app pool…", null, r => ProjectEdits.CanEdit(_services, r), r => ProjectEdits.EditAppPool(_services, r),
            "application pool clr pipeline identity 32-bit idle iis");
        AddForProject("project.editDatabase", "Change database connection…", null, r => ProjectEdits.CanEdit(_services, r),
            r => ProjectEdits.ChangeDatabase(_services, r), "sql server connection string web.config");
        AddForProject("project.exportForDeployment", "Export for deployment…", null, r => ProjectEdits.CanEdit(_services, r),
            r => ProjectEdits.ExportForDeployment(_services, r), "deploy publish live server production package zip bacpac");
        AddForProject("project.upgradeDnn", "Upgrade DNN…", null, r => ProjectEdits.CanEdit(_services, r), r => ProjectEdits.UpgradeDnn(_services, r),
            "update version release newer platform backup");
        AddForProject("project.restoreBackup", "Restore backup…", null, r => ProjectEdits.CanEdit(_services, r), PickBackup,
            "revert undo go back put back backup zip bacpac");

        // ── Pages ──
        Add("pages.projects", "Go to Projects", "Pages", "Ctrl+1", () => Go(NavProjects), keywords: "table sites");
        Add("pages.newProject", "New project", "Pages", "Ctrl+2", () => Go(NavSetup), keywords: "create setup install");
        Add("pages.hostProject", "Host project", "Pages", "Ctrl+3", () => Go(NavExisting), keywords: "existing folder");
        Add("settings.open", "Open Settings", "Pages", "Ctrl+,", () => { if (ModalKey == "Settings") CloseModal(); else ShowModal("Settings"); }, keywords: "preferences options", inTerminal: true);
        Add("troubleshoot.open", "Run troubleshooting", "Pages", null, () => ShowModal("Troubleshoot"), keywords: "restart reset clean up data");

        // ── Settings: each category, straight from the palette (Settings itself is a page, above) ──
        foreach (var (id, category, name, keywords) in new[]
        {
            ("settings.general", "General", "General", "start sign in background tray scale font terminal"),
            ("settings.projects", "Projects", "Projects", "folder hostname port dnn defaults keep warm"),
            ("settings.releases", "Releases", "DNN releases", "repositories github versions packages"),
            ("settings.databaseServer", "Sql", "Database server", "sql server express localdb connection ssms"),
            ("settings.docker", "Docker", "Docker container", "container volume edition collation compose"),
            ("settings.iis", "Iis", "IIS", "windows features"),
            ("settings.keyboard", "Keyboard", "Keyboard shortcuts", "keys bindings hotkeys"),
            ("settings.help", "Help", "Help", "getting started guide tour tutorial what's new"),
            ("settings.about", "About", "About", "version license repository folders"),
        })
            Add(id, name, "Settings", null, () => OpenSettings(category), keywords: "open preferences options " + keywords);
        Add("view.next", "Next page", "Pages", "Ctrl+Tab", () => StepPage(1), keywords: "switch view");
        Add("view.previous", "Previous page", "Pages", "Ctrl+Shift+Tab", () => StepPage(-1), keywords: "switch view");
        // Next is up (or right), previous down (or left) - in every next / previous pair.
        Add("view.nextTab", "Next tab", "Pages", "Ctrl+PageUp", () => StepTab(1), () => CanStepTab, "details category panel");
        Add("view.previousTab", "Previous tab", "Pages", "Ctrl+PageDown", () => StepTab(-1), () => CanStepTab, "details category panel");
        Add("view.close", "Close this view", "Pages", "Ctrl+W", CloseView, () => CanCloseView, "back details settings troubleshoot");

        // ── Layout ──
        Add("view.toggleSidebar", "Show or hide the sidebar", "Layout", "Ctrl+B", ToggleSidebar, keywords: "primary side bar pages",
            inTerminal: true);
        Add("view.toggleMenuBar", "Show or hide the menu bar", "Layout", null,
            () => ChangeLayout(l => l.MenuBarVisible = !l.MenuBarVisible), keywords: "title name");
        Add("view.toggleStatusBar", "Show or hide the status bar", "Layout", null,
            () => ChangeLayout(l => l.StatusBarVisible = !l.StatusBarVisible), keywords: "bottom iis");
        Add("layout.customize", "Customize layout…", "Layout", null, ShowCustomizeLayout,
            keywords: "sidebar panel position alignment justify center quick input density compact menu bar status bar");

        // ── Panel and terminal ──
        Add("panel.toggle", "Show or hide the panel", "Panel", "Ctrl+J", TogglePanel, keywords: "output logs terminal bottom", inTerminal: true);
        Add("panel.maximize", "Maximize or restore the panel", "Panel", "Ctrl+Shift+M", () =>
        {
            if (!LogOpen) SetLogOpen(true);
            SetPanelMaximized(!_panelMaximized);
        }, inTerminal: true);
        Add("panel.output", "Show Output", "Panel", "Ctrl+Shift+U", () =>
        {
            OpenPanel();
            TerminalPanel.ShowActivity();
        }, keywords: "activity operations", inTerminal: true);
        Add("panel.clearOutput", "Clear the output", "Panel", null, _log.Clear, () => !_runner.IsBusy, "clear logs activity");
        Add("panel.appLog", "Show DNN Manager's log", "Panel", null, () =>
        {
            OpenPanel();
            TerminalPanel.ShowAppLog(null, switchTo: true);
        }, keywords: "logs dnnmanager file warnings errors", inTerminal: true);
        Add("terminal.toggle", "Show the terminal", "Terminal", "Ctrl+`", ToggleTerminal, keywords: "shell", inTerminal: true);
        Add("terminal.new", "New terminal", "Terminal", "Ctrl+Shift+`", () =>
        {
            OpenPanel();
            TerminalPanel.NewTerminal();
        }, keywords: "shell powershell", inTerminal: true);
    }

    // ─── Shortcuts ──────────────────────────────────────────────────────────

    /// <summary>
    /// A key press that is a command's shortcut runs it - before the control with the keyboard sees it. In a terminal
    /// only the commands meant for it there (the palette, the panel, Settings…) are taken; every other key goes to the
    /// shell. While the palette is open, or a shortcut is being recorded on Settings, every key goes to them.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // The tour has the keys while it is open (← → Esc); nothing under it runs.
        if (e.Handled || Palette.IsOpen || Tour.IsOpen || Shortcut.Of(e) is not { } pressed) return;
        if (Keyboard.FocusedElement is FrameworkElement { Tag: ShortcutRecording }) return;
        if (_commands.Match(pressed) is not { } command) return;
        if (Keyboard.FocusedElement is TerminalView && !command.InTerminal) return;
        e.Handled = true;
        Execute(command, fromPalette: false);
    }

    /// <summary>The tag a control has while it records a shortcut - every key goes to it then.</summary>
    public const string ShortcutRecording = "RecordingShortcut";

    /// <summary>
    /// Runs <paramref name="command"/>. From the palette a project command always asks which project - the selected one
    /// first, so Enter takes it. Its shortcut acts on the selected project; with none, the palette asks.
    /// </summary>
    private void Execute(AppCommand command, bool fromPalette)
    {
        if (!command.IsProjectCommand)
        {
            if (command.IsAvailable()) command.Run?.Invoke();
            return;
        }
        var selected = CurrentProject;
        if (!fromPalette && selected is not null)
        {
            if (command.ForProject!(selected)) command.RunOn!(selected);
            else Toast.Show($"{command.Title}: not for {selected.Name} as it is now ({selected.StateText}).", ToastKind.Warning);
            return;
        }
        var fitting = _store.Projects.Where(command.ForProject!)
            .OrderBy(r => r != selected).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (fitting.Count == 0)
        {
            Toast.Show($"{command.Title}: no project it applies to now.", ToastKind.Warning);
            return;
        }
        Palette.ShowPick($"{command.Title} - which project?", fitting.Select(r => ProjectItem(r, () => command.RunOn!(r))).ToList());
    }

    /// <summary>Restore backup… from the palette: which of the project's backups, newest first.</summary>
    private void PickBackup(ProjectRow row)
    {
        var backups = ProjectEdits.Backups(_services, row);
        if (backups.Count == 0)
        {
            Toast.Show($"'{row.Name}' has no backup with its site and database yet - Export → Site and database makes one.", ToastKind.Warning);
            return;
        }
        Palette.ShowPick($"Restore which backup of '{row.Name}'?", backups
            .Select(b => new PaletteItem($"{b.Created:yyyy-MM-dd HH:mm:ss}" + (b.Note is { } note ? $"  -  {note}" : ""), b.Folder, "",
                () => ProjectEdits.RestoreBackup(_services, row, b)))
            .ToList());
    }

    /// <summary>The project the commands act on: the open Details', else the table's selected row - on the Projects page.</summary>
    private ProjectRow? CurrentProject => PageHost.Content is ProjectsPage projects ? projects.CurrentProject : null;

    // ─── The command palette ────────────────────────────────────────────────

    /// <summary>Ctrl+P (the projects) or Ctrl+Shift+P (the commands) - what fits now.</summary>
    private void ShowPalette(bool commands)
    {
        var projects = _store.Projects.Where(r => r.IsDnn).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => ProjectItem(r, () => OpenDetails(r))).ToList();
        Palette.ShowQuick(projects, CommandItems(), commands);
    }

    // The search in the middle of the title bar (VS Code's command center): it opens the palette on the projects.
    private void CommandCenter_Click(object sender, RoutedEventArgs e) => ShowPalette(commands: false);

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => SizeCommandCenter();

    /// <summary>
    /// Centred on the window, as VS Code's: 38 % of it, at most 600 - narrower where the name or the buttons beside it
    /// would be in the way, and gone when there's no room left for it.
    /// </summary>
    private void SizeCommandCenter()
    {
        var width = TitleBar.ActualWidth;
        var center = width / 2;
        var room = 2 * Math.Min(center - TitleLeft.Margin.Left - TitleLeft.ActualWidth, width - TitleRight.ActualWidth - center) - 32;
        var fit = Math.Min(Math.Min(width * 0.38, 600), room);
        CommandCenter.Visibility = fit < 120 ? Visibility.Collapsed : Visibility.Visible;
        CommandCenter.Width = Math.Max(fit, 0);
    }

    private static PaletteItem ProjectItem(ProjectRow row, Action run) =>
        new(row.Name, row.HasUrl ? $"{row.StateText} · {row.Url}" : row.StateText, "", run, row.Path);

    /// <summary>
    /// The commands that make sense now - a project command when any project fits it (which one is asked next). Those
    /// last run from the palette first, "recently used", then the "other commands" - as VS Code lists them.
    /// </summary>
    private List<PaletteItem> CommandItems()
    {
        var recent = RecentCommands;
        var commands = _commands.All
            .Where(c => c.InPalette)
            .OrderBy(c => recent.IndexOf(c.Id) is var at and >= 0 ? at : int.MaxValue)
            .ToList();
        var items = new List<PaletteItem>();
        foreach (var command in commands)
        {
            string detail;
            if (command.IsProjectCommand)
            {
                if (!_store.Projects.Any(command.ForProject!)) continue;
                detail = "choose a project";
            }
            else if (!command.IsAvailable()) continue;
            else detail = "";
            var run = command;
            var group = recent.Count == 0 ? "" : recent.Contains(command.Id) ? "recently used" : "other commands";
            items.Add(new PaletteItem(command.Label, detail, _commands.ShortcutOf(command)?.ToString() ?? "", () =>
            {
                Remember(run);
                Execute(run, fromPalette: true);
            }, command.Keywords) { Group = group });
        }
        // None of the recent ones can run now (or is listed): no "other commands" without "recently used" above.
        return items.Any(i => i.Group == "recently used") ? items : [.. items.Select(i => i with { Group = "" })];
    }

    private List<string>? _recentCommands;

    /// <summary>The commands last run from the palette, the newest first (<see cref="PaletteState"/>) - read once, kept for the next start.</summary>
    private List<string> RecentCommands => _recentCommands ??= _workspace.Load<PaletteState>().Recent.Distinct().Take(PaletteState.Kept).ToList();

    private void Remember(AppCommand command)
    {
        var recent = RecentCommands;
        recent.Remove(command.Id);
        recent.Insert(0, command.Id);
        if (recent.Count > PaletteState.Kept) recent.RemoveRange(PaletteState.Kept, recent.Count - PaletteState.Kept);
        _workspace.Store.Save(new PaletteState { Recent = [.. recent] });
    }

    // ─── What the commands do ───────────────────────────────────────────────

    /// <summary>Shows a page with the keyboard in it - on its table, tabs or first field. Closes Settings or Troubleshoot first.</summary>
    private void Go(RadioButton nav)
    {
        if (!CloseModal(focusPage: false)) return;
        nav.IsChecked = true;
        FocusPage();
    }

    /// <summary>The keyboard on what is shown: Settings or Troubleshoot when open over the page, else the page.</summary>
    private void FocusPage() => Dispatcher.BeginInvoke(() =>
    {
        if (_modal is { } modal) modal.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        else if (PageHost.Content is ProjectsPage projects) projects.FocusContent();
        else (PageHost.Content as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }, DispatcherPriority.Loaded);

    private void OpenDetails(ProjectRow row)
    {
        if (!CloseModal(focusPage: false)) return;
        NavProjects.IsChecked = true;
        if (PageHost.Content is not ProjectsPage projects) return;
        projects.OpenProject(row);
        projects.FocusContent();
    }

    private void OpenSettings(string category)
    {
        ShowModal("Settings");
        if (_modal is SettingsPage settings) settings.ShowCategory(category);
    }

    private void SearchProjects()
    {
        if (!CloseModal(focusPage: false)) return;
        NavProjects.IsChecked = true;
        (PageHost.Content as ProjectsPage)?.FocusSearch();
    }

    /// <summary>
    /// What Ctrl+F searches: Settings' search while it is open, the panel when it has the keyboard, else the page's own
    /// search box.
    /// </summary>
    private object? SearchTarget() =>
        _modal is not null ? _modal as SettingsPage
        : LogOpen && TerminalPanel.IsKeyboardFocusWithin ? TerminalPanel
        : PageHost.Content is ProjectsPage ? PageHost.Content
        : LogOpen ? TerminalPanel : null;

    private void SearchHere()
    {
        switch (SearchTarget())
        {
            case Controls.TerminalPanel panel: panel.OpenSearch(); break;
            case ProjectsPage projects: projects.FocusSearch(); break;
            case SettingsPage settings: settings.FocusSearch(); break;
        }
    }

    // The sidebar's pages, in order - Ctrl+Tab goes round them.
    private RadioButton[] SidebarPages => [NavProjects, NavSetup, NavExisting];

    private void StepPage(int by)
    {
        var pages = SidebarPages;
        // From Settings or Troubleshoot: on from the page under it (Go closes them).
        var current = Array.IndexOf(pages, pages.FirstOrDefault(p => p.IsChecked == true) ?? _lastNav ?? NavProjects);
        Go(pages[((current + by) % pages.Length + pages.Length) % pages.Length]);
    }

    private bool CanStepTab =>
        _modal is SettingsPage || LogOpen && TerminalPanel.IsKeyboardFocusWithin || PageHost.Content is ProjectsPage { DetailsOpen: true };

    private void StepTab(int by)
    {
        if (_modal is SettingsPage settings) settings.StepCategory(by);
        else if (LogOpen && TerminalPanel.IsKeyboardFocusWithin) TerminalPanel.StepPane(by);
        else if (PageHost.Content is ProjectsPage { Details: { } details }) details.StepTab(by);
    }

    private bool CanCloseView => _modal is not null || PageHost.Content is ProjectsPage { DetailsOpen: true };

    /// <summary>Ctrl+W: Settings and Troubleshoot closed, back to the page under them; the Details back to the table.</summary>
    private void CloseView()
    {
        if (_modal is not null) CloseModal();
        else if (PageHost.Content is ProjectsPage projects)
        {
            projects.CloseDetails();
            projects.FocusContent();
        }
    }

    /// <summary>Ctrl+J: the panel slides open (with the keyboard in it) or closed (the keyboard back on the page).</summary>
    private void TogglePanel()
    {
        if (!PanelOpening)
        {
            SlidePanel(true);
            TerminalPanel.Focus();
            return;
        }
        ClosePanel();
    }

    /// <summary>
    /// Ctrl+`: the terminal with the keyboard in it; pressed again in the terminal, the panel goes and the keyboard is
    /// back on the page - like VS Code.
    /// </summary>
    private void ToggleTerminal()
    {
        if (LogOpen && TerminalPanel.IsTerminalShown && Keyboard.FocusedElement is TerminalView)
        {
            SetLogOpen(false);
            FocusPage();
            return;
        }
        OpenPanel();
        TerminalPanel.ShowTerminal();
    }

    private async Task CheckForUpdatesAsync()
    {
        await _updater.CheckAsync();
        Toast.Show(_updater.StatusText, _updater.State == UpdateState.Unreachable ? ToastKind.Warning : ToastKind.Info,
            _updater.CanInstall ? "Update" : null, _updater.CanInstall ? () => _ = _updater.UpdateAsync() : null);
    }
}
