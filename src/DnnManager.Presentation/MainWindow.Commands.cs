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
            string keywords = "", bool inTerminal = false) =>
            _commands.Add(new AppCommand
            {
                Id = id, Title = title, Area = area, DefaultShortcut = shortcut, Run = run, IsAvailable = available ?? (() => true),
                Keywords = keywords, InTerminal = inTerminal
            });

        void AddForProject(string id, string title, string? shortcut, Func<ProjectRow, bool> fits, Action<ProjectRow> run, string keywords = "") =>
            _commands.Add(new AppCommand
            {
                Id = id, Title = title, Area = "Project", DefaultShortcut = shortcut, ForProject = r => r.IsDnn && fits(r), RunOn = run,
                Keywords = keywords
            });

        // ── Application ──
        Add("workbench.commandPalette", "Show all commands", "Application", "Ctrl+Shift+P", () => ShowPalette(commands: true),
            keywords: "command palette actions", inTerminal: true);
        Add("view.search", "Search in this view", "Application", "Ctrl+F", SearchHere, () => SearchTarget() is not null,
            "find filter output log terminal", inTerminal: true);
        Add("app.checkUpdates", "Check for updates", "Application", null, () => _ = CheckForUpdatesAsync(), keywords: "version release github");
        Add("app.installUpdate", "Install the update", "Application", null, () => _ = _updater.UpdateAsync(),
            () => _updater.CanInstall && !_runner.IsBusy, "upgrade new version");
        Add("app.restart", "Restart DNN Manager", "Application", null, () => AppRestart.Restart(), () => !_runner.IsBusy, "reload");
        Add("app.colorTheme", "Color theme…", "Application", null, ShowThemePick, keywords: "dark light modern system appearance");

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
            SetLogOpen(true);
            TerminalPanel.ShowLogs(r, null);
        }, "dnn iis event log");
        AddForProject("project.terminal", "Open terminal in project folder", null, r => _terminal.Settings.Enabled && Directory.Exists(r.Path), r =>
        {
            SetLogOpen(true);
            TerminalPanel.NewTerminal(directory: r.Path);
        }, "shell powershell command prompt");
        AddForProject("project.keepWarm", "Keep website warm", null, r => !r.KeepWarmOn && r.CanToggleKeepWarm, _store.ToggleKeepWarm, "ping alive flame");
        AddForProject("project.stopKeepWarm", "Stop keeping website warm", null, r => r.KeepWarmOn, _store.ToggleKeepWarm, "ping alive flame");

        // ── Pages ──
        Add("pages.projects", "Go to Projects", "Pages", "Ctrl+1", () => Go(NavProjects), keywords: "table sites");
        Add("pages.newProject", "New project", "Pages", "Ctrl+2", () => Go(NavSetup), keywords: "create setup install");
        Add("pages.hostProject", "Host project", "Pages", "Ctrl+3", () => Go(NavExisting), keywords: "existing folder");
        Add("settings.open", "Open Settings", "Pages", "Ctrl+,", () => { if (ModalKey == "Settings") CloseModal(); else ShowModal("Settings"); }, keywords: "preferences options", inTerminal: true);
        Add("settings.keyboard", "Open Keyboard Shortcuts", "Pages", null, () => OpenSettings("Keyboard"), keywords: "keys bindings hotkeys");
        Add("troubleshoot.open", "Run troubleshooting", "Pages", null, () => ShowModal("Troubleshoot"), keywords: "restart reset clean up data");
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
            SetLogOpen(true);
            TerminalPanel.ShowActivity();
        }, keywords: "activity operations", inTerminal: true);
        Add("panel.clearOutput", "Clear the output", "Panel", null, _log.Clear, () => !_runner.IsBusy, "clear logs activity");
        Add("terminal.toggle", "Show the terminal", "Terminal", "Ctrl+`", ToggleTerminal, () => _terminal.Settings.Enabled, "shell", inTerminal: true);
        Add("terminal.new", "New terminal", "Terminal", "Ctrl+Shift+`", () =>
        {
            SetLogOpen(true);
            TerminalPanel.NewTerminal();
        }, () => _terminal.Settings.Enabled, "shell powershell", inTerminal: true);
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
        if (e.Handled || Palette.IsOpen || Shortcut.Of(e) is not { } pressed) return;
        if (Keyboard.FocusedElement is FrameworkElement { Tag: ShortcutRecording }) return;
        if (_commands.Match(pressed) is not { } command) return;
        if (Keyboard.FocusedElement is TerminalView && !command.InTerminal) return;
        e.Handled = true;
        Execute(command, fromPalette: false);
    }

    /// <summary>The tag a control has while it records a shortcut - every key goes to it then.</summary>
    public const string ShortcutRecording = "RecordingShortcut";

    /// <summary>
    /// Runs <paramref name="command"/>. A project command acts on the selected project; with none - or from the palette
    /// on one it doesn't fit - the palette asks which project.
    /// </summary>
    private void Execute(AppCommand command, bool fromPalette)
    {
        if (!command.IsProjectCommand)
        {
            if (command.IsAvailable()) command.Run?.Invoke();
            return;
        }
        var selected = CurrentProject;
        if (selected is not null && command.ForProject!(selected))
        {
            command.RunOn!(selected);
            return;
        }
        if (selected is not null && !fromPalette)
        {
            Toast.Show($"{command.Title}: not for {selected.Name} as it is now ({selected.StateText}).", ToastKind.Warning);
            return;
        }
        var fitting = _store.Projects.Where(command.ForProject!).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (fitting.Count == 0)
        {
            Toast.Show($"{command.Title}: no project it applies to now.", ToastKind.Warning);
            return;
        }
        Palette.ShowPick($"{command.Title} - which project?", fitting.Select(r => ProjectItem(r, () => command.RunOn!(r))).ToList());
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

    /// <summary>The commands that make sense now - a project command when the selected project, or any, fits it.</summary>
    private List<PaletteItem> CommandItems()
    {
        var selected = CurrentProject;
        var items = new List<PaletteItem>();
        foreach (var command in _commands.All)
        {
            string detail;
            if (command.IsProjectCommand)
            {
                if (selected is not null && command.ForProject!(selected)) detail = selected.Name;
                else if (_store.Projects.Any(command.ForProject!)) detail = "choose a project";
                else continue;
            }
            else if (!command.IsAvailable()) continue;
            else detail = "";
            var run = command;
            items.Add(new PaletteItem(command.Label, detail, _commands.ShortcutOf(command)?.ToString() ?? "", () => Execute(run, fromPalette: true),
                command.Keywords));
        }
        return items;
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
        SetLogOpen(true);
        TerminalPanel.ShowTerminal();
    }

    private async Task CheckForUpdatesAsync()
    {
        await _updater.CheckAsync();
        Toast.Show(_updater.StatusText, _updater.State == UpdateState.Unreachable ? ToastKind.Warning : ToastKind.Info,
            _updater.CanInstall ? "Update" : null, _updater.CanInstall ? () => _ = _updater.UpdateAsync() : null);
    }
}
