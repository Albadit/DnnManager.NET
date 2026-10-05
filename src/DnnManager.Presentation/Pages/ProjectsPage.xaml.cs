using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.Options;

namespace DnnManager.Presentation.Pages;

/// <summary>
/// Every website in IIS as a table of servers - IIS is the list: its state, bindings and worker process (CPU,
/// memory), what its folder holds (DNN, database), with
/// start / stop / restart / remove per row or for the checked rows, a search box and a choice of columns.
/// <para>
/// The table is a view of the <see cref="ServerStore"/>'s rows and has no Refresh: the store changes the rows in
/// place as things happen - here or outside the app - so the check boxes, the search, the sorting, the scroll position
/// and the expanded rows stay as they are. The only "loading" is the first snapshot.
/// </para>
/// </summary>
public partial class ProjectsPage : UserControl
{
    private readonly IServiceProvider _services;
    private readonly ServerStore _store;
    private readonly SettingsStore _settings;
    private readonly ActivityLog _log;
    private readonly ProjectMenu _menu;
    private readonly ICollectionView _view;
    private readonly CheckBox _selectAll;
    private readonly Dictionary<string, DataGridColumn> _optionalColumns;
    private readonly List<ProjectColumnOption> _columnOptions;
    // The window the page is in, once it is - the live figures pause while it is minimized.
    private Window? _window;
    private readonly EfficiencyMode _efficiency;
    // The row under the mouse at the last right-click (null: empty space) - what the context menu is for.
    private ProjectRow? _menuRow;
    // The grid's own scroll viewer, found on first use (LayoutUpdated runs often).
    private ScrollViewer? _gridViewer;
    private bool _selectionQueued;

    public ProjectsPage(IServiceProvider services, OperationRunner runner, ServerStore store, SettingsStore settings,
        ActivityLog log, IOptions<AppOptions> options, EfficiencyMode efficiency)
    {
        _store = store; _settings = settings; _log = log; _efficiency = efficiency;
        InitializeComponent();
        _services = services;
        _menu = new ProjectMenu(services, runner, (action, row) => ControlSites(action, [row]), row => Remove([row]), OpenProject, ToggleKeepWarm);

        // The store's rows, filtered by the search box and sorted by the column the user clicked. A row whose
        // searchable text or sorted-by value changes is looked at again by itself (live shaping): it leaves, comes
        // back or moves to its place - the rest of the table isn't touched.
        _view = CollectionViewSource.GetDefaultView(_store.Projects);
        _view.Filter = item => item is ProjectRow row && Passes(row);
        if (_view is ICollectionViewLiveShaping live)
        {
            if (live.CanChangeLiveFiltering)
            {
                live.LiveFilteringProperties.Add(nameof(ProjectRow.SearchKey));
                // Only DNN sites: one whose DNN install appears or goes comes or leaves.
                live.LiveFilteringProperties.Add(nameof(ProjectRow.IsDnn));
                // For "Only show running".
                live.LiveFilteringProperties.Add(nameof(ProjectRow.State));
                live.IsLiveFiltering = true;
            }
            // No properties named: the ones the table is sorted by at the time.
            if (live.CanChangeLiveSorting) live.IsLiveSorting = true;
        }
        ProjectsGrid.ItemsSource = _view;

        _selectAll = new CheckBox { Style = (Style)FindResource("TableCheckBox"), ToolTip = "Select all" };
        _selectAll.Click += SelectAll_Click;
        SelectColumn.Header = _selectAll;

        _optionalColumns = new Dictionary<string, DataGridColumn>
        {
            ["id"] = IdColumn, ["url"] = UrlColumn, ["ports"] = PortsColumn, ["dnn"] = DnnColumn,
            ["database"] = DatabaseColumn, ["sql"] = SqlColumn, ["status"] = StatusColumn, ["cpu"] = CpuColumn,
            ["memory"] = MemoryColumn, ["memoryPercent"] = MemoryPercentColumn, ["disk"] = DiskColumn, ["network"] = NetworkColumn, ["pid"] = PidColumn,
            ["lastStarted"] = LastStartedColumn, ["size"] = SizeColumn, ["path"] = PathColumn,
        };
        var shown = options.Value.ProjectColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _columnOptions = ProjectColumns.All.Select(c => new ProjectColumnOption(c.Key, c.Header) { IsVisible = shown.Contains(c.Key) }).ToList();
        foreach (var option in _columnOptions)
        {
            _optionalColumns[option.Key].Visibility = option.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            option.PropertyChanged += (_, _) => ColumnToggled();
        }
        ColumnList.ItemsSource = _columnOptions;
        _store.SetTrafficWanted(NetworkColumn.Visibility == Visibility.Visible);
        _store.SetSizesShown(SizeColumn.Visibility == Visibility.Visible);

        // Rows come and go with their projects; each one's check box and state decide what the bulk buttons may do.
        foreach (var row in _store.Projects) row.PropertyChanged += Row_PropertyChanged;
        _store.Projects.CollectionChanged += Projects_CollectionChanged;
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => QueueUpdateSelection();
        _store.ConnectionChanged += (_, _) => ShowConnection();
        ShowConnection();

        // What only this page shows (worker processes, SQL, sizes…) is kept current only while it is on screen - the
        // page is kept, hidden, while other pages are shown. Coming back reads everything once, at once.
        IsVisibleChanged += (_, _) => UpdateWatching();
        // Covered by other windows is as good as minimized.
        efficiency.Changed += (_, _) => UpdateWatching();
        Loaded += (_, _) =>
        {
            if (_window is null && Window.GetWindow(this) is { } window)
            {
                _window = window;
                window.StateChanged += (_, _) => UpdateWatching();
            }
            UpdateWatching();
        };

        // Look for installed IDEs now (vswhere takes a moment) so the first right-click opens at once.
        _ = Task.Run(() => (IdeLocator.Installed, IdeLocator.ManagementStudios));
    }

