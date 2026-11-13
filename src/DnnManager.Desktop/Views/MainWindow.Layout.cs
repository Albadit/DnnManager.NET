using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;
using DnnManager.Desktop.Views.Workbench;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Views;

// The layout (src/DnnManager.Presentation/MainWindow.Layout.cs and MainWindow.SideBars.cs): Customize Layout - the side
// bar's side, how far the panel reaches, the menu bar and the status bar, where the command palette opens, the density - the
// side bars (Ctrl+B, Ctrl+Alt+B) and the bottom panel (Ctrl+J), each dragged wider or taller on its sash and kept in the
// workspace; and the gear at the bottom of the activity bar, with its menu.
public partial class MainWindow
{
    // As saved in the settings (layout) - changed by Customize Layout, applied and saved at once.
    private LayoutSettings _layout;
    // The activity bar shown - the workspace's.
    private bool _sidebarVisible = true;

    // The gap around the cards in the Default density; the line between them in the Compact one.
    private const double CardGap = 5, Line = 1;
    // The body's columns (MainWindow.axaml): the activity bar's on the left, the left side bar, the page, the right side
    // bar, the activity bar's on the right - with the gaps between them.
    private const int LeftColumnIndex = 0, EdgeLeftIndex = 1, LeftSideIndex = 2, PageIndex = 4, RightSideIndex = 6, EdgeRightIndex = 7, RightColumnIndex = 8;
    private const double DefaultSideBarWidth = 300, MinSideBarWidth = 170, MaxSideBarWidth = 800, MinPanelHeight = 90, MinPageHeight = 60;

    private double ActivityBarWidth => _layout.Compact ? 40 : 48;

    // Under the cards, above the status bar: nothing - the status bar keeps them apart; the gap without it.
    private double StatusGap => _layout.StatusBarVisible || _layout.Compact ? 0 : CardGap;

    /// <summary>Whether the panel reaches under the side bar on the left, and the one on the right - Panel Alignment.</summary>
    private (bool Left, bool Right) PanelReach => _layout.SidebarRight
        ? (_layout.PanelUnderSecondarySideBar, _layout.PanelUnderSidebar)
        : (_layout.PanelUnderSidebar, _layout.PanelUnderSecondarySideBar);

    private ColumnDefinition Column(int index) => Body.ColumnDefinitions[index];

    /// <summary>Lays the window out as <see cref="_layout"/> says.</summary>
    private void ApplyLayout()
    {
        var layout = _layout;
        var right = layout.SidebarRight;
        var compact = layout.Compact;

        // The activity bar on its side, at its width in this density.
        Resources["NavIconPadding"] = compact ? new Thickness(1, 5, 1, 5) : new Thickness(3, 9, 3, 9);
        Resources["NavIconMargin"] = compact ? new Thickness(4, 1, 4, 1) : new Thickness(6, 1, 6, 1);
        Resources["LayoutButtonWidth"] = compact ? 24.0 : 28.0;
        Resources["LayoutButtonHeight"] = compact ? 20.0 : 24.0;
        var barColumn = right ? RightColumnIndex : LeftColumnIndex;
        Column(right ? LeftColumnIndex : RightColumnIndex).Width = new GridLength(0);
        Column(barColumn).Width = new GridLength(_sidebarVisible ? ActivityBarWidth + (compact ? 0 : CardGap + 1) : 0);
        Grid.SetColumn(Sidebar, barColumn);
        Sidebar.IsVisible = _sidebarVisible;
        var edge = new Thickness(0, Math.Max(0, 6 - 1), 0, 0);
        SidebarTop.Margin = edge;
        SidebarBottom.Margin = new Thickness(0, 0, 0, 5);

        MenuBar.IsVisible = layout.MenuBarVisible;
        StatusRow.IsVisible = layout.StatusBarVisible;
        Palette.Centered = layout.QuickInputCentered;
        ApplyCards(compact, right);
        ApplySideBars();
        ApplyPanel();
        UpdateLayoutButtons();
    }

