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
    // The row under the mouse at the last right-click (null: empty space) - what the context menu is for.
    private ProjectRow? _menuRow;
    // The grid's own scroll viewer, found on first use (LayoutUpdated runs often).
    private ScrollViewer? _gridViewer;
    private bool _selectionQueued;

    public ProjectsPage(IServiceProvider services, OperationRunner runner, ServerStore store, SettingsStore settings,
        ActivityLog log, IOptions<AppOptions> options)
    {
        _store = store; _settings = settings; _log = log;
        InitializeComponent();
        _services = services;
        _menu = new ProjectMenu(services, runner, (action, row) => ControlSites(action, [row]), row => Remove([row]), OpenProject);

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

        _selectAll = new CheckBox { Style = (Style)FindResource("TableCheckBox"), ToolTip = "Select all shown / none" };
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

        // Rows come and go with their projects; each one's check box and state decide what the bulk buttons may do.
        foreach (var row in _store.Projects) row.PropertyChanged += Row_PropertyChanged;
        _store.Projects.CollectionChanged += Projects_CollectionChanged;
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => QueueUpdateSelection();
        _store.ConnectionChanged += (_, _) => ShowConnection();
        ShowConnection();

        // What only this page shows (worker processes, SQL, sizes…) is kept current only while it is on screen - the
        // page is kept, hidden, while other pages are shown. Coming back reads everything once, at once.
        IsVisibleChanged += (_, _) => UpdateWatching();
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

    private void UpdateWatching() => _store.SetWatching(IsVisible && _window?.WindowState != WindowState.Minimized);

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
                break;
            // Checked or not, and what the row may do now (it comes with every change of state): the bulk buttons.
            case nameof(ProjectRow.IsChecked) or nameof(ProjectRow.CanStart):
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
        BulkStartButton.ToolTip = BulkTip("Start", chosen.Count(r => r.CanStart), "stopped");
        BulkStopButton.ToolTip = BulkTip("Stop", chosen.Count(r => r.CanStop), "running");
        BulkRestartButton.ToolTip = BulkTip("Restart", chosen.Count(r => r.CanRestart), "running");
        BulkRemoveButton.ToolTip = $"Remove the {Plural(chosen.Count, "selected project")} - asks first";

        // A message in the middle only while there are no rows to show: before the first snapshot, without projects,
        // or when the search matches none. Never over rows that are there.
        ShowOverlay(!loaded ? "Loading projects…"
            : total == 0 ? _store.Connection == MonitorConnection.Reconnecting && _store.ConnectionDetail is { } problem ? problem
                : _store.Runtime == Application.Abstractions.IisServerState.NotInstalled
                    ? "IIS isn't installed - enable its Windows features on the Environment page."
                    : "IIS has no DNN websites yet - create one with New project or Host project."
            : shown.Count > 0 ? null
            : OnlyRunning.IsChecked != true ? $"No project matches “{SearchText}”."
            : SearchText.Length == 0 ? "No project is running."
            : $"No running project matches “{SearchText}”.");
    }

    private static string BulkTip(string verb, int count, string state) =>
        count == 0 ? $"None of the selected sites is {state}." : $"{verb} {Plural(count, $"{state} site")}";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // ─── Actions ──────────────────────────────────────────────────────────

    private void BulkStart_Click(object sender, RoutedEventArgs e) => ControlSites(SiteAction.Start, Checked.Where(r => r.CanStart).ToList());
    private void BulkStop_Click(object sender, RoutedEventArgs e) => ControlSites(SiteAction.Stop, Checked.Where(r => r.CanStop).ToList());
    private void BulkRestart_Click(object sender, RoutedEventArgs e) => ControlSites(SiteAction.Restart, Checked.Where(r => r.CanRestart).ToList());
    private void BulkRemove_Click(object sender, RoutedEventArgs e) => Remove(Checked);

    private void RowStart_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Start, [row]));
    private void RowStop_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Stop, [row]));
    private void RowRestart_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Restart, [row]));
    private void RowRemove_Click(object sender, RoutedEventArgs e) => OnRow(sender, row => Remove([row]));


    private static void OnRow(object sender, Action<ProjectRow> action)
    {
        if (sender is FrameworkElement { DataContext: ProjectRow row }) action(row);
    }

    private async void ControlSites(SiteAction action, IReadOnlyList<ProjectRow> rows) => await _store.ControlSitesAsync(action, rows);

    private async void Remove(IReadOnlyList<ProjectRow> rows) => await _store.RemoveAsync(rows);

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
        // The sites' traffic is only read while its column is there to show it.
        _store.SetTrafficWanted(NetworkColumn.Visibility == Visibility.Visible);
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

    /// <summary>Remembers the shown columns in settings.json; failing only means the next start shows the old ones.</summary>
    private void SaveColumns()
    {
        var keys = _columnOptions.Where(o => o.IsVisible).Select(o => o.Key).ToList();
        try { _settings.Update(s => s.Appearance.ProjectColumns = keys); }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            _log.Fail($"Could not save the columns to {_settings.FilePath}: {ex.Message}");
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

    // Space checks / unchecks the selected row, like the check box; Enter opens it.
    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProjectsGrid.SelectedItem is not ProjectRow row) return;
        if (e.Key == Key.Space) row.IsChecked = !row.IsChecked;
        else if (e.Key == Key.Enter) OpenProject(row);
        else return;
        e.Handled = true;
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
    }

    private void CloseProject()
    {
        ProjectHost.Content = null;
        ProjectHost.Visibility = Visibility.Collapsed;
        TableView.Visibility = Visibility.Visible;
        ProjectsGrid.Focus();
    }

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
        if (e.OriginalSource is ScrollViewer viewer && viewer == (_gridViewer ??= FindScrollViewer(ProjectsGrid)))
            UpdateActionsShift(viewer);
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
