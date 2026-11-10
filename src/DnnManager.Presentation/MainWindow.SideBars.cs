using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation;

/// <summary>
/// A page with a view of its own for the window's primary side bar - as VS Code's Source Control shows its changes there,
/// beside the diff in the editor.
/// </summary>
public interface ISideBarPage
{
    /// <summary>The view - shown in the primary side bar while the page is. Not in the page's own tree.</summary>
    FrameworkElement SideBar { get; }
}

// The side bars, as VS Code's: the primary beside the activity bar - the page's own view (ISideBarPage), Ctrl+B - and the
// secondary on the page's other side, empty for now, Ctrl+Alt+B. Each a card of the workbench, slid open or closed, its
// width dragged on the sash between it and the page and kept in the window area of the state.
public partial class MainWindow
{
    // Shown - the primary only on a page with a view for it; remembered for those that have one.
    private bool _primaryVisible = true, _secondaryVisible;
    // Their widths as dragged - null: their own.
    private double? _primaryWidth, _secondaryWidth;
    // Their widths now: sliding, dragged, or settled.
    private double _primaryNow, _secondaryNow;
    // Sliding closed - shown until they are.
    private bool _primaryClosing, _secondaryClosing;
    private readonly Slide _primarySlide = new(), _secondarySlide = new();

    private const double DefaultSideBarWidth = 300, MinSideBarWidth = 170, MaxSideBarWidth = 800;

    private FrameworkElement? PageSideBar => (PageHost.Content as ISideBarPage)?.SideBar;

    private bool PrimaryShown => _primaryVisible && PageSideBar is not null;

    private double PrimaryWidth => _primaryWidth ?? DefaultSideBarWidth;

    private double SecondaryWidth => _secondaryWidth ?? DefaultSideBarWidth;

    /// <summary>The side bars as the last start left them.</summary>
    private void RestoreSideBars(WindowLayout layout)
    {
        _primaryVisible = !layout.PrimarySideBarHidden;
        _secondaryVisible = layout.SecondarySideBarOpen;
        _primaryWidth = Kept(layout.PrimarySideBarWidth);
        _secondaryWidth = Kept(layout.SecondarySideBarWidth);
        _secondaryNow = _secondaryVisible ? SecondaryWidth : 0;

        static double? Kept(double? width) => width is >= MinSideBarWidth and <= MaxSideBarWidth ? width : null;
    }

    /// <summary>Customize Layout's reset: the primary shown, the secondary hidden, both at their own widths.</summary>
    private void ResetSideBars()
    {
        _primarySlide.Stop();
        _secondarySlide.Stop();
        _primaryClosing = _secondaryClosing = false;
        _primaryVisible = true;
        _secondaryVisible = false;
        _primaryWidth = _secondaryWidth = null;
        _primaryNow = PrimaryShown ? PrimaryWidth : 0;
        _secondaryNow = 0;
    }

    /// <summary>
    /// The side bars on their sides - the primary beside the activity bar, the secondary across the page from it - shown or
    /// hidden at their widths, with their sashes: in the gap between the cards, or (Compact) over the line, half on either
    /// side of it.
    /// </summary>
    private void ApplySideBars()
    {
        var right = _layout.SidebarRight;
        Grid.SetColumn(PrimarySideBar, right ? RightSideIndex : LeftSideIndex);
        Grid.SetColumn(PrimarySash, right ? RightSideIndex - 1 : LeftSideIndex + 1);
        Grid.SetColumn(SecondarySideBar, right ? LeftSideIndex : RightSideIndex);
        Grid.SetColumn(SecondarySash, right ? LeftSideIndex + 1 : RightSideIndex - 1);
        ApplySideBar(primary: true);
        ApplySideBar(primary: false);
    }

    private void ApplySideBar(bool primary)
    {
        var right = _layout.SidebarRight;
        var compact = _layout.Compact;
        var onLeft = primary != right;
        var (card, sash, column, gap) = onLeft
            ? (primary ? PrimarySideBar : SecondarySideBar, primary ? PrimarySash : SecondarySash, WorkbenchLeft, WorkbenchLeftGap)
            : (primary ? PrimarySideBar : SecondarySideBar, primary ? PrimarySash : SecondarySash, WorkbenchRight, WorkbenchRightGap);
        var shown = primary ? PrimaryShown : _secondaryVisible;
        var width = Math.Max(0, primary ? _primaryNow : _secondaryNow);
        // Full height beside the panel - or above it, where the panel's alignment reaches under it.
        var (reachLeft, reachRight) = PanelReach;
        var panelUnder = onLeft ? reachLeft : reachRight;
        Grid.SetRowSpan(card, panelUnder ? 1 : 3);
        Grid.SetRowSpan(sash, panelUnder ? 1 : 3);
        card.Margin = new Thickness(0, 0, 0, compact || panelUnder ? 0 : StatusGap);
        card.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        column.Width = new GridLength(shown ? width : 0);
        gap.Width = new GridLength(shown && !compact ? CardGap : 0);
        sash.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        sash.IsGripVisible = !compact;
        sash.Width = compact ? SashWidth : double.NaN;
        sash.Margin = compact ? new Thickness(-SashWidth / 2, 0, -SashWidth / 2, 0) : new Thickness(0);
    }

    /// <summary>The page shown: its view in the primary side bar - or none, the primary side bar hidden on this page.</summary>
    private void ShowPageSideBar()
    {
        var view = PageSideBar;
        if (!ReferenceEquals(PrimaryHost.Content, view))
        {
            var hadKeyboard = PrimaryHost.IsKeyboardFocusWithin;
            PrimaryHost.Content = view;
            if (hadKeyboard) FocusPage();
        }
        _primarySlide.Stop();
        _primaryClosing = false;
        _primaryNow = PrimaryShown ? PrimaryWidth : 0;
        ApplySideBar(primary: true);
        UpdateLayoutButtons();
    }

