using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Input;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Presentation.Pages;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ActivityLog _log;
    private readonly OperationRunner _runner;

    // One entry per sidebar item. A page is created on its first visit and kept, so its lists (folders, DNN
    // versions…) load once instead of on every visit; its Refresh button, or a finished operation, reloads them.
    // Projects needs neither: it shows the ServerStore, which keeps itself current. Settings is read from
    // settings.json on every visit.
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Projects"]      = typeof(ProjectsPage),
        ["Setup"]         = typeof(SetupPage),
        ["Existing"]      = typeof(ExistingFolderPage),
        ["Settings"]      = typeof(SettingsPage),
        ["Troubleshoot"]  = typeof(TroubleshootPage),
    };

    private readonly Dictionary<string, UserControl> _pages = new();
    // Kept pages an operation may have changed since they were last shown - refreshed on their next visit.
    private readonly HashSet<UserControl> _stale = new();

    public MainWindow(IServiceProvider services, ActivityLog log, OperationRunner runner, IOptions<AppOptions> options,
        DnnReleaseCatalog releases, ServerStore store, TerminalService terminal, SiteLogCatalog logs, EfficiencyMode efficiency)
    {
        _services = services; _log = log; _runner = runner;
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
        StatusBar.Attach(store, runner, version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}");
        IisStatus.Attach(store, runner);
        Loaded += (_, _) => store.Start();
        SizeChanged += (_, _) => UpdateCompact();
        BaseDirText.Text = options.Value.BaseDirectory;
        BaseDirText.ToolTip = options.Value.BaseDirectory;
        // Settings saved: they apply at once. The kept pages were filled in with the old ones (folders, repositories,
        // the container's name) - they are made anew on their next visit. Projects follows by itself.
        options.Value.Changed += () =>
        {
            UpdateCompact(); // the UI scale may have changed
            BaseDirText.Text = options.Value.BaseDirectory;
            BaseDirText.ToolTip = options.Value.BaseDirectory;
            foreach (var (key, kept) in _pages.Where(p => p.Value is not ProjectsPage).ToList())
            {
                _pages.Remove(key);
                _stale.Remove(kept);
            }
            releases.Preload();
        };

        // The bottom panel (Activity, Logs, Terminal). Closed at first - the status bar shows the running operation, its
        // progress and Cancel; its terminal button opens the panel, and a click on the operation opens it on Activity.
        TerminalPanel.Attach(_log, terminal, store, logs, efficiency, options.Value);
        TerminalPanel.CloseRequested += (_, _) => SetLogOpen(false);
        TerminalPanel.MaximizeToggled += (_, _) => SetPanelMaximized(!_panelMaximized);
        StatusBar.ActivityToggled += (_, _) => SetLogOpen(!LogOpen);
        StatusBar.OperationClicked += (_, _) =>
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
        Loaded += (_, _) => NavProjects.IsChecked = true;
        Closing += OnClosing;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key } || !Pages.TryGetValue(key, out var type)) return;

        // Settings saves only on Save - leaving with edits would lose them, so ask first.
        if (PageHost.Content is SettingsPage { HasUnsavedChanges: true })
        {
            if (type == typeof(SettingsPage)) return; // back on Settings after "No" below - keep the page and its edits
            if (!Dialogs.Confirm("The settings have unsaved changes. Leave the page and lose them?", "Discard changes", "Stay"))
            {
                Dispatcher.BeginInvoke(() => NavSettings.IsChecked = true);
                return;
            }
        }

        if (type == typeof(SettingsPage) || !_pages.TryGetValue(key, out var page))
        {
            page = (UserControl)ActivatorUtilities.CreateInstance(_services, type);
            if (type != typeof(SettingsPage)) _pages[key] = page;
            // Settings and Troubleshoot take the whole width; closing them goes back to the sidebar page before them.
            if (page is SettingsPage settings) settings.CloseRequested += (_, _) => (_lastNav ?? NavProjects).IsChecked = true;
            if (page is TroubleshootPage troubleshoot) troubleshoot.CloseRequested += (_, _) => (_lastNav ?? NavProjects).IsChecked = true;
        }
        else if (_stale.Remove(page) && page is IRefreshable refreshable)
        {
            refreshable.Refresh();
        }

        var full = page is SettingsPage or TroubleshootPage;
        if (!full) _lastNav = (RadioButton)sender;
        Sidebar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ContentArea, full ? 0 : 1);
        Grid.SetColumnSpan(ContentArea, full ? 2 : 1);
        PageHost.Content = page;
    }

    // The sidebar entry of the page shown before Settings or Troubleshoot was opened.
    private RadioButton? _lastNav;

    private void OnRunnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Let the page refresh whatever the operation changed (new folder, removed site…); the other kept pages
        // catch up when they're next shown. The status bar shows the running operation itself.
        if (e.PropertyName != nameof(OperationRunner.Current)) return;
        if (_runner.IsBusy)
        {
            // Something is happening: with the panel open, show it - the Activity tab.
            if (LogOpen) TerminalPanel.ShowActivity();
            return;
        }
        foreach (var kept in _pages.Values.Where(p => p != PageHost.Content)) _stale.Add(kept);
        if (PageHost.Content is IRefreshable page) page.Refresh();
    }

    // ─── Sidebar ────────────────────────────────────────────────────────────

    // Narrower than this, the sidebar shows only its icons, to leave the room to the page.
    private const double CompactBelow = 1100;

    // Measured in the page's own units: at 150 % UI scale a 1500 px window lays out like a 1000 px one.
    private void UpdateCompact() => IsCompact = ActualWidth / ThemeManager.Scale < CompactBelow;
    private const double ExpandedSidebarWidth = 230, CompactSidebarWidth = 56;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool),
        typeof(MainWindow), new PropertyMetadata(false, (d, e) => ((MainWindow)d).SlideSidebar((bool)e.NewValue)));

    // The sidebar's width - a number, so it can be animated (a grid column's width can't); the column follows it.
    private static readonly DependencyProperty SidebarWidthProperty = DependencyProperty.Register("SidebarWidth", typeof(double),
        typeof(MainWindow), new PropertyMetadata(ExpandedSidebarWidth, (d, e) =>
            ((MainWindow)d).SidebarColumn.Width = new GridLength((double)e.NewValue)));

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
    /// buttons - no title, IIS text or projects folder. Its parts follow this with triggers (ExpandedOnly, SidebarNav).
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

    // Without Windows' frame, a maximized window reaches past the screen edges by its resize border - keep the content
    // (and the window buttons) on screen.
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

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

    private bool LogOpen => TerminalPanel.Visibility == Visibility.Visible;

    // Ctrl+` shows or hides the panel, Ctrl+Shift+M gives it the window (or takes it back) - like VS Code.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetLogOpen(!LogOpen);
            e.Handled = true;
        }
        else if (key == Key.M && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (!LogOpen) SetLogOpen(true);
            SetPanelMaximized(!_panelMaximized);
            e.Handled = true;
        }
    }

    // The panel has the window: the page is hidden and the panel takes its room (the sidebar and status bar stay).
    private bool _panelMaximized;
    private GridLength _restoredLogHeight;

    private void SetPanelMaximized(bool maximized)
    {
        if (maximized == _panelMaximized) return;
        _panelMaximized = maximized;
        TerminalPanel.IsMaximized = maximized;
        if (maximized)
        {
            _restoredLogHeight = LogRow.Height;
            PageRow.MinHeight = 0;
            PageRow.Height = new GridLength(0);
            PageHost.Visibility = Visibility.Collapsed;
            LogSplitter.Visibility = Visibility.Collapsed;
            SplitterRow.Height = new GridLength(0);
            LogRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            PageRow.Height = new GridLength(1, GridUnitType.Star);
            PageRow.MinHeight = 200;
            PageHost.Visibility = Visibility.Visible;
            if (!LogOpen) return;
            LogSplitter.Visibility = Visibility.Visible;
            SplitterRow.Height = new GridLength(5);
            LogRow.Height = _restoredLogHeight;
        }
    }

    /// <summary>
    /// Hides or shows the terminal panel (the activity log and the shells). Hidden, it takes no room at all - the
    /// shells keep running, and the status bar still shows the running operation, its progress and Cancel.
    /// </summary>
    private void SetLogOpen(bool open)
    {
        StatusBar.IsActivityOpen = open;
        // Hiding the panel gives the page its room back.
        if (!open) SetPanelMaximized(false);
        if (open == LogOpen) return;
        if (open)
        {
            TerminalPanel.Visibility = Visibility.Visible;
            LogSplitter.Visibility = Visibility.Visible;
            SplitterRow.Height = new GridLength(5);
            LogRow.MinHeight = 90;
            LogRow.Height = _logHeight;
            TerminalPanel.Opened();
        }
        else
        {
            _logHeight = LogRow.Height;
            TerminalPanel.Visibility = Visibility.Collapsed;
            LogSplitter.Visibility = Visibility.Collapsed;
            SplitterRow.Height = new GridLength(0);
            LogRow.MinHeight = 0;
            LogRow.Height = new GridLength(0);
        }
    }


    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (PageHost.Content is SettingsPage { HasUnsavedChanges: true } &&
            !Dialogs.Confirm("The settings have unsaved changes. Quit and lose them?", "Quit and discard", "Stay"))
        {
            e.Cancel = true;
            return;
        }
        if (!_runner.IsBusy) return;
        if (!Dialogs.Confirm($"'{_runner.Current}' is still running. Quit anyway?", "Quit anyway", "Keep running"))
        {
            e.Cancel = true;
            return;
        }
        _runner.Cancel();
    }
}

/// <summary>A page that reloads its data after an operation finishes.</summary>
public interface IRefreshable
{
    void Refresh();
}
