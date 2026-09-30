using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DnnManager.Presentation.Services;
using DnnManager.Presentation.Terminal;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The panel at the bottom of the window: the <b>Activity</b> tab - the log of what DNN Manager does, always there -
/// and any number of real terminals (PowerShell, Command Prompt, Git Bash…), opened with + or from the dropdown next
/// to it and listed on the right. With the terminal switched off in the settings only Activity is left.
/// </summary>
public partial class TerminalPanel : UserControl
{
    /// <summary>A tab in the list: Activity (no session), or a shell - which can be renamed.</summary>
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
        /// <summary>The facts under that: a terminal's process ID, program and folder.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; } = [];
        public TerminalSession? Session { get; init; }
        public TerminalView? View { get; init; }
        public bool CanClose => Session is not null;

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

    private readonly ObservableCollection<Tab> _tabs = new();
    private readonly Tab _activity = new()
    {
        Title = "Activity",
        Description = "What DNN Manager does: each step of the running operation."
    };
    private ActivityLog _log = null!;
    private TerminalService _service = null!;

    public TerminalPanel()
    {
        InitializeComponent();
        _tabs.Add(_activity);
        Tabs.ItemsSource = _tabs;
        Tabs.SelectedItem = _activity;
    }

    /// <summary>The panel's own hide button was pressed.</summary>
    public event EventHandler? CloseRequested;

    public void Attach(ActivityLog log, TerminalService service)
    {
        _log = log; _service = service;
        LogList.Attach(log.Entries);
        service.SettingsChanged += (_, _) => ApplySettings();
        ApplySettings();
    }

    /// <summary>The font, and whether shells can be opened, from the settings - now and whenever they change.</summary>
    private void ApplySettings()
    {
        var font = _service.Font;
        var size = _service.Settings.FontSize;
        LogList.FontFamily = font;
        LogList.FontSize = size;
        foreach (var tab in _tabs) tab.View?.SetFont(font, size);

        var enabled = _service.Settings.Enabled;
        NewButton.Visibility = ShellsButton.Visibility = TabsPane.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        NewButton.ToolTip = $"New terminal ({_service.DefaultShell.Name})";
        if (!enabled) CloseAll();
    }

    // ─── Tabs ─────────────────────────────────────────────────────────────

    /// <summary>Shows the activity log - e.g. when the running operation is clicked in the status bar.</summary>
    public void ShowActivity() => Tabs.SelectedItem = _activity;

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
        var tab = new Tab { Title = title, Description = "Double-click to rename.", Details = details, Session = session, View = view };
        // The shell ended by itself ("exit"): its tab goes too.
        session.Exited += (_, _) => Close(tab);
        _tabs.Add(tab);
        Tabs.SelectedItem = tab;
    }

    private void Close(Tab tab)
    {
        if (tab.Session is null || !_tabs.Contains(tab)) return;
        var index = _tabs.IndexOf(tab);
        var wasShown = Tabs.SelectedItem == tab;
        _tabs.Remove(tab);
        tab.Session.Dispose();
        if (wasShown) Tabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
    }

    /// <summary>Ends every shell - when the window closes, or the terminal is switched off.</summary>
    public void CloseAll()
    {
        foreach (var tab in _tabs.Where(t => t.Session is not null).ToList()) Close(tab);
    }

    /// <summary>Shells are open - closing the window ends what runs in them.</summary>
    public int OpenTerminals => _tabs.Count(t => t.Session is not null);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not Tab tab)
        {
            // A list always shows one tab: clicking the shown one again doesn't unselect it.
            Tabs.SelectedItem = _activity;
            return;
        }

        var shell = tab.View is not null;
        // The scrollbar follows the shown terminal.
        if (TerminalHost.Content is TerminalView previous) previous.ScrollChanged -= View_ScrollChanged;
        TerminalHost.Content = tab.View;
        if (tab.View is not null)
        {
            tab.View.ScrollChanged += View_ScrollChanged;
            SyncScrollBar(tab.View);
        }
        TerminalHost.Visibility = TerminalScroll.Visibility = shell ? Visibility.Visible : Visibility.Collapsed;
        LogList.Visibility = shell ? Visibility.Collapsed : Visibility.Visible;
        PasteButton.Visibility = shell ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = shell ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.ToolTip = shell ? "Copy the selected text (Ctrl+C, or right-click)"
            : "Copy the selected text, or the whole log when nothing is selected";
        if (shell) FocusTerminal();
        else LogList.ScrollToEnd();
    }

    /// <summary>The panel was just shown: the log at its newest line, or the keyboard in the shown terminal.</summary>
    public void Opened()
    {
        if (TerminalHost.Visibility == Visibility.Visible) FocusTerminal();
        else LogList.ScrollToEnd();
    }

    /// <summary>Puts the keyboard in the shown terminal, once it is laid out.</summary>
    public void FocusTerminal() =>
        Dispatcher.BeginInvoke(() => (TerminalHost.Content as TerminalView)?.Focus(), DispatcherPriority.Input);

    // ─── Buttons ──────────────────────────────────────────────────────────

    private void New_Click(object sender, RoutedEventArgs e) => NewTerminal();

    // The installed shells, the default one marked.
    private void Shells_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShellsButton, Placement = PlacementMode.Bottom };
        foreach (var shell in _service.Shells)
        {
            var item = new MenuItem { Header = shell == _service.DefaultShell ? $"{shell.Name}  (default)" : shell.Name, ToolTip = shell.ExePath };
            item.Click += (_, _) => NewTerminal(shell);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Close(tab);
    }

    /// <summary>The tab a button in its row, or an item of its right-click menu, belongs to.</summary>
    private static Tab? TabOf(object sender) => sender switch
    {
        MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: Tab tab } } } => tab,
        FrameworkElement { DataContext: Tab tab } => tab,
        _ => null
    };

    // ─── Renaming a terminal ──────────────────────────────────────────────

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) BeginRename(tab);
    }

    // A double-click on a terminal's name.
    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || TabOf(sender) is not { CanClose: true } tab) return;
        BeginRename(tab);
        e.Handled = true;
    }

    // F2 on the selected terminal.
    private void Tabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || Tabs.SelectedItem is not Tab { CanClose: true, IsRenaming: false } tab) return;
        BeginRename(tab);
        e.Handled = true;
    }

    // Activity has no menu: it can't be renamed or closed.
    private void Tab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TabOf(sender) is not { CanClose: true }) e.Handled = true;
    }

    private static void BeginRename(Tab tab)
    {
        if (!tab.CanClose) return;
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

    // Activity: the selected part of the log, or - with nothing selected - the whole log with timestamps.
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalHost.Content is TerminalView view && TerminalHost.Visibility == Visibility.Visible)
        {
            view.Copy();
            FocusTerminal();
            return;
        }
        var text = LogList.Selection.IsEmpty ? _log.ToText() : LogList.Selection.Text.TrimEnd();
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        (TerminalHost.Content as TerminalView)?.Paste();
        FocusTerminal();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _log.Clear();

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

    // The newest line of whatever is shown: the terminal's prompt, or the end of the activity log.
    private void ScrollToBottom_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalHost.Content is TerminalView view && TerminalHost.Visibility == Visibility.Visible)
        {
            view.ScrollToBottom();
            FocusTerminal();
        }
        else
        {
            LogList.ScrollToEnd();
        }
    }
}