    // ─── Following the store ──────────────────────────────────────────────

    private void UpdateWatching() =>
        _store.SetWatching(IsVisible && _window?.WindowState != WindowState.Minimized && !_efficiency.IsSaving);

    private void Projects_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The open site was removed from IIS: its overview has nothing left to show.
        if (e.OldItems is not null && ProjectHost.Content is ProjectView { Row: var open } && e.OldItems.Contains(open)) CloseProject();
        if (e.OldItems is not null) foreach (ProjectRow row in e.OldItems) row.PropertyChanged -= Row_PropertyChanged;
        if (e.NewItems is not null) foreach (ProjectRow row in e.NewItems) row.PropertyChanged += Row_PropertyChanged;
        // Also for a row the search hides (the view says nothing about those): "4 of 12 projects" counts them.
        QueueUpdateSelection();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectRow.IsExpanded):
                if (sender is ProjectRow row && ProjectsGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
                    ShowDetails(container);
                WorkspaceChanged?.Invoke(this, EventArgs.Empty);
                break;
            // Checked or not, and what the row may do now (it comes with every change of state): the bulk buttons.
            case nameof(ProjectRow.IsChecked) or nameof(ProjectRow.CanStart) or nameof(ProjectRow.KeepWarmOn) or nameof(ProjectRow.CanToggleKeepWarm):
                QueueUpdateSelection();
                break;
        }
    }

    /// <summary>
    /// <see cref="UpdateSelection"/>, once for everything that changes together - an operation starting or ending
    /// changes what every row may do - and before the window is next drawn.
    /// </summary>
    private void QueueUpdateSelection()
    {
        if (_selectionQueued) return;
        _selectionQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _selectionQueued = false;
            UpdateSelection();
        }, DispatcherPriority.Normal);
    }

    /// <summary>
    /// The small indicator where Refresh used to be. While what is shown is current there is nothing to say and it
    /// isn't there; reconnecting leaves everything on screen - only this says so.
    /// </summary>
    private void ShowConnection()
    {
        var reconnecting = _store.Connection == MonitorConnection.Reconnecting;
        LiveIndicator.Visibility = reconnecting ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Text = "Reconnecting…";
        LiveDot.SetResourceReference(Shape.StrokeProperty, "LogWarn");
        LiveDot.Fill = Brushes.Transparent;
        UpdateSelection();
    }

    // Made when it is about to show, so "last synchronised" is the time of the last read, not of the last change.
    private void LiveIndicator_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var last = _store.LastSync is { } at ? $"{Environment.NewLine}Last synchronised {at:HH:mm:ss}." : "";
        LiveIndicator.ToolTip = (_store.ConnectionDetail ?? "Synchronising again.") + Environment.NewLine +
                                "What is shown stays as it was until that works again." + last;
    }

    private void ShowOverlay(string? text)
    {
        OverlayText.Text = text ?? "";
        Overlay.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ─── Search, counts and check boxes ───────────────────────────────────

    private string SearchText => SearchBox.Text.Trim();

    // The table lists the DNN sites in IIS - IIS's Default Web Site and other sites without DNN aren't projects.
    private IEnumerable<ProjectRow> DnnSites => _store.Projects.Where(r => r.IsDnn);

    private bool Passes(ProjectRow row) => row.IsDnn &&
        (OnlyRunning.IsChecked != true || row.State == SiteRunState.Running) && (SearchText.Length == 0 || row.Matches(SearchText));

    private IEnumerable<ProjectRow> Shown => _store.Projects.Where(Passes);

    /// <summary>The checked rows the search shows - what the bulk actions act on.</summary>
    private List<ProjectRow> Checked => Shown.Where(r => r.IsChecked).ToList();

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateSelection();
    }

    // A switch slides for a moment. What it switches - the table filtered anew, other columns - keeps the UI thread
    // busy for long enough to turn that slide into a jump, so it follows once the switch has arrived.
    private static readonly TimeSpan SwitchSlide = TimeSpan.FromMilliseconds(180);

    private async void OnlyRunning_Click(object sender, RoutedEventArgs e)
    {
        await Task.Delay(SwitchSlide);
        _view.Refresh();
        UpdateSelection();
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    // Set as a local value: the grid overrides one from a style or binding with its RowDetailsVisibilityMode.
    private static void ShowDetails(DataGridRow container) =>
        container.DetailsVisibility = container.Item is ProjectRow { IsExpanded: true } ? Visibility.Visible : Visibility.Collapsed;

    // A row container is made (or reused for another row) - give it that row's details state.
    private void Grid_LoadingRow(object? sender, DataGridRowEventArgs e) => ShowDetails(e.Row);

    // Select all when not every shown row is checked, else none.
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var shown = Shown.ToList();
        var check = !shown.All(r => r.IsChecked);
        foreach (var row in shown) row.IsChecked = check;
        UpdateSelection();
    }

    /// <summary>The counts under the table, the header check box and the bulk buttons, after any change.</summary>
    private void UpdateSelection()
    {
        var total = DnnSites.Count();
        var shown = Shown.ToList();
        var chosen = shown.Where(r => r.IsChecked).ToList();
        var noun = total == 1 ? "project" : "projects";
        var loaded = _store.IsLoaded;

        CountText.Text = !loaded ? "" : shown.Count == total ? $"{total} {noun}" : $"{shown.Count} of {total} {noun}";
        SelectedText.Text = chosen.Count > 0 ? $"Selected {chosen.Count} of {total}" : "";
        _selectAll.IsChecked = chosen.Count == 0 ? false : chosen.Count == shown.Count ? true : null;
        _selectAll.IsEnabled = shown.Count > 0;

        // Only what makes sense for some of the checked rows: Start for a stopped one, Stop / Restart for a running one.
        BulkActions.Visibility = chosen.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BulkStartButton.IsEnabled = chosen.Any(r => r.CanStart);
        BulkStopButton.IsEnabled = chosen.Any(r => r.CanStop);
        BulkRestartButton.IsEnabled = chosen.Any(r => r.CanRestart);
        BulkRemoveButton.IsEnabled = chosen.Count > 0 && chosen.All(r => r.CanRemove);
        BulkStartButton.ToolTip = "Start";
        BulkStopButton.ToolTip = "Stop";
        BulkRestartButton.ToolTip = "Restart";
        BulkRemoveButton.ToolTip = "Remove";

        // Keep warm: on for those that aren't yet - or, when all that can be are, off for them all.
        var warmable = chosen.Where(r => r.CanToggleKeepWarm).ToList();
        var allWarm = warmable.Count > 0 && warmable.All(r => r.KeepWarmOn);
        BulkKeepWarmButton.IsEnabled = warmable.Count > 0;
        BulkKeepWarmIcon.Data = (Geometry)FindResource(allWarm ? "FlameFilled" : "FlameOutline");
        BulkKeepWarmIcon.SetResourceReference(Shape.FillProperty, allWarm ? "KeepWarmFg" : "TextPrimary");
        BulkKeepWarmButton.ToolTip = allWarm ? "Stop keeping warm" : "Keep warm";

        // A message in the middle only while there are no rows to show: before the first snapshot, without projects,
        // or when the search matches none. Never over rows that are there.
        ShowOverlay(!loaded ? "Loading projects…"
            : total == 0 ? _store.Connection == MonitorConnection.Reconnecting && _store.ConnectionDetail is { } problem ? problem
                : _store.Runtime == Application.Abstractions.IisServerState.NotInstalled
                    ? "IIS isn't installed - enable its Windows features in Settings → IIS."
                    : "IIS has no DNN websites yet - create one with New project or Host project."
            : shown.Count > 0 ? null
            : OnlyRunning.IsChecked != true ? $"No project matches “{SearchText}”."
            : SearchText.Length == 0 ? "No project is running."
            : $"No running project matches “{SearchText}”.");
    }

    // ─── Actions ──────────────────────────────────────────────────────────

    private void BulkStart_Click(object sender, RoutedEventArgs e) => ControlSites(SiteAction.Start, Checked.Where(r => r.CanStart).ToList());
    private void BulkStop_Click(object sender, RoutedEventArgs e) => ControlSites(SiteAction.Stop, Checked.Where(r => r.CanStop).ToList());
    private void BulkRestart_Click(object sender, RoutedEventArgs e) => ControlSites(SiteAction.Restart, Checked.Where(r => r.CanRestart).ToList());
    private void BulkRemove_Click(object sender, RoutedEventArgs e) => Remove(Checked);

    /// <summary>
    /// Keeps the checked sites warm - or, when every one that can be is already, stops keeping them warm. Sites without an
    /// address to request are left out.
    /// </summary>
    private void BulkKeepWarm_Click(object sender, RoutedEventArgs e)
    {
        var warmable = Checked.Where(r => r.CanToggleKeepWarm).ToList();
        var on = !warmable.All(r => r.KeepWarmOn);
        foreach (var row in warmable.Where(r => r.KeepWarmOn != on)) ToggleKeepWarm(row);
        UpdateSelection();
    }

    private void RowStart_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Start, [row]));
    private void RowStop_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Stop, [row]));
    private void RowRestart_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Restart, [row]));
    private void RowRemove_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => Remove([row]));
    private void RowKeepWarm_Click(object sender, RoutedEventArgs e) => OnRow(sender, ToggleKeepWarm);

    private static void OnRow(object sender, Action<ProjectRow> action)
    {
        if (sender is FrameworkElement { DataContext: ProjectRow row }) action(row);
    }

    private async void ControlSites(SiteAction action, IReadOnlyList<ProjectRow> rows) => await _store.ControlSitesAsync(action, rows);

    private async void Remove(IReadOnlyList<ProjectRow> rows) => await _store.RemoveAsync(rows);

    private void ToggleKeepWarm(ProjectRow row) => _store.ToggleKeepWarm(row);

    // ─── Columns ──────────────────────────────────────────────────────────

    private bool _columnsQueued;

    /// <summary>
    /// A column was switched in the menu: the table follows when the switch has slid over - once for all the
    /// switches changed together (Show all, Default) - and the choice is saved.
    /// </summary>
    private async void ColumnToggled()
    {
        if (_columnsQueued) return;
        _columnsQueued = true;
        await Task.Delay(SwitchSlide);
        _columnsQueued = false;

        foreach (var option in _columnOptions)
            _optionalColumns[option.Key].Visibility = option.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        // The sites' traffic and folder sizes are only read while their column is there to show them.
        _store.SetTrafficWanted(NetworkColumn.Visibility == Visibility.Visible);
        _store.SetSizesShown(SizeColumn.Visibility == Visibility.Visible);
        SaveColumns();
    }

    private void ShowAllColumns_Click(object sender, RoutedEventArgs e) => SetColumns(_ => true);

    private void HideAllColumns_Click(object sender, RoutedEventArgs e) => SetColumns(_ => false);

    private void DefaultColumns_Click(object sender, RoutedEventArgs e) =>
        SetColumns(key => AppearanceSettings.DefaultProjectColumns.Contains(key, StringComparer.OrdinalIgnoreCase));

    private void SetColumns(Func<string, bool> visible)
    {
        foreach (var option in _columnOptions) option.IsVisible = visible(option.Key);
    }

    /// <summary>Remembers the shown columns in the settings; failing only means the next start shows the old ones.</summary>
    private void SaveColumns()
    {
        var keys = _columnOptions.Where(o => o.IsVisible).Select(o => o.Key).ToList();
        try { _settings.Update(s => s.Appearance.ProjectColumns = keys); }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            _log.Fail($"Could not save the columns to {_settings.Location}: {ex.Message}");
        }
    }

    private void ColumnsPopup_Opened(object sender, EventArgs e) => ColumnsButton.IsHitTestVisible = false;

    // After the click that closed the menu has been handled, so that click doesn't open it again.
    private void ColumnsPopup_Closed(object sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ColumnsButton.IsHitTestVisible = true, DispatcherPriority.Input);

    // ─── Mouse and keyboard ───────────────────────────────────────────────

    /// <summary>The row <paramref name="source"/> is in, or null for the header, the empty space or a button in the row.</summary>
    private static ProjectRow? RowAt(object source, bool ignoreButtons)
    {
        var node = source as DependencyObject;
        while (node is not null and not DataGridRow)
        {
            if (ignoreButtons && node is ButtonBase) return null;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return (node as DataGridRow)?.Item as ProjectRow;
    }

    // A double-click on a row (not on its check box or buttons) opens the site's overview.
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource, ignoreButtons: true) is { } row) OpenProject(row);
    }

    private void RowOpen_Click(object sender, RoutedEventArgs e) => OnRow(sender, OpenProject);

    private void OpenSite_Click(object sender, RoutedEventArgs e) => OnRow(sender, row =>
    {
        if (row.HasUrl) Shell.Open(row.Url);
    });

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OnRow(sender, Projects.ProjectMenu.OpenFolder);

    // Space checks / unchecks the selected row, like the check box; Enter opens it; Del removes it (asking first) - as
    // in the terminal list. Up and Down move the selection (the grid's own); Right shows the row's details in the table,
    // Left hides them - as its chevron does, like a tree.
    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is not ProjectRow row || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Space) row.IsChecked = !row.IsChecked;
        else if (e.Key == Key.Right) row.IsExpanded = true;
        else if (e.Key == Key.Left) row.IsExpanded = false;
        else if (e.Key == Key.Enter) OpenProject(row);
        else if (e.Key == Key.Delete && row.CanRemove) Remove([row]);
        else return;
        e.Handled = true;
    }

    // A click anywhere on a row - its name, address or a button too, which don't take the keyboard - selects it and puts
    // the keyboard in the table, so the arrows go on from there, as in the terminal list.
    // A click in the table's empty space (not on a row, a column header or a scrollbar) puts the keyboard there too - on
    // the selected row, or the first.
    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowAt(e.OriginalSource, ignoreButtons: false);
        if (row is null)
        {
            if (Within<DataGridColumnHeader>(e.OriginalSource) || Within<ScrollBar>(e.OriginalSource) || ProjectsGrid.Items.Count == 0) return;
            row = ProjectsGrid.SelectedItem as ProjectRow ?? ProjectsGrid.Items[0] as ProjectRow;
            if (row is null) return;
        }
        ProjectsGrid.SelectedItem = row;
        Dispatcher.BeginInvoke(() => FocusRow(row), DispatcherPriority.Input);
    }

    /// <summary>
    /// The keyboard left the table: its row is no longer selected - as a list that isn't being used. Not for a menu or the
    /// command palette, which act on that row, a question it asked, or another app (the window inactive).
    /// </summary>
    private void Grid_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) return;
        // Once the keyboard has arrived where it went.
        Dispatcher.BeginInvoke(() =>
        {
            if (ProjectsGrid.IsKeyboardFocusWithin || Window.GetWindow(this) is not { IsActive: true }) return;
            if (ProjectsGrid.ContextMenu?.IsOpen == true || Keyboard.FocusedElement is { } focused &&
                (Within<ContextMenu>(focused) || Within<CommandPalette>(focused))) return;
            ProjectsGrid.SelectedItem = null;
        }, DispatcherPriority.Input);
    }

    // A click on the page outside the table - its title, the room beside it - takes the keyboard out of the table, and so
    // its selection (Grid_IsKeyboardFocusWithinChanged). To the page itself, so the window's keys still work; what takes
    // the keyboard on a click (the search box) takes it from there.
    private void Page_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TableView.IsVisible || IsIn(e.OriginalSource, ProjectsGrid)) return;
        if (ProjectsGrid.IsKeyboardFocusWithin) Focus();
        else ProjectsGrid.SelectedItem = null;
    }

    private static bool IsIn(object source, DependencyObject ancestor)
    {
        for (var d = source as DependencyObject; d is not null;
             d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    // The rows clipped to the frame's rounded corners (its radius less its border).
    private void Grid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ProjectsGrid.Clip = new RectangleGeometry(new Rect(e.NewSize), 5, 5);

    /// <summary>Whether <paramref name="source"/> is a <typeparamref name="T"/> or inside one.</summary>
    private static bool Within<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null;
             d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    /// <summary>The keyboard on <paramref name="row"/>'s first cell - unless something in the row has it already (its check box).</summary>
    private void FocusRow(ProjectRow row)
    {
        if (!TableView.IsVisible || ProjectsGrid.ItemContainerGenerator.ContainerFromItem(row) is not DataGridRow container ||
            container.IsKeyboardFocusWithin) return;
        if (ProjectsGrid.Columns.FirstOrDefault(c => c.Visibility == Visibility.Visible) is { } column &&
            column.GetCellContent(container)?.Parent is DataGridCell cell)
        {
            ProjectsGrid.CurrentCell = new DataGridCellInfo(cell);
            cell.Focus();
        }
        else container.Focus();
    }

    // ─── A site's overview ────────────────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="row"/>'s overview in place of the table (which keeps its search, selection and scroll
    /// position for when it comes back).
    /// </summary>
    public void OpenProject(ProjectRow row)
    {
        var view = new ProjectView(row, _services, action => ControlSites(action, [row]));
        view.BackRequested += (_, _) => CloseProject();
        ProjectHost.Content = view;
        ProjectHost.Visibility = Visibility.Visible;
        TableView.Visibility = Visibility.Collapsed;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseProject()
    {
        // Back on the table, on the project that was open.
        var open = (ProjectHost.Content as ProjectView)?.Row;
        ProjectHost.Content = null;
        ProjectHost.Visibility = Visibility.Collapsed;
        TableView.Visibility = Visibility.Visible;
        if (open is not null && ProjectsGrid.Items.Contains(open))
        {
            ProjectsGrid.SelectedItem = open;
            Dispatcher.BeginInvoke(() => FocusRow(open), DispatcherPriority.Input);
        }
        else ProjectsGrid.Focus();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ─── The keyboard (commands) ──────────────────────────────────────────

    /// <summary>The project the keyboard's commands act on: the one whose Details are open, else the table's selected row.</summary>
    public ProjectRow? CurrentProject => ProjectHost.Content is ProjectView view ? view.Row : ProjectsGrid.SelectedItem as ProjectRow;

    /// <summary>The Details are open - Ctrl+W closes them.</summary>
    public bool DetailsOpen => ProjectHost.Content is ProjectView;

    public ProjectView? Details => ProjectHost.Content as ProjectView;

    public void CloseDetails()
    {
        if (DetailsOpen) CloseProject();
    }

    /// <summary>The keyboard in the search box, its text selected - Ctrl+F on the table.</summary>
    public void FocusSearch()
    {
        CloseDetails();
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>The keyboard in the table (or the Details), on the selected row - the first when none is.</summary>
    public void FocusContent() => Dispatcher.BeginInvoke(() =>
    {
        if (ProjectHost.Content is ProjectView view)
        {
            view.FocusTabs();
            return;
        }
        if (ProjectsGrid.SelectedItem is null && ProjectsGrid.Items.Count > 0) ProjectsGrid.SelectedIndex = 0;
        if (ProjectsGrid.SelectedItem is { } row && ProjectsGrid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
            container.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        else ProjectsGrid.Focus();
    }, DispatcherPriority.Input);

    // ─── Kept between starts ──────────────────────────────────────────────

    /// <summary>Details opened or closed, the table sorted, scrolled or a row expanded - what the workspace keeps changed.</summary>
    public event EventHandler? WorkspaceChanged;

    /// <summary>The table as it is set - search, filter, sorting, expanded and selected rows, scroll position.</summary>
    public ProjectsTableState CaptureTable()
    {
        var sort = _view.SortDescriptions.Count > 0 ? _view.SortDescriptions[0] : (SortDescription?)null;
        return new ProjectsTableState
        {
            Search = SearchBox.Text.Length > 0 ? SearchBox.Text : null,
            OnlyRunning = OnlyRunning.IsChecked == true,
            SortBy = sort?.PropertyName,
            SortDescending = sort?.Direction == ListSortDirection.Descending,
            Expanded = _store.Projects.Where(r => r.IsExpanded).Select(r => r.Name).ToList(),
            Selected = (ProjectsGrid.SelectedItem as ProjectRow)?.Name,
            ScrollOffset = (_gridViewer ??= FindScrollViewer(ProjectsGrid))?.VerticalOffset ?? 0
        };
    }

    /// <summary>The project whose Details are open, and their tab; nulls when the table is shown.</summary>
    public (string? Project, string? Tab) CaptureDetails() =>
        ProjectHost.Content is ProjectView view ? (view.Row.Name, view.Tab) : (null, null);

    /// <summary>
    /// Puts the table and the Details back as they were - once the projects have been read. Returns what couldn't be
    /// restored (the project is gone), or null.
    /// </summary>
    public async Task<string?> RestoreAsync(ProjectsTableState table, string? project, string? tab)
    {
        SearchBox.Text = table.Search ?? "";
        OnlyRunning.IsChecked = table.OnlyRunning;
        Sort(table.SortBy, table.SortDescending);
        _view.Refresh();
        UpdateSelection();

        if (!_store.IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            EventHandler wait = (_, _) => { if (_store.IsLoaded) loaded.TrySetResult(); };
            _store.ConnectionChanged += wait;
            // The projects can't be read at all (IIS broken): what is there is restored after a while.
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            _store.ConnectionChanged -= wait;
        }

        var rows = _store.Projects.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in table.Expanded)
            if (rows.TryGetValue(name, out var expanded)) expanded.IsExpanded = true;
        if (table.Selected is { } selected && rows.TryGetValue(selected, out var selectedRow)) ProjectsGrid.SelectedItem = selectedRow;
        // Once the rows are laid out - there is nothing to scroll before.
        var offset = table.ScrollOffset;
        if (offset > 0) _ = Dispatcher.BeginInvoke(() => (_gridViewer ??= FindScrollViewer(ProjectsGrid))?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);

        if (project is null) return null;
        // Gone (removed, renamed) - the table is the closest place to it.
        if (!rows.TryGetValue(project, out var row)) return $"{project} isn't in IIS any more - here is the Projects table.";
        OpenProject(row);
        (ProjectHost.Content as ProjectView)?.ShowTab(tab);
        return null;
    }

    /// <summary>Sorts the table by the column that sorts by <paramref name="property"/> - as clicking its header would.</summary>
    private void Sort(string? property, bool descending)
    {
        if (property is null) return;
        var column = ProjectsGrid.Columns.FirstOrDefault(c => c.CanUserSort && c.SortMemberPath == property);
        if (column is null) return;
        var direction = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var other in ProjectsGrid.Columns) other.SortDirection = null;
        column.SortDirection = direction;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(property, direction));
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e) =>
        // After the grid has sorted.
        Dispatcher.BeginInvoke(() => WorkspaceChanged?.Invoke(this, EventArgs.Empty), DispatcherPriority.Background);

    /// <summary>
    /// Gives the filler column whatever width the other columns leave, so Actions sits at the right edge - none when
    /// they don't fit and the table scrolls sideways. Runs after every layout pass (the columns size to their
    /// content); it only changes the width when it's off, so it settles at once.
    /// </summary>
    private void Grid_LayoutUpdated(object? sender, EventArgs e)
    {
        _gridViewer ??= FindScrollViewer(ProjectsGrid);
        if (_gridViewer is not { ViewportWidth: > 0 } viewer) return;
        var others = ProjectsGrid.Columns.Where(c => c != FillerColumn && c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth);
        var width = Math.Max(0, Math.Floor(viewer.ViewportWidth - others));
        if (Math.Abs(FillerColumn.Width.Value - width) >= 1) FillerColumn.Width = new DataGridLength(width);
        UpdateActionsShift(viewer);
    }

    private void Grid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || viewer != (_gridViewer ??= FindScrollViewer(ProjectsGrid))) return;
        UpdateActionsShift(viewer);
        if (e.VerticalChange != 0) WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps the Actions column at the right edge of the visible table: its cells and header move left by as much as
    /// the table is scrolled short of its right end (a render transform - no new layout pass).
    /// </summary>
    private void UpdateActionsShift(ScrollViewer viewer)
    {
        var x = -Math.Max(0, Math.Floor(viewer.ExtentWidth - viewer.ViewportWidth - viewer.HorizontalOffset));
        if (ActionsShift.X != x) ActionsShift.X = x;
    }

    /// <summary>The Actions column's cells and header are drawn moved by this - see <see cref="UpdateActionsShift"/>.</summary>
    public TranslateTransform ActionsShift { get; } = new();

    // WPF has no sideways wheel scrolling: Shift + wheel scrolls the table horizontally.
    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _gridViewer ??= FindScrollViewer(ProjectsGrid);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || _gridViewer is not { } viewer) return;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    // A right-click selects the row under the mouse, like Explorer, so the menu acts on that project.
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _menuRow = RowAt(e.OriginalSource, ignoreButtons: false);
        if (_menuRow is not null) ProjectsGrid.SelectedItem = _menuRow;
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Opened from the keyboard (Menu key / Shift+F10) the cursor position is -1: use the selection.
        var row = e.CursorLeft < 0 ? ProjectsGrid.SelectedItem as ProjectRow : _menuRow;
        if (row is null || ProjectsGrid.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        _menu.Fill(menu, row);
    }
}