    /// <summary>
    /// The activity bar, the side bars, the page and the panel as VS Code draws them. Default: rounded cards with a gap
    /// around them on the frame's colour - the title bar and status bar one surface with it. Compact: flush, divided by lines.
    /// </summary>
    private void ApplyCards(bool compact, bool sidebarRight)
    {
        var radius = compact ? new CornerRadius(0) : new CornerRadius(6);
        var border = new Thickness(compact ? 0 : 1);
        Column(EdgeLeftIndex).Width = Column(EdgeRightIndex).Width = new GridLength(compact ? 0 : CardGap);
        Sidebar.Margin = compact ? new Thickness(0)
            : sidebarRight ? new Thickness(0, 0, CardGap, StatusGap) : new Thickness(CardGap, 0, 0, StatusGap);
        foreach (var card in new[] { Sidebar, PrimarySideBar, PageCard, SecondarySideBar, PanelCard })
        {
            card.CornerRadius = radius;
            card.BorderThickness = border;
        }
        if (compact)
        {
            Sidebar.BorderThickness = sidebarRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
            PrimarySideBar.BorderThickness = SecondarySideBar.BorderThickness = new Thickness(0);
        }
        PageCard.Margin = new Thickness(0, 0, 0, PanelOpen ? 0 : StatusGap);
        TitleBarFrame.BorderThickness = new Thickness(0, 0, 0, compact ? 1 : 0);
        StatusBar.BorderThickness = new Thickness(0, compact ? 1 : 0, 0, 0);
        foreach (var sash in new[] { PrimarySash, SecondarySash, PanelSash })
        {
            sash.Classes.Set("line", compact);
        }
        Dispatcher.UIThread.Post(PlaceToasts, DispatcherPriority.Loaded);
    }

    // ─── The side bars ──────────────────────────────────────────────────────

    // Shown - the primary only on a page with a view for it; remembered for those that have one.
    private bool _primaryVisible = true, _secondaryVisible;
    // Their widths as dragged - null: their own.
    private double? _primaryWidth, _secondaryWidth;

    private Control? PageSideBar => (ViewHost.Content as ISideBarPage)?.SideBar;

    private bool PrimaryShown => _primaryVisible && PageSideBar is not null;

    private void RestoreSideBars(WindowLayout layout)
    {
        _primaryVisible = !layout.PrimarySideBarHidden;
        _secondaryVisible = layout.SecondarySideBarOpen;
        _primaryWidth = Kept(layout.PrimarySideBarWidth);
        _secondaryWidth = Kept(layout.SecondarySideBarWidth);

        static double? Kept(double? width) => width is >= MinSideBarWidth and <= MaxSideBarWidth ? width : null;
    }

    private void ApplySideBars()
    {
        var right = _layout.SidebarRight;
        ApplySideBar(PrimarySideBar, PrimarySash, onLeft: !right, PrimaryShown, _primaryWidth ?? DefaultSideBarWidth);
        ApplySideBar(SecondarySideBar, SecondarySash, onLeft: right, _secondaryVisible, _secondaryWidth ?? DefaultSideBarWidth);
    }

    private void ApplySideBar(Border card, GridSplitter sash, bool onLeft, bool shown, double width)
    {
        var compact = _layout.Compact;
        var (column, gap) = onLeft ? (LeftSideIndex, LeftSideIndex + 1) : (RightSideIndex, RightSideIndex - 1);
        Grid.SetColumn(card, column);
        Grid.SetColumn(sash, gap);
        var (reachLeft, reachRight) = PanelReach;
        var panelUnder = PanelOpen && (onLeft ? reachLeft : reachRight);
        Grid.SetRowSpan(card, panelUnder ? 1 : 3);
        Grid.SetRowSpan(sash, panelUnder ? 1 : 3);
        card.Margin = new Thickness(0, 0, 0, compact || panelUnder ? 0 : StatusGap);
        card.IsVisible = shown;
        sash.IsVisible = shown;
        Column(column).Width = new GridLength(shown ? width : 0);
        Column(gap).Width = new GridLength(shown ? compact ? Line : CardGap : 0);
    }

    /// <summary>The page shown: its view in the primary side bar, if it has one.</summary>
    private void ShowPageSideBar()
    {
        var view = PageSideBar;
        if (!ReferenceEquals(PrimaryHost.Content, view)) PrimaryHost.Content = view;
        ApplySideBars();
        UpdateLayoutButtons();
    }

