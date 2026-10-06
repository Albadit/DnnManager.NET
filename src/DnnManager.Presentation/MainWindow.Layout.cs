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

// The window's layout, as VS Code's: the sidebar and the panel shown or hidden (the title bar's layout buttons, Ctrl+B,
// Ctrl+J), and Customize Layout - the sidebar's side, how far the panel reaches, the status bar, where the command
// palette opens, the density. And the gear at the bottom of the sidebar, with its menu.
public partial class MainWindow
{
    // As saved in the settings (layout) - changed by Customize Layout, applied and saved at once.
    private LayoutSettings _layout;
    // Whether the sidebar is shown - the workspace's, like the panel's (the window area of the state).
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
        Grid.SetColumn(PageCard, page);
        foreach (var part in new FrameworkElement[] { PanelSash, PanelCard })
        {
            Grid.SetColumn(part, under ? 0 : page);
            Grid.SetColumnSpan(part, under ? 2 : 1);
        }
        // The sidebar's sash on the page's edge beside it, as tall as the sidebar; the corner where it meets the panel's.
        foreach (var part in new FrameworkElement[] { SidebarSash, SashCorner })
        {
            Grid.SetColumn(part, page);
            part.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        Grid.SetRowSpan(SidebarSash, under ? 1 : 3);

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
        // The sidebar to its width in this density.
        SlideSidebar(IsCompact);
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
        PageCard.Margin = compact ? new Thickness(0) : new Thickness(CardGap, 0, CardGap, 0);
        PanelCard.Margin = compact ? new Thickness(0) : new Thickness(CardGap, 0, CardGap, StatusGap);
        // On the window's edge; the page's card keeps the gap on its other side. Over the panel, the splitter's row is the gap
        // under it.
        var bottom = panelUnderSidebar ? 0 : StatusGap;
        Sidebar.Margin = compact ? new Thickness(0)
            : sidebarRight ? new Thickness(0, 0, CardGap, bottom) : new Thickness(CardGap, 0, 0, bottom);
        foreach (var card in new[] { Sidebar, PageCard, PanelCard })
        {
            card.CornerRadius = radius;
            card.BorderThickness = border;
        }
        // Compact: only the sidebar's line beside the page.
        if (compact) Sidebar.BorderThickness = sidebarRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        SidebarSash.Width = SashCorner.Width = compact ? SashWidth : CardGap;
        // Their lines as long as the cards beside them, not across the gaps at their ends: the panel's along its card, the
        // sidebar's from the cards' top to their bottom.
        PanelSash.Padding = compact ? new Thickness(0) : new Thickness(CardGap, 0, CardGap, 0);
        SidebarSash.Padding = compact ? new Thickness(0) : new Thickness(0, 0, 0, panelUnderSidebar ? 0 : StatusGap);

        TitleBarFrame.BorderThickness = new Thickness(0, 0, 0, compact ? 1 : 0);
        Resources["StatusBarBorder"] = new Thickness(0, compact ? 1 : 0, 0, 0);
        FitSashes();
        ClipCard(PageHost);
        ClipCard(TerminalPanel);
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
        SidebarSash.IsGripVisible = !compact && _sidebarVisible;
        PanelSash.IsGripVisible = !compact && LogOpen;
        // Closed, a part's sash stays - to drag it open again - on the edge it opens from: inside the window's resize border,
        // which a pointer on the window's very edge resizes the window with.
        var resizeBorder = WindowChrome.GetWindowChrome(this).ResizeBorderThickness.Left;

        // The sidebar's: in the gap, or (Compact) over the sidebar's line, half on either side of it; hidden, on the window's
        // edge. Not in a window narrow enough for only its icons - the window's width sets that.
        var sidebar = _sidebarVisible && !IsCompact;
        SidebarSash.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        var edge = _sidebarVisible ? compact ? -SashWidth / 2 : 0 : resizeBorder;
        SidebarSash.Margin = _layout.SidebarRight ? new Thickness(0, 0, edge, 0) : new Thickness(edge, 0, 0, 0);

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

        // The corner: where the two meet - the sidebar's place across, the panel's down.
        SashCorner.Visibility = sidebar && bothShown ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(SashCorner, Grid.GetRow(PanelSash));
        SashCorner.VerticalAlignment = PanelSash.VerticalAlignment;
        SashCorner.Height = PanelSash.Height;
        SashCorner.Margin = new Thickness(SidebarSash.Margin.Left, PanelSash.Margin.Top, SidebarSash.Margin.Right, 0);
    }

