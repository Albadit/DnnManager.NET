using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>
/// The Projects table: WPF's DataGrid with a lean UI Automation tree.
///
/// <para><b>Why.</b> The DataGrid's own automation gives every cell, every text in it and every column's resize grips an
/// element of their own - some 48 a row. Whenever any program on the PC listens to UI Automation (Windows' text input
/// does, as do PowerToys, screen readers and many others), WPF goes through those elements after every layout and tells
/// the listener about each one that changed: the CPU and memory figures every two seconds, every row when the table is
/// sorted or filtered, every button when an operation starts - each a call into the other program. With 19 sites that
/// was up to half a second of the UI thread at a time (a sort, the table shown again).</para>
///
/// <para><b>What it exposes instead.</b> A list of rows, each one element named after its project ("shop, Running,
/// http://shop.dnndev.me") holding only what can be operated in it - its check box, its expand toggle, its links and
/// action buttons - and an expanded row's details; above them the column headers. Plain texts, the figures that change
/// all the time and the resize grips aren't elements: nothing to keep a listener up to date about, and a screen reader
/// reads a row by its project instead of "DnnManager.Presentation.Pages.Projects.ProjectRow".</para>
///
/// <para><b>Selection and focus.</b> The list and its rows have UI Automation's selection patterns, so a screen reader says
/// which row is selected; and the keyboard, which is on a cell of the row (the table's own navigation needs one), is
/// reported as on the row - the cell isn't in the tree (<see cref="OnPreviewGotKeyboardFocus"/>).</para>
/// </summary>
public sealed class ProjectsTable : DataGrid
{
    // One peer per row container for as long as it lives: UI Automation goes by an element's identity.
    private readonly ConditionalWeakTable<DataGridRow, RowPeer> _rows = new();

    protected override AutomationPeer OnCreateAutomationPeer() => new TablePeer(this);

    /// <summary>
    /// A cell is getting the keyboard: its focus event is raised as the row's, which is in the tree - a screen reader
    /// then says the row, not an element it can't find. Only while something listens.
    /// </summary>
    protected override void OnPreviewGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        if (e.NewFocus is not DataGridCell cell || !AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged)) return;
        if (RowOf(cell) is { } row && UIElementAutomationPeer.CreatePeerForElement(cell) is { } cellPeer)
            cellPeer.EventsSource = PeerOf(row);
    }

    /// <summary>The selection changed: the rows that came in and went out say so to a listener.</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (!AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected) &&
            !AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection)) return;
        foreach (var item in e.AddedItems)
            if (ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                PeerOf(row).RaiseAutomationEvent(SelectedItems.Count == 1
                    ? AutomationEvents.SelectionItemPatternOnElementSelected
                    : AutomationEvents.SelectionItemPatternOnElementAddedToSelection);
        foreach (var item in e.RemovedItems)
            if (ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                PeerOf(row).RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection);
    }

    private static DataGridRow? RowOf(DependencyObject element)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is DataGridRow row) return row;
        return null;
    }

    // The panels the rows and the column headers are in - found once (a new template finds them again).
    private DataGridRowsPresenter? _rowsPanel;
    private DataGridColumnHeadersPresenter? _headersPanel;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rowsPanel = null;
        _headersPanel = null;
    }

    private RowPeer PeerOf(DataGridRow row) => _rows.GetValue(row, r => new RowPeer(r));

    private DataGridColumnHeadersPresenter? HeadersPanel => _headersPanel ??= Find<DataGridColumnHeadersPresenter>(this);

    /// <summary>The rows made now (the table is virtualized: those on screen), in the order shown.</summary>
    private IEnumerable<DataGridRow> RealizedRows()
    {
        if ((_rowsPanel ??= Find<DataGridRowsPresenter>(this)) is not { } panel) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
            if (VisualTreeHelper.GetChild(panel, i) is DataGridRow { IsVisible: true } row) yield return row;
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private sealed class TablePeer(ProjectsTable owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
    {
        protected override string GetClassNameCore() => nameof(ProjectsTable);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

        public bool CanSelectMultiple => owner.SelectionMode == DataGridSelectionMode.Extended;

        public bool IsSelectionRequired => false;

        /// <summary>The selected rows on screen (the table is virtualized: a row scrolled away has no element).</summary>
        public IRawElementProviderSimple[] GetSelection() =>
        [
            .. owner.SelectedItems.Cast<object>()
                .Select(item => owner.ItemContainerGenerator.ContainerFromItem(item))
                .OfType<DataGridRow>()
                .Select(row => ProviderFromPeer(owner.PeerOf(row)))
        ];

        internal IRawElementProviderSimple Provider => ProviderFromPeer(this);

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = new List<AutomationPeer>();
            // The column headers (they sort when clicked) - their own peers, as WPF makes them.
            if (owner.HeadersPanel is { IsVisible: true } headers &&
                UIElementAutomationPeer.CreatePeerForElement(headers) is { } headersPeer)
                children.Add(headersPeer);
            foreach (var row in owner.RealizedRows()) children.Add(owner.PeerOf(row));
            return children;
        }
    }

    /// <summary>
    /// A row: named after its project - its name, state, address, DNN version, database and SQL state -, its CPU and memory
    /// as its item status; its children are what can be operated in it. Selected like an item of a list.
    /// </summary>
    private sealed class RowPeer(DataGridRow row) : FrameworkElementAutomationPeer(row), ISelectionItemProvider
    {
        protected override string GetClassNameCore() => nameof(DataGridRow);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetNameCore() => row.Item is ProjectRow project ? project.AutomationName : "";

        protected override string GetItemStatusCore() => row.Item is ProjectRow project ? project.AutomationStatus : "";

        // The keyboard is on one of its cells (or on it).
        protected override bool HasKeyboardFocusCore() =>
            row.IsKeyboardFocused || System.Windows.Input.Keyboard.FocusedElement is DataGridCell cell && RowOf(cell) == row;

        protected override bool IsKeyboardFocusableCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

        private ProjectsTable? Table => ItemsControl.ItemsControlFromItemContainer(row) as ProjectsTable;

        public bool IsSelected => row.IsSelected;

        public IRawElementProviderSimple? SelectionContainer =>
            Table is { } table && UIElementAutomationPeer.CreatePeerForElement(table) is TablePeer peer ? peer.Provider : null;

        public void Select()
        {
            if (Table is not { } table) return;
            table.SelectedItem = row.Item;
            table.ScrollIntoView(row.Item);
        }

        public void AddToSelection()
        {
            if (Table is not { } table) return;
            if (table.SelectionMode == DataGridSelectionMode.Single) table.SelectedItem = row.Item;
            else if (!table.SelectedItems.Contains(row.Item)) table.SelectedItems.Add(row.Item);
        }

        public void RemoveFromSelection()
        {
            if (Table is { } table && table.SelectedItems.Contains(row.Item)) table.SelectedItems.Remove(row.Item);
        }

        protected override List<AutomationPeer> GetChildrenCore()
        {
            var children = new List<AutomationPeer>();
            Collect(row, children);
            return children;
        }

        // The operable controls (buttons, check boxes, toggles) and the details - not the texts beside them.
        private static void Collect(DependencyObject parent, List<AutomationPeer> into)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement { IsVisible: false }) continue;
                if (child is ButtonBase or DataGridDetailsPresenter)
                {
                    if (UIElementAutomationPeer.CreatePeerForElement((UIElement)child) is { } peer) into.Add(peer);
                    continue;
                }
                Collect(child, into);
            }
        }
    }
}