    private void TogglePrimarySideBar()
    {
        if (PageSideBar is null) return;
        _primaryVisible = !_primaryVisible;
        ApplySideBars();
        UpdateLayoutButtons();
        _workspace.Changed();
    }

    private void ToggleSecondarySideBar()
    {
        _secondaryVisible = !_secondaryVisible;
        ApplySideBars();
        UpdateLayoutButtons();
        _workspace.Changed();
    }

    private void ToggleActivityBar()
    {
        _sidebarVisible = !_sidebarVisible;
        ApplyLayout();
        _workspace.Changed();
    }

    /// <summary>Keyboard: the primary side bar wider or narrower, as a drag of its sash.</summary>
    private void ResizePrimaryBy(double by)
    {
        if (!PrimaryShown) return;
        _primaryWidth = Math.Clamp((_primaryWidth ?? DefaultSideBarWidth) + by, MinSideBarWidth, MaxSideBarWidth);
        ApplySideBars();
        _workspace.Changed();
    }

    private void SideBarSash_DragCompleted(object? sender, VectorEventArgs e)
    {
        // The width as dragged - kept within its limits; dragged far enough, closed.
        var primaryColumn = Grid.GetColumn(PrimarySideBar);
        var secondaryColumn = Grid.GetColumn(SecondarySideBar);
        if (ReferenceEquals(sender, PrimarySash))
        {
            var width = Column(primaryColumn).ActualWidth;
            if (width < MinSideBarWidth / 2) _primaryVisible = false;
            else _primaryWidth = Math.Clamp(width, MinSideBarWidth, MaxSideBarWidth);
        }
        else
        {
            var width = Column(secondaryColumn).ActualWidth;
            if (width < MinSideBarWidth / 2) _secondaryVisible = false;
            else _secondaryWidth = Math.Clamp(width, MinSideBarWidth, MaxSideBarWidth);
        }
        Column(PageIndex).Width = new GridLength(1, GridUnitType.Star);
        ApplySideBars();
        UpdateLayoutButtons();
        _workspace.Changed();
    }

    // ─── The bottom panel ───────────────────────────────────────────────────

    // Its height as it was left (dragged), given back when it opens again.
    private double _panelHeight = 260, _restoredPanelHeight = 260;
    private bool _panelMaximized;

    private bool PanelOpen => PanelCard.IsVisible;

    private void ApplyPanel()
    {
        var compact = _layout.Compact;
        var open = PanelOpen;
        var (reachLeft, reachRight) = PanelReach;
        var from = reachLeft && open ? LeftSideIndex : PageIndex;
        var to = reachRight && open ? RightSideIndex : PageIndex;
        foreach (var part in new Control[] { PanelSash, PanelCard })
        {
            Grid.SetColumn(part, from);
            Grid.SetColumnSpan(part, to - from + 1);
        }
        var rows = Body.RowDefinitions;
        rows[1].Height = new GridLength(_panelMaximized ? 0 : open ? compact ? Line : CardGap : StatusGap);
        PanelSash.IsVisible = open && !_panelMaximized;
        PanelCard.Margin = new Thickness(0, 0, 0, open ? StatusGap : 0);
        PageCard.Margin = new Thickness(0, 0, 0, open ? 0 : StatusGap);
        if (_panelMaximized)
        {
            rows[0].MinHeight = 0;
            rows[0].Height = new GridLength(0);
            rows[2].Height = new GridLength(1, GridUnitType.Star);
            rows[2].MinHeight = 0;
        }
        else
        {
            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[0].MinHeight = open ? MinPageHeight : 0;
            rows[2].MinHeight = open ? MinPanelHeight : 0;
            rows[2].Height = new GridLength(open ? FitPanel(_panelHeight) : 0);
        }
        PageCard.IsVisible = !_panelMaximized;
        ApplySideBars();
        Dispatcher.UIThread.Post(PlaceToasts, DispatcherPriority.Loaded);
    }

    // No taller than the body leaves room for beside the page's least room.
    private double FitPanel(double height)
    {
        var body = Body.Bounds.Height;
        return body <= 0 ? height : Math.Clamp(height, MinPanelHeight, Math.Max(MinPanelHeight, body - MinPageHeight - CardGap));
    }

