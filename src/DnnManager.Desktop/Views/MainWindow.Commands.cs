using Avalonia.Controls;
using Avalonia.Input;
using DnnManager.Desktop.Services;
using DnnManager.Desktop.Views.Workbench;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Views;

// The keyboard (src/DnnManager.Presentation/MainWindow.Commands.cs): every command DNN Manager has (AppCommands), the
// shortcuts that run them, and the command palette - like VS Code. A command shows in the palette only when it can do
// something now, and its shortcut does nothing otherwise (the key then goes on to the control that has the keyboard).
public partial class MainWindow
{
    private const double KeyboardResizeStep = 20;

    /// <summary>The commands, in the palette's order - the same ids (and so the same saved shortcuts) as the Windows app's.</summary>
    private void RegisterCommands()
    {
        void Add(string id, string title, string area, string? shortcut, Action run, Func<bool>? available = null, string keywords = "",
            bool inPalette = true) =>
            _commands.Add(new AppCommand
            {
                Id = id, Title = title, Area = area, DefaultShortcut = shortcut, Run = run, IsAvailable = available ?? (() => true),
                Keywords = keywords, InPalette = inPalette
            });

        // ── Application ──
        Add("workbench.commandPalette", "Show all commands", "Application", "Ctrl+Shift+P", () => ShowPalette(commands: true),
            keywords: "command palette actions", inPalette: false);
        Add("view.search", "Search in this view", "Application", "Ctrl+F", SearchHere, () => SearchTarget() is not null, "find filter output log");
        Add("app.checkUpdates", "Check for updates", "Application", null, () => _ = CheckForUpdatesAsync(), keywords: "version release github");
        Add("app.whatsNew", "What's new in this version", "Application", null, () => _ = ShowWhatsNewAsync(), () => !IsNewUser,
            "release notes changes changelog version");
        Add("app.installUpdate", "Install the update", "Application", null, () => _ = _updater.UpdateAsync(),
            () => _updater.CanInstall && !_runner.IsBusy, "upgrade new version");
        Add("app.restart", "Restart DNN Manager", "Application", null, () => _ = Services.AppRestart.RestartAsync(), () => !_runner.IsBusy, "reload");
        Add("app.quit", "Quit DNN Manager", "Application", null, () => _ = QuitAsync(), keywords: "exit close stop background notification area tray");
        Add("app.colorTheme", "Color theme…", "Application", null, ShowThemePick, keywords: "dark light modern system appearance");

        // ── Help ──
        Add("help.page", "Help for this page", "Help", "F1", () => _ = ShowPageHelpAsync(), keywords: "explain what is this question how");
        Add("help.gettingStarted", "Getting started guide", "Help", null, () => _ = ShowGuideAsync(),
            keywords: "welcome onboarding tutorial introduction beginner first start how to");
        Add("help.tour", "Take the tour", "Help", null, StartTour, keywords: "tutorial walkthrough show around highlight window");
        Add("help.userGuide", "User guide on GitHub", "Help", null, OpenUserGuide, keywords: "documentation docs manual read");

        // ── Projects ──
        Add("projects.quickOpen", "Go to project…", "Projects", "Ctrl+P", () => ShowPalette(commands: false), keywords: "open find quick");

        // ── Pages ──
        Add("pages.projects", "Go to Projects", "Pages", "Ctrl+1", () => Go("Projects"), keywords: "table sites");
        Add("pages.newProject", "New project", "Pages", "Ctrl+2", () => Go("Setup"), keywords: "create setup install");
        Add("pages.deploy", "Go to Source Control", "Pages", "Ctrl+3", () => Go("SourceControl"), keywords: "deploy ftp sftp push pull server live");
        Add("scm.showSourceControl", "Show Source Control", "Source Control", "Ctrl+Shift+G", () => ShowSourceControl(SideView.SourceControl),
            keywords: "changes push pull git view");
        Add("scm.showExplorer", "Show Explorer", "Source Control", "Ctrl+Shift+E", () => ShowSourceControl(SideView.Explorer),
            keywords: "files tree folder open editors project");
        Add("scm.showSearch", "Search the project's files", "Source Control", null, () => ShowSourceControl(SideView.Search),
            keywords: "find replace grep text in files");
        // The Windows app's Ctrl+Shift+F: the Projects table's search - on Source Control, its Search (VS Code's).
        Add("projects.search", "Search projects", "Projects", "Ctrl+Shift+F", () => ShowSourceControl(SideView.Search),
            keywords: "find filter table files");
        Add("settings.open", "Open Settings", "Pages", "Ctrl+,", () =>
        {
            if (_modalKey == "Settings") _ = CloseModalAsync();
            else ShowModal("Settings");
        }, keywords: "preferences options");
        Add("troubleshoot.open", "Run troubleshooting", "Pages", null, () => ShowModal("Troubleshoot"), keywords: "restart reset clean up data");

        // ── Settings: each category, straight from the palette ──
        foreach (var (id, category, name, keywords) in new[]
        {
            ("settings.general", "General", "General", "start sign in background tray scale font"),
            ("settings.projects", "Projects", "Projects", "folder hostname port dnn defaults keep warm"),
            ("settings.releases", "Releases", "DNN releases", "repositories github versions packages upgrade path"),
            ("settings.databaseServer", "Sql", "Database server", "sql server express localdb connection ssms"),
            ("settings.docker", "Docker", "Docker container", "container volume edition collation compose"),
            ("settings.iis", "Iis", "IIS", "windows features"),
            ("settings.deploy", "Deploy", "Source Control", "deploy ftp sftp server push pull history versions ignore rules"),
            ("settings.keyboard", "Keyboard", "Keyboard shortcuts", "keys bindings hotkeys"),
            ("settings.help", "Help", "Help", "getting started guide tour tutorial what's new"),
            ("settings.about", "About", "About", "version license repository folders"),
        })
            Add(id, name, "Settings", null, () => OpenSettings(category), keywords: "open preferences options " + keywords);
        Add("view.next", "Next page", "Pages", "Ctrl+Tab", () => StepPage(1), keywords: "switch view");
        Add("view.previous", "Previous page", "Pages", "Ctrl+Shift+Tab", () => StepPage(-1), keywords: "switch view");
        Add("view.nextTab", "Next tab", "Pages", "Ctrl+PageUp", () => StepTab(1), () => _modal is SettingsView, "category panel");
        Add("view.previousTab", "Previous tab", "Pages", "Ctrl+PageDown", () => StepTab(-1), () => _modal is SettingsView, "category panel");
        Add("view.close", "Close this view", "Pages", "Ctrl+W", CloseView, () => CanCloseView, "back settings troubleshoot tab editor");

        // ── Layout ──
        Add("view.toggleSidebar", "Show or hide the primary side bar", "Layout", "Ctrl+B", TogglePrimarySideBar, () => PageSideBar is not null,
            "sidebar source control changes view");
        Add("view.toggleSecondarySideBar", "Show or hide the secondary side bar", "Layout", "Ctrl+Alt+B", ToggleSecondarySideBar, keywords: "auxiliary bar right");
        Add("view.toggleActivityBar", "Show or hide the activity bar", "Layout", null, ToggleActivityBar, keywords: "pages icons navigation sidebar");
        Add("view.sidebarWider", "Make the primary side bar wider", "Layout", null, () => ResizePrimaryBy(KeyboardResizeStep), () => PrimaryShown, "size width sash");
        Add("view.sidebarNarrower", "Make the primary side bar narrower", "Layout", null, () => ResizePrimaryBy(-KeyboardResizeStep), () => PrimaryShown, "size width sash");
        Add("view.toggleMenuBar", "Show or hide the menu bar", "Layout", null, () => ChangeLayout(l => l.MenuBarVisible = !l.MenuBarVisible), keywords: "title name");
        Add("view.toggleStatusBar", "Show or hide the status bar", "Layout", null, () => ChangeLayout(l => l.StatusBarVisible = !l.StatusBarVisible),
            keywords: "bottom iis");
        Add("layout.customize", "Customize layout…", "Layout", null, ShowCustomizeLayout,
            keywords: "sidebar panel position alignment justify center quick input density compact menu bar status bar");

        // ── Panel ──
        Add("panel.toggle", "Show or hide the panel", "Panel", "Ctrl+J", TogglePanel, keywords: "output logs bottom");
        Add("panel.taller", "Make the panel taller", "Panel", null, () => ResizePanelBy(KeyboardResizeStep), () => PanelOpen, "size height sash");
        Add("panel.shorter", "Make the panel shorter", "Panel", null, () => ResizePanelBy(-KeyboardResizeStep), () => PanelOpen, "size height sash");
        Add("panel.maximize", "Maximize or restore the panel", "Panel", "Ctrl+Shift+M", () => SetPanelMaximized(!_panelMaximized));
        Add("panel.output", "Show Output", "Panel", "Ctrl+Shift+U", ShowOutput, keywords: "activity operations");
        Add("panel.clearOutput", "Clear the output", "Panel", null, _log.Clear, () => !_runner.IsBusy, "clear logs activity");
        Add("panel.appLog", "Show DNN Manager's log", "Panel", null, () =>
        {
            OpenPanel();
            OutputPanel.ShowLogs();
        }, keywords: "logs dnnmanager file warnings errors");
        // The Windows app's terminal shortcut: here the panel (there is no terminal in it yet).
        Add("terminal.toggle", "Show or hide the panel (terminal)", "Panel", "Ctrl+`", TogglePanel, keywords: "shell output", inPalette: false);
        _commands.Changed += (_, _) => UpdateLayoutButtons();
    }

