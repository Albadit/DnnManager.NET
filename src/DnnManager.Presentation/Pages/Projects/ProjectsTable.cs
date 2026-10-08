using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
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
/// </summary>
public sealed class ProjectsTable : DataGrid
{
    // One peer per row container for as long as it lives: UI Automation goes by an element's identity.
    private readonly ConditionalWeakTable<DataGridRow, RowPeer> _rows = new();

    protected override AutomationPeer OnCreateAutomationPeer() => new TablePeer(this);

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

    private sealed class TablePeer(ProjectsTable owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ProjectsTable);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

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

    /// <summary>A row: named after its project; its children are what can be operated in it.</summary>
    private sealed class RowPeer(DataGridRow row) : FrameworkElementAutomationPeer(row)
    {
        protected override string GetClassNameCore() => nameof(DataGridRow);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetNameCore() => row.Item is ProjectRow project ? project.AutomationName : "";

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