    /// <summary>Opens or closes the panel - closed, it takes no room; Output stays as it was.</summary>
    private void SetPanelOpen(bool open)
    {
        if (!open) SetPanelMaximized(false);
        if (open == PanelOpen) return;
        if (!open)
        {
            _panelHeight = Math.Max(MinPanelHeight, Body.RowDefinitions[2].ActualHeight);
            OutputPanel.CloseSearch();
        }
        PanelCard.IsVisible = open;
        ApplyPanel();
        UpdateLayoutButtons();
        _workspace.Changed();
    }

    private void SetPanelMaximized(bool maximized)
    {
        if (maximized == _panelMaximized) return;
        if (maximized)
        {
            if (!PanelOpen) SetPanelOpen(true);
            _restoredPanelHeight = Body.RowDefinitions[2].ActualHeight is > 0 and var h ? h : _panelHeight;
        }
        else _panelHeight = _restoredPanelHeight;
        _panelMaximized = maximized;
        OutputPanel.IsMaximized = maximized;
        ApplyPanel();
        _workspace.Changed();
    }

    private void OpenPanel()
    {
        if (!PanelOpen) SetPanelOpen(true);
    }

    /// <summary>The panel open on Output (<paramref name="show"/>) or closed - what a page asks for as it runs something.</summary>
    public void ShowOutput(bool show)
    {
        if (show) ShowOutput();
        else ClosePanel();
    }

    private void ClosePanel()
    {
        var hadKeyboard = OutputPanel.IsKeyboardFocusWithin;
        SetPanelOpen(false);
        if (hadKeyboard) FocusPage();
    }

    /// <summary>Ctrl+J: the panel open (with the keyboard in it) or closed (the keyboard back on the page).</summary>
    private void TogglePanel()
    {
        if (PanelOpen)
        {
            ClosePanel();
            return;
        }
        SetPanelOpen(true);
        OutputPanel.Focus();
    }

    /// <summary>Output shown - a toast's Show output, the running operation's name, Ctrl+Shift+U.</summary>
    private void ShowOutput()
    {
        OpenPanel();
        OutputPanel.ShowOutput();
    }

    private void ResizePanelBy(double by)
    {
        if (!PanelOpen || _panelMaximized) return;
        _panelHeight = FitPanel(Body.RowDefinitions[2].ActualHeight + by);
        ApplyPanel();
        _workspace.Changed();
    }

    private void PanelSash_DragCompleted(object? sender, VectorEventArgs e)
    {
        var height = Body.RowDefinitions[2].ActualHeight;
        // Dragged far enough down: closed, with the height it had before.
        if (height < MinPanelHeight / 2)
        {
            SetPanelOpen(false);
            return;
        }
        _panelHeight = FitPanel(height);
        Body.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ApplyPanel();
        _workspace.Changed();
    }

    // ─── Toasts ─────────────────────────────────────────────────────────────

    /// <summary>The toasts at the page's bottom-right - above the panel, beside the side bars; at the window's when the page is hidden.</summary>
    private void PlaceToasts()
    {
        double below = 0, beside = 0;
        if (PageCard.IsVisible && PageCard.Bounds.Height > 0 && PageCard.TranslatePoint(new Point(PageCard.Bounds.Width, PageCard.Bounds.Height), Body) is { } corner)
        {
            below = Math.Max(0, Body.Bounds.Height - corner.Y);
            beside = Math.Max(0, Body.Bounds.Width - corner.X);
        }
        var margin = new Thickness(0, 0, 24 + beside, 16 + below);
        if (Toasts.Margin != margin) Toasts.Margin = margin;
    }

    // ─── The title bar's layout buttons ─────────────────────────────────────

