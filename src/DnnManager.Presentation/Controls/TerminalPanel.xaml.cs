using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using DnnManager.Application.Configuration;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;
using DnnManager.Presentation.Terminal;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The panel at the bottom of the window, like VS Code's - three tabs that don't mix: <b>Output</b>, what DNN
/// Manager does (its operations, errors and warnings); <b>Logs</b>, a website's logs as they are written; and
/// <b>Terminal</b>, command-line shells (PowerShell, Command Prompt, Git Bash…), listed on the right. Ctrl+F searches
/// the shown tab; the header's maximize button gives the panel the window (MainWindow does that).
/// </summary>
public partial class TerminalPanel : UserControl
{
    /// <summary>A terminal in the list: its shell, which can be renamed.</summary>
    private sealed class Tab : INotifyPropertyChanged
    {
        private string _title = "";
        private string _editTitle = "";
        private bool _isRenaming;

        public string Title
        {
            get => _title;
            set { _title = value; Changed(nameof(Title)); }
        }

        /// <summary>Shown under the name when the mouse rests on the tab.</summary>
        public string Description { get; init; } = "";
        /// <summary>The facts under that: the process ID, program and folder.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; } = [];
        /// <summary>Which icon it has: "powershell", "cmd" or "bash" (ShellIcon).</summary>
        public string Icon { get; init; } = "powershell";
        /// <summary>Runs with administrator rights: marked in its name, and warned about above the terminal.</summary>
        public bool IsAdministrator { get; init; }
        public required TerminalSession Session { get; init; }
        public required TerminalView View { get; init; }

        /// <summary>The name as it is being typed while renaming.</summary>
        public string EditTitle
        {
            get => _editTitle;
            set { _editTitle = value; Changed(nameof(EditTitle)); }
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set { _isRenaming = value; Changed(nameof(IsRenaming)); Changed(nameof(IsNotRenaming)); }
        }

        public bool IsNotRenaming => !_isRenaming;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private enum Pane { Activity, Logs, Terminal }

    private readonly ObservableCollection<Tab> _tabs = [];
    private ActivityLog _log = null!;
    private TerminalService _service = null!;
    private EfficiencyMode _efficiency = null!;
    private Pane _pane = Pane.Activity;

    public TerminalPanel()
    {
        InitializeComponent();
        Tabs.ItemsSource = _tabs;
        _searchAgain = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => SearchAgain(), Dispatcher);
        _searchAgain.Stop();
    }

    /// <summary>The panel's own hide button was pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The maximize / restore button was pressed - the window gives the panel its room, or takes it back.</summary>
    public event EventHandler? MaximizeToggled;

    private bool _maximized;

    /// <summary>Whether the panel has the window - shown on its button: the corners pointing out (maximize) or in (restore).</summary>
    public bool IsMaximized
    {
        get => _maximized;
        set
        {
            _maximized = value;
            MaximizeIcon.Data = (Geometry)FindResource(value ? "PanelRestore" : "PanelMaximize");
            MaximizeButton.ToolTip = value ? "Restore panel" : "Maximize panel";
        }
    }

