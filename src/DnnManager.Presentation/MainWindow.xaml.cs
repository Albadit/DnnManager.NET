using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Input;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ActivityLog _log;
    private readonly OperationRunner _runner;
    private readonly AppUpdater _updater;
    private readonly WorkspaceService _workspace;
    private readonly ServerStore _store;
    private readonly AppCommands _commands;
    private readonly TerminalService _terminal;
    private readonly SettingsStore _settings;
    private readonly AppOptions _options;

    // One entry per sidebar item. A page is created on its first visit and kept, so its lists (folders, DNN
    // versions…) load once instead of on every visit; its Refresh button, or a finished operation, reloads them.
    // Projects needs neither: it shows the ServerStore, which keeps itself current. Settings is read from
    // settings.json on every visit. Settings and Troubleshoot open over the page (ShowModal), the others in it.
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Projects"]      = typeof(ProjectsPage),
        ["Setup"]         = typeof(SetupPage),
        ["Existing"]      = typeof(ExistingFolderPage),
        ["Settings"]      = typeof(SettingsPage),
        ["Troubleshoot"]  = typeof(TroubleshootPage),
    };

    private readonly Dictionary<string, UserControl> _pages = [];
    // Kept pages an operation may have changed since they were last shown - refreshed on their next visit.
    private readonly HashSet<UserControl> _stale = [];

    public MainWindow(IServiceProvider services, ActivityLog log, OperationRunner runner, IOptions<AppOptions> options,
        DnnReleaseCatalog releases, ServerStore store, TerminalService terminal, SiteLogCatalog logs, EfficiencyMode efficiency,
        AppUpdater updater, WorkspaceService workspace, AppCommands commands, SettingsStore settings)
    {
        _services = services; _log = log; _runner = runner; _updater = updater; _workspace = workspace; _store = store;
        _commands = commands; _terminal = terminal; _settings = settings; _options = options.Value;
        _layout = options.Value.Layout.Copy();
        InitializeComponent();
        Toast.Attach(ToastHost);
        // Minimized, what only the window shows pauses (its parts follow EfficiencyMode.IsSaving). Attached before the
        // pages follow the window's state, so restoring it ends EcoQoS before they catch up.
        efficiency.Attach(this);
        // Ask GitHub for the DNN versions now, in the background, so New project has them when it's opened.
        releases.Preload();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        // The IIS indicator and the status bar's figures show the store's state; it starts following the system when
        // the window is up.
        StatusBar.Attach(store, version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}");
        OperationToast.Attach(runner);
        IisStatus.Attach(store, runner);
        Loaded += (_, _) => store.Start();
        SizeChanged += (_, _) => UpdateCompact();
        Root.SizeChanged += (_, _) => FitPanel();
        // Settings saved: they apply at once. The kept pages were filled in with the old ones (folders, repositories,
        // the container's name) - they are made anew on their next visit. Projects follows by itself.
        options.Value.Changed += () =>
        {
            UpdateCompact(); // the UI scale may have changed
            // Reset to defaults, or saved on Settings: the layout as the file has it.
            _layout = options.Value.Layout.Copy();
            ApplyLayout();
            foreach (var (key, kept) in _pages.Where(p => p.Value is not ProjectsPage).ToList())
            {
                _pages.Remove(key);
                _stale.Remove(kept);
            }
            releases.Preload();
        };

        // The bottom panel (Activity, Logs, Terminal). Closed at first - the running operation is a toast, with its
        // progress and Cancel; the title bar's panel button (Ctrl+J) opens the panel, a click on the operation opens it
        // on Activity.
        TerminalPanel.Attach(_log, terminal, store, logs, efficiency, options.Value);
        TerminalPanel.CloseRequested += (_, _) => ClosePanel();
        TerminalPanel.MaximizeToggled += (_, _) => SetPanelMaximized(!_panelMaximized);
        OperationToast.OperationClicked += (_, _) =>
        {
            SetLogOpen(true);
            TerminalPanel.ShowActivity();
        };
        // "Open in terminal" on a project: a new shell in its folder.
        terminal.OpenRequested += directory =>
        {
            SetLogOpen(true);
            TerminalPanel.NewTerminal(directory: directory);
        };
        // "View logs" on a site: the Logs tab with that log.
        terminal.LogsRequested += (site, source) =>
        {
            SetLogOpen(true);
            TerminalPanel.ShowLogs(site, source);
        };
        Closed += (_, _) =>
        {
            TerminalPanel.CloseAll();
            TerminalPanel.StopLogs();
        };
        SetLogOpen(false);
        ThemeManager.Track(this);

        _runner.PropertyChanged += OnRunnerChanged;
        // A failed operation is said where it is seen - its steps are in the activity log, which may be closed.
        _runner.Failed += (title, error) => Toast.Show($"{title} failed: {error}", ToastKind.Error, "Show output", () =>
        {
            SetLogOpen(true);
            TerminalPanel.ShowActivity();
        });

        // On short screens (e.g. 768px laptops) the default height would push the window off screen.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        // DNN Manager's own updates: the Update button follows the updater.
        _updater.PropertyChanged += (_, _) => ShowUpdate();
        _runner.PropertyChanged += (_, _) => ShowUpdate();

        // Everything the keyboard can do - by shortcut and in the command palette; the buttons' tooltips name the shortcuts.
        RegisterCommands();
        CommandTip.Commands = commands;

        // The workspace - the window, where the user was, what was typed, the panel - as the last start
        // left it (after a close, a restart, an update or a crash), and kept as it changes.
        var layout = workspace.Load<WindowLayout>();
        PlaceWindow(layout);
        _sidebarVisible = !layout.SidebarHidden;
        _sidebarWidth = layout.SidebarWidth is >= MinSidebarWidth and <= MaxSidebarWidth ? layout.SidebarWidth : null;
        ApplyLayout();
        TrackWorkspace();
        Loaded += (_, _) =>
        {
            // Opened maximized: StateChanged didn't run, so the overhang is kept in here.
            if (WindowState == WindowState.Maximized) Window_StateChanged(this, EventArgs.Empty);
            RestoreWorkspace(layout);
            _updater.Start();
        };
        Closing += OnClosing;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key } nav || !Pages.ContainsKey(key)) return;
        var page = PageFor(key);
        if (_stale.Remove(page) && page is IRefreshable refreshable) refreshable.Refresh();
        _lastNav = nav;
        PageHost.Content = page;
    }

    // The sidebar entry of the page shown.
    private RadioButton? _lastNav;

    /// <summary>The page for <paramref name="key"/>: the one kept from its last visit, or a new one - Settings always new.</summary>
    private UserControl PageFor(string key)
    {
        var type = Pages[key];
        if (type != typeof(SettingsPage) && _pages.TryGetValue(key, out var kept)) return kept;

        var page = (UserControl)ActivatorUtilities.CreateInstance(_services, type);
        if (type != typeof(SettingsPage)) _pages[key] = page;
        if (page is ProjectsPage projects) projects.WorkspaceChanged += (_, _) => _workspace.Changed();
        // What was typed on it before DNN Manager restarted - once the page has filled itself in.
        if (type != typeof(SettingsPage) && _drafts.Remove(key, out var draft))
        {
            RoutedEventHandler? fill = null;
            fill = (_, _) =>
            {
                page.Loaded -= fill;
                Dispatcher.BeginInvoke(() => FormDraft.Restore(page, draft), System.Windows.Threading.DispatcherPriority.Background);
            };
            page.Loaded += fill;
        }
        // Their ✕ (and Esc) close them, back to the page under them.
        if (page is SettingsPage settings) settings.CloseRequested += (_, _) => CloseModal();
        if (page is TroubleshootPage troubleshoot) troubleshoot.CloseRequested += (_, _) => CloseModal();
        return page;
    }

    // ─── Settings and Troubleshoot, over the page ───────────────────────────

    // Settings or Troubleshoot while it is open over the page - like VS Code's modal editors.
    private UserControl? _modal;

    private string? ModalKey => _modal is null ? null : Pages.FirstOrDefault(p => p.Value == _modal.GetType()).Key;

    /// <summary>Opens Settings or Troubleshoot over the page - the page dimmed behind it, the keyboard in it.</summary>
    private void ShowModal(string key)
    {
        if (ModalKey == key) return;
        if (!CloseModal(focusPage: false)) return;
        var page = PageFor(key);
        if (_stale.Remove(page) && page is IRefreshable refreshable) refreshable.Refresh();
        _modal = page;
        ModalContent.Content = page;
        ModalHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
            System.Windows.Threading.DispatcherPriority.Loaded);
        _workspace.Changed();
    }

    /// <summary>
    /// Closes Settings or Troubleshoot - asking first when the settings have unsaved changes, as Settings saves only on
    /// Save. False when the user chose to stay.
    /// </summary>
    private bool CloseModal(bool focusPage = true)
    {
        if (_modal is null) return true;
        if (_modal is SettingsPage { HasUnsavedChanges: true } &&
            !Dialogs.Confirm("The settings have unsaved changes. Close them and lose the changes?", "Discard changes", "Stay"))
            return false;
        _modal = null;
        ModalContent.Content = null;
        ModalHost.Visibility = Visibility.Collapsed;
        if (focusPage) FocusPage();
        _workspace.Changed();
        return true;
    }

    // A click on the dimmed page closes it; one in it doesn't.
    private void ModalBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseModal();

    private void ModalBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Let the page refresh whatever the operation changed (new folder, removed site…); the other kept pages
        // catch up when they're next shown. The operation's toast shows it running.
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        if (_runner.IsBusy)
        {
            // Something is happening: with the panel open, show it - the Activity tab.
            if (LogOpen) TerminalPanel.ShowActivity();
            return;
        }
        foreach (var kept in _pages.Values.Where(p => p != PageHost.Content && p != _modal)) _stale.Add(kept);
        if (PageHost.Content is IRefreshable page) page.Refresh();
        if (_modal is IRefreshable over) over.Refresh();
    }

    // ─── Updating DNN Manager ───────────────────────────────────────────────

    private void Update_Click(object sender, RoutedEventArgs e) => _ = _updater.UpdateAsync();

    /// <summary>The Update button: there while a newer release can be installed; its tooltip says how the update is going.</summary>
    private void ShowUpdate()
    {
        var u = _updater;
        UpdateButton.Visibility = u.CanInstall || u.IsUpdating ? Visibility.Visible : Visibility.Collapsed;
        // One update at a time - and not while an operation runs, which closing would cut off.
        UpdateButton.IsEnabled = u.CanInstall && !_runner.IsBusy;
        // It always says "Update"; how the update is going is in its tooltip and on Settings → About.
        UpdateButton.ToolTip = u.State switch
        {
            UpdateState.Downloading => $"Downloading {u.Latest?.Tag}…",
            UpdateState.Installing => $"Installing {u.Latest?.Tag}…",
            UpdateState.Restarting => "Restarting…",
            UpdateState.Failed => "Update failed - try again",
            _ => $"Update to {u.Latest?.Tag}"
        };
    }

    // The sidebar's page entries, by page.
    private RadioButton? NavFor(string? page) => page switch
    {
        "Projects" => NavProjects, "Setup" => NavSetup, "Existing" => NavExisting, _ => null
    };

    // ─── The workspace, kept between starts ─────────────────────────────────

    // While the last start's workspace is being put back (the projects take a moment to be read), that is what is
    // saved - not the half-restored one.
    private WorkspaceState? _restoringWorkspace;
    private LogsState? _restoringLogs;
    // What was typed on New project and Host project - put back when their page is first made.
    private readonly Dictionary<string, Dictionary<string, string>> _drafts = new(StringComparer.OrdinalIgnoreCase);
    // Whether the window was maximized before it was minimized - what it comes back as.
    private bool _wasMaximized;

    /// <summary>The window where it was, as big as it was - centred as usual when that place is off the screens now.</summary>
    private void PlaceWindow(WindowLayout layout)
    {
        if (layout is { Left: { } left, Top: { } top, Width: { } width, Height: { } height } && OnScreen(left, top, width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = Math.Max(width, MinWidth);
            Height = Math.Max(height, MinHeight);
        }
        if (layout.Maximized) WindowState = WindowState.Maximized;
        _wasMaximized = layout.Maximized;
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _wasMaximized = WindowState == WindowState.Maximized; };
    }

    // The title bar on a screen as they are now - a monitor unplugged since would leave the window out of reach.
    internal static bool OnScreen(double left, double top, double width)
    {
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return width >= 200 && screens.Contains(new Point(left + 40, top + 10)) && screens.Contains(new Point(left + width - 40, top + 10));
    }

    /// <summary>What the workspace saves, and everything that changes it - saved a moment later (<see cref="WorkspaceService"/>).</summary>
    private void TrackWorkspace()
    {
        _workspace.Track(CaptureLayout);
        _workspace.Track(() => _restoringWorkspace ?? CaptureWorkspace());
        _workspace.Track(CaptureForms);
        _workspace.Track(() => _restoringLogs ?? TerminalPanel.CaptureLogs());
        // Terminals were kept by builds before 1.7.0's release - gone: a shell doesn't outlive DNN Manager.
        _workspace.Store.DeleteFile("terminals.json");

        // Anything typed, ticked or chosen anywhere in the window - pages, tabs, forms, the panel.
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, e) => { if (e.OriginalSource is TextBox) _workspace.Changed(); }), handledEventsToo: true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => _workspace.Changed()), true);
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => _workspace.Changed()), true);
        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, _) => _workspace.Changed()), true);
        SizeChanged += (_, _) => _workspace.Changed();
        LocationChanged += (_, _) => _workspace.Changed();
        StateChanged += (_, _) => _workspace.Changed();
        TerminalPanel.WorkspaceChanged += (_, _) => _workspace.Changed();
    }

    private WindowLayout CaptureLayout()
    {
        // Maximized or minimized: the place it goes back to.
        var bounds = WindowState == WindowState.Normal || RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var panel = _panelMaximized ? _restoredLogHeight : LogOpen ? LogRow.Height : _logHeight;
        return new WindowLayout
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized || WindowState == WindowState.Minimized && _wasMaximized,
            SidebarHidden = !_sidebarVisible, SidebarWidth = _sidebarWidth,
            PanelOpen = LogOpen, PanelHeight = panel.IsAbsolute ? panel.Value : null, PanelMaximized = _panelMaximized,
            TerminalListWidth = TerminalPanel.TerminalListWidth
        };
    }

    private WorkspaceState CaptureWorkspace()
    {
        var state = new WorkspaceState
        {
            // Settings or Troubleshoot when one is open over the page, else the page.
            Page = ModalKey ?? (_lastNav ?? NavProjects).Tag as string,
            SidebarPage = (_lastNav ?? NavProjects).Tag as string,
            SettingsCategory = (_modal as SettingsPage)?.Category
        };
        // The table and the open Details count even under Settings: closing it goes back to them.
        if (_pages.GetValueOrDefault("Projects") is ProjectsPage projects)
        {
            state.Projects = projects.CaptureTable();
            (state.Project, state.ProjectTab) = projects.CaptureDetails();
        }
        return state;
    }

    private FormsState CaptureForms()
    {
        var forms = new FormsState();
        foreach (var key in new[] { "Setup", "Existing" })
        {
            if (_pages.GetValueOrDefault(key) is { } page) forms.Drafts[key] = FormDraft.Capture(page);
            // Not opened since this start: kept as the last one left it.
            else if (_drafts.TryGetValue(key, out var pending)) forms.Drafts[key] = pending;
        }
        if (_modal is SettingsPage settings && settings.CaptureDraft() is { } draft) forms.Drafts["Settings"] = draft;
        return forms;
    }

    /// <summary>
    /// The workspace as the last start left it: the panel, the page, the Settings category and unsaved edits, the Projects
    /// table and the Details that were open, the log on the Logs tab. What is gone (a project, a log) falls back to the
    /// place above it. Then says so - and, after an update, whether it worked. Terminals aren't kept: their shells end
    /// with DNN Manager, so each start begins without any.
    /// </summary>
    private async void RestoreWorkspace(WindowLayout layout)
    {
        var state = _workspace.Load<WorkspaceState>();
        var logs = _workspace.Load<LogsState>();
        var update = TakeUpdate();
        foreach (var (key, draft) in _workspace.Load<FormsState>().Drafts) _drafts[key] = draft;
        _restoringWorkspace = state;
        _restoringLogs = logs;
        try
        {
            if (layout.TerminalListWidth is { } listWidth) TerminalPanel.TerminalListWidth = listWidth;
            if (layout.PanelHeight is { } height && height >= MinPanelHeight) _logHeight = new GridLength(height);
            SetLogOpen(layout.PanelOpen);
            if (layout.PanelOpen && layout.PanelMaximized) SetPanelMaximized(true);
            TerminalPanel.RestoreLogs(logs);

            // The Projects page first - it holds the table and the Details; then the sidebar page, then Settings or
            // Troubleshoot over it.
            NavProjects.IsChecked = true;
            (NavFor(state.SidebarPage) ?? NavProjects).IsChecked = true;
            NavFor(state.Page)?.IsChecked = true;
            if (state.Page is "Settings" or "Troubleshoot") ShowModal(state.Page);
            var settingsBack = false;
            if (_modal is SettingsPage settings)
            {
                if (state.SettingsCategory is { } category) settings.ShowCategory(category);
                if (_drafts.TryGetValue("Settings", out var draft))
                {
                    settings.RestoreDraft(draft);
                    settingsBack = settings.HasUnsavedChanges;
                }
            }
            _drafts.Remove("Settings");

            var missing = _pages.GetValueOrDefault("Projects") is ProjectsPage projects
                ? await projects.RestoreAsync(state.Projects, state.Project, state.ProjectTab) : null;
            // The Logs tab's log, now that the sites are read.
            if (logs.LogSite is { } site && _store.Projects.FirstOrDefault(r => r.Name.Equals(site, StringComparison.OrdinalIgnoreCase)) is { } row)
                TerminalPanel.ShowLog(row, logs.LogGroup, logs.LogTitle);
            Report(update, missing, settingsBack);
        }
        catch (Exception ex)
        {
            // Whatever the saved workspace holds, DNN Manager starts: on the Projects page, as on a first start.
            App.Log?.LogWarning(ex, "Could not restore the workspace - starting on the Projects page");
            NavProjects.IsChecked = true;
        }
        finally
        {
            _restoringWorkspace = null;
            _restoringLogs = null;
            _workspace.Changed();
        }
    }

    /// <summary>The update that started this process, if one did - read once, then gone.</summary>
    private UpdateRecord? TakeUpdate()
    {
        var record = _workspace.Load<UpdateRecord>();
        if (record.ToVersion is null) return null;
        _workspace.Store.Delete<UpdateRecord>();
        var age = DateTime.UtcNow - record.SavedUtc;
        return age >= TimeSpan.Zero && age <= UpdateRecord.MaxAge ? record : null;
    }

    /// <summary>
    /// What the user should know after the restore: the update's outcome - this is the new version (by its own version
    /// number), or why it isn't - what couldn't be put back, unsaved settings that are back.
    /// </summary>
    private void Report(UpdateRecord? update, string? missing, bool settingsBack)
    {
        var settingsNote = settingsBack ? " Your unsaved settings are back - type any password again, they aren't kept." : "";
        if (update is null)
        {
            if (missing is not null) Toast.Show(missing + settingsNote, ToastKind.Warning);
            else if (settingsBack) Toast.Show(settingsNote.Trim());
            return;
        }

        var result = UpdateResult.TryRead(update.ResultFile);
        var expected = update.ToVersion is { } to && AppReleaseFeed.TryParseVersion(to, out var v) ? v : null;
        var current = _updater.Current;
        var isNew = expected is not null && !AppReleaseFeed.IsNewer(expected, current) && !AppReleaseFeed.IsNewer(current, expected);
        if (isNew && result?.Installed != false)
        {
            _log.Success($"DNN Manager was updated from {update.FromVersion} to {current}.");
            Toast.Show($"Updated to v{current}" + (missing is null ? " - you're back where you left off." : $". {missing}") + settingsNote,
                missing is null ? ToastKind.Success : ToastKind.Warning);
            return;
        }
        var why = result is { Installed: false } ? result.Message : $"this is still v{current}.";
        Action? showLog = result?.LogFile is { } log && File.Exists(log) ? () => Shell.Open(log) : null;
        _log.Warn($"The update to {update.ToVersion} wasn't installed: {why}");
        Toast.Show($"The update to v{update.ToVersion} wasn't installed: {why}", ToastKind.Error, showLog is null ? null : "Show log", showLog);
    }

    // ─── Sidebar ────────────────────────────────────────────────────────────

    // Narrower than this, the sidebar shows only its icons, to leave the room to the page.
    private const double CompactBelow = 1100;

    // Measured in the page's own units: at 150 % UI scale a 1500 px window lays out like a 1000 px one.
    private void UpdateCompact() => IsCompact = ActualWidth / ThemeManager.Scale < CompactBelow;
    // The sidebar's widths - with names, and icons only; narrower in Customize Layout's Compact density.
    private const double DefaultSidebarWidth = 230;
    private double ExpandedSidebarWidth => _sidebarWidth ?? (_layout.Compact ? 190 : DefaultSidebarWidth);
    private double CompactSidebarWidth => _layout.Compact ? 40 : 48;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool),
        typeof(MainWindow), new PropertyMetadata(false, (d, e) =>
        {
            var window = (MainWindow)d;
            window.SlideSidebar((bool)e.NewValue);
            window.FitSidebarEdges();
        }));

    // The sidebar's width - a number, so it can be animated (a grid column's width can't); the column follows it.
    private static readonly DependencyProperty SidebarWidthProperty = DependencyProperty.Register("SidebarWidth", typeof(double),
        typeof(MainWindow), new PropertyMetadata(DefaultSidebarWidth, (d, _) => ((MainWindow)d).ApplySidebarWidth()));

    /// <summary>Slides the sidebar to its narrow or wide width - at once while the window is still being set up.</summary>
    private void SlideSidebar(bool compact)
    {
        var width = compact ? CompactSidebarWidth : ExpandedSidebarWidth;
        var slide = new DoubleAnimation(width, TimeSpan.FromMilliseconds(IsLoaded ? 180 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(SidebarWidthProperty, slide);
    }

    /// <summary>
    /// The window is narrow: the sidebar shows only the page icons (names as tooltips) and the IIS dot with its
    /// buttons - no title, IIS text or projects folder. Its parts follow this with triggers (SidebarLabel, SidebarNav).
    /// </summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    // ─── Title bar ──────────────────────────────────────────────────────────

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    // Without Windows' frame, a maximized window still reaches past the screen edges by the frame it would have - keep the
    // content (and the window buttons) on screen.
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? MaximizedOverhang() : new Thickness(0);
        MaximizeButton.SetResourceReference(ContentProperty, maximized ? "GlyphRestore" : "GlyphMaximize");
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    // Another monitor's scaling makes the frame - and so the overhang - another size.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (WindowState == WindowState.Maximized) Root.Margin = MaximizedOverhang();
    }

    /// <summary>
    /// How far the maximized window reaches past its monitor's work area on each side - measured, as it is the resize
    /// border plus a padded border, whose size depends on the scaling (<see cref="SystemParameters.WindowResizeBorderThickness"/>
    /// alone falls short of it).
    /// </summary>
    private Thickness MaximizedOverhang()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target ||
            !GetWindowRect(hwnd, out var window) || !GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
            return SystemParameters.WindowResizeBorderThickness;
        // Device pixels to the window's units.
        var scale = target.TransformFromDevice;
        var work = info.Work;
        return new Thickness(
            Math.Max(0, work.Left - window.Left) * scale.M11, Math.Max(0, work.Top - window.Top) * scale.M22,
            Math.Max(0, window.Right - work.Right) * scale.M11, Math.Max(0, window.Bottom - work.Bottom) * scale.M22);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const int WmNcHitTest = 0x0084, WmNcMouseLeave = 0x02A2, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2;
    private const int HtMaxButton = 9;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    /// <summary>
    /// Tells Windows the maximize button is one (HTMAXBUTTON), so Windows 11 shows its Snap layouts when the pointer
    /// rests on it. Windows then sends the clicks there as non-client messages, so the click and the hover are handled here.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                var over = IsOverMaximize(lParam);
                MaximizeButton.Tag = over ? "Hover" : null;
                if (over)
                {
                    handled = true;
                    return HtMaxButton;
                }
                break;
            case WmNcMouseLeave:
                MaximizeButton.Tag = null;
                break;
            case WmNcLButtonDown when wParam == HtMaxButton:
                handled = true;
                break;
            case WmNcLButtonUp when wParam == HtMaxButton:
                handled = true;
                ToggleMaximize();
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaximize(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible) return false;
        // Screen coordinates in device pixels, signed (a second monitor can be left of or above the first).
        var point = new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var inside = MaximizeButton.PointFromScreen(point);
        return inside.X >= 0 && inside.Y >= 0 && inside.X < MaximizeButton.ActualWidth && inside.Y < MaximizeButton.ActualHeight;
    }

    // Height the panel had when it was hidden (it may have been resized with the splitter), restored on show.
    private GridLength _logHeight = new(260);

    private bool LogOpen => PanelCard.Visibility == Visibility.Visible;


    // The least room the page keeps beside an open panel - small, so the window shrinks as far as VS Code's.
    private const double MinPageHeight = 60;

    /// <summary>
    /// The panel no taller than the body (between the title bar and the status bar) has room for beside the page's least
    /// room - it gets lower with the window. A limit, not its height: a taller window gives it back the height it had.
    /// Maximized it has all the room anyway.
    /// </summary>
    private void FitPanel()
    {
        // The body's row, not the body: content too tall for it makes the body itself as tall (and clipped).
        var body = Root.RowDefinitions[1].ActualHeight;
        var room = body - SplitterRow.ActualHeight - MinPageHeight;
        LogRow.MaxHeight = _panelMaximized || body <= 0 ? double.PositiveInfinity : Math.Max(LogRow.MinHeight, room);
    }

    // Once the change is laid out: the rows' heights are known then.
    private void FitPanelSoon() => Dispatcher.BeginInvoke(FitPanel, System.Windows.Threading.DispatcherPriority.Loaded);

    // The panel has the window: the page is hidden and the panel takes its room (the sidebar and status bar stay).
    private bool _panelMaximized;
    private GridLength _restoredLogHeight;

    private void SetPanelMaximized(bool maximized)
    {
        if (maximized == _panelMaximized) return;
        _panelMaximized = maximized;
        FitPanel();
        FitPanelSoon();
        TerminalPanel.IsMaximized = maximized;
        if (maximized)
        {
            _restoredLogHeight = LogRow.Height;
            PageRow.MinHeight = 0;
            PageRow.Height = new GridLength(0);
            PageCard.Visibility = Visibility.Collapsed;
            LogRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            PageRow.Height = new GridLength(1, GridUnitType.Star);
            PageRow.MinHeight = MinPageHeight;
            PageCard.Visibility = Visibility.Visible;
            if (LogOpen) LogRow.Height = _restoredLogHeight;
        }
        FitSashes();
    }

    /// <summary>
    /// Hides or shows the terminal panel (the activity log and the shells). Hidden, it takes no room at all - the
    /// shells keep running, and the running operation's toast still shows its progress and Cancel. Shown, it
    /// takes the keyboard when on the Terminal tab - unless <paramref name="takeKeyboard"/> is false (Customize Layout,
    /// which keeps it).
    /// </summary>
    private void SetLogOpen(bool open, bool takeKeyboard = true)
    {
        // Hiding the panel gives the page its room back.
        if (!open) SetPanelMaximized(false);
        if (open && _panelClosing)
        {
            _panelSlide.Stop();
            _panelClosing = false;
            LogRow.MinHeight = MinPanelHeight;
            LogRow.Height = _logHeight;
        }
        if (open == LogOpen) return;
        if (open)
        {
            PanelCard.Visibility = Visibility.Visible;
            LogRow.MinHeight = MinPanelHeight;
            LogRow.Height = _logHeight;
            FitPanelSoon();
            if (takeKeyboard) TerminalPanel.Opened();
        }
        else
        {
            _logHeight = LogRow.Height;
            // Its search goes with it - opened again, the panel shows without it.
            TerminalPanel.CloseSearch(focus: false);
            PanelCard.Visibility = Visibility.Collapsed;
            LogRow.MinHeight = 0;
            LogRow.Height = new GridLength(0);
        }
        FitSashes();
        UpdateLayoutButtons();
    }


    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Unsaved settings are kept for the next start - all but a password, so only that is asked about.
        if (_modal is SettingsPage { HasUnsavedPassword: true } &&
            !Dialogs.Confirm("You changed a password in the settings and didn't save it. The other unsaved settings are kept " +
                             "for the next start, but passwords aren't. Quit anyway?", "Quit", "Stay"))
        {
            e.Cancel = true;
            return;
        }
        if (_runner.IsBusy)
        {
            if (!Dialogs.Confirm($"'{_runner.Current}' is still running. Quit anyway?", "Quit anyway", "Keep running"))
            {
                e.Cancel = true;
                return;
            }
            _runner.Cancel();
        }
        // The workspace as it is now - before the panel goes with the window. Nothing is saved after this.
        _workspace.SaveNow(last: true);
    }
}

/// <summary>A page that reloads its data after an operation finishes.</summary>
public interface IRefreshable
{
    void Refresh();
}