    /// <summary>The side bar and panel buttons: drawn filled in while that part is shown.</summary>
    private void UpdateLayoutButtons()
    {
        var right = _layout.SidebarRight;
        SidebarToggle.IsEnabled = PageSideBar is not null;
        SidebarIconFill.Opacity = PrimaryShown ? 1 : 0;
        SidebarIcon.RenderTransform = right ? new ScaleTransform(-1, 1) : null;
        ToolTip.SetTip(SidebarToggle, PageSideBar is null ? "This page has nothing for the primary side bar"
            : (PrimaryShown ? "Hide the primary side bar" : "Show the primary side bar") + Keys("view.toggleSidebar"));
        SecondaryIconFill.Opacity = _secondaryVisible ? 1 : 0;
        SecondaryIcon.RenderTransform = right ? new ScaleTransform(-1, 1) : null;
        ToolTip.SetTip(SecondaryToggle, (_secondaryVisible ? "Hide the secondary side bar" : "Show the secondary side bar") + Keys("view.toggleSecondarySideBar"));
        PanelIconFill.Opacity = PanelOpen ? 1 : 0;
        ToolTip.SetTip(PanelToggle, (PanelOpen ? "Hide the panel" : "Show the panel: Output and Logs") + Keys("panel.toggle"));
    }

    // " (Ctrl+B)" - a command's shortcut for a tooltip.
    private string Keys(string command) => _commands.ShortcutText(command) is { Length: > 0 } keys ? $" ({keys})" : "";