    // ─── Resizing ───────────────────────────────────────────────────────────

    // The sidebar's width as dragged (null: its density's own, ExpandedSidebarWidth) - kept in the window area of the state.
    private double? _sidebarWidth;
    // The sidebar's least and greatest width as it is left; the page keeps at least MinPageWidth beside it.
    private const double MinSidebarWidth = 160, MaxSidebarWidth = 600, MinPageWidth = 320;
    // The panel's least height as it is left.
    private const double MinPanelHeight = 90;

    // Where a drag started: the pointer, and the sidebar's width and the panel's height then (0 while closed). A drag is
    // measured from there - not step by step: a closed part's sash stays put while the pointer moves on. And the panel's
    // height before it, given back when the drag closes it.
    private Point _dragFrom;
    private double _dragSidebar, _dragPanel, _panelBefore;
    // A part sliding to where a drag left it: closed, or out to its least size.
    private readonly Slide _sidebarSlide = new(), _panelSlide = new();

    /// <summary>
    /// A drag starts: a closed part opens - at nothing, to follow the pointer out. While dragged, a part has no least size:
    /// it follows the pointer down to nothing, and only settles when let go (Sash_DragCompleted).
    /// </summary>
    private void Sash_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragFrom = Mouse.GetPosition(Body);
        if (sender != PanelSash)
        {
            _sidebarSlide.Stop();
            _sidebarClosing = false;
            if (!_sidebarVisible)
            {
                SetSidebarVisible(true);
                SetSidebarWidth(0);
            }
            _dragSidebar = (double)GetValue(SidebarWidthProperty);
        }
        if (sender != SidebarSash)
        {
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
        // The corner: both sashes drawn as dragged while it is.
        if (sender == SashCorner) SidebarSash.IsActive = PanelSash.IsActive = true;
    }

    private Vector Dragged => Mouse.GetPosition(Body) - _dragFrom;

    private void SidebarSash_DragDelta(object sender, DragDeltaEventArgs e) => ResizeSidebar(Dragged.X);

    private void PanelSash_DragDelta(object sender, DragDeltaEventArgs e) => ResizePanel(Dragged.Y);

