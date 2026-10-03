using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    // As saved in settings.json (layout) - changed by Customize Layout, applied and saved at once.
    private LayoutSettings _layout;
    // Whether the sidebar is shown - the workspace's, like the panel's (state\window.json).
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
        Sidebar.BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);

        // The panel under the page only (the sidebar beside it, full height), or under the sidebar too.
        var under = layout.PanelUnderSidebar;
        Grid.SetRowSpan(Sidebar, under ? 1 : 3);
        Grid.SetColumn(PageHost, page);
        foreach (var part in new FrameworkElement[] { LogSplitter, TerminalPanel })
        {
            Grid.SetColumn(part, under ? 0 : page);
            Grid.SetColumnSpan(part, under ? 2 : 1);
        }

        DockPanel.SetDock(IisStatus, right ? Dock.Right : Dock.Left);
        IisStatus.SetSide(right);
        StatusRow.Visibility = layout.StatusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        // Compact: everything of the frame smaller, in width and height - the sidebar and its entries, the title bar and its
        // buttons, the status bar.
        var compact = layout.Compact;
        var titleBar = compact ? 30.0 : 36.0;
        Resources["TitleBarHeight"] = titleBar;
        Resources["NavPadding"] = compact ? new Thickness(9, 5, 9, 5) : new Thickness(13, 9, 13, 9);
        Resources["NavMargin"] = compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 1, 8, 1);
        // The 30 px icon in the middle of the icons-only sidebar: 4 + 1 + 30 + 1 + 4 = 40, or 6 + 3 + 30 + 3 + 6 = 48.
        Resources["NavIconPadding"] = compact ? new Thickness(1, 5, 1, 5) : new Thickness(3, 9, 3, 9);
        Resources["NavIconMargin"] = compact ? new Thickness(4, 1, 4, 1) : new Thickness(6, 1, 6, 1);
        Resources["CaptionButtonWidth"] = compact ? 40.0 : 46.0;
        Resources["LayoutButtonWidth"] = compact ? 24.0 : 28.0;
        Resources["LayoutButtonHeight"] = compact ? 20.0 : 24.0;
        Resources["StatusBarHeight"] = compact ? 24.0 : 30.0;
        // The sidebar to its width in this density.
        SlideSidebar(IsCompact);
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

    /// <summary>The sidebar's column as wide as the sidebar (nothing while it's hidden), and the IIS cell under it.</summary>
    private void ApplySidebarWidth()
    {
        var width = (double)GetValue(SidebarWidthProperty);
        SidebarColumn.Width = new GridLength(_sidebarVisible ? width : 0);
        // With the sidebar hidden, the IIS cell keeps the icons-only sidebar's width - its dot and menu.
        IisStatus.Width = _sidebarVisible ? width : CompactSidebarWidth;
        IisStatus.IsCompact = IsCompact || !_sidebarVisible;
    }

    /// <summary>The title bar's sidebar and panel buttons: drawn filled in while that part is shown.</summary>
    private void UpdateLayoutButtons()
    {
        SidebarIconFill.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Hidden;
        SidebarIcon.RenderTransform = _layout.SidebarRight ? new ScaleTransform(-1, 1) : Transform.Identity;
        SidebarToggle.ToolTip = _sidebarVisible ? "Hide sidebar" : "Show sidebar";
        PanelIconFill.Visibility = LogOpen ? Visibility.Visible : Visibility.Hidden;
        PanelToggle.ToolTip = LogOpen ? "Hide panel" : "Show panel";
    }

    /// <summary>A command's shortcut as shown ("Ctrl+B"), or "".</summary>
    private string ShortcutText(string commandId) =>
        _commands.Find(commandId) is { } command && _commands.ShortcutOf(command) is { } shortcut ? shortcut.ToString() : "";

    private void SetSidebarVisible(bool visible)
    {
        if (visible == _sidebarVisible) return;
        var hadKeyboard = Sidebar.IsKeyboardFocusWithin;
        _sidebarVisible = visible;
        ApplyLayout();
        if (hadKeyboard) FocusPage();
        _workspace.Changed();
    }

    /// <summary>Ctrl+B: the sidebar shown or hidden.</summary>
    private void ToggleSidebar() => SetSidebarVisible(!_sidebarVisible);

    /// <summary>Changes the layout: applied at once and saved in settings.json - applied even when it can't be saved.</summary>
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
        ChangeLayout(l =>
        {
            var defaults = new LayoutSettings();
            l.SidebarPosition = defaults.SidebarPosition;
            l.PanelAlignment = defaults.PanelAlignment;
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
        void Add(string group, string title, bool chosen, Action run, string lines, string fill = "", string? command = null,
            string keywords = "") =>
            items.Add(new PaletteItem(title, "", command is null ? "" : ShortcutText(command), run, $"{group} {keywords}")
            {
                Group = group, IsChecked = chosen, KeepOpen = true,
                Icon = Geometry.Parse(IconFrame + lines), IconFill = fill.Length > 0 ? Geometry.Parse(fill) : null
            });
        void Set(string group, string title, string value, string current, Action<LayoutSettings, string> set, string lines, string fill = "") =>
            Add(group, title, current.Equals(value, StringComparison.OrdinalIgnoreCase), () => ChangeLayout(l => set(l, value)), lines, fill);

        const string visibility = "Visibility";
        Add(visibility, "Sidebar", _sidebarVisible, ToggleSidebar, right ? RightColumnLine : LeftColumnLine,
            right ? RightColumnFill : LeftColumnFill, "view.toggleSidebar", "primary side bar");
        // Shown or hidden here, the keyboard stays in the list.
        Add(visibility, "Panel", LogOpen, () => SetLogOpen(!LogOpen, takeKeyboard: false), " M1.5,9.5 H14.5", "M1.5,9.5 H14.5 V13.5 H1.5 Z",
            "panel.toggle", "output logs terminal bottom");
        Add(visibility, "Status Bar", layout.StatusBarVisible, () => ChangeLayout(l => l.StatusBarVisible = !l.StatusBarVisible),
            " M1.5,11.5 H14.5", "M1.5,11.5 H14.5 V13.5 H1.5 Z", "view.toggleStatusBar");

        const string position = "Sidebar Position";
        Set(position, "Left", "left", layout.SidebarPosition, (l, v) => l.SidebarPosition = v, LeftColumnLine, LeftColumnFill);
        Set(position, "Right", "right", layout.SidebarPosition, (l, v) => l.SidebarPosition = v, RightColumnLine, RightColumnFill);

        const string alignment = "Panel Alignment";
        Set(alignment, "Left", "left", layout.PanelAlignment, (l, v) => l.PanelAlignment = v,
            " M1.5,9.5 H10 M10,2.5 V13.5", "M1.5,9.5 H10 V13.5 H1.5 Z");
        Set(alignment, "Right", "right", layout.PanelAlignment, (l, v) => l.PanelAlignment = v,
            " M6,2.5 V13.5 M6,9.5 H14.5", "M6,9.5 H14.5 V13.5 H6 Z");
        Set(alignment, "Center", "center", layout.PanelAlignment, (l, v) => l.PanelAlignment = v,
            " M5,2.5 V13.5 M11,2.5 V13.5 M5,9.5 H11", "M5,9.5 H11 V13.5 H5 Z");
        Set(alignment, "Justify", "justify", layout.PanelAlignment, (l, v) => l.PanelAlignment = v,
            " M1.5,9.5 H14.5 M5,2.5 V9.5 M11,2.5 V9.5", "M1.5,9.5 H14.5 V13.5 H1.5 Z");

        const string quickInput = "Quick Input Position";
        Set(quickInput, "Top", "top", layout.QuickInputPosition, (l, v) => l.QuickInputPosition = v, "", "M4,4 H12 V6 H4 Z");
        Set(quickInput, "Center", "center", layout.QuickInputPosition, (l, v) => l.QuickInputPosition = v, "", "M4,7 H12 V9 H4 Z");

        const string density = "Layout Density";
        Set(density, "Default", "default", layout.Density, (l, v) => l.Density = v, " M4,6.5 H12 M4,9.5 H12");
        Set(density, "Compact", "compact", layout.Density, (l, v) => l.Density = v, " M4,5.5 H12 M4,8 H12 M4,10.5 H12");
        return items;
    }

    // The icons' window frame, and a sidebar's column on either side - in a 16 × 16 box.
    private const string IconFrame = "M1.5,2.5 H14.5 V13.5 H1.5 Z";
    private const string LeftColumnLine = " M6,2.5 V13.5", LeftColumnFill = "M1.5,2.5 H6 V13.5 H1.5 Z";
    private const string RightColumnLine = " M10,2.5 V13.5", RightColumnFill = "M10,2.5 H14.5 V13.5 H10 Z";

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
        MenuItem Item(string header, string? command, Action run)
        {
            var item = new MenuItem { Header = header, InputGestureText = command is null ? "" : ShortcutText(command) };
            item.Click += (_, _) => run();
            return item;
        }

        menu.Items.Add(Item("Command Palette…", "workbench.commandPalette", () => ShowPalette(commands: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings", "settings.open", () => ShowModal("Settings")));
        menu.Items.Add(Item("Keyboard Shortcuts", "settings.keyboard", () => OpenSettings("Keyboard")));
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
                    Text = current ? "" : "", Width = 12, FontSize = 12, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                }
            };
            theme.Click += (_, _) => SetTheme(value);
            themes.Items.Add(theme);
        }
        menu.Items.Add(themes);
        menu.Items.Add(Item("Customize Layout…", "layout.customize", ShowCustomizeLayout));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Troubleshoot", "troubleshoot.open", () => ShowModal("Troubleshoot")));
        menu.Items.Add(Item("Check for Updates…", "app.checkUpdates", () => _ = CheckForUpdatesAsync()));
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
