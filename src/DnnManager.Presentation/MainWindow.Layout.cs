using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace DnnManager.Presentation;

// The window's layout, as VS Code's: the activity bar (x:Name Sidebar - the pages, as icons), the side bars
// (MainWindow.SideBars) and the panel shown or hidden (the title bar's layout buttons, Ctrl+B, Ctrl+Alt+B, Ctrl+J), and
// Customize Layout - the side bar's side, how far the panel reaches, the status bar, where the command palette opens, the
// density. And the gear at the bottom of the activity bar, with its menu.
public partial class MainWindow
{
    // As saved in the settings (layout) - changed by Customize Layout, applied and saved at once.
    private LayoutSettings _layout;
    // Whether the activity bar is shown - the workspace's, like the panel's (the window area of the state).
    private bool _sidebarVisible = true;

    // The sidebar's column: the left one, or the right one with the sidebar on the right.
    private ColumnDefinition SidebarColumn => _layout.SidebarRight ? RightColumn : LeftColumn;

    /// <summary>Lays the window out as <see cref="_layout"/> and <see cref="_sidebarVisible"/> say.</summary>
    private void ApplyLayout()
    {
        var layout = _layout;
        var right = layout.SidebarRight;
        int side = right ? 1 : 0, page = 1 - side;
        (right ? LeftColumn : RightColumn).Width = new GridLength(1, GridUnitType.Star);
        ApplySidebarWidth();
        Sidebar.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(Sidebar, side);

        // The panel under the page only (the sidebar beside it, full height), or under the sidebar too.
        var under = layout.PanelUnderSidebar;
        Grid.SetRowSpan(Sidebar, under ? 1 : 3);
        Grid.SetColumn(Workbench, page);
        foreach (var part in new FrameworkElement[] { PanelSash, PanelCard })
        {
            Grid.SetColumn(part, under ? 0 : page);
            Grid.SetColumnSpan(part, under ? 2 : 1);
        }

        DockPanel.SetDock(IisStatus, right ? Dock.Right : Dock.Left);
        IisStatus.SetSide(right);
        MenuBar.Visibility = layout.MenuBarVisible ? Visibility.Visible : Visibility.Collapsed;
        StatusRow.Visibility = layout.StatusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        // Compact: the frame narrower - the sidebar and its entries, the title bar's buttons. The title bar and the status bar
        // are as low in both densities: no room in them goes to waste - the cards (Default) or the page and panel have it.
        var compact = layout.Compact;
        const double titleBar = 30;
        Resources["TitleBarHeight"] = titleBar;
        Resources["NavPadding"] = compact ? new Thickness(4, 5, 6, 5) : new Thickness(6, 9, 8, 9);
        Resources["NavMargin"] = compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 1, 8, 1);
        // The 30 px icon in the middle of the icons-only sidebar: 4 + 1 + 30 + 1 + 4 = 40, or 6 + 3 + 30 + 3 + 6 = 48.
        Resources["NavIconPadding"] = compact ? new Thickness(1, 5, 1, 5) : new Thickness(3, 9, 3, 9);
        Resources["NavIconMargin"] = compact ? new Thickness(4, 1, 4, 1) : new Thickness(6, 1, 6, 1);
        Resources["CaptionButtonWidth"] = compact ? 40.0 : 46.0;
        Resources["LayoutButtonWidth"] = compact ? 24.0 : 28.0;
        Resources["LayoutButtonHeight"] = compact ? 20.0 : 24.0;
        Resources["StatusBarHeight"] = 24.0;
        ApplyCards(compact, right, under);
        ApplySideBars();
        // The activity bar to its width in this density.
        SlideSidebar();
        FitSidebarEdges();
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome.IsFrozen)
        {
            chrome = (WindowChrome)chrome.Clone();
            WindowChrome.SetWindowChrome(this, chrome);
        }
        chrome.CaptionHeight = titleBar;

        Palette.Centered = layout.QuickInputCentered;
        UpdateLayoutButtons();
        // The title bar's and status bar's heights may have changed: the panel's room with them.
        FitPanelSoon();
    }

    // The gap around the cards in the Default density.
    private const double CardGap = 5;
    // How far a sash reaches across a line - the sidebar's or the panel's in the Compact density, a closed part's edge; in
    // the Default density's gap it is as wide as the gap.
    private const double SashWidth = 6;

    // How much wider the sidebar's column is than the sidebar's width: in the Default density the gap on its outer side and
    // a second border, so what is inside keeps its width (the icons-only sidebar its 48 px).
    private double SidebarCardExtra => _layout.Compact ? 0 : CardGap + 1;

    /// <summary>The page shown: its view in the primary side bar, if it has one.</summary>
    private void ApplyPageChrome() => ShowPageSideBar();

    /// <summary>
    /// The sidebar, the page and the panel as VS Code draws them. Default: rounded cards, a gap around them on the frame's
    /// colour - the title bar and the status bar one surface with it, no lines between them. Compact: flush, divided by
    /// lines.
    /// </summary>
    private void ApplyCards(bool compact, bool sidebarRight, bool panelUnderSidebar)
    {
        var radius = compact ? new CornerRadius(0) : (CornerRadius)FindResource("CardRadius");
        var border = new Thickness(compact ? 0 : 1);
        // Right under the title bar, as they reach down to the status bar: the gap only between them and on the sides.
        Workbench.Margin = compact ? new Thickness(0) : new Thickness(CardGap, 0, CardGap, 0);
        PanelCard.Margin = compact ? new Thickness(0) : new Thickness(CardGap, 0, CardGap, StatusGap);
        // On the window's edge; the page's card keeps the gap on its other side. Over the panel, the splitter's row is the gap
        // under it.
        var bottom = panelUnderSidebar ? 0 : StatusGap;
        Sidebar.Margin = compact ? new Thickness(0)
            : sidebarRight ? new Thickness(0, 0, CardGap, bottom) : new Thickness(CardGap, 0, 0, bottom);
        foreach (var card in new[] { Sidebar, PrimarySideBar, PageCard, SecondarySideBar, PanelCard })
        {
            card.CornerRadius = radius;
            card.BorderThickness = border;
        }
        // Compact: only a line between the parts - the activity bar's and the side bars' toward the page.
        if (compact)
        {
            Sidebar.BorderThickness = sidebarRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            PrimarySideBar.BorderThickness = sidebarRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            SecondarySideBar.BorderThickness = sidebarRight ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        }
        // Its line along its card, not across the gaps at its ends.
        PanelSash.Padding = compact ? new Thickness(0) : new Thickness(CardGap, 0, CardGap, 0);

        TitleBarFrame.BorderThickness = new Thickness(0, 0, 0, compact ? 1 : 0);
        Resources["StatusBarBorder"] = new Thickness(0, compact ? 1 : 0, 0, 0);
        FitSashes();
        ClipCard(PrimaryHost);
        ClipCard(SecondaryHost);
        ClipCard(PageHost);
        ClipCard(TerminalPanel);
        // The page may have moved without changing size (the sidebar to the other side): once it is laid out.
        Dispatcher.BeginInvoke(PlaceToasts, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // How far toasts keep from the page's bottom-right corner.
    private static readonly Thickness ToastGap = new(0, 0, 24, 16);

    /// <summary>
    /// The toasts at the page's bottom-right - above the bottom panel, so they don't cover its Output, and beside the
    /// sidebar wherever it is. With the page hidden (the panel maximized) they are at the window's.
    /// </summary>
    private void PlaceToasts()
    {
        var below = 0d;
        var beside = 0d;
        if (PageCard.IsVisible && PageCard.ActualHeight > 0)
        {
            // The toasts lie over the same row as Body: its corner is theirs.
            var corner = PageCard.TranslatePoint(new Point(PageCard.ActualWidth, PageCard.ActualHeight), Body);
            below = Math.Max(0, Body.ActualHeight - corner.Y);
            beside = Math.Max(0, Body.ActualWidth - corner.X);
        }
        var margin = new Thickness(0, 0, ToastGap.Right + beside, ToastGap.Bottom + below);
        if (Toasts.Margin != margin) Toasts.Margin = margin;
    }

    // Under the cards, above the status bar: nothing - the status bar's own height keeps them apart; the gap without it.
    private double StatusGap => _layout.StatusBarVisible ? 0 : CardGap;

    /// <summary>
    /// The row between the page and the panel, and where the sashes are. The row - Compact: nothing, the panel's 1 px line
    /// along its top divides them. Default: the gap between the cards; nothing over the maximized panel (right under the
    /// title bar); under the page (the panel hidden), the gap above the status bar - none while it is shown. The sashes: see each below; their three dots only in the Default
    /// density - the Compact one has just its lines.
    /// </summary>
    private void FitSashes()
    {
        var compact = _layout.Compact;
        var bothShown = LogOpen && !_panelMaximized;
        SplitterRow.Height = new GridLength(compact || _panelMaximized ? 0 : LogOpen ? CardGap : StatusGap);
        // Compact: the panel's line along its top, as the sidebar's beside the page - not under the title bar's own (maximized).
        if (compact) PanelCard.BorderThickness = new Thickness(0, bothShown ? 1 : 0, 0, 0);
        // The dots on a sash between two parts - not on a closed part's edge.
        PanelSash.IsGripVisible = !compact && LogOpen;
        // Closed, a part's sash stays - to drag it open again - on the edge it opens from: inside the window's resize border,
        // which a pointer on the window's very edge resizes the window with.
        var resizeBorder = WindowChrome.GetWindowChrome(this).ResizeBorderThickness.Left;

        // The panel's: in the gap between the page and the panel, or (Compact) over the panel's line, half on either side of
        // it; closed, along the page's bottom.
        PanelSash.Visibility = _panelMaximized ? Visibility.Collapsed : Visibility.Visible;
        var overLine = LogOpen && compact;
        Grid.SetRow(PanelSash, overLine ? 2 : LogOpen ? 1 : 0);
        PanelSash.VerticalAlignment = overLine ? VerticalAlignment.Top : LogOpen ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        PanelSash.Height = LogOpen && !compact ? double.NaN : SashWidth;
        // Closed, with the status bar hidden, the page reaches down to the window's edge (Compact) or the gap above it.
        var aboveBorder = LogOpen || _layout.StatusBarVisible ? 0 : Math.Max(0, resizeBorder - (compact ? 0 : StatusGap));
        PanelSash.Margin = overLine ? new Thickness(0, -SashWidth / 2, 0, 0) : new Thickness(0, 0, 0, aboveBorder);
    }

    // ─── Resizing ───────────────────────────────────────────────────────────

    // The page keeps at least this much width beside the side bars.
    private const double MinPageWidth = 320;
    // The panel's least height as it is left.
    private const double MinPanelHeight = 90;

    // Where a drag started: the pointer, and the panel's height then (0 while closed). A drag is measured from there - not
    // step by step: a closed part's sash stays put while the pointer moves on. And the panel's height before it, given back
    // when the drag closes it.
    private Point _dragFrom;
    private double _dragPanel, _panelBefore;
    // A part sliding: the activity bar to its width, the panel to where a drag left it - closed, or out to its least size.
    private readonly Slide _sidebarSlide = new(), _panelSlide = new();

    /// <summary>
    /// A drag starts: a closed panel opens - at nothing, to follow the pointer out. While dragged, it has no least size: it
    /// follows the pointer down to nothing, and only settles when let go (Sash_DragCompleted).
    /// </summary>
    private void Sash_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragFrom = Mouse.GetPosition(Body);
        _panelSlide.Stop();
        _panelClosing = false;
        _panelBefore = LogOpen ? LogRow.ActualHeight : _logHeight.Value;
        _dragPanel = LogOpen ? LogRow.ActualHeight : 0;
        if (!LogOpen)
        {
            SetLogOpen(true, takeKeyboard: false);
            LogRow.Height = new GridLength(0);
        }
        LogRow.MinHeight = 0;
    }

    private Vector Dragged => Mouse.GetPosition(Body) - _dragFrom;

    private void PanelSash_DragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(Dragged.Y);

    private void Sash_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SettlePanel();
        _workspace.Changed();
    }

    // How much a keyboard resize changes a side bar or the panel.
    private const double KeyboardResizeStep = 40;

    /// <summary>The panel taller (or shorter) by <paramref name="by"/> - its sash, from the keyboard - within its least height and what the page leaves it.</summary>
    private void ResizePanelBy(double by)
    {
        if (!LogOpen) return;
        FitPanel();
        LogRow.Height = new GridLength(Math.Clamp(LogRow.ActualHeight + by, MinPanelHeight, LogRow.MaxHeight));
        _workspace.Changed();
    }

    /// <summary>The panel dragged <paramref name="dragged"/> down since the drag started: it follows, from nothing up to what the page leaves it (FitPanel).</summary>
    private void ResizePanel(double dragged)
    {
        FitPanel();
        LogRow.Height = new GridLength(Math.Clamp(_dragPanel - dragged, 0, LogRow.MaxHeight));
    }

    /// <summary>
    /// Let go: as tall as it was left - or, lower than its least height, it slides there; under half of it, it slides closed
    /// (its height before the drag given back for when it opens).
    /// </summary>
    private void SettlePanel()
    {
        var height = LogRow.ActualHeight;
        if (height >= MinPanelHeight) LogRow.MinHeight = MinPanelHeight;
        else if (height < MinPanelHeight / 2)
            _panelSlide.Run(height, 0, SetPanelHeight, () =>
            {
                SetLogOpen(false);
                _logHeight = new GridLength(Math.Max(MinPanelHeight, _panelBefore));
            });
        else _panelSlide.Run(height, MinPanelHeight, SetPanelHeight, () => LogRow.MinHeight = MinPanelHeight);
    }

    // The activity bar at a width now - not slid (SlideSidebar): it follows a Slide.
    private void SetSidebarWidth(double width)
    {
        BeginAnimation(SidebarWidthProperty, null);
        SetValue(SidebarWidthProperty, width);
    }

    private void SetPanelHeight(double height) => LogRow.Height = new GridLength(height);

    /// <summary>
    /// A size sliding from one value to another in 150 ms, easing out - frame by frame, as a grid row's height can't be
    /// animated. Run again or stopped, it leaves the size where it is and doesn't call what it was to do when done.
    /// </summary>
    private sealed class Slide
    {
        private EventHandler? _frame;

        public void Run(double from, double to, Action<double> apply, Action? done = null)
        {
            Stop();
            // Animations off: where it goes, at once.
            if (!Motion.Enabled)
            {
                apply(to);
                done?.Invoke();
                return;
            }
            var clock = Stopwatch.StartNew();
            _frame = (_, _) =>
            {
                var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 150);
                apply(from + (to - from) * (1 - Math.Pow(1 - t, 3)));
                if (t < 1) return;
                Stop();
                done?.Invoke();
            };
            CompositionTarget.Rendering += _frame;
        }

        public void Stop()
        {
            if (_frame is null) return;
            CompositionTarget.Rendering -= _frame;
            _frame = null;
        }
    }

    // A card's content clipped to its rounded corners - inside the card's border, so it doesn't cover it.
    private void CardContent_SizeChanged(object sender, SizeChangedEventArgs e) => ClipCard((FrameworkElement)sender);

    private static void ClipCard(FrameworkElement content)
    {
        if (content.Parent is not Border card) return;
        var radius = Math.Max(0, card.CornerRadius.TopLeft - card.BorderThickness.Left);
        content.Clip = radius > 0 ? new RectangleGeometry(new Rect(0, 0, content.ActualWidth, content.ActualHeight), radius, radius) : null;
    }

    /// <summary>
    /// The first entry as far from the activity bar's top, and the gear from its bottom, as the entries are from its sides,
    /// in either density. An entry's own margin is part of it.
    /// </summary>
    private void FitSidebarEdges()
    {
        var margin = (Thickness)FindResource("NavIconMargin");
        var edge = Math.Max(0, margin.Left - margin.Top);
        SidebarTop.Margin = new Thickness(0, edge, 0, 0);
        SidebarBottom.Margin = new Thickness(0, 0, 0, edge);
    }

    /// <summary>The activity bar's column as wide as the activity bar (nothing while it's hidden).</summary>
    private void ApplySidebarWidth()
    {
        var width = (double)GetValue(SidebarWidthProperty) + SidebarCardExtra;
        SidebarColumn.Width = new GridLength(_sidebarVisible ? width : 0);
        FitSashes();
    }

    /// <summary>The title bar's side bar and panel buttons: drawn filled in while that part is shown.</summary>
    private void UpdateLayoutButtons()
    {
        var right = _layout.SidebarRight;
        // On a page without a view for it, there is nothing to show or hide.
        SidebarToggle.IsEnabled = PageSideBar is not null;
        SidebarIconFill.Visibility = PrimaryShown ? Visibility.Visible : Visibility.Hidden;
        SidebarIcon.RenderTransform = right ? new ScaleTransform(-1, 1) : Transform.Identity;
        SidebarToggle.ToolTip = PageSideBar is null ? "This page has nothing for the primary side bar"
            : PrimaryShown ? "Hide the primary side bar" : "Show the primary side bar";
        ToolTipService.SetShowOnDisabled(SidebarToggle, true);
        SecondaryIconFill.Visibility = _secondaryVisible ? Visibility.Visible : Visibility.Hidden;
        SecondaryIcon.RenderTransform = right ? new ScaleTransform(-1, 1) : Transform.Identity;
        SecondaryToggle.ToolTip = _secondaryVisible ? "Hide the secondary side bar" : "Show the secondary side bar";
        PanelIconFill.Visibility = LogOpen ? Visibility.Visible : Visibility.Hidden;
        PanelToggle.ToolTip = LogOpen ? "Hide the panel" : "Show the panel: Output, Logs and Terminal";
    }

    /// <summary>A command's shortcut as shown ("Ctrl+B"), or "".</summary>
    private string ShortcutText(string commandId) =>
        _commands.Find(commandId) is { } command && _commands.ShortcutOf(command) is { } shortcut ? shortcut.ToString() : "";

    /// <summary>An entry of the gear's or Help's menu, with its command's shortcut on the right.</summary>
    private MenuItem MenuEntry(string header, string? command, Action run)
    {
        var item = new MenuItem { Header = header, InputGestureText = command is null ? "" : ShortcutText(command) };
        item.Click += (_, _) => run();
        return item;
    }

    private void SetSidebarVisible(bool visible)
    {
        // Shown while it slides closed (ToggleSidebar): it stays, back at its width.
        if (visible && _sidebarClosing)
        {
            _sidebarSlide.Stop();
            _sidebarClosing = false;
            SlideSidebar();
        }
        if (visible == _sidebarVisible) return;
        var hadKeyboard = Sidebar.IsKeyboardFocusWithin;
        _sidebarVisible = visible;
        ApplyLayout();
        if (hadKeyboard) FocusPage();
        _workspace.Changed();
    }

    // The activity bar sliding closed (ToggleSidebar) - shown until it is.
    private bool _sidebarClosing;

    /// <summary>
    /// Customize Layout's Activity Bar: it slides open or closed. Pressed again while it slides, it turns back from where it
    /// is.
    /// </summary>
    private void ToggleSidebar()
    {
        var width = (double)GetValue(SidebarWidthProperty);
        if (_sidebarVisible && !_sidebarClosing)
        {
            _sidebarClosing = true;
            _sidebarSlide.Run(width, 0, SetSidebarWidth, () =>
            {
                _sidebarClosing = false;
                SetSidebarVisible(false);
            });
            return;
        }
        var from = _sidebarVisible ? width : 0;
        _sidebarClosing = false;
        SetSidebarVisible(true);
        // Not a frame at its full width first: shown, it would slide there (SlideSidebar).
        SetSidebarWidth(from);
        _sidebarSlide.Run(from, ActivityBarWidth, SetSidebarWidth);
    }

    // The panel sliding closed (SlidePanel) - open until it is.
    private bool _panelClosing;

    // Open, or as good as: sliding open, not closed.
    private bool PanelOpening => LogOpen && !_panelClosing;

    /// <summary>
    /// Ctrl+J, the title bar's button, the panel's ✕, Customize Layout: the panel slides open - to the height it had - or
    /// closed. Pressed again while it slides, it turns back from where it is. Maximized, it opens and closes at once.
    /// </summary>
    private void SlidePanel(bool open, bool takeKeyboard = true)
    {
        if (open == PanelOpening) return;
        if (_panelMaximized)
        {
            SetLogOpen(open, takeKeyboard);
            return;
        }
        var from = LogOpen ? LogRow.ActualHeight : 0;
        if (open)
        {
            _panelSlide.Stop();
            _panelClosing = false;
            var to = _logHeight.Value;
            SetLogOpen(true, takeKeyboard);
            LogRow.MinHeight = 0;
            FitPanel();
            SetPanelHeight(from);
            _panelSlide.Run(from, Math.Min(to, LogRow.MaxHeight), SetPanelHeight, () => LogRow.MinHeight = MinPanelHeight);
            return;
        }
        _panelClosing = true;
        // Opened again, it comes back at this height.
        _logHeight = new GridLength(Math.Max(MinPanelHeight, from));
        var height = _logHeight;
        LogRow.MinHeight = 0;
        _panelSlide.Run(from, 0, SetPanelHeight, () =>
        {
            _panelClosing = false;
            SetLogOpen(false);
            _logHeight = height;
        });
    }

    /// <summary>
    /// The panel opens as its button opens it - sliding - for what shows something in it: a toast's Show output, the
    /// running operation, View logs, a terminal, a command. Open already, it stays as it is.
    /// </summary>
    private void OpenPanel(bool takeKeyboard = true)
    {
        if (!PanelOpening) SlidePanel(true, takeKeyboard);
    }

    /// <summary>The panel slides closed; the keyboard, if it was in it, goes back to the page.</summary>
    private void ClosePanel()
    {
        var hadKeyboard = TerminalPanel.IsKeyboardFocusWithin;
        SlidePanel(false);
        if (hadKeyboard) FocusPage();
    }

    /// <summary>Changes the layout: applied at once and saved in the settings - applied even when it can't be saved.</summary>
    private void ChangeLayout(Action<LayoutSettings> change)
    {
        var next = _layout.Copy();
        change(next);
        try
        {
            _settings.Update(s => s.Layout = next.Copy());
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            App.Log?.LogWarning(ex, "Could not save the layout");
            Toast.Show($"The layout is changed, but it couldn't be saved: {ex.Message}", ToastKind.Warning);
        }
        _layout = next;
        _options.Layout = next.Copy();
        ApplyLayout();
    }

    private void TogglePrimary_Click(object sender, RoutedEventArgs e) => TogglePrimarySideBar();

    private void ToggleSecondary_Click(object sender, RoutedEventArgs e) => ToggleSecondarySideBar();

    private void TogglePanel_Click(object sender, RoutedEventArgs e) => TogglePanel();

    private void CustomizeLayout_Click(object sender, RoutedEventArgs e) => ShowCustomizeLayout();

    // ─── Customize Layout ───────────────────────────────────────────────────

    /// <summary>VS Code's Customize Layout: every choice in one list that stays open, each applied as it is chosen.</summary>
    private void ShowCustomizeLayout() => Palette.ShowSettings("Customize Layout", "Search layout options", LayoutItems, ResetLayout);

    /// <summary>Back to the default layout - the activity bar and the primary side bar shown at their widths, the secondary
    /// hidden; the panel stays as it is.</summary>
    private void ResetLayout()
    {
        _sidebarVisible = true;
        ResetSideBars();
        ChangeLayout(l =>
        {
            var defaults = new LayoutSettings();
            l.SidebarPosition = defaults.SidebarPosition;
            l.PanelAlignment = defaults.PanelAlignment;
            l.MenuBarVisible = defaults.MenuBarVisible;
            l.StatusBarVisible = defaults.StatusBarVisible;
            l.QuickInputPosition = defaults.QuickInputPosition;
            l.Density = defaults.Density;
        });
        _workspace.Changed();
    }

    private IReadOnlyList<PaletteItem> LayoutItems()
    {
        var layout = _layout;
        var right = layout.SidebarRight;
        var items = new List<PaletteItem>();
        // The icon and its fill are keys in Themes\Icons.xaml.
        void Add(string group, string title, bool chosen, Action run, string icon, string? fill = null, string? command = null,
            string keywords = "") =>
            items.Add(new PaletteItem(title, "", command is null ? "" : ShortcutText(command), run, $"{group} {keywords}")
            {
                Group = group, IsChecked = chosen, KeepOpen = true,
                Icon = (Geometry)FindResource(icon), IconFill = fill is null ? null : (Geometry)FindResource(fill)
            });
        void Set(string group, string title, string value, string current, Action<LayoutSettings, string> set, string icon, string? fill = null) =>
            Add(group, title, current.Equals(value, StringComparison.OrdinalIgnoreCase), () => ChangeLayout(l => set(l, value)), icon, fill);

        const string visibility = "Visibility";
        Add(visibility, "Menu Bar", layout.MenuBarVisible, () => ChangeLayout(l => l.MenuBarVisible = !l.MenuBarVisible),
            "LayoutMenuBar", "LayoutMenuBarFill", "view.toggleMenuBar", "title name");
        Add(visibility, "Activity Bar", _sidebarVisible, ToggleSidebar, right ? "LayoutSidebarRight" : "LayoutSidebarLeft",
            right ? "LayoutSidebarRightFill" : "LayoutSidebarLeftFill", "view.toggleActivityBar", "pages icons navigation sidebar");
        Add(visibility, "Primary Side Bar", PrimaryShown, TogglePrimarySideBar, right ? "LayoutSidebarRight" : "LayoutSidebarLeft",
            right ? "LayoutSidebarRightFill" : "LayoutSidebarLeftFill", "view.toggleSidebar", "sidebar source control view");
        Add(visibility, "Secondary Side Bar", _secondaryVisible, ToggleSecondarySideBar, right ? "LayoutSidebarLeft" : "LayoutSidebarRight",
            right ? "LayoutSidebarLeftFill" : "LayoutSidebarRightFill", "view.toggleSecondarySideBar", "auxiliary bar right");
        // Shown or hidden here, the keyboard stays in the list.
        Add(visibility, "Panel", LogOpen, () => SlidePanel(!PanelOpening, takeKeyboard: false), "LayoutPanel", "LayoutPanelFill",
            "panel.toggle", "output logs terminal bottom");
        Add(visibility, "Status Bar", layout.StatusBarVisible, () => ChangeLayout(l => l.StatusBarVisible = !l.StatusBarVisible),
            "LayoutStatusBar", "LayoutStatusBarFill", "view.toggleStatusBar");

        const string position = "Primary Side Bar Position";
        Set(position, "Left", "left", layout.SidebarPosition, (l, v) => l.SidebarPosition = v, "LayoutSidebarLeft", "LayoutSidebarLeftFill");
        Set(position, "Right", "right", layout.SidebarPosition, (l, v) => l.SidebarPosition = v, "LayoutSidebarRight", "LayoutSidebarRightFill");

        const string alignment = "Panel Alignment";
        Set(alignment, "Left", "left", layout.PanelAlignment, (l, v) => l.PanelAlignment = v, "LayoutPanelLeft", "LayoutPanelLeftFill");
        Set(alignment, "Right", "right", layout.PanelAlignment, (l, v) => l.PanelAlignment = v, "LayoutPanelRight", "LayoutPanelRightFill");
        Set(alignment, "Center", "center", layout.PanelAlignment, (l, v) => l.PanelAlignment = v, "LayoutPanelCenter", "LayoutPanelCenterFill");
        Set(alignment, "Justify", "justify", layout.PanelAlignment, (l, v) => l.PanelAlignment = v, "LayoutPanelJustify", "LayoutPanelFill");

        const string quickInput = "Quick Input Position";
        Set(quickInput, "Top", "top", layout.QuickInputPosition, (l, v) => l.QuickInputPosition = v, "LayoutFrame", "LayoutQuickInputTopFill");
        Set(quickInput, "Center", "center", layout.QuickInputPosition, (l, v) => l.QuickInputPosition = v, "LayoutFrame",
            "LayoutQuickInputCenterFill");

        const string density = "Layout Density";
        Set(density, "Default", "default", layout.Density, (l, v) => l.Density = v, "LayoutDefault");
        Set(density, "Compact", "compact", layout.Density, (l, v) => l.Density = v, "LayoutCompact");
        return items;
    }

    // ─── The gear ───────────────────────────────────────────────────────────

    /// <summary>
    /// The gear at the bottom of the activity bar: VS Code's Manage menu - the command palette, Settings, Keyboard Shortcuts,
    /// the themes, Customize Layout, Troubleshoot and Check for Updates. Beside the gear, its bottom level with it.
    /// </summary>
    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        var onRight = _layout.SidebarRight;
        // The menu's room for its shadow (MenuStyles: 8 on its right and below) - left out, so its card lines up.
        var shadow = 8 * ThemeManager.Scale;
        var menu = new ContextMenu
        {
            PlacementTarget = ManageButton,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                [new CustomPopupPlacement(new Point(onRight ? -popup.Width - 6 + shadow : target.Width + 6, target.Height - popup.Height + shadow),
                    PopupPrimaryAxis.None)]
        };
        menu.Items.Add(MenuEntry("Command Palette…", "workbench.commandPalette", () => ShowPalette(commands: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Settings", "settings.open", () => ShowModal("Settings")));
        menu.Items.Add(MenuEntry("Keyboard Shortcuts", "settings.keyboard", () => OpenSettings("Keyboard")));
        var themes = new MenuItem { Header = "Themes" };
        foreach (var (label, value) in Themes)
        {
            var current = IsTheme(value);
            var theme = new MenuItem
            {
                Header = label,
                // The chosen one checked; the others with room for the check, so the names line up.
                Icon = new TextBlock
                {
                    Text = current ? (string)FindResource("GlyphCheck") : "", Width = 12, FontSize = 12, FontFamily = (FontFamily)FindResource("IconFont")
                }
            };
            theme.Click += (_, _) => SetTheme(value);
            themes.Items.Add(theme);
        }
        menu.Items.Add(themes);
        menu.Items.Add(MenuEntry("Customize Layout…", "layout.customize", ShowCustomizeLayout));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Troubleshoot", "troubleshoot.open", () => ShowModal("Troubleshoot")));
        menu.Items.Add(MenuEntry("Check for Updates…", "app.checkUpdates", () => _ = CheckForUpdatesAsync()));
        menu.IsOpen = true;
    }

    // ─── Themes ─────────────────────────────────────────────────────────────

    private static readonly (string Label, string Value)[] Themes =
        [("Dark", "dark"), ("Light", "light"), ("System", "system")];

    private bool IsTheme(string value) => _options.Theme.Equals(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The theme as chosen from the gear's menu or the palette: applied and saved at once, as on Settings.</summary>
    private void SetTheme(string value)
    {
        try
        {
            _settings.Update(s => s.Appearance.Theme = value);
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            Toast.Show($"The theme couldn't be saved: {ex.Message}", ToastKind.Error);
            return;
        }
        _options.Theme = value;
        ThemeManager.Initialize(value);
    }

    /// <summary>The palette's Color Theme: the themes, the current one checked.</summary>
    private void ShowThemePick() => Palette.ShowPick("Select a color theme",
        Themes.Select(t => new PaletteItem(t.Label, "", "", () => SetTheme(t.Value), "theme color") { IsChecked = IsTheme(t.Value) }).ToList());
}