    private void TogglePrimary_Click(object? sender, RoutedEventArgs e) => TogglePrimarySideBar();
    private void ToggleSecondary_Click(object? sender, RoutedEventArgs e) => ToggleSecondarySideBar();
    private void TogglePanel_Click(object? sender, RoutedEventArgs e) => TogglePanel();
    private void CustomizeLayout_Click(object? sender, RoutedEventArgs e) => ShowCustomizeLayout();

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
            Toast.Show($"The layout is changed, but it couldn't be saved: {ex.Message}", ToastKind.Warning);
        }
        _layout = next;
        _options.Layout = next.Copy();
        ApplyLayout();
    }

    // ─── Customize Layout ───────────────────────────────────────────────────

    /// <summary>VS Code's Customize Layout: every choice in one list that stays open, each applied as it is chosen.</summary>
    private void ShowCustomizeLayout() => Palette.ShowSettings("Customize Layout", "Search layout options", LayoutItems, ResetLayout);

    /// <summary>The default layout back - the activity bar and the primary side bar shown at their widths; the panel stays.</summary>
    private void ResetLayout()
    {
        _sidebarVisible = true;
        _primaryVisible = true;
        _secondaryVisible = false;
        _primaryWidth = _secondaryWidth = null;
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
        Geometry? Shape(string? key) => key is null ? null : this.FindResource(key) as Geometry;
        void Add(string group, string title, bool chosen, Action run, string icon, string? fill = null, string? command = null, string keywords = "") =>
            items.Add(new PaletteItem(title, "", command is null ? "" : _commands.ShortcutText(command), run, $"{group} {keywords}")
            {
                Group = group, IsChecked = chosen, KeepOpen = true, Icon = Shape(icon), IconFill = Shape(fill)
            });
        void Set(string group, string title, string value, string current, Action<LayoutSettings, string> set, string icon, string? fill = null) =>
            Add(group, title, current.Equals(value, StringComparison.OrdinalIgnoreCase), () => ChangeLayout(l => set(l, value)), icon, fill);

        const string visibility = "Visibility";
        Add(visibility, "Menu Bar", layout.MenuBarVisible, () => ChangeLayout(l => l.MenuBarVisible = !l.MenuBarVisible),
            "LayoutMenuBar", "LayoutMenuBarFill", "view.toggleMenuBar", "title name");
        Add(visibility, "Activity Bar", _sidebarVisible, ToggleActivityBar, right ? "LayoutActivityBarRight" : "LayoutActivityBarLeft",
            right ? "LayoutActivityBarRightFill" : "LayoutActivityBarLeftFill", "view.toggleActivityBar", "pages icons navigation sidebar");
        Add(visibility, "Primary Side Bar", PrimaryShown, TogglePrimarySideBar, right ? "LayoutSidebarRight" : "LayoutSidebarLeft",
            right ? "LayoutSidebarRightFill" : "LayoutSidebarLeftFill", "view.toggleSidebar", "sidebar source control view");
        Add(visibility, "Secondary Side Bar", _secondaryVisible, ToggleSecondarySideBar, right ? "LayoutSidebarLeft" : "LayoutSidebarRight",
            right ? "LayoutSidebarLeftFill" : "LayoutSidebarRightFill", "view.toggleSecondarySideBar", "auxiliary bar right");
        Add(visibility, "Panel", PanelOpen, () => SetPanelOpen(!PanelOpen), "LayoutPanel", "LayoutPanelFill", "panel.toggle", "output logs bottom");
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
        Set(quickInput, "Center", "center", layout.QuickInputPosition, (l, v) => l.QuickInputPosition = v, "LayoutFrame", "LayoutQuickInputCenterFill");

        const string density = "Layout Density";
        Set(density, "Default", "default", layout.Density, (l, v) => l.Density = v, "LayoutDefault");
        Set(density, "Compact", "compact", layout.Density, (l, v) => l.Density = v, "LayoutCompact");
        return items;
    }

    // ─── The gear's menu ────────────────────────────────────────────────────

    /// <summary>VS Code's Manage menu: the command palette, Settings, Keyboard Shortcuts, the themes, Customize Layout, Troubleshoot, updates.</summary>
    private void Manage_Click(object? sender, RoutedEventArgs e)
    {
        var themes = new MenuItem { Header = "Themes" };
        foreach (var (label, value) in Themes)
        {
            var theme = new MenuItem { Header = label, Icon = new TextBlock { Text = IsTheme(value) ? "✓" : "", Width = 12 } };
            theme.Click += (_, _) => SetTheme(value);
            themes.Items.Add(theme);
        }
        var menu = new ContextMenu
        {
            Placement = _layout.SidebarRight ? PlacementMode.LeftEdgeAlignedBottom : PlacementMode.RightEdgeAlignedBottom,
            HorizontalOffset = _layout.SidebarRight ? -6 : 6,
            Items =
            {
                MenuEntry("Command Palette…", "workbench.commandPalette", () => ShowPalette(commands: true)),
                new Separator(),
                MenuEntry("Settings", "settings.open", () => ShowModal("Settings")),
                MenuEntry("Keyboard Shortcuts", "settings.keyboard", () => OpenSettings("Keyboard")),
                themes,
                MenuEntry("Customize Layout…", "layout.customize", ShowCustomizeLayout),
                new Separator(),
                MenuEntry("Troubleshoot", "troubleshoot.open", () => ShowModal("Troubleshoot")),
                MenuEntry("Check for Updates…", "app.checkUpdates", () => _ = CheckForUpdatesAsync())
            }
        };
        menu.Open(ManageButton);
    }

    /// <summary>An entry of the gear's or Help's menu, with its command's shortcut on the right.</summary>
    private MenuItem MenuEntry(string header, string? command, Action run)
    {
        var item = new MenuItem { Header = header };
        if (command is not null && _commands.Find(command) is { } found && _commands.ShortcutOf(found) is { } keys)
            item.InputGesture = new KeyGesture(keys.Key, keys.Modifiers);
        item.Click += (_, _) => run();
        return item;
    }

    // ─── Themes ─────────────────────────────────────────────────────────────

    private static readonly (string Label, string Value)[] Themes = [("Dark", "dark"), ("Light", "light"), ("System", "system")];

    private bool IsTheme(string value) => _options.Theme.Equals(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The theme chosen from the gear's menu or the palette: applied at once and saved, as on Settings.</summary>
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
        App.ApplyTheme(value);
    }

    private void ShowThemePick() => Palette.ShowPick("Select a color theme",
        Themes.Select(t => new PaletteItem(t.Label, "", "", () => SetTheme(t.Value), "theme color") { IsChecked = IsTheme(t.Value) }).ToList());

    // ─── The command centre ─────────────────────────────────────────────────

    private void CommandCenter_Click(object? sender, RoutedEventArgs e) => ShowPalette(commands: false);

    /// <summary>
    /// Centred on the window, as VS Code's: 38 % of it, at most 600 - narrower where the name or the buttons beside it would
    /// be in the way, and gone when there's no room left for it.
    /// </summary>
    private void SizeCommandCenter()
    {
        var width = TitleBar.Bounds.Width;
        if (width <= 0) return;
        var center = width / 2;
        var room = 2 * Math.Min(center - TitleLeft.Margin.Left - TitleLeft.Bounds.Width, width - TitleRight.Bounds.Width - center) - 32;
        var fit = Math.Min(Math.Min(width * 0.38, 600), room);
        CommandCenter.IsVisible = fit >= 120;
        CommandCenter.Width = Math.Max(fit, 0);
    }
}
