using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DnnManager.Application;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Infrastructure.Processes;
using DnnManager.Infrastructure.Settings;
using DnnManager.Infrastructure.Startup;
using DnnManager.Infrastructure.Updates;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shell = DnnManager.Desktop.Services.Shell;

namespace DnnManager.Desktop.Views;

/// <summary>A page that reloads what it shows after an operation has finished (WPF's IRefreshable).</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>A page with a view for the primary side bar (VS Code's: Source Control's changes) - Ctrl+B shows or hides it.</summary>
public interface ISideBarPage
{
    Control SideBar { get; }
}

/// <summary>
/// The window (src/DnnManager.Presentation/MainWindow.xaml.cs and its partial files): the activity bar's pages, Settings and
/// Troubleshoot over them, the side bars, the bottom panel, toasts, the status bar, the title bar with the command centre,
/// Update, Help and the layout buttons - and the workspace kept between starts. Its partial files: MainWindow.Commands
/// (shortcuts and the command palette), MainWindow.Layout (Customize Layout, the side bars and the panel), MainWindow.Help
/// (the guide, page help, the tour).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Whether the icon font (Segoe Fluent Icons - Icons.axaml's Glyph…) is there: on Windows, where the icons are the
    /// Windows app's; elsewhere drawn icons stand in (the font is Windows' own and can't be shipped).
    /// </summary>
    public static bool HasIconFont { get; } = OperatingSystem.IsWindows();
    public static bool NoIconFont { get; } = !OperatingSystem.IsWindows();

    private readonly IServiceProvider _services = Program.Services;
    private readonly ActivityLog _log;
    private readonly OperationRunner _runner;
    private readonly AppUpdater _updater;
    private readonly WorkspaceService _workspace;
    private readonly AppCommands _commands;
    private readonly SettingsStore _settings;
    private readonly AppOptions _options;
    private readonly ResourceSampler _sampler;

    // One per page, made on its first visit and kept - Settings is made anew each time, as in the Windows app.
    private readonly Dictionary<string, Control> _views = [];
    // Kept pages an operation may have changed since they were last shown - refreshed on their next visit.
    private readonly HashSet<Control> _stale = [];
    // The activity bar's page shown (under Settings or Troubleshoot when one is open).
    private string _page = DefaultPage;

    /// <summary>
    /// The page a first start opens on: Projects on Windows, as the Windows app; Source Control on macOS and Linux, where
    /// Projects needs Windows (IIS).
    /// </summary>
    public static string DefaultPage { get; } = OperatingSystem.IsWindows() ? "Projects" : "SourceControl";

    /// <summary>The activity bar's pages, in their order - Ctrl+1, Ctrl+2, Ctrl+3 and Ctrl+Tab go round them.</summary>
    private static readonly string[] SidebarPages = ["Projects", "Setup", "SourceControl"];

    /// <summary>Settings and Troubleshoot: they open over the page, as VS Code's modal editors.</summary>
    private static bool IsModalPage(string name) => name is "Settings" or "Troubleshoot";

    public MainWindow()
    {
        InitializeComponent();
        _log = _services.GetRequiredService<ActivityLog>();
        _runner = _services.GetRequiredService<OperationRunner>();
        _updater = _services.GetRequiredService<AppUpdater>();
        _workspace = _services.GetRequiredService<WorkspaceService>();
        _commands = _services.GetRequiredService<AppCommands>();
        _settings = _services.GetRequiredService<SettingsStore>();
        _options = _services.GetRequiredService<IOptions<AppOptions>>().Value;
        var paths = _services.GetRequiredService<AppDataPaths>();
        _layout = _options.Layout.Copy();

        Toast.Attach(ToastHost);
        OperationToast.Attach(_runner);
        OperationToast.OperationClicked += (_, _) => ShowOutput();
        OutputPanel.Attach(_log, paths.LogsDirectory);
        OutputPanel.CloseRequested += (_, _) => ClosePanel();
        OutputPanel.MaximizeToggled += (_, _) => SetPanelMaximized(!_panelMaximized);
        VersionText.Text = $"v{_updater.Current}";
        ToolTip.SetTip(VersionText, $"DNN Manager {_updater.Informational} on {Platform.Name}");

        // The status bar's figures: this PC's - every two seconds while the window can be seen.
        _sampler = new ResourceSampler(() => _options.BaseDirectory);
        _sampler.Sampled += sample => Dispatcher.UIThread.Post(() => ShowResources(sample));
        ToolTip.SetTip(CpuText, $"Processor use of this PC ({Environment.ProcessorCount} logical processors)");
        CpuPart.IsVisible = !OperatingSystem.IsMacOS();
        Opened += (_, _) => _sampler.Start();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            if (WindowState == WindowState.Minimized) _sampler.Pause();
            else _sampler.Start();
            UpdateMaximized();
        };

        PlaceTitleBar();
        // IIS under the activity bar (the Projects page's status cell) - it hides itself where there is no IIS.
        StatusLeft.Content = new Projects.IisStatus();
        // The UI scale and the font size chosen in Settings, from the start.
        Opened += (_, _) => AppearanceLayout.Apply(this, Program.Settings.Appearance);
        _runner.PropertyChanged += OnRunnerChanged;
        _runner.Refused += message => Toast.Show(message, ToastKind.Warning);
        // A failed operation is said where it is seen - its steps are in Output, which may be closed.
        _runner.Failed += (title, error) => Toast.Show($"{title} failed: {error}", ToastKind.Error, "Show output", ShowOutput);
        _updater.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(ShowUpdate);

        // Saved elsewhere (Settings, a reset): the layout as saved, the theme, the tray icon - at once.
        _options.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _layout = _options.Layout.Copy();
            ApplyLayout();
            App.ApplyTheme(_options.Theme);
            foreach (var (key, kept) in _views.Where(v => v.Key != "Projects").ToList())
            {
                if (ReferenceEquals(ViewHost.Content, kept) || ReferenceEquals(ModalContent.Content, kept)) continue;
                _views.Remove(key);
                _stale.Remove(kept);
            }
            FollowKeepRunning();
        });

        foreach (var notice in Program.StartupNotices)
        {
            if (notice.IsWarning) _log.Warn(notice.Message);
            else _log.Info(notice.Message);
        }
        ToastWarnings(Program.StartupNotices.Where(n => n.IsWarning).ToList());

        // Every command - by shortcut, in the command palette; the tooltips name the shortcuts.
        RegisterCommands();
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Window_KeyDownLate, RoutingStrategies.Bubble);
        Tour.Ended += Tour_Ended;

        // The workspace as the last start left it, and kept as it changes. What the last DNN Manager left unfinished is
        // taken now, before anything here can start an operation of its own.
        _unfinished = _runner.TakeUnfinished();
        var layout = _workspace.Load<WindowLayout>();
        PlaceWindow(layout);
        RestoreSideBars(layout);
        _sidebarVisible = !layout.SidebarHidden;
        Show(DefaultPage);
        ApplyLayout();
        TrackWorkspace();
        Opened += (_, _) =>
        {
            RestoreWorkspace(layout);
            if (Program.IsRealStart) _updater.Start();
            SizeCommandCenter();
        };
        TitleBar.SizeChanged += (_, _) => SizeCommandCenter();
        Body.SizeChanged += (_, _) => PlaceToasts();
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _sampler.Dispose();
            _tray?.Dispose();
        };
        FollowKeepRunning();
        ShowUpdate();
    }

    // ─── Pages ──────────────────────────────────────────────────────────────

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string view }) Go(view);
        else UpdateNav();
    }

    /// <summary>
    /// The page <paramref name="name"/> made the first time - THE place where the pages are hosted. Projects, New project and
    /// Troubleshoot say they are coming until their views are dropped in here: <c>new ProjectsView()</c>,
    /// <c>new SetupView()</c>, <c>new TroubleshootView()</c>.
    /// </summary>
    private static Control CreatePage(string name) => name switch
    {
        "SourceControl" => new SourceControlView(),
        "Settings" => new SettingsView(),
        // Projects and New project say themselves, on macOS and Linux, that they need Windows (IIS).
        "Projects" => new Projects.ProjectsView(),
        "Setup" => new Setup.SetupView(),
        "Troubleshoot" => new TroubleshootView(),
        _ => NotHere(name, PlatformFeature.Iis)
    };

    /// <summary>
    /// Shows <paramref name="name"/>: an activity bar page in the page's place, Settings or Troubleshoot over it. "Deploy"
    /// (the Windows app's name for Source Control, in a workspace it saved) is Source Control.
    /// </summary>
    public void Show(string name)
    {
        if (name == "Deploy") name = "SourceControl";
        if (IsModalPage(name))
        {
            ShowModal(name);
            return;
        }
        var view = PageFor(name);
        _page = name;
        if (_stale.Remove(view) && view is IRefreshable refreshable) refreshable.Refresh();
        ViewHost.Content = view;
        UpdateNav();
        ShowPageSideBar();
        if (view is SourceControlView scm) scm.Activated();
        else if (view is Projects.ProjectsView projects) projects.Activated();
        else if (view is Setup.SetupView setup) setup.Activated();
        _workspace.Changed();
    }

    /// <summary>Shows a page with the keyboard in it - Settings or Troubleshoot closed first.</summary>
    private async void Go(string name)
    {
        if (!await CloseModalAsync(focusPage: false))
        {
            UpdateNav();
            return;
        }
        Show(name);
        FocusPage();
    }

    private Control PageFor(string name)
    {
        if (name != "Settings" && _views.TryGetValue(name, out var kept)) return kept;
        var view = CreatePage(name);
        if (name != "Settings") _views[name] = view;
        WireModal(view);
        return view;
    }

    private void UpdateNav()
    {
        foreach (var button in new[] { NavProjects, NavSetup, NavSourceControl })
            button.IsChecked = (string?)button.Tag == _page;
    }

    /// <summary>A page of what isn't here: why (Windows only), or that it comes back in an update.</summary>
    private static Control NotHere(string title, PlatformFeature feature)
    {
        var why = Platform.WhyNot(feature) ??
                  "DNN Manager 2.0 has a new look, the same on Windows, macOS and Linux. This page is being moved to it and comes " +
                  "back in an update. Source Control and Settings work already; your sites in IIS are untouched.";
        return new Border
        {
            Padding = new Thickness(40),
            Child = new StackPanel
            {
                MaxWidth = 560, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = Platform.IsWindows ? "Coming back in an update" : $"Not available on {Platform.Name}", FontSize = 15 },
                    new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                }
            }
        };
    }

    /// <summary>The keyboard on what is shown: Settings or Troubleshoot when open, else the page.</summary>
    private void FocusPage() => Dispatcher.UIThread.Post(() =>
    {
        var target = ModalContent.Content as Control ?? ViewHost.Content as Control;
        if (target is null) return;
        var first = target.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(e => e.Focusable && e.IsEffectivelyVisible && e.IsEffectivelyEnabled && e is not ScrollViewer);
        if (first is not null) first.Focus();
        else target.Focus();
    }, DispatcherPriority.Loaded);

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        Dispatcher.UIThread.Post(() =>
        {
            ShowUpdate();
            if (_runner.IsBusy)
            {
                // Something is happening: with the panel open, Output shows it.
                if (PanelOpen) OutputPanel.ShowOutput();
                return;
            }
            // Ended: the page shown refreshes what it changed; the other kept pages when they are next shown.
            foreach (var kept in _views.Values.Where(v => !ReferenceEquals(v, ViewHost.Content) && !ReferenceEquals(v, _modal))) _stale.Add(kept);
            if (ViewHost.Content is IRefreshable page) page.Refresh();
            if (_modal is IRefreshable over) over.Refresh();
        });
    }

    // ─── Settings and Troubleshoot, over the page ───────────────────────────

    // Settings or Troubleshoot while one is open over the page.
    private Control? _modal;
    private string? _modalKey;

    private void ShowModal(string key)
    {
        if (_modalKey == key) return;
        _ = ShowModalAsync(key);
    }

    private async Task ShowModalAsync(string key)
    {
        if (!await CloseModalAsync(focusPage: false)) return;
        var page = PageFor(key);
        if (_stale.Remove(page) && page is IRefreshable refreshable) refreshable.Refresh();
        if (page is SettingsView settings)
        {
            settings.OfferGuide = true;
            settings.OfferTour = true;
            settings.OfferPageHelp = true;
            settings.OfferWhatsNew = !IsNewUser;
        }
        _modal = page;
        _modalKey = key;
        ModalContent.Content = page;
        ModalHost.IsVisible = true;
        PlaceToasts();
        FocusPage();
        _workspace.Changed();
    }

    /// <summary>The page's own ✕ (and Esc, Help's buttons) - Settings' and Troubleshoot's events, whatever page they are on.</summary>
    private void WireModal(Control page)
    {
        if (page is SettingsView settings)
        {
            settings.CloseRequested += (_, _) => _ = CloseModalAsync();
            settings.HelpRequested += (_, what) => RunHelp(what);
            return;
        }
        // Troubleshoot (and any page opened over another) says it wants to close by an event of that name.
        if (page.GetType().GetEvent("CloseRequested") is { EventHandlerType: { } type } close && type == typeof(EventHandler))
            close.AddEventHandler(page, new EventHandler((_, _) => _ = CloseModalAsync()));
    }

    /// <summary>
    /// Closes Settings or Troubleshoot - asking first when the settings have unsaved changes. False when the user chose
    /// to stay.
    /// </summary>
    private async Task<bool> CloseModalAsync(bool focusPage = true)
    {
        if (_modal is null) return true;
        if (_modal is SettingsView { HasUnsavedChanges: true } &&
            !await Dialogs.ConfirmAsync("The settings have unsaved changes. Close them and lose the changes?", "Discard changes", "Stay"))
            return false;
        _modal = null;
        _modalKey = null;
        ModalContent.Content = null;
        ModalHost.IsVisible = false;
        if (focusPage) FocusPage();
        _workspace.Changed();
        return true;
    }

    private void ModalBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => _ = CloseModalAsync();

    private void ModalBox_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // ─── The title bar ──────────────────────────────────────────────────────

    /// <summary>
    /// The title bar as the Windows app's: on Windows the window keeps only its border (and shadow) and the title bar draws
    /// its own minimize, maximize and close; on macOS the system's three on the left; on Linux, where Avalonia draws the
    /// decorations itself, its buttons on the right (CaptionSpace leaves room). The title bar is the window's drag area
    /// (double-click maximizes, Windows' Snap works) - its buttons take their clicks.
    /// </summary>
    private void PlaceTitleBar()
    {
        var mac = OperatingSystem.IsMacOS();
        if (OperatingSystem.IsWindows()) WindowDecorations = WindowDecorations.BorderOnly;
        CaptionButtons.IsVisible = OperatingSystem.IsWindows();
        TitleLeft.Margin = new Thickness(mac ? 78 : 12, 0, 0, 0);
        var linuxDrawn = OperatingSystem.IsLinux() && IsExtendedIntoWindowDecorations;
        CaptionSpace.Width = linuxDrawn ? 3 * 46 : mac ? 4 : 0;
        CaptionDivider.IsVisible = !mac;
        WindowDecorationProperties.SetElementRole(TitleBarFrame, WindowDecorationsElementRole.TitleBar);
        foreach (var part in new Control[] { CommandCenter, TitleRight })
            WindowDecorationProperties.SetElementRole(part, WindowDecorationsElementRole.User);
    }

    // Without the system's title bar (an Avalonia that doesn't take the role): dragged and double-clicked here.
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not (Border or Grid or DockPanel or StackPanel or TextBlock or Image) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximized()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Maximized without the system's frame, Windows lets the window reach past the screen's edge by its frame.
        Root.Margin = maximized && OperatingSystem.IsWindows() ? OffScreenMargin : default;
        MaximizeButton.Content = this.FindResource(maximized ? "GlyphRestore" : "GlyphMaximize");
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    // ─── Updating DNN Manager ───────────────────────────────────────────────

    private void Update_Click(object? sender, RoutedEventArgs e) => _ = _updater.UpdateAsync();

    /// <summary>The Update button: there while a newer release can be installed; its tooltip says how the update is going.</summary>
    private void ShowUpdate()
    {
        var u = _updater;
        UpdateButton.IsVisible = u.CanInstall || u.IsUpdating;
        UpdateButton.IsEnabled = u.CanInstall && !_runner.IsBusy;
        var downloading = u.State == UpdateState.Downloading;
        UpdateProgress.IsVisible = downloading;
        UpdateProgress.Value = downloading ? u.DownloadProgress : 0;
        ToolTip.SetTip(UpdateButton, u.State switch
        {
            UpdateState.Downloading => $"Downloading {u.Latest?.Tag}… {u.DownloadProgress:P0}",
            UpdateState.Installing => $"Installing {u.Latest?.Tag}…",
            UpdateState.Restarting => "Restarting…",
            UpdateState.Failed => "Update failed - try again",
            _ => $"Install {u.Latest?.Tag} - DNN Manager closes and opens again by itself, where you were"
        });
        SizeCommandCenter();
        // macOS and Linux: the program is the one file downloaded - a newer one is said once, with where to get it.
        if (!OperatingSystem.IsWindows() && !_toldNewer && u.State == UpdateState.Available && u.Latest is { } latest)
        {
            _toldNewer = true;
            _log.Info($"DNN Manager {latest.Tag.TrimStart('v')} is out (you have {u.Informational}) - download it from {latest.PageUrl}");
            Toast.Show($"DNN Manager {latest.Tag.TrimStart('v')} is out.", ToastKind.Info, "Download", () => Shell.Open(latest.PageUrl));
        }
    }

    private bool _toldNewer;

    private async Task CheckForUpdatesAsync()
    {
        await _updater.CheckAsync();
        Toast.Show(_updater.StatusText, _updater.State == UpdateState.Unreachable ? ToastKind.Warning : ToastKind.Info,
            _updater.CanInstall ? "Update" : _updater.State == UpdateState.Available && _updater.Latest is { } latest ? "Download" : null,
            _updater.CanInstall ? () => _ = _updater.UpdateAsync()
            : _updater.State == UpdateState.Available && _updater.Latest is { } release ? () => Shell.Open(release.PageUrl) : null);
    }

    // ─── The status bar ─────────────────────────────────────────────────────

    /// <summary>
    /// This PC's memory, CPU and disk in the status bar. A sample without the CPU use (the first one) is skipped on Windows
    /// and Linux, so the three figures appear and change together.
    /// </summary>
    public void ShowResources(HostResources r)
    {
        if (r.CpuPercent is null && !OperatingSystem.IsMacOS()) return;
        RamText.Text = r.MemoryTotalBytes == 0 ? "RAM 0.00 GB" : $"RAM {ByteSize.Format((long)r.MemoryUsedBytes)}";
        RamWidest.Text = r.MemoryTotalBytes == 0 ? "" : $"RAM {ByteSize.Format((long)r.MemoryTotalBytes)}";
        ToolTip.SetTip(RamText, r.MemoryTotalBytes == 0 ? null
            : $"Memory in use: {ByteSize.Format((long)r.MemoryUsedBytes)} of {ByteSize.Format((long)r.MemoryTotalBytes)} ({100d * r.MemoryUsedBytes / r.MemoryTotalBytes:0}%)");
        if (r.CpuPercent is { } cpu) CpuText.Text = $"CPU {cpu:0.00}%";
        DiskText.Text = r.DiskRoot is null ? "Disk: --.-- GB used (limit --.-- GB)"
            : $"Disk: {ByteSize.Format(r.DiskUsedBytes)} used (limit {ByteSize.Format(r.DiskTotalBytes)})";
        DiskWidest.Text = r.DiskRoot is null ? "" : $"Disk: {ByteSize.Format(r.DiskTotalBytes)} used (limit {ByteSize.Format(r.DiskTotalBytes)})";
        ToolTip.SetTip(DiskText, r.DiskRoot is null ? null
            : $"{r.DiskRoot} (the projects folder's drive): {ByteSize.Format(r.DiskUsedBytes)} of {ByteSize.Format(r.DiskTotalBytes)} used, " +
              $"{ByteSize.Format(r.DiskTotalBytes - r.DiskUsedBytes)} free");
    }

    /// <summary>What a view has to say for a moment - a toast, as the Windows app says it.</summary>
    public void Say(string text) => Toast.Show(text);

    /// <summary>The start's warnings where they are seen: one in full, several counted - with a button to Output.</summary>
    private void ToastWarnings(IReadOnlyList<SettingsNotice> warnings)
    {
        if (warnings.Count == 0) return;
        var message = warnings.Count == 1
            ? warnings[0].Message
            : $"DNN Manager started with {warnings.Count} warnings that need your attention - Output says what they are and what to do.";
        Toast.Show(message, ToastKind.Warning, "Show output", ShowOutput);
    }

    // ─── The workspace, kept between starts ─────────────────────────────────

    private UnfinishedOperation? _unfinished;
    // While the last start's workspace is put back, that is what is saved - not the half-restored one.
    private WorkspaceState? _restoring;

    /// <summary>The window where it was, as big as it was - centred as usual when that place is off the screens now.</summary>
    private void PlaceWindow(WindowLayout layout)
    {
        if (layout is { Left: { } left, Top: { } top, Width: { } width, Height: { } height } && OnScreen(left, top, width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var scale = Screens.Primary?.Scaling ?? 1;
            Position = new PixelPoint((int)(left * scale), (int)(top * scale));
            Width = Math.Max(width, MinWidth);
            Height = Math.Max(height, MinHeight);
        }
        else if (Screens.Primary is { } screen)
        {
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling);
        }
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    // The title bar on a screen as they are now - a monitor unplugged since would leave the window out of reach.
    private bool OnScreen(double left, double top, double width)
    {
        if (width < 200) return false;
        var scale = Screens.Primary?.Scaling ?? 1;
        var a = new PixelPoint((int)((left + 40) * scale), (int)((top + 10) * scale));
        var b = new PixelPoint((int)((left + width - 40) * scale), (int)((top + 10) * scale));
        return Screens.All.Any(s => s.Bounds.Contains(a)) && Screens.All.Any(s => s.Bounds.Contains(b));
    }

    private void TrackWorkspace()
    {
        _workspace.Track(CaptureLayout);
        _workspace.Track(() => _restoring ?? CaptureWorkspace());
        _workspace.Track(() => new LogsState { Pane = OutputPanel.Pane });
        PositionChanged += (_, _) => _workspace.Changed();
        SizeChanged += (_, _) => _workspace.Changed();
    }

    // The window's place and size when it isn't maximized (kept while it is).
    private PixelPoint _normalPosition;
    private Size _normalSize;

    private WindowLayout CaptureLayout()
    {
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
            _normalSize = new Size(Width, Height);
        }
        var scale = DesktopScaling;
        var panel = _panelMaximized ? _restoredPanelHeight : PanelOpen ? Body.RowDefinitions[2].ActualHeight : _panelHeight;
        return new WindowLayout
        {
            Left = _normalPosition.X / scale, Top = _normalPosition.Y / scale,
            Width = _normalSize.Width > 0 ? _normalSize.Width : Width, Height = _normalSize.Height > 0 ? _normalSize.Height : Height,
            Maximized = WindowState == WindowState.Maximized,
            SidebarHidden = !_sidebarVisible,
            PrimarySideBarHidden = !_primaryVisible, PrimarySideBarWidth = _primaryWidth,
            SecondarySideBarOpen = _secondaryVisible, SecondarySideBarWidth = _secondaryWidth,
            PanelOpen = PanelOpen, PanelHeight = panel > 0 ? panel : null, PanelMaximized = _panelMaximized
        };
    }

    private WorkspaceState CaptureWorkspace() => new()
    {
        Page = _modalKey ?? _page,
        SidebarPage = _page,
        SettingsCategory = (_modal as SettingsView)?.Category
    };

    /// <summary>
    /// The workspace as the last start left it: the panel, the page, Settings or Troubleshoot over it. Then says what the
    /// user should know - after an update, whether it worked - and shows What's new and the getting started guide.
    /// </summary>
    private async void RestoreWorkspace(WindowLayout layout)
    {
        var state = _workspace.Load<WorkspaceState>();
        var lastRun = _workspace.Load<VersionState>().LastRun;
        // DNN Manager has never run here: no version and no window place saved.
        var firstStart = lastRun is null && layout.Width is null;
        var current = _updater.Current.ToString();
        var update = TakeUpdate();
        NoteFirstVersion(firstStart);
        var whatsNewSince = IsNewUser ? null : WhatsNewSince(update, lastRun);
        if (lastRun != current) _workspace.Store.Save(new VersionState { LastRun = current });
        CheckInstallLocation(newVersion: lastRun != current);
        _restoring = state;
        try
        {
            if (layout.PanelHeight is { } height && height >= MinPanelHeight) _panelHeight = height;
            if (_workspace.Load<LogsState>().Pane == "Logs") OutputPanel.ShowLogs();
            SetPanelOpen(layout.PanelOpen);
            if (layout.PanelOpen && layout.PanelMaximized) SetPanelMaximized(true);

            var page = state.SidebarPage is "Deploy" ? "SourceControl" : state.SidebarPage;
            if (page is not null && SidebarPages.Contains(page)) Show(page);
            if (state.Page is "Settings" or "Troubleshoot")
            {
                await ShowModalAsync(state.Page);
                if (_modal is SettingsView settings && state.SettingsCategory is { } category) settings.ShowCategory(category);
            }
            Report(update);
            ReportUnfinished();
            if (!Program.IsRealStart) return;
            // What changed, then the getting started guide - once the window is up, one after the other.
            await Task.Delay(300);
            if (whatsNewSince is not null) await Workbench.WhatsNewDialog.ShowAsync(this, ReleaseNotes.Between(whatsNewSince, _updater.Current));
            await ShowGuideAtStartAsync(firstStart);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever the saved workspace holds, DNN Manager starts - on its first page.
            _services.GetService<ILogger<MainWindow>>()?.LogWarning(ex, "Could not restore the workspace");
            Show(DefaultPage);
        }
        finally
        {
            _restoring = null;
            _workspace.Changed();
        }
    }

    private Version? WhatsNewSince(UpdateRecord? update, string? lastRun)
    {
        var current = _updater.Current;
        foreach (var before in new[] { update?.FromVersion, lastRun })
            if (before is not null && AppReleaseFeed.TryParseVersion(before, out var version) && AppReleaseFeed.IsNewer(current, version))
                return version;
        return null;
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

    /// <summary>After an update: this is the new version, or why it isn't (with the helper's log).</summary>
    private void Report(UpdateRecord? update)
    {
        if (update is null) return;
        var result = UpdateResult.TryRead(update.ResultFile);
        var expected = update.ToVersion is { } to && AppReleaseFeed.TryParseVersion(to, out var v) ? v : null;
        var current = _updater.Current;
        if (expected is not null && !AppReleaseFeed.IsNewer(expected, current) && !AppReleaseFeed.IsNewer(current, expected) && result?.Installed != false)
        {
            _log.Success($"DNN Manager was updated from {update.FromVersion} to {current}.");
            Toast.Show($"Updated to v{current} - you're back where you left off.", ToastKind.Success);
            return;
        }
        var why = result is { Installed: false } ? result.Message : $"this is still v{current}.";
        Action? showLog = result?.LogFile is { } log && File.Exists(log) ? () => Shell.Open(log) : null;
        _log.Warn($"The update to {update.ToVersion} wasn't installed: {why}");
        Toast.Show($"The update to v{update.ToVersion} wasn't installed: {why}", ToastKind.Error, showLog is null ? null : "Show log", showLog);
    }

    /// <summary>An operation the last DNN Manager ended before it did: said, with what it had left to undo.</summary>
    private void ReportUnfinished()
    {
        if (_unfinished is not { } unfinished) return;
        _unfinished = null;
        var left = unfinished.Left.Count == 0
            ? "It hadn't changed anything that needed undoing yet."
            : $"It may have left half done what these would have undone: {string.Join("; ", unfinished.Left)}.";
        var message = $"'{unfinished.Title}' didn't finish - DNN Manager ended while it ran (started {unfinished.StartedUtc.ToLocalTime():g}). {left}";
        _log.Warn(message);
        Toast.Show(message, ToastKind.Warning, "Show output", ShowOutput);
    }

    /// <summary>
    /// Windows, with administrator rights: a DNN Manager installed where programs without them can change it is said once
    /// per version, and a sign-in task that would start such a copy elevated is removed.
    /// </summary>
    private async void CheckInstallLocation(bool newVersion)
    {
        if (!OperatingSystem.IsWindows() || !Program.IsRealStart || !TrustedPrograms.IsElevated || _updater.Target is null ||
            Environment.ProcessPath is not { } exe)
            return;
        try
        {
            var adminOnly = await Task.Run(() => TrustedPrograms.IsAdminOnly(exe));
            if ((newVersion || !adminOnly) && _services.GetService<StartupTask>() is { } startup &&
                await Task.Run(() => startup.RemoveIfUnsafeAsync()) is { } removed)
            {
                _log.Warn(removed);
                Toast.Show(removed, ToastKind.Warning);
            }
            if (newVersion && !adminOnly)
                Toast.Show($"DNN Manager is installed in {Path.GetDirectoryName(exe)}, which programs without administrator rights can change - " +
                           "and it runs with those rights. Install it for all users: run its Setup from GitHub, which installs it in Program Files.",
                    ToastKind.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.GetService<ILogger<MainWindow>>()?.LogWarning(ex, "Could not check where DNN Manager is installed");
        }
    }

    // ─── Running in the background, quitting ────────────────────────────────

    // The notification area's icon - there while "Keep DNN Manager running when you close the window" is on.
    private TrayIcon? _tray;
    // Closing quits now (not hides): Quit, a restart, an update, Setup.
    private bool _quitting, _asking, _quittingForSetup;

    /// <summary>The tray icon there while the setting is on - only for a real start (not the tests).</summary>
    private void FollowKeepRunning()
    {
        var wanted = _options.KeepRunningWhenClosed && Program.IsRealStart;
        if (wanted == (_tray is not null)) return;
        if (_tray is null)
        {
            var open = new NativeMenuItem("Open DNN Manager");
            open.Click += (_, _) => ShowFromBackground();
            var quit = new NativeMenuItem("Quit DNN Manager");
            quit.Click += (_, _) => _ = QuitAsync();
            _tray = new TrayIcon
            {
                Icon = Icon, ToolTipText = "DNN Manager", IsVisible = true,
                Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } }
            };
            _tray.Clicked += (_, _) => ShowFromBackground();
        }
        else
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    /// <summary>The window back from the tray - or from a second start (SingleInstance).</summary>
    internal void ShowFromBackground()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Quits DNN Manager - from the tray, the palette, a restart or an update. A running operation is asked about (and
    /// cancelled, its work put back, before the window closes). False when the user chose to stay.
    /// </summary>
    public async Task<bool> QuitAsync()
    {
        if (_quitting) return true;
        if (_asking) return false;
        _asking = true;
        try
        {
            if (!_quittingForSetup)
            {
                if (_runner.IsBusy || _modal is SettingsView { HasUnsavedPassword: true }) ShowFromBackground();
                if (_modal is SettingsView { HasUnsavedPassword: true } &&
                    !await Dialogs.ConfirmAsync("You changed a password in the settings and didn't save it - passwords aren't kept " +
                                                "for the next start. Quit anyway?", "Quit", "Stay"))
                    return false;
                if (_runner.IsBusy && !await Dialogs.ConfirmAsync($"'{_runner.Current}' is still running. Quit anyway? It is cancelled first, and what it did is " +
                                                                  "put back - DNN Manager closes once that is done.", "Quit anyway", "Keep running", danger: true))
                    return false;
            }
            if (_runner.IsBusy) await StopRunningAsync(_quittingForSetup ? TimeSpan.FromSeconds(20) : null);
            _quitting = true;
            // Not from inside a Closing handler: closed once that has returned.
            Dispatcher.UIThread.Post(Close);
            return true;
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>Setup replaces or removes DNN Manager: it quits without asking - a running operation gets 20 seconds to stop.</summary>
    internal void QuitForSetup()
    {
        _quittingForSetup = true;
        _ = QuitAsync();
    }

    // How long quitting waits for an operation to stop and undo itself before it offers Quit now.
    private static readonly TimeSpan OfferQuitNowAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Cancels the running operation and waits until it has stopped and put back what it did. One that takes a while gets
    /// Quit now; with <paramref name="limit"/>, the undo is stopped after that long.
    /// </summary>
    private async Task StopRunningAsync(TimeSpan? limit)
    {
        var title = _runner.Current;
        Toast.Show($"Quitting once '{title}' has stopped and put back what it did…");
        _runner.Cancel();
        var idle = _runner.WhenIdleAsync();
        if (limit is { } wait)
        {
            if (await Task.WhenAny(idle, Task.Delay(wait)) != idle)
            {
                _runner.SkipUndo();
                await Task.WhenAny(idle, Task.Delay(TimeSpan.FromSeconds(3)));
            }
            return;
        }
        if (await Task.WhenAny(idle, Task.Delay(OfferQuitNowAfter)) == idle) return;
        var now = new TaskCompletionSource();
        Toast.Show($"'{title}' is still stopping and putting back what it did. Quit now leaves what isn't undone yet as it " +
                   "is - the next start says what that is.", ToastKind.Warning, "Quit now", () =>
        {
            _runner.SkipUndo();
            now.TrySetResult();
        });
        await Task.WhenAny(idle, now.Task);
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        // Windows shutting down, or the app ending: closed - the workspace saved.
        if (_quitting || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            _workspace.SaveNow(last: true);
            return;
        }
        // Running in the background: the window hides, everything in it goes on.
        if (_tray is not null && !Services.AppRestart.Requested)
        {
            e.Cancel = true;
            _workspace.SaveNow();
            Hide();
            return;
        }
        // Nothing to ask about: it closes now.
        if (!_quittingForSetup && !_runner.IsBusy && _modal is not SettingsView { HasUnsavedPassword: true })
        {
            _quitting = true;
            _workspace.SaveNow(last: true);
            return;
        }
        e.Cancel = true;
        _ = QuitAsync();
    }
}
