using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DnnManager.Application;
using DnnManager.Application.Abstractions;
using DnnManager.Application.Configuration;
using DnnManager.Application.UseCases;
using DnnManager.Infrastructure.Monitoring;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>
/// Projects - every website in IIS as a table (the Windows app's Pages/ProjectsPage): its state, bindings and worker
/// process (CPU, memory), what its folder holds (DNN, database), with start / stop / restart / keep warm / remove per row
/// or for the checked rows, a search box and a choice of columns. The table is a view of the <see cref="ServerStore"/>'s
/// rows and has no Refresh: the store changes the rows in place as things happen, so the check boxes, the search, the
/// sorting and the expanded rows stay as they are. On macOS and Linux - no IIS - it says so instead.
/// </summary>
public partial class ProjectsView : UserControl
{
    // A switch slides for a moment; what it switches follows once it has arrived.
    private static readonly TimeSpan SwitchSlide = TimeSpan.FromMilliseconds(180);

    private readonly IServiceProvider _services = Program.Services;
    private readonly ServerStore? _store;
    private readonly ActivityLog _log = null!;
    private readonly ProjectMenu _menu = null!;
    private readonly ObservableCollection<ProjectRow> _shown = [];
    private readonly List<ProjectColumnOption> _columnOptions = [];
    private readonly HashSet<ProjectRow> _followed = [];
    private string? _sortBy;
    private bool _sortDescending;
    private bool _refreshQueued, _selectionQueued, _columnsQueued;
    private IDisposable? _noticeTimer;
    private Action? _noticeAction;
    private Window? _window;

