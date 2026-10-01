using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    private readonly ObservableCollection<Tab> _tabs = new();
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

    // The maximize button's corners: pointing out (maximize), pointing in (restore).
    private static readonly Geometry MaximizeGlyph = Geometry.Parse("M1,4.5 V1 H4.5 M7.5,1 H11 V4.5 M11,7.5 V11 H7.5 M4.5,11 H1 V7.5");
    private static readonly Geometry RestoreGlyph = Geometry.Parse("M4.5,1 V4.5 H1 M7.5,1 V4.5 H11 M11,7.5 H7.5 V11 M4.5,11 V7.5 H1");

    /// <summary>Whether the panel has the window - shown on its button.</summary>
    public bool IsMaximized
    {
        get => MaximizeIcon.Data == RestoreGlyph;
        set
        {
            MaximizeIcon.Data = value ? RestoreGlyph : MaximizeGlyph;
            MaximizeButton.ToolTip = value ? "Restore the panel (Ctrl+Shift+M)" : "Maximize the panel (Ctrl+Shift+M)";
        }
    }

    internal void Attach(ActivityLog log, TerminalService service, ServerStore store, SiteLogCatalog logs, EfficiencyMode efficiency)
    {
        _log = log; _service = service; _efficiency = efficiency;
        LogList.Attach(log.Entries);
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
        LogList.FontFamily = font;
        LogList.FontSize = size;
        Logs.SetFont(font, size);
        foreach (var tab in _tabs) tab.View.SetFont(font, size);

        var enabled = _service.Settings.Enabled;
        TerminalTab.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        NewButton.ToolTip = $"New terminal ({_service.DefaultShell.Name})";
        if (enabled) return;
        CloseAll();
        if (_pane == Pane.Terminal) OutputTab.IsChecked = true;
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
        _pane = pane;
        LogList.Visibility = pane == Pane.Activity ? Visibility.Visible : Visibility.Collapsed;
        Logs.Visibility = pane == Pane.Logs ? Visibility.Visible : Visibility.Collapsed;
        TerminalPane.Visibility = TerminalList.Visibility = ListResizer.Visibility = TerminalTools.Visibility =
            pane == Pane.Terminal ? Visibility.Visible : Visibility.Collapsed;
        // The strip: copy on every tab, paste for a terminal, clear for the output.
        PasteButton.Visibility = pane == Pane.Terminal ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = pane == Pane.Activity ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.ToolTip = pane == Pane.Terminal ? "Copy the selected text (Ctrl+C, or right-click)"
            : pane == Pane.Logs ? "Copy the selected text (Ctrl+C, or right-click)" : "Copy the selected text, or the whole output when nothing is selected";
        // Logs has its own bar at the top: the search sits under it.
        SearchBar.Margin = new Thickness(0, pane == Pane.Logs ? 46 : 6, 20, 0);

        // Like VS Code: the terminal tab with no shell open starts one.
        if (pane == Pane.Terminal && _tabs.Count == 0 && _service.Settings.Enabled) NewTerminal();
        ShowTerminalState();
        if (pane == Pane.Terminal) FocusTerminal();
        else if (pane == Pane.Activity) LogList.ScrollToEnd();
        if (SearchBar.Visibility == Visibility.Visible) Search(reveal: true);
    }

    /// <summary>Shows the activity log - e.g. when the running operation is clicked in the status bar.</summary>
    public void ShowActivity() => ShowPane(Pane.Activity);

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
        else if (_pane == Pane.Activity) LogList.ScrollToEnd();
    }

    // ─── Terminals ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a terminal with <paramref name="shell"/> (the default one when null) in <paramref name="directory"/>
    /// (the projects folder when null) and shows it.
    /// </summary>
    public void NewTerminal(TerminalShell? shell = null, string? directory = null)
    {
        if (!_service.Settings.Enabled) return;
        shell ??= _service.DefaultShell;
        var inProject = directory is not null;
        directory ??= _service.WorkingDirectory;
        if (!Directory.Exists(directory)) directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // "PowerShell", "PowerShell (2)"… - or the folder's name for a terminal opened on a project.
        var name = inProject ? $"{Path.GetFileName(directory.TrimEnd('\\'))} - {shell.Name}" : shell.Name;
        var title = name;
        for (var n = 2; _tabs.Any(t => t.Title == title); n++) title = $"{name} ({n})";

        TerminalSession session;
        try
        {
            session = new TerminalSession(shell.CommandLine, directory, 100, 24);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException)
        {
            Dialogs.Error($"Could not start {shell.Name}: {ex.Message}");
            return;
        }

        var view = new TerminalView(session);
        view.SetFont(_service.Font, _service.Settings.FontSize);
        var details = new List<KeyValuePair<string, string>>
        {
            new("Terminal", shell.Name),
            new("Process ID", session.ProcessId.ToString()),
            new("Program", shell.ExePath),
        };
        if (shell.Arguments.Length > 0) details.Add(new("Arguments", shell.Arguments));
        details.Add(new("Started in", directory));
        details.Add(new("Started", DateTime.Now.ToString("g")));
        var tab = new Tab { Title = title, Description = "Double-click to rename.", Details = details, Session = session, View = view, Icon = IconOf(shell) };
        // The shell ended by itself ("exit"): its tab goes too.
        session.Exited += (_, _) => Close(tab);
        // A shell that prints keeps the app out of EcoQoS while minimized: its output is read at full speed.
        session.Output += (_, _) => _efficiency.NoteTerminalOutput();
        _tabs.Add(tab);
        Tabs.SelectedItem = tab;
        if (_pane != Pane.Terminal) ShowPane(Pane.Terminal);
        ShowTerminalState();
    }

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
            FocusTerminal();
        }
        ShowTerminalState();
        if (_pane == Pane.Terminal && SearchBar.Visibility == Visibility.Visible) Search(reveal: true);
    }

    private void ShowTerminalState()
    {
        var none = _tabs.Count == 0;
        NoTerminal.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        TerminalScroll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        PasteButton.IsEnabled = !none;
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

    // The installed shells, the default one marked.
    private void Shells_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShellsButton, Placement = PlacementMode.Bottom };
        foreach (var shell in _service.Shells)
        {
            var item = new MenuItem
            {
                Header = shell == _service.DefaultShell ? $"{shell.Name}  (default)" : shell.Name,
                ToolTip = shell.ExePath,
                Icon = new ContentControl { Content = IconOf(shell), ContentTemplate = (DataTemplate)FindResource("ShellIcon"), Focusable = false }
            };
            item.Click += (_, _) => NewTerminal(shell);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }


    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Maximize_Click(object sender, RoutedEventArgs e) => MaximizeToggled?.Invoke(this, EventArgs.Empty);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Close(tab);
    }

    /// <summary>The terminal a button in its row, or an item of its right-click menu, belongs to.</summary>
    private static Tab? TabOf(object sender) => sender switch
    {
        MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: Tab tab } } } => tab,
        FrameworkElement { DataContext: Tab tab } => tab,
        _ => null
    };

    // Activity: the selected part of the log, or - with nothing selected - the whole log with timestamps. Logs: the
    // selected line. Terminal: the selected text.
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        switch (_pane)
        {
            case Pane.Terminal:
                (TerminalHost.Content as TerminalView)?.Copy();
                FocusTerminal();
                break;
            case Pane.Logs:
                Logs.Copy();
                break;
            default:
                var text = LogList.Selection.IsEmpty ? _log.ToText() : LogList.Selection.Text.TrimEnd();
                if (text.Length > 0) Clipboard.SetText(text);
                break;
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        (TerminalHost.Content as TerminalView)?.Paste();
        FocusTerminal();
    }

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

    // F2 on the selected terminal.
    private void Tabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || ShownTab is not { IsRenaming: false } tab) return;
        BeginRename(tab);
        e.Handled = true;
    }

    private static void BeginRename(Tab tab)
    {
        tab.EditTitle = tab.Title;
        tab.IsRenaming = true;
    }

    /// <summary>Takes the typed name (an empty one keeps the old name), or drops it.</summary>
    private void EndRename(Tab tab, bool keep)
    {
        if (!tab.IsRenaming) return;
        tab.IsRenaming = false;
        if (keep && tab.EditTitle.Trim() is { Length: > 0 } name) tab.Title = name;
        if (Tabs.SelectedItem == tab) FocusTerminal();
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
        EndRename(tab, keep: e.Key == Key.Enter);
        e.Handled = true;
    }

    private void Rename_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TabOf(sender) is { } tab) EndRename(tab, keep: true);
    }

    // ─── Search (Ctrl+F) ──────────────────────────────────────────────────

    // Looks again a moment after the text changed (new output keeps coming), not at every line.
    private readonly DispatcherTimer _searchAgain;
    private ISearchTarget? _searched;
    private int _matchCount, _matchIndex = -1;

    private ISearchTarget? Target => _pane switch
    {
        Pane.Activity => LogList,
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

    /// <summary>Takes the highlights away - the text itself stays as it was.</summary>
    public void CloseSearch()
    {
        SearchBar.Visibility = Visibility.Collapsed;
        Detach();
        if (_pane == Pane.Terminal) FocusTerminal();
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
        _matchCount = _searched.Find(_query);
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
            : _matchCount == 0 ? "No results" : $"{_matchIndex + 1} / {_matchCount}";
        SearchCount.SetResourceReference(TextBlock.ForegroundProperty, _query.Error is null ? "TextMuted" : "ErrorText");
        SearchCount.ToolTip = _query.Error;
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

    // The newest line of whatever is shown: the terminal's prompt, the log's end, or the end of the activity log.
    private void ScrollToBottom_Click(object sender, RoutedEventArgs e)
    {
        switch (_pane)
        {
            case Pane.Terminal:
                (TerminalHost.Content as TerminalView)?.ScrollToBottom();
                FocusTerminal();
                break;
            case Pane.Logs:
                Logs.ScrollToEnd();
                break;
            default:
                LogList.ScrollToEnd();
                break;
        }
    }
}