    // ─── Shown or hidden ───────────────────────────────────────────────────

    /// <summary>
    /// Ctrl+B, the title bar's button, Customize Layout: the primary side bar slides open or closed - on a page with a view
    /// for it; hidden, it stays hidden on the others until shown again.
    /// </summary>
    private void TogglePrimarySideBar()
    {
        if (PageSideBar is not null) Toggle(primary: true);
    }

    /// <summary>Ctrl+Alt+B, the title bar's button, Customize Layout: the secondary side bar slides open or closed.</summary>
    private void ToggleSecondarySideBar() => Toggle(primary: false);

    /// <summary>A side bar slides open - to its width - or closed. Pressed again while it slides, it turns back from where it is.</summary>
    private void Toggle(bool primary)
    {
        var slide = primary ? _primarySlide : _secondarySlide;
        var visible = primary ? _primaryVisible : _secondaryVisible;
        var closing = primary ? _primaryClosing : _secondaryClosing;
        var now = primary ? _primaryNow : _secondaryNow;
        if (visible && !closing)
        {
            SetClosing(primary, true);
            slide.Run(now, 0, w => SetSideBarWidth(primary, w), () =>
            {
                SetClosing(primary, false);
                SetSideBarVisible(primary, false);
            });
            return;
        }
        SetClosing(primary, false);
        if (!visible)
        {
            now = 0;
            SetSideBarWidth(primary, 0);
            SetSideBarVisible(primary, true);
        }
        slide.Run(now, primary ? PrimaryWidth : SecondaryWidth, w => SetSideBarWidth(primary, w));
    }

    private void SetClosing(bool primary, bool closing)
    {
        if (primary) _primaryClosing = closing;
        else _secondaryClosing = closing;
    }

    private void SetSideBarVisible(bool primary, bool visible)
    {
        var card = primary ? PrimarySideBar : SecondarySideBar;
        var hadKeyboard = card.IsKeyboardFocusWithin;
        if (primary) _primaryVisible = visible;
        else _secondaryVisible = visible;
        ApplySideBar(primary);
        UpdateLayoutButtons();
        if (hadKeyboard && !visible) FocusPage();
        _workspace.Changed();
    }

    private void SetSideBarWidth(bool primary, double width)
    {
        if (primary) _primaryNow = width;
        else _secondaryNow = width;
        ApplySideBar(primary);
    }

    // ─── Resizing ───────────────────────────────────────────────────────────

    // Where a side bar's drag started: the pointer, and its width then.
    private double _sideDragFrom, _sideDragWidth;

    /// <summary>The widest a side bar can be: what leaves the page its least width beside the other one.</summary>
    private double MaxSideBarWidthNow(bool primary)
    {
        var other = primary ? (_secondaryVisible ? _secondaryNow + CardGap : 0) : (PrimaryShown ? _primaryNow + CardGap : 0);
        var room = Body.ActualWidth - SidebarColumn.ActualWidth - EdgeLeft.ActualWidth - EdgeRight.ActualWidth;
        return Math.Clamp(room - other - CardGap - MinPageWidth, MinSideBarWidth, MaxSideBarWidth);
    }

    private void SideBarSash_DragStarted(object sender, DragStartedEventArgs e)
    {
        var primary = sender == PrimarySash;
        (primary ? _primarySlide : _secondarySlide).Stop();
        SetClosing(primary, false);
        _sideDragFrom = Mouse.GetPosition(Body).X;
        _sideDragWidth = primary ? _primaryNow : _secondaryNow;
        ((Sash)sender).IsActive = true;
    }

    /// <summary>Dragged: the side bar follows the pointer, from nothing up to its greatest width - it settles when let go.</summary>
    private void SideBarSash_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var primary = sender == PrimarySash;
        var moved = Mouse.GetPosition(Body).X - _sideDragFrom;
        // Toward the page: to the right for the side bar on the left.
        var onLeft = primary != _layout.SidebarRight;
        SetSideBarWidth(primary, Math.Clamp(_sideDragWidth + (onLeft ? moved : -moved), 0, MaxSideBarWidthNow(primary)));
    }

    /// <summary>
    /// Let go: as wide as it was left, kept as the user's width - or, narrower than its least width, it slides there; under
    /// half of it, it slides closed (its width before the drag kept for when it opens).
    /// </summary>
    private void SideBarSash_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var primary = sender == PrimarySash;
        ((Sash)sender).IsActive = false;
        var slide = primary ? _primarySlide : _secondarySlide;
        var width = primary ? _primaryNow : _secondaryNow;
        if (width < MinSideBarWidth / 2)
        {
            SetClosing(primary, true);
            slide.Run(width, 0, w => SetSideBarWidth(primary, w), () =>
            {
                SetClosing(primary, false);
                SetSideBarVisible(primary, false);
            });
            return;
        }
        var kept = Math.Max(MinSideBarWidth, width);
        if (primary) _primaryWidth = kept;
        else _secondaryWidth = kept;
        if (kept > width) slide.Run(width, kept, w => SetSideBarWidth(primary, w));
        _workspace.Changed();
    }

    /// <summary>The primary side bar wider (or narrower) by <paramref name="by"/> - its sash, from the keyboard.</summary>
    private void ResizePrimaryBy(double by)
    {
        if (!PrimaryShown) return;
        _primarySlide.Stop();
        var width = Math.Clamp(_primaryNow + by, MinSideBarWidth, MaxSideBarWidthNow(primary: true));
        _primaryWidth = width;
        SetSideBarWidth(true, width);
        _workspace.Changed();
    }
}