    public ProjectsView()
    {
        InitializeComponent();
        if (Platform.WhyNot(PlatformFeature.Iis) is { } why)
        {
            // What only Windows has: the page says so, and what to use instead.
            TableView.IsVisible = false;
            NotHere.IsVisible = true;
            NotHereTitle.Text = $"Not available on {Platform.Name}";
            NotHereText.Text = why;
            return;
        }

        _store = ProjectsServices.Store;
        _log = _services.GetRequiredService<ActivityLog>();
        var runner = _services.GetRequiredService<OperationRunner>();
        _menu = new ProjectMenu(_services, runner, (action, row) => ControlSites(action, [row]), row => Remove([row]), OpenProject, ToggleKeepWarm);
        Rows.ItemsSource = _shown;
        Resources["ProjectsDetailsWidth"] = 800d;

        // The optional columns, as the settings have them - hidden ones by a class on the table (HideColumns).
        var shownColumns = _services.GetRequiredService<IOptions<AppOptions>>().Value.ProjectColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, header) in ProjectColumns.All)
        {
            var option = new ProjectColumnOption(key, header) { IsVisible = shownColumns.Contains(key) };
            option.PropertyChanged += (_, _) => ColumnToggled();
            _columnOptions.Add(option);
            // ".hide-url .col-url": the column's header and cells collapse - its shared width with them.
            TableContent.Styles.Add(new Style(x => x.OfType<DockPanel>().Class("hide-" + key).Descendant().Class("col-" + key))
            {
                Setters = { new Setter(IsVisibleProperty, false) }
            });
        }
        ColumnList.ItemsSource = _columnOptions;
        ApplyColumns();

        foreach (var row in _store.Projects) Follow(row);
        _store.Projects.CollectionChanged += Projects_CollectionChanged;
        _store.ConnectionChanged += (_, _) => ShowConnection();
        ProjectNotice.Shown += notice => Dispatcher.UIThread.Post(() => ShowNotice(notice));

        Rows.AddHandler(KeyDownEvent, Rows_KeyDown, RoutingStrategies.Tunnel);
        Rows.DoubleTapped += Rows_DoubleTapped;
        Rows.ContextRequested += Rows_ContextRequested;
        Rows.AddHandler(PointerPressedEvent, Rows_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        HScroll.SizeChanged += (_, _) => FitTable();
        HScroll.ScrollChanged += (_, _) => UpdateActionsShift();
        DataContext = this;
        KeyDown += View_KeyDown;
        ToolTip.SetTip(LiveIndicator, "Reconnecting");
        LiveIndicator.PointerEntered += (_, _) => ToolTip.SetTip(LiveIndicator, LiveTip());

        AttachedToVisualTree += (_, _) =>
        {
            if (_window is null && TopLevel.GetTopLevel(this) is Window window)
            {
                _window = window;
                window.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Window.WindowStateProperty) UpdateWatching();
                };
            }
            UpdateWatching();
        };
        DetachedFromVisualTree += (_, _) => _store.SetWatching(false);

        // Look for installed IDEs now (vswhere takes a moment) so the first right-click opens at once.
        _ = Task.Run(() => (IdeLocator.Installed, IdeLocator.ManagementStudios));
        ProjectsServices.StartStore();
        ShowConnection();
        Refresh();
    }

    /// <summary>Shown (the window's Projects page): IIS is followed - started once - and the table is current.</summary>
    public void Activated()
    {
        if (_store is null) return;
        ProjectsServices.StartStore();
        UpdateWatching();
        Refresh();
        if (ProjectHost.Content is ProjectView view) view.Focus();
        else Rows.Focus();
    }

    // ─── Following the store ──────────────────────────────────────────────

    /// <summary>What only this page shows (worker processes, SQL, sizes…) is kept current only while it is on screen.</summary>
    private void UpdateWatching() =>
        _store?.SetWatching(this.IsAttachedToVisualTree() && _window?.WindowState != WindowState.Minimized);

    private void Follow(ProjectRow row)
    {
        if (_followed.Add(row)) row.PropertyChanged += Row_PropertyChanged;
    }

    private void Projects_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The open site was removed from IIS: its overview has nothing left to show.
        if (e.OldItems is not null && ProjectHost.Content is ProjectView { Row: var open } && e.OldItems.Contains(open)) CloseProject();
        if (e.OldItems is not null)
            foreach (ProjectRow row in e.OldItems)
            {
                row.PropertyChanged -= Row_PropertyChanged;
                _followed.Remove(row);
            }
        if (e.NewItems is not null) foreach (ProjectRow row in e.NewItems) Follow(row);
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var row in _followed) row.PropertyChanged -= Row_PropertyChanged;
            _followed.Clear();
            foreach (var row in _store!.Projects) Follow(row);
        }
        QueueRefresh();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // What the search, the filter and the order look at: the row may come, go or move.
            case nameof(ProjectRow.SearchKey) or nameof(ProjectRow.IsDnn) or nameof(ProjectRow.State) or nameof(ProjectRow.FilterVersion):
                QueueRefresh();
                break;
            case nameof(ProjectRow.IsChecked) or nameof(ProjectRow.CanStart) or nameof(ProjectRow.CanStop) or nameof(ProjectRow.KeepWarmOn)
                or nameof(ProjectRow.CanToggleKeepWarm) or nameof(ProjectRow.CanRemove):
                QueueUpdateSelection();
                break;
            default:
                if (_sortBy is not null && e.PropertyName == SortProperty(_sortBy)) QueueRefresh();
                break;
        }
    }

    private static string SortProperty(string sortBy) => sortBy == "MemoryPercent" ? nameof(ProjectRow.MemorySort) : sortBy;

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void QueueUpdateSelection()
    {
        if (_selectionQueued) return;
        _selectionQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionQueued = false;
            UpdateSelection();
        });
    }

    /// <summary>The rows the search and filter show, in the order chosen - the shown list moved to it, not made anew.</summary>
    private void Refresh()
    {
        if (_store is null) return;
        IEnumerable<ProjectRow> rows = _store.Projects.Where(Passes);
        if (_sortBy is { } by)
        {
            var key = SortKey(by);
            rows = _sortDescending ? rows.OrderByDescending(key, Comparer<object?>.Default) : rows.OrderBy(key, Comparer<object?>.Default);
        }
        var wanted = rows.ToList();
        var selected = Rows.SelectedItem;
        var keep = wanted.ToHashSet();
        for (var i = _shown.Count - 1; i >= 0; i--)
            if (!keep.Contains(_shown[i])) _shown.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < _shown.Count && ReferenceEquals(_shown[i], wanted[i])) continue;
            var at = _shown.IndexOf(wanted[i]);
            if (at >= 0) _shown.Move(at, i);
            else _shown.Insert(i, wanted[i]);
        }
        if (selected is not null && _shown.Contains((ProjectRow)selected)) Rows.SelectedItem = selected;
        UpdateSelection();
    }

    private static Func<ProjectRow, object?> SortKey(string by) => by switch
    {
        "Name" => r => r.Name.ToLowerInvariant(),
        "Url" => r => r.Url.ToLowerInvariant(),
        "Dnn" => r => r.Dnn,
        "Database" => r => r.Database.ToLowerInvariant(),
        "Sql" => r => r.Sql,
        "CpuSort" => r => r.CpuSort,
        "MemorySort" or "MemoryPercent" => r => r.MemorySort,
        "PidText" => r => r.PidText,
        "LastStartedSort" => r => r.LastStartedSort,
        "State" => r => r.State,
        "PortsText" => r => r.PortsText,
        "IdSort" => r => r.IdSort,
        "SizeBytes" => r => r.SizeBytes,
        "DiskSort" => r => r.DiskSort,
        "NetworkSort" => r => r.NetworkSort,
        "Path" => r => r.Path.ToLowerInvariant(),
        _ => r => r.Name
    };

    /// <summary>The small indicator where Refresh used to be: only while reconnecting.</summary>
    private void ShowConnection()
    {
        if (_store is null) return;
        LiveIndicator.IsVisible = _store.Connection == MonitorConnection.Reconnecting;
        UpdateSelection();
    }

    private string LiveTip()
    {
        var last = _store?.LastSync is { } at ? $"{Environment.NewLine}Last synchronised {at:HH:mm:ss}." : "";
        return (_store?.ConnectionDetail ?? "Synchronising again.") + Environment.NewLine + "What is shown stays as it was until that works again." + last;
    }

    private void ShowOverlay(string? text)
    {
        OverlayText.Text = text ?? "";
        Overlay.IsVisible = text is not null;
    }

    // ─── Search, counts and check boxes ───────────────────────────────────

    private string SearchText => (SearchBox.Text ?? "").Trim();

    // The table lists the DNN sites in IIS - IIS's Default Web Site and other sites without DNN aren't projects.
    private IEnumerable<ProjectRow> DnnSites => _store!.Projects.Where(r => r.IsDnn);

    private bool Passes(ProjectRow row) => row.IsDnn &&
        (OnlyRunning.IsChecked != true || row.State == SiteRunState.Running) && (SearchText.Length == 0 || row.Matches(SearchText));

    /// <summary>The checked rows the search shows - what the bulk actions act on.</summary>
    private List<ProjectRow> Checked => _shown.Where(r => r.IsChecked).ToList();

    private void Search_TextChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(SearchBox.Text)) return;
        SearchBox.Text = "";
        e.Handled = true;
    }

    private async void OnlyRunning_Changed(object? sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        await Task.Delay(SwitchSlide);
        Refresh();
    }

    // Select all when not every shown row is checked, else none.
    private void SelectAll_Click(object? sender, RoutedEventArgs e) => ToggleAll();

    private void ToggleAll()
    {
        var check = !_shown.All(r => r.IsChecked);
        foreach (var row in _shown) row.IsChecked = check;
        UpdateSelection();
    }

    /// <summary>The counts under the table, the header check box and the bulk buttons, after any change.</summary>
    private void UpdateSelection()
    {
        if (_store is null) return;
        var total = DnnSites.Count();
        var shown = _shown.ToList();
        var chosen = shown.Where(r => r.IsChecked).ToList();
        var noun = total == 1 ? "project" : "projects";
        var loaded = _store.IsLoaded;

        CountText.Text = !loaded ? "" : shown.Count == total ? $"{total} {noun}" : $"{shown.Count} of {total} {noun}";
        SelectedText.Text = chosen.Count > 0 ? $"Selected {chosen.Count} of {total}" : "";
        SelectAll.IsChecked = chosen.Count == 0 ? false : chosen.Count == shown.Count ? true : null;
        SelectAll.IsEnabled = shown.Count > 0;

        BulkActions.IsVisible = chosen.Count > 0;
        BulkStartButton.IsEnabled = chosen.Any(r => r.CanStart);
        BulkStopButton.IsEnabled = chosen.Any(r => r.CanStop);
        BulkRestartButton.IsEnabled = chosen.Any(r => r.CanRestart);
        BulkRemoveButton.IsEnabled = chosen.Count > 0 && chosen.All(r => r.CanRemove);

        // Keep warm: on for those that aren't yet - or, when all that can be are, off for them all.
        var warmable = chosen.Where(r => r.CanToggleKeepWarm).ToList();
        var allWarm = warmable.Count > 0 && warmable.All(r => r.KeepWarmOn);
        BulkKeepWarmButton.IsEnabled = warmable.Count > 0;
        BulkKeepWarmIcon.Data = ThemeBrush.Geometry(allWarm ? "FlameFilled" : "FlameOutline");
        BulkKeepWarmIcon.Fill = ThemeBrush.Get(allWarm ? "KeepWarmFg" : "TextPrimary", this);
        var warmTip = allWarm ? "Stop keeping the selected websites warm" : "Keep the selected websites warm, so they open fast after a while without visits";
        ToolTip.SetTip(BulkKeepWarmButton, warmTip);
        Avalonia.Automation.AutomationProperties.SetName(BulkKeepWarmButton, allWarm ? "Stop keeping the selected websites warm" : "Keep the selected websites warm");

        // A message in the middle only while there are no rows to show. Never over rows that are there.
        ShowOverlay(!loaded ? "Loading projects…"
            : total == 0 ? _store.Connection == MonitorConnection.Reconnecting && _store.ConnectionDetail is { } problem ? problem
                : _store.Runtime == IisServerState.NotInstalled
                    ? "IIS isn't installed - enable its Windows features in Settings → IIS."
                    : "IIS has no DNN websites yet - create one with New project."
            : shown.Count > 0 ? null
            : OnlyRunning.IsChecked != true ? $"No project matches “{SearchText}”."
            : SearchText.Length == 0 ? "No project is running."
            : $"No running project matches “{SearchText}”.");
    }

    // ─── Actions ──────────────────────────────────────────────────────────

    private void BulkStart_Click(object? sender, RoutedEventArgs e) => ControlSites(SiteAction.Start, Checked.Where(r => r.CanStart).ToList());
    private void BulkStop_Click(object? sender, RoutedEventArgs e) => ControlSites(SiteAction.Stop, Checked.Where(r => r.CanStop).ToList());
    private void BulkRestart_Click(object? sender, RoutedEventArgs e) => ControlSites(SiteAction.Restart, Checked.Where(r => r.CanRestart).ToList());
    private void BulkRemove_Click(object? sender, RoutedEventArgs e) => Remove(Checked);

    private void BulkKeepWarm_Click(object? sender, RoutedEventArgs e)
    {
        var warmable = Checked.Where(r => r.CanToggleKeepWarm).ToList();
        var on = !warmable.All(r => r.KeepWarmOn);
        foreach (var row in warmable.Where(r => r.KeepWarmOn != on)) ToggleKeepWarm(row);
        UpdateSelection();
    }

    private void RowStart_Click(object? sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Start, [row]));
    private void RowStop_Click(object? sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Stop, [row]));
    private void RowRestart_Click(object? sender, RoutedEventArgs e) => OnRow(sender, row => ControlSites(SiteAction.Restart, [row]));
    private void RowRemove_Click(object? sender, RoutedEventArgs e) => OnRow(sender, row => Remove([row]));
    private void RowKeepWarm_Click(object? sender, RoutedEventArgs e) => OnRow(sender, ToggleKeepWarm);
    private void RowOpen_Click(object? sender, RoutedEventArgs e) => OnRow(sender, OpenProject);

    private void OpenSite_Click(object? sender, RoutedEventArgs e) => OnRow(sender, row =>
    {
        if (row.HasUrl) ProjectShell.Open(row.Url);
    });

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => OnRow(sender, ProjectMenu.OpenFolder);

    private void OnRow(object? sender, Action<ProjectRow> action)
    {
        if (sender is not StyledElement { DataContext: ProjectRow row }) return;
        Rows.SelectedItem = row;
        action(row);
    }

    private async void ControlSites(SiteAction action, IReadOnlyList<ProjectRow> rows)
    {
        if (_store is not null) await _store.ControlSitesAsync(action, rows);
    }

    private async void Remove(IReadOnlyList<ProjectRow> rows)
    {
        if (_store is not null) await _store.RemoveAsync(rows);
    }

    private void ToggleKeepWarm(ProjectRow row) => _store?.ToggleKeepWarm(row);

    // ─── Columns ──────────────────────────────────────────────────────────

    /// <summary>A column was switched: the table follows once the switch has slid over - once for all changed together.</summary>
    private async void ColumnToggled()
    {
        if (_columnsQueued) return;
        _columnsQueued = true;
        await Task.Delay(SwitchSlide);
        _columnsQueued = false;
        ApplyColumns();
        SaveColumns();
    }

    private void ApplyColumns()
    {
        foreach (var option in _columnOptions) TableContent.Classes.Set("hide-" + option.Key, !option.IsVisible);
        // The sites' traffic and folder sizes are only read while their column is there to show them.
        _store?.SetTrafficWanted(_columnOptions.Any(o => o is { Key: "network", IsVisible: true }));
        _store?.SetSizesShown(_columnOptions.Any(o => o is { Key: "size", IsVisible: true }));
    }

    private void ShowAllColumns_Click(object? sender, RoutedEventArgs e) => SetColumns(_ => true);
    private void HideAllColumns_Click(object? sender, RoutedEventArgs e) => SetColumns(_ => false);

    private void DefaultColumns_Click(object? sender, RoutedEventArgs e) =>
        SetColumns(key => AppearanceSettings.DefaultProjectColumns.Contains(key, StringComparer.OrdinalIgnoreCase));

    private void SetColumns(Func<string, bool> visible)
    {
        foreach (var option in _columnOptions) option.IsVisible = visible(option.Key);
    }

    /// <summary>Remembers the shown columns in the settings; failing only means the next start shows the old ones.</summary>
    private void SaveColumns()
    {
        var keys = _columnOptions.Where(o => o.IsVisible).Select(o => o.Key).ToList();
        var settings = _services.GetRequiredService<SettingsStore>();
        try { settings.Update(s => s.Appearance.ProjectColumns = keys); }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException)
        {
            _log.Fail($"Could not save the columns to {settings.Location}: {ex.Message}");
        }
    }

    /// <summary>Opens the column chooser - the Columns button, from the keyboard.</summary>
    public void ChooseColumns() => ColumnsButton.Flyout?.ShowAt(ColumnsButton);

    // ─── Sorting ──────────────────────────────────────────────────────────

    private void Header_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string property }) SortBy(property);
    }

    /// <summary>Sorts by <paramref name="property"/> - the other way round when it is sorted by it already.</summary>
    public void SortBy(string property)
    {
        _sortDescending = _sortBy == property && !_sortDescending;
        _sortBy = property;
        foreach (var header in HeaderGrid.Children.OfType<Button>())
        {
            var text = header.Content as string ?? "";
            text = text.TrimEnd(' ', '▲', '▼');
            header.Content = (string?)header.Tag == property ? $"{text} {(_sortDescending ? "▼" : "▲")}" : text.Length == 0 ? " " : text;
        }
        Refresh();
    }

    /// <summary>The table's visible width: the expanded rows' facts wrap at it, and the filler fills it.</summary>
    private void FitTable()
    {
        var width = HScroll.Viewport.Width > 0 ? HScroll.Viewport.Width : HScroll.Bounds.Width;
        if (width <= 0) return;
        TableContent.MinWidth = width;
        UpdateActionsShift();
        Resources["ProjectsDetailsWidth"] = Math.Max(200, width);
    }

    /// <summary>The Actions column's cells and header are drawn moved by this - see <see cref="UpdateActionsShift"/>.</summary>
    public TranslateTransform ActionsShift { get; } = new();

    /// <summary>
    /// Keeps the Actions column at the right edge of the visible table: its cells and header move left by as much as the
    /// table is scrolled short of its right end (a render transform - no new layout pass).
    /// </summary>
    private void UpdateActionsShift()
    {
        var x = -Math.Max(0, Math.Floor(HScroll.Extent.Width - HScroll.Viewport.Width - HScroll.Offset.X));
        if (ActionsShift.X != x) ActionsShift.X = x;
    }

    // ─── Mouse and keyboard ───────────────────────────────────────────────

    /// <summary>The row <paramref name="source"/> is in - null for the header or the empty space; also null on a button when <paramref name="ignoreButtons"/>.</summary>
    private static ProjectRow? RowAt(object? source, bool ignoreButtons)
    {
        for (var node = source as Visual; node is not null; node = node.GetVisualParent())
        {
            if (ignoreButtons && node is Button or ToggleButton or CheckBox) return null;
            if (node is ListBoxItem { DataContext: ProjectRow row }) return row;
        }
        return null;
    }

    private void Rows_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowAt(e.Source, ignoreButtons: true) is { } row) OpenProject(row);
    }

    // A click anywhere on a row - its name or a button too - selects it and puts the keyboard in the table.
    private void Rows_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (RowAt(e.Source, ignoreButtons: false) is { } row) Rows.SelectedItem = row;
    }

    private void Rows_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // A right-click selects the row under the mouse, like Explorer; from the keyboard (Menu key / Shift+F10) the selection.
        var row = RowAt(e.Source, ignoreButtons: false) ?? (e.TryGetPosition(Rows, out _) ? null : Rows.SelectedItem as ProjectRow)
                  ?? Rows.SelectedItem as ProjectRow;
        e.Handled = true;
        if (row is null) return;
        Rows.SelectedItem = row;
        var menu = _menu.Build(row);
        var target = Rows.ContainerFromItem(row) as Control ?? Rows;
        menu.Open(target);
    }

    // Space checks / unchecks the selected row; Enter opens it; Del removes it (asking first); Right shows its facts,
    // Left hides them; Ctrl+A ticks every row shown - or none when all are. F5, Shift+F5 and Ctrl+Shift+R start, stop
    // and restart it.
    private void Rows_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.A && command && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ToggleAll();
            e.Handled = true;
            return;
        }
        if (Rows.SelectedItem is not ProjectRow row) return;
        if (RowCommand(row, e)) e.Handled = true;
    }

    private bool RowCommand(ProjectRow row, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None) { if (row.CanStart) ControlSites(SiteAction.Start, [row]); return true; }
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.Shift) { if (row.CanStop) ControlSites(SiteAction.Stop, [row]); return true; }
        if (e.Key == Key.R && command && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { if (row.CanRestart) ControlSites(SiteAction.Restart, [row]); return true; }
        if (e.KeyModifiers != KeyModifiers.None) return false;
        switch (e.Key)
        {
            case Key.Space: row.IsChecked = !row.IsChecked; return true;
            case Key.Right: row.IsExpanded = true; return true;
            case Key.Left: row.IsExpanded = false; return true;
            case Key.Enter: OpenProject(row); return true;
            case Key.Delete:
                if (row.CanRemove) Remove([row]);
                return true;
            default: return false;
        }
    }

    // Ctrl+F: the search box, its text selected.
    private void View_KeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key == Key.F && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (command && e.Key == Key.W && DetailsOpen)
        {
            CloseDetails();
            e.Handled = true;
        }
    }

    // ─── A site's overview ────────────────────────────────────────────────

    /// <summary>Shows <paramref name="row"/>'s overview in place of the table (which keeps its search, selection and order).</summary>
    public void OpenProject(ProjectRow row)
    {
        var view = new ProjectView(row, _services, action => ControlSites(action, [row]));
        view.BackRequested += (_, _) => CloseProject();
        ProjectHost.Content = view;
        ProjectHost.IsVisible = true;
        TableView.IsVisible = false;
    }

    private void CloseProject()
    {
        var open = (ProjectHost.Content as ProjectView)?.Row;
        ProjectHost.Content = null;
        ProjectHost.IsVisible = false;
        TableView.IsVisible = true;
        if (open is not null && _shown.Contains(open))
        {
            Rows.SelectedItem = open;
            Dispatcher.UIThread.Post(() => (Rows.ContainerFromItem(open) as Control ?? Rows).Focus(), DispatcherPriority.Input);
        }
        else Rows.Focus();
    }

    /// <summary>The project the keyboard's commands act on: the one whose Details are open, else the table's selected row.</summary>
    public ProjectRow? CurrentProject => ProjectHost.Content is ProjectView view ? view.Row : Rows.SelectedItem as ProjectRow;

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
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    // ─── Notices ──────────────────────────────────────────────────────────

    private void ShowNotice(Notice notice)
    {
        if (_store is null) return;
        NoticeText.Text = notice.Message;
        NoticeText.Foreground = notice.Kind == NoticeKind.Warning ? ThemeBrush.Get("LogWarn", this) : ThemeBrush.Get("TextPrimary", this);
        _noticeAction = notice.Action;
        NoticeAction.Content = notice.ActionText;
        NoticeAction.IsVisible = notice.ActionText is not null && notice.Action is not null;
        NoticeBox.IsVisible = true;
        _noticeTimer?.Dispose();
        _noticeTimer = DispatcherTimer.RunOnce(() => NoticeBox.IsVisible = false, TimeSpan.FromSeconds(notice.Kind == NoticeKind.Warning ? 10 : 6));
    }

    private void NoticeClose_Click(object? sender, RoutedEventArgs e)
    {
        _noticeTimer?.Dispose();
        NoticeBox.IsVisible = false;
    }

    private void NoticeAction_Click(object? sender, RoutedEventArgs e)
    {
        NoticeBox.IsVisible = false;
        _noticeAction?.Invoke();
    }
}