    private void SashCorner_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var dragged = Dragged;
        ResizeSidebar(dragged.X);
        ResizePanel(dragged.Y);
    }

    private void Sash_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SidebarSash.IsActive = PanelSash.IsActive = false;
        if (sender != PanelSash) SettleSidebar();
        if (sender != SidebarSash) SettlePanel();
        _workspace.Changed();
    }

    /// <summary>The sidebar dragged <paramref name="dragged"/> toward the page since the drag started: it follows, from nothing up to its greatest width.</summary>
    private void ResizeSidebar(double dragged)
    {
        var max = Math.Clamp(Body.ActualWidth - SidebarCardExtra - MinPageWidth, MinSidebarWidth, MaxSidebarWidth);
        SetSidebarWidth(Math.Clamp(_dragSidebar + (_layout.SidebarRight ? -dragged : dragged), 0, max));
    }

    /// <summary>The panel dragged <paramref name="dragged"/> down since the drag started: it follows, from nothing up to what the page leaves it (FitPanel).</summary>
    private void ResizePanel(double dragged)
    {
        FitPanel();
        LogRow.Height = new GridLength(Math.Clamp(_dragPanel - dragged, 0, LogRow.MaxHeight));
    }

    /// <summary>
    /// Let go: as wide as it was left, kept as the user's width - or, narrower than its least width, it slides there; under
    /// half of it, it slides closed (its width before the drag kept for when it opens).
    /// </summary>
    private void SettleSidebar()
    {
        var width = (double)GetValue(SidebarWidthProperty);
        if (width >= MinSidebarWidth) _sidebarWidth = width;
        else if (width < MinSidebarWidth / 2) _sidebarSlide.Run(width, 0, SetSidebarWidth, () => SetSidebarVisible(false));
        else
        {
            _sidebarWidth = MinSidebarWidth;
            _sidebarSlide.Run(width, MinSidebarWidth, SetSidebarWidth);
        }
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

    // The sidebar at a width now - not slid (SlideSidebar): it follows the pointer, or a Slide.
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
    /// The first entry as far from the sidebar's top, and the gear from its bottom, as the entries are from its sides - with
    /// their names or only their icons, in either density. An entry's own margin is part of it.
    /// </summary>
    private void FitSidebarEdges()
    {
        var margin = (Thickness)FindResource(IsCompact ? "NavIconMargin" : "NavMargin");
        var edge = Math.Max(0, margin.Left - margin.Top);
        SidebarTop.Margin = new Thickness(0, edge, 0, 0);
        SidebarBottom.Margin = new Thickness(0, 0, 0, edge);
    }

    /// <summary>The sidebar's column as wide as the sidebar (nothing while it's hidden), and the IIS cell under it.</summary>
    private void ApplySidebarWidth()
    {
        var width = (double)GetValue(SidebarWidthProperty) + SidebarCardExtra;
        SidebarColumn.Width = new GridLength(_sidebarVisible ? width : 0);
        // The IIS cell: at least as wide as the sidebar while that shows its names, so they line up - wider when its content
        // needs it; under the icons-only or a hidden sidebar, as wide as its content.
        IisStatus.MinWidth = _sidebarVisible && !IsCompact ? width : 0;
        FitSashes();
    }

    /// <summary>The title bar's sidebar and panel buttons: drawn filled in while that part is shown.</summary>
    private void UpdateLayoutButtons()
    {
        SidebarIconFill.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Hidden;
        SidebarIcon.RenderTransform = _layout.SidebarRight ? new ScaleTransform(-1, 1) : Transform.Identity;
        SidebarToggle.ToolTip = _sidebarVisible ? "Hide the pages on the left" : "Show the pages on the left";
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
            SlideSidebar(IsCompact);
        }
        if (visible == _sidebarVisible) return;
        var hadKeyboard = Sidebar.IsKeyboardFocusWithin;
        _sidebarVisible = visible;
        ApplyLayout();
        if (hadKeyboard) FocusPage();
        _workspace.Changed();
    }

    // The sidebar sliding closed (ToggleSidebar) - shown until it is.
    private bool _sidebarClosing;

    /// <summary>
    /// Ctrl+B, the title bar's button, Customize Layout: the sidebar slides open or closed. Pressed again while it slides, it
    /// turns back from where it is.
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
        _sidebarSlide.Run(from, IsCompact ? CompactSidebarWidth : ExpandedSidebarWidth, SetSidebarWidth);
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

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void TogglePanel_Click(object sender, RoutedEventArgs e) => TogglePanel();

    private void CustomizeLayout_Click(object sender, RoutedEventArgs e) => ShowCustomizeLayout();

    // ─── Customize Layout ───────────────────────────────────────────────────

    /// <summary>VS Code's Customize Layout: every choice in one list that stays open, each applied as it is chosen.</summary>
    private void ShowCustomizeLayout() => Palette.ShowSettings("Customize Layout", "Search layout options", LayoutItems, ResetLayout);

    /// <summary>Back to the default layout, the sidebar shown - the panel stays as it is.</summary>
    private void ResetLayout()
    {
        _sidebarVisible = true;
        _sidebarWidth = null;
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
        Add(visibility, "Sidebar", _sidebarVisible, ToggleSidebar, right ? "LayoutSidebarRight" : "LayoutSidebarLeft",
            right ? "LayoutSidebarRightFill" : "LayoutSidebarLeftFill", "view.toggleSidebar", "primary side bar");
        // Shown or hidden here, the keyboard stays in the list.
        Add(visibility, "Panel", LogOpen, () => SlidePanel(!PanelOpening, takeKeyboard: false), "LayoutPanel", "LayoutPanelFill",
            "panel.toggle", "output logs terminal bottom");
        Add(visibility, "Status Bar", layout.StatusBarVisible, () => ChangeLayout(l => l.StatusBarVisible = !l.StatusBarVisible),
            "LayoutStatusBar", "LayoutStatusBarFill", "view.toggleStatusBar");

        const string position = "Sidebar Position";
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
    /// The gear at the bottom of the sidebar: VS Code's Manage menu - the command palette, Settings, Keyboard Shortcuts,
    /// the themes, Customize Layout, Troubleshoot and Check for Updates. Beside the gear, its bottom level with it.
    /// </summary>
    private void Manage_Click(object sender, RoutedEventArgs e)
    {
        var onRight = _layout.SidebarRight;
        var menu = new ContextMenu
        {
            PlacementTarget = ManageButton,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) =>
                [new CustomPopupPlacement(new Point(onRight ? -popup.Width - 6 : target.Width + 6, target.Height - popup.Height), PopupPrimaryAxis.None)]
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