    internal void Attach(ActivityLog log, TerminalService service, ServerStore store, SiteLogCatalog logs, EfficiencyMode efficiency,
        AppOptions options)
    {
        _log = log; _service = service; _efficiency = efficiency;
        Pipeline.Attach(log, options);
        // An operation failed: a red dot on Output, also while another tab is shown - until Output is opened, or the
        // next operation starts.
        log.RunEnded += run =>
        {
            if (run.Status == RunStatus.Failed) OutputDot.Visibility = Visibility.Visible;
        };
        log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityLog.Latest) && log.Latest is null or { IsRunning: true })
                OutputDot.Visibility = Visibility.Collapsed;
        };
        Logs.Attach(store, logs);
        service.SettingsChanged += (_, _) => ApplySettings();
        ApplySettings();
        ShowPane(Pane.Activity);
    }

    /// <summary>The font, and whether shells can be opened, from the settings - now and whenever they change.</summary>
    private void ApplySettings()
    {
        var font = _service.Font;
        var size = _service.Settings.FontSize;
        Pipeline.SetFontSize(size);
        Logs.SetFont(font, size);
        foreach (var tab in _tabs) tab.View.SetFont(font, size);
        NewButton.ToolTip = "New terminal";
    }

    // ─── The three tabs ───────────────────────────────────────────────────

    private void PanelTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _log is null) return;
        ShowPane(sender == LogsTab ? Pane.Logs : sender == TerminalTab ? Pane.Terminal : Pane.Activity);
    }

    private void ShowPane(Pane pane)
    {
        // Checking the tab comes back here (PanelTab_Checked).
        var radio = pane switch { Pane.Logs => LogsTab, Pane.Terminal => TerminalTab, _ => OutputTab };
        if (radio.IsChecked != true)
        {
            radio.IsChecked = true;
            return;
        }
        var opened = pane == Pane.Activity && _pane != Pane.Activity;
        _pane = pane;
        Pipeline.Visibility = pane == Pane.Activity ? Visibility.Visible : Visibility.Collapsed;
        // Opened: the failure it pointed to is seen.
        if (opened) OutputDot.Visibility = Visibility.Collapsed;
        Logs.Visibility = pane == Pane.Logs ? Visibility.Visible : Visibility.Collapsed;
        TerminalPane.Visibility = TerminalList.Visibility = ListResizer.Visibility = TerminalTools.Visibility =
            pane == Pane.Terminal ? Visibility.Visible : Visibility.Collapsed;
        // Clearing is for the output only.
        ClearButton.Visibility = pane == Pane.Activity ? Visibility.Visible : Visibility.Collapsed;
        // Logs has its own bar at the top: the search sits under it.
        SearchBar.Margin = new Thickness(0, pane == Pane.Logs ? 46 : 6, 20, 0);

        // Like VS Code: the terminal tab with no shell open starts one.
        if (pane == Pane.Terminal && _tabs.Count == 0) NewTerminal();
        ShowTerminalState();
        if (pane == Pane.Terminal) FocusTerminal();
        else if (pane == Pane.Activity) Pipeline.Log.ScrollToEnd();
        if (SearchBar.Visibility == Visibility.Visible) Search(reveal: true);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the activity log - e.g. when the running operation is clicked in the status bar.</summary>
    public void ShowActivity() => ShowPane(Pane.Activity);

    /// <summary>Shows the Terminal tab with the keyboard in its terminal - a new one when none is open (Ctrl+`).</summary>
    public void ShowTerminal()
    {
        if (_pane == Pane.Terminal) FocusTerminal();
        else ShowPane(Pane.Terminal);
    }

    /// <summary>The Terminal tab is shown.</summary>
    public bool IsTerminalShown => _pane == Pane.Terminal;

    /// <summary>The next tab (<paramref name="by"/> 1) or the one before (-1) - Output, Logs, Terminal - round the end.</summary>
    public void StepPane(int by)
    {
        var panes = new[] { Pane.Activity, Pane.Logs, Pane.Terminal };
        var index = Array.IndexOf(panes, _pane);
        ShowPane(panes[((index + by) % panes.Length + panes.Length) % panes.Length]);
    }

    /// <summary>Shows <paramref name="site"/>'s log <paramref name="source"/> (its newest when null) on the Logs tab.</summary>
    internal void ShowLogs(ProjectRow site, SiteLogSource? source)
    {
        ShowPane(Pane.Logs);
        Logs.Show(site, source);
    }

    /// <summary>The panel was just shown: the log at its newest line, or the keyboard in the shown terminal.</summary>
    public void Opened()
    {
        if (_pane == Pane.Terminal) FocusTerminal();
        else if (_pane == Pane.Activity) Pipeline.Log.ScrollToEnd();
    }

    // ─── Terminals ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a terminal with <paramref name="shell"/> (the default one when null) in <paramref name="directory"/>
    /// (the projects folder when null) and shows it.
    /// </summary>
    /// <remarks>
    /// A terminal runs as the signed-in user, without DNN Manager's administrator rights - unless the shell is one of
    /// <see cref="TerminalService.AdministratorShells"/> (<see cref="NewAdministratorTerminal"/>). When it can't start
    /// without them, that is said, and nothing starts in its place.
    /// </remarks>
    public void NewTerminal(TerminalShell? shell = null, string? directory = null)
    {
        shell ??= _service.DefaultShell;
        if (shell.Refusal is { } refusal)
        {
            Dialogs.Error(refusal);
            return;
        }
        var inProject = directory is not null;
        directory ??= _service.WorkingDirectory;
        if (!Directory.Exists(directory)) directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // "PowerShell", "PowerShell (2)"… - or the folder's name for a terminal opened on a project; "Administrator: …"
        // for one with those rights, as Windows titles its windows.
        var shellName = shell.AsAdministrator ? $"Administrator: {shell.Name}" : shell.Name;
        var name = inProject ? $"{Path.GetFileName(directory.TrimEnd('\\'))} - {shellName}" : shellName;
        var title = name;
        for (var n = 2; _tabs.Any(t => t.Title == title); n++) title = $"{name} ({n})";

        TerminalSession session;
        try
        {
            session = new TerminalSession(shell.CommandLine, directory, 100, 24, shell.AsAdministrator);
        }
        catch (Infrastructure.Terminal.UnelevatedUnavailableException ex)
        {
            Dialogs.Error($"{ex.Message}{Environment.NewLine}{Environment.NewLine}Nothing was started with administrator rights in its place - " +
                          "for a terminal with them, choose New Administrator terminal (the arrow beside +).");
            return;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException or InvalidOperationException)
        {
            Dialogs.Error($"Could not start {shell.Name}: {ex.Message}");
            return;
        }

        var view = new TerminalView(session);
        view.SetFont(_service.Font, _service.Settings.FontSize);
        var details = new List<KeyValuePair<string, string>>
        {
            new("Terminal", shell.Name),
            new("Runs as", shell.AsAdministrator ? "Administrator - every command has DNN Manager's administrator rights" : "You, without administrator rights"),
            new("Process ID", session.ProcessId.ToString()),
            new("Program", shell.ExePath),
        };
        if (shell.Arguments.Length > 0) details.Add(new("Arguments", shell.Arguments));
        details.Add(new("Started in", directory));
        details.Add(new("Started", DateTime.Now.ToString("g")));
        var tab = new Tab
        {
            Title = title, Description = "Click again or Enter: type in it · Del: close it · F2 or double-click: rename · drag or Alt+↑/↓: move",
            Details = details, Session = session, View = view, Icon = IconOf(shell), IsAdministrator = shell.AsAdministrator
        };
        // The shell ended by itself ("exit"): its tab goes too.
        session.Exited += (_, _) => Close(tab);
        // A shell that prints keeps the app out of EcoQoS while minimized: its output is read at full speed.
        session.Output += (_, _) => _efficiency.NoteTerminalOutput();
        _tabs.Add(tab);
        Tabs.SelectedItem = tab;
        if (_pane != Pane.Terminal) ShowPane(Pane.Terminal);
        ShowTerminalState();
    }

    /// <summary>
    /// Opens a terminal with administrator rights - the shell the settings name when it may run so, otherwise Command
    /// Prompt - in <paramref name="directory"/> (the projects folder when null).
    /// </summary>
    public void NewAdministratorTerminal(string? directory = null) => NewTerminal(_service.DefaultAdministratorShell, directory);

    /// <summary>The icon of <paramref name="shell"/>: PowerShell (5 and 7), Command Prompt, or Bash.</summary>
    private static string IconOf(TerminalShell shell) => shell.Key switch
    {
        "cmd" => "cmd",
        "gitbash" => "bash",
        _ => "powershell"
    };

    private void Close(Tab tab)
    {
        if (!_tabs.Contains(tab)) return;
        var index = _tabs.IndexOf(tab);
        var wasShown = Tabs.SelectedItem == tab;
        _tabs.Remove(tab);
        tab.Session.Dispose();
        if (wasShown && _tabs.Count > 0) Tabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
        ShowTerminalState();
    }

    /// <summary>Ends every shell and stops following a log - when the window closes, or the terminal is switched off.</summary>
    public void CloseAll()
    {
        foreach (var tab in _tabs.ToList()) Close(tab);
    }

    /// <summary>Stops following the shown log - when the window closes.</summary>
    public void StopLogs() => Logs.Stop();

    /// <summary>Shells are open - closing the window ends what runs in them.</summary>
    public int OpenTerminals => _tabs.Count;

    private Tab? ShownTab => Tabs.SelectedItem as Tab;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A list always shows one terminal: clicking the shown one again doesn't unselect it.
        if (Tabs.SelectedItem is null && _tabs.Count > 0)
        {
            Tabs.SelectedItem = e.RemovedItems.OfType<Tab>().FirstOrDefault(_tabs.Contains) ?? _tabs[0];
            return;
        }
        if (TerminalHost.Content is TerminalView previous)
        {
            previous.ScrollChanged -= View_ScrollChanged;
            previous.ClearSearch();
        }
        TerminalHost.Content = ShownTab?.View;
        if (ShownTab?.View is { } view)
        {
            view.ScrollChanged += View_ScrollChanged;
            SyncScrollBar(view);
            // Chosen in the list (a click, the arrows): the keyboard stays there - Del closes it, Enter goes in. Opened
            // or shown otherwise: the keyboard goes into it.
            if (!Tabs.IsKeyboardFocusWithin) FocusTerminal();
        }
        ShowTerminalState();
        if (_pane == Pane.Terminal && SearchBar.Visibility == Visibility.Visible) Search(reveal: true);
    }

    private void ShowTerminalState()
    {
        var none = _tabs.Count == 0;
        NoTerminal.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        TerminalScroll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        // One line over an Administrator terminal: what is typed there runs with DNN Manager's rights.
        AdministratorWarning.Visibility = ShownTab is { IsAdministrator: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Puts the keyboard in the shown terminal, once it is laid out.</summary>
    public void FocusTerminal() =>
        Dispatcher.BeginInvoke(() => (TerminalHost.Content as TerminalView)?.Focus(), DispatcherPriority.Input);

    // The list of terminals between narrow (an icon and a few letters) and wide (long names).
    private const double ListMinWidth = 110, ListMaxWidth = 420;

    // Dragging its left edge: to the left makes it wider. Never more than leaves the shell some room.
    private void ListResizer_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var max = Math.Min(ListMaxWidth, Math.Max(ListMinWidth, ActualWidth - 260));
        TerminalList.Width = Math.Clamp(TerminalList.Width - e.HorizontalChange, ListMinWidth, max);
    }

    // ─── Buttons ──────────────────────────────────────────────────────────

    private void New_Click(object sender, RoutedEventArgs e) => NewTerminal();

    // The installed shells, the default one marked - as you; then the same as Administrator, those that may not run so
    // greyed with why.
    private void Shells_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShellsButton, Placement = PlacementMode.Bottom };
        foreach (var shell in _service.Shells)
            menu.Items.Add(ShellItem(shell, shell == _service.DefaultShell ? $"{shell.Name}  (default)" : shell.Name));
        menu.Items.Add(new Separator());
        var administrator = new MenuItem
        {
            Header = "New Administrator terminal",
            ToolTip = "Every command typed in it runs with DNN Manager's administrator rights - only for what needs them."
        };
        foreach (var shell in _service.AdministratorShells) administrator.Items.Add(ShellItem(shell, shell.Name));
        menu.Items.Add(administrator);
        menu.IsOpen = true;
    }

    private MenuItem ShellItem(TerminalShell shell, string header)
    {
        var item = new MenuItem
        {
            Header = header,
            ToolTip = shell.Refusal ?? shell.ExePath,
            IsEnabled = shell.Refusal is null,
            Icon = new ContentControl { Content = IconOf(shell), ContentTemplate = (DataTemplate)FindResource("ShellIcon"), Focusable = false }
        };
        // Greyed, it still says why - to the mouse and to a screen reader.
        ToolTipService.SetShowOnDisabled(item, true);
        if (shell.Refusal is not null) AutomationProperties.SetHelpText(item, shell.Refusal);
        item.Click += (_, _) => NewTerminal(shell);
        return item;
    }


    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Maximize_Click(object sender, RoutedEventArgs e) => MaximizeToggled?.Invoke(this, EventArgs.Empty);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;
        var inList = Tabs.IsKeyboardFocusWithin;
        Close(tab);
        // As with Del: the keyboard stays in the list, on the terminal shown now.
        if (inList) FocusShownTab();
    }

    /// <summary>The terminal a button in its row, or an item of its right-click menu, belongs to.</summary>
    private static Tab? TabOf(object sender) => sender switch
    {
        MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: Tab tab } } } => tab,
        FrameworkElement { DataContext: Tab tab } => tab,
        _ => null
    };

    private void Clear_Click(object sender, RoutedEventArgs e) => _log.Clear();

    // ─── Renaming a terminal ──────────────────────────────────────────────

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) BeginRename(tab);
    }

    // A double-click on a terminal's name.
    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || TabOf(sender) is not { } tab) return;
        BeginRename(tab);
        e.Handled = true;
    }

    // In the list: F2 renames the selected terminal, Del closes it, Enter puts the keyboard in it, Alt+Up / Alt+Down move
    // it up or down the list. (Not while its name is being typed - those keys are the rename box's then.)
    private void Tabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ShownTab is not { IsRenaming: false } tab) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            MoveTab(tab, _tabs.IndexOf(tab) + (key == Key.Up ? -1 : 1));
            FocusShownTab();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (key)
        {
            case Key.F2:
                BeginRename(tab);
                break;
            case Key.Delete:
                Close(tab);
                FocusShownTab();
                break;
            case Key.Enter:
                FocusTerminal();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Moves <paramref name="tab"/> to <paramref name="index"/> in the list (kept within it), still the one shown.</summary>
    private void MoveTab(Tab tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        index = Math.Clamp(index, 0, _tabs.Count - 1);
        if (from < 0 || from == index) return;
        _tabs.Move(from, index);
        Tabs.SelectedItem = tab;
    }

    /// <summary>
    /// A click on a terminal in the list puts the keyboard there - on that entry - so Del, F2 and Enter act on it at
    /// once; a second click on it (the keyboard already there) goes into the terminal. Holding the button and moving
    /// drags it to another place in the list. (Not on its rename box or bin: they take the click themselves.)
    /// </summary>
    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: Tab tab } item || e.OriginalSource is not DependencyObject source ||
            IsInside<TextBox>(source, item) || IsInside<ButtonBase>(source, item)) return;
        _dragging = tab;
        _dragFrom = e.GetPosition(Tabs);
        if (e.ClickCount == 1 && item.IsSelected && item.IsKeyboardFocused) FocusTerminal();
        else item.Focus();
    }

    /// <summary>A click in the list away from the terminals: the keyboard on the one shown.</summary>
    private void Tabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || IsInside<ListBoxItem>(source, Tabs) || IsInside<ScrollBar>(source, Tabs)) return;
        FocusShownTab();
        e.Handled = true;
    }

    // ─── Dragging a terminal to another place in the list ─────────────────

    private Tab? _dragging;
    private Point _dragFrom;

    // Moved far enough with the button held: the drag starts (Windows' drag distance, so a click isn't one).
    private void Tabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is not { IsRenaming: false } tab || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(Tabs) - _dragFrom;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = null;
        DragDrop.DoDragDrop(Tabs, new DataObject(typeof(Tab), tab), DragDropEffects.Move);
    }

    private void Tabs_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragging = null;

    // Over another terminal: the dragged one takes its place at once - the list shows where it will be.
    private void Tabs_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(Tab)) is not Tab dragged)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (Tabs.InputHitTest(e.GetPosition(Tabs)) is DependencyObject over && ItemOf(over) is { DataContext: Tab target } && target != dragged)
            MoveTab(dragged, _tabs.IndexOf(target));
    }

    private void Tabs_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        FocusShownTab();
    }

    private ListBoxItem? ItemOf(DependencyObject element)
    {
        for (var current = element; current is not null && current != Tabs; current = VisualTreeHelper.GetParent(current))
            if (current is ListBoxItem item) return item;
        return null;
    }

    private static bool IsInside<T>(DependencyObject element, DependencyObject stop) where T : DependencyObject
    {
        for (var current = element; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
            if (current is T) return true;
        return false;
    }

    /// <summary>The keyboard on the list's selected terminal - after one was closed with Del, so the next Del closes the next.</summary>
    private void FocusShownTab() => Dispatcher.BeginInvoke(() =>
    {
        if (ShownTab is { } shown && Tabs.ItemContainerGenerator.ContainerFromItem(shown) is ListBoxItem item) item.Focus();
        else Focus();
    }, DispatcherPriority.Input);

    private static void BeginRename(Tab tab)
    {
        tab.EditTitle = tab.Title;
        tab.IsRenaming = true;
    }

    /// <summary>Takes the typed name (an empty one keeps the old name), or drops it.</summary>
    /// <param name="backToList">Ended with Enter or Esc: the keyboard goes back to the list, on the renamed terminal (a
    /// second click or Enter goes into it). Ended by a click elsewhere: it stays where it went.</param>
    private void EndRename(Tab tab, bool keep, bool backToList)
    {
        if (!tab.IsRenaming) return;
        tab.IsRenaming = false;
        if (keep && tab.EditTitle.Trim() is { Length: > 0 } name) tab.Title = name;
        if (backToList && Tabs.SelectedItem == tab) FocusShownTab();
    }

    // The box appears: put the keyboard in it, with the old name selected.
    private void Rename_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void Rename_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || TabOf(sender) is not { } tab) return;
        EndRename(tab, keep: e.Key == Key.Enter, backToList: true);
        e.Handled = true;
    }

    private void Rename_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TabOf(sender) is { } tab) EndRename(tab, keep: true, backToList: false);
    }

    // ─── Kept between starts ──────────────────────────────────────────────

    /// <summary>The panel's tab or search changed - what the workspace keeps. (Terminals aren't kept: their shells end with DNN Manager.)</summary>
    public event EventHandler? WorkspaceChanged;

    /// <summary>The width of the terminal list - kept with the window.</summary>
    public double TerminalListWidth
    {
        get => TerminalList.Width;
        set => TerminalList.Width = Math.Clamp(value, ListMinWidth, ListMaxWidth);
    }

    internal LogsState CaptureLogs() => new()
    {
        Pane = _pane switch { Pane.Logs => "Logs", Pane.Terminal => "Terminal", _ => "Output" },
        LogSite = Logs.CurrentSite, LogGroup = Logs.CurrentSource?.Group, LogTitle = Logs.CurrentSource?.Title,
        SearchOpen = SearchBar.Visibility == Visibility.Visible, SearchText = SearchBox.Text.Length > 0 ? SearchBox.Text : null,
        MatchCase = MatchCase.IsChecked == true, WholeWord = WholeWord.IsChecked == true, UseRegex = UseRegex.IsChecked == true
    };

    /// <summary>The tab shown and the search, as they were. The Logs tab's log follows once the sites are read (<see cref="ShowLog"/>).</summary>
    internal void RestoreLogs(LogsState state)
    {
        SearchBox.Text = state.SearchText ?? "";
        MatchCase.IsChecked = state.MatchCase;
        WholeWord.IsChecked = state.WholeWord;
        UseRegex.IsChecked = state.UseRegex;
        // The Terminal tab only with a terminal open (never after a start: they aren't kept) - it doesn't start a shell by itself.
        var pane = state.Pane switch { "Logs" => Pane.Logs, "Terminal" when _tabs.Count > 0 => Pane.Terminal, _ => Pane.Activity };
        ShowPane(pane);
        if (state.SearchOpen) SearchBar.Visibility = Visibility.Visible;
    }

    /// <summary>The Logs tab on <paramref name="site"/>'s log of kind <paramref name="group"/> named <paramref name="title"/> - without switching to it.</summary>
    internal void ShowLog(ProjectRow site, string? group, string? title) => Logs.Show(site, group, title);

    /// <summary>The Logs tab on DNN Manager's own log named <paramref name="title"/> (its newest when null); shown when <paramref name="switchTo"/>.</summary>
    internal void ShowAppLog(string? title, bool switchTo)
    {
        if (switchTo) ShowPane(Pane.Logs);
        Logs.ShowApp(title);
    }

    // ─── Search (Ctrl+F) ──────────────────────────────────────────────────

    // Looks again a moment after the text changed (new output keeps coming), not at every line.
    private readonly DispatcherTimer _searchAgain;
    private ISearchTarget? _searched;
    private int _matchCount, _matchIndex = -1;

    private ISearchTarget? Target => _pane switch
    {
        Pane.Activity => Pipeline.Log,
        Pane.Logs => Logs,
        _ => TerminalHost.Content as TerminalView
    };

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
        }
        else if (key == Key.F3 && SearchBar.Visibility == Visibility.Visible)
        {
            Step(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
        // Alt+C / Alt+W / Alt+R switch the search's options while it is open, as in VS Code.
        else if (Keyboard.Modifiers == ModifierKeys.Alt && SearchBar.Visibility == Visibility.Visible &&
                 (key switch { Key.C => MatchCase, Key.W => WholeWord, Key.R => UseRegex, _ => null }) is { } option)
        {
            option.IsChecked = option.IsChecked != true;
            Search(reveal: true);
            e.Handled = true;
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => OpenSearch();

    /// <summary>Shows the search bar with the keyboard in it - what was typed before still there, selected.</summary>
    public void OpenSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
        Search(reveal: true);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Search(reveal: true);

    private void SearchOption_Click(object sender, RoutedEventArgs e)
    {
        Search(reveal: true);
        SearchBox.Focus();
    }

    /// <summary>The search box's text with the options as they are switched.</summary>
    private SearchQuery Query() =>
        new(SearchBox.Text, MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true);

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Step(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
        }
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e) => Step(1);
    private void SearchPrevious_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();

    /// <summary>
    /// Takes the highlights away - the text itself stays as it was - and gives the keyboard back to the terminal, unless
    /// <paramref name="focus"/> is false (the panel is being hidden).
    /// </summary>
    public void CloseSearch(bool focus = true)
    {
        if (SearchBar.Visibility != Visibility.Visible) return;
        SearchBar.Visibility = Visibility.Collapsed;
        Detach();
        if (focus && _pane == Pane.Terminal) FocusTerminal();
    }

    private void Detach()
    {
        _searchAgain.Stop();
        if (_searched is null) return;
        _searched.ContentChanged -= Target_ContentChanged;
        _searched.ClearSearch();
        _searched = null;
    }

    /// <summary>
    /// Finds what is typed in the shown tab - starting at the newest match, as the newest output is what is looked
    /// at - and follows that tab's text as it grows.
    /// </summary>
    private void Search(bool reveal)
    {
        var target = Target;
        if (!ReferenceEquals(target, _searched))
        {
            Detach();
            _searched = target;
            if (target is not null) target.ContentChanged += Target_ContentChanged;
        }
        _query = Query();
        _query.BeginPass();
        _matchCount = target?.Find(_query) ?? 0;
        _matchIndex = _matchCount - 1;
        if (_matchIndex >= 0) target!.ShowMatch(_matchIndex, reveal);
        ShowCount();
    }

    private void Target_ContentChanged(object? sender, EventArgs e)
    {
        _searchAgain.Stop();
        _searchAgain.Start();
    }

    // New text arrived: the matches are counted again, the current one kept - without jumping to it.
    private void SearchAgain()
    {
        _searchAgain.Stop();
        if (_searched is null || SearchBar.Visibility != Visibility.Visible) return;
        var index = _matchIndex;
        var took = System.Diagnostics.Stopwatch.StartNew();
        _query.BeginPass();
        _matchCount = _searched.Find(_query);
        // A search that takes long waits longer before it runs again: a busy log mustn't keep the UI thread searching.
        _searchAgain.Interval = took.Elapsed > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(300);
        _matchIndex = _matchCount == 0 ? -1 : Math.Clamp(index < 0 ? _matchCount - 1 : index, 0, _matchCount - 1);
        if (_matchIndex >= 0) _searched.ShowMatch(_matchIndex, reveal: false);
        ShowCount();
    }

    private void Step(int by)
    {
        if (_searched is null || _matchCount == 0) return;
        _matchIndex = ((_matchIndex + by) % _matchCount + _matchCount) % _matchCount;
        _searched.ShowMatch(_matchIndex);
        ShowCount();
    }

    // The query last searched for - looked for again as new text arrives.
    private SearchQuery _query = new("");

    private void ShowCount()
    {
        // A regular expression that isn't one: said, in red, with what is wrong with it.
        SearchCount.Text = _query.Error is not null ? "Invalid regular expression" : SearchBox.Text.Length == 0 ? ""
            : (_matchCount == 0 ? "No results" : $"{_matchIndex + 1} / {_matchCount}") + (_query.Stopped ? " (stopped)" : "");
        SearchCount.SetResourceReference(TextBlock.ForegroundProperty, _query.Error is null ? "TextMuted" : "ErrorText");
        SearchCount.ToolTip = _query.Error ?? (_query.Stopped
            ? "The search took too long and stopped - the matches after where it stopped aren't counted. A simpler pattern finds them all."
            : null);
    }

    // ─── Scrolling ────────────────────────────────────────────────────────

    // Set while the scrollbar is being given the view's position, so that isn't taken for the user dragging it.
    private bool _syncingScroll;

    private void View_ScrollChanged(object? sender, EventArgs e)
    {
        if (sender is TerminalView view && ReferenceEquals(view, TerminalHost.Content)) SyncScrollBar(view);
    }

    /// <summary>The scrollbar's range is the lines that scrolled off the top; its thumb is as long as the rows shown.</summary>
    private void SyncScrollBar(TerminalView view)
    {
        _syncingScroll = true;
        TerminalScroll.Maximum = view.ScrollMaximum;
        TerminalScroll.ViewportSize = view.VisibleRows;
        TerminalScroll.LargeChange = Math.Max(1, view.VisibleRows - 1);
        TerminalScroll.Value = view.ScrollPosition;
        // Nothing to scroll through yet: shown, but not usable.
        TerminalScroll.IsEnabled = view.ScrollMaximum > 0;
        _syncingScroll = false;
    }

    private void TerminalScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingScroll && TerminalHost.Content is TerminalView view) view.ScrollTo((int)Math.Round(e.NewValue));
    }

}