    // ─── Shortcuts ──────────────────────────────────────────────────────────

    /// <summary>
    /// A key press that is a command's shortcut runs it - before the control with the keyboard sees it, when the command can
    /// run now. While the palette or the tour is open every key goes to them.
    /// </summary>
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Settings → Keyboard Shortcuts recording a new shortcut: the keys are its, not a command's.
        if (SettingsView.IsRecordingShortcut) return;
        if (e.Handled || Palette.IsOpen || Tour.IsOpen || Shortcut.Of(e) is not { } pressed) return;
        if (_commands.Match(pressed) is not { } command || !command.IsAvailable()) return;
        e.Handled = true;
        command.Run?.Invoke();
    }

    // Esc that nothing else took: Settings or Troubleshoot closed (a page of their own takes Esc first).
    private void Window_KeyDownLate(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        if (_modal is not null)
        {
            _ = CloseModalAsync();
            e.Handled = true;
        }
    }

    // ─── The command palette ────────────────────────────────────────────────

    /// <summary>Ctrl+P (the projects) or Ctrl+Shift+P (the commands) - what fits now.</summary>
    private void ShowPalette(bool commands) => Palette.ShowQuick(ProjectItems(), CommandItems(), commands);

    /// <summary>
    /// The projects Go to project lists: the projects folder's, each opening Source Control on it - until the Projects page is
    /// hosted here, which lists them (as the Windows app's palette lists its table).
    /// </summary>
    private List<PaletteItem> ProjectItems()
    {
        var folder = _options.BaseDirectory;
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => new PaletteItem(name, Path.Combine(folder, name), "", () => Go("SourceControl"), "project site folder"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The commands that make sense now - those last run from the palette first ("recently used"), as VS Code lists them.</summary>
    private List<PaletteItem> CommandItems()
    {
        var recent = RecentCommands;
        var items = new List<PaletteItem>();
        foreach (var command in _commands.All.Where(c => c.InPalette && c.IsAvailable())
                     .OrderBy(c => recent.IndexOf(c.Id) is var at and >= 0 ? at : int.MaxValue))
        {
            var run = command;
            var group = recent.Count == 0 ? "" : recent.Contains(command.Id) ? "recently used" : "other commands";
            items.Add(new PaletteItem(command.Label, "", _commands.ShortcutOf(command)?.ToString() ?? "", () =>
            {
                Remember(run);
                if (run.IsAvailable()) run.Run?.Invoke();
            }, command.Keywords) { Group = group });
        }
        return items.Any(i => i.Group == "recently used") ? items : [.. items.Select(i => i with { Group = "" })];
    }

    private List<string>? _recentCommands;

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

    private async void OpenSettings(string category)
    {
        if (_modalKey != "Settings") await ShowModalAsync("Settings");
        (_modal as SettingsView)?.ShowCategory(category);
    }

    /// <summary>Source Control with <paramref name="view"/> in its side bar.</summary>
    private async void ShowSourceControl(SideView view)
    {
        if (!await CloseModalAsync(focusPage: false)) return;
        Show("SourceControl");
        (ViewHost.Content as SourceControlView)?.ShowView(view);
    }

    /// <summary>What Ctrl+F searches: the panel when it has the keyboard (or is open on a page without a search of its own).</summary>
    private object? SearchTarget() =>
        _modal is not null ? null
        : PanelOpen && OutputPanel.IsKeyboardFocusWithin ? OutputPanel
        : PanelOpen && ViewHost.Content is not SourceControlView ? OutputPanel : null;

    private void SearchHere()
    {
        if (SearchTarget() is OutputPanel panel) panel.OpenSearch();
    }

    private void StepPage(int by)
    {
        var current = Array.IndexOf(SidebarPages, _page);
        Go(SidebarPages[((Math.Max(current, 0) + by) % SidebarPages.Length + SidebarPages.Length) % SidebarPages.Length]);
    }

    private void StepTab(int by) => (_modal as SettingsView)?.StepCategory(by);

    private bool CanCloseView => _modal is not null || ViewHost.Content is SourceControlView { HasOpenTab: true };

    /// <summary>Ctrl+W: Settings and Troubleshoot closed, back to the page under them; Source Control's chosen tab closed.</summary>
    private void CloseView()
    {
        if (_modal is not null) _ = CloseModalAsync();
        else (ViewHost.Content as SourceControlView)?.CloseActiveTab();
    }
}
