using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// A stop on the tour: the part of the window it is about (null, or not shown: the stop is passed over), what to say
/// about it (<c>**bold**</c> allowed), and what to do first so it can be seen (open the panel, show Projects).
/// </summary>
public sealed record TourStop(string Title, string Text, Func<FrameworkElement?> Target, Action? Enter = null);

/// <summary>
/// The tour of the window, after the getting started guide or from Help: the window dimmed but for one part at a time,
/// ringed in blue, with a card beside it saying what it is for. Back / Next (← / →), Skip tour (Esc), Finish on the last.
/// While it is open nothing under it can be clicked; it follows the part as the window is resized.
/// </summary>
public partial class GuidedTour : UserControl
{
    private const double Gap = 12, Edge = 12, HolePadding = 4;

    private IReadOnlyList<TourStop> _stops = [];
    private int _index = -1;
    // The first stop shown - Back goes no further.
    private int _first = -1;
    private FrameworkElement? _target;
    private Rect _hole = Rect.Empty;
    private Size _size;

    public GuidedTour()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Place();
        // Measured before it is laid out, the card's height can fall short of what its wrapped text takes - placed again
        // at its real size, so it never covers the part it is about.
        Callout.SizeChanged += (_, e) =>
        {
            if (!e.HeightChanged || _target is null) return;
            _hole = Rect.Empty;
            Place();
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>The tour is over - true when it was finished, false when it was skipped.</summary>
    public event EventHandler<bool>? Ended;

    public void Start(IReadOnlyList<TourStop> stops)
    {
        if (IsOpen || stops.Count == 0) return;
        _stops = stops;
        _index = _first = -1;
        Visibility = Visibility.Visible;
        // Every layout of the window may move the part (a column appearing, the panel sliding): the ring follows it.
        // Only while the tour is open - closed, it costs nothing.
        LayoutUpdated += OnLayoutUpdated;
        Go(0, 1);
    }

    /// <summary>
    /// Shows stop <paramref name="index"/> - or, when its part can't be seen, the next one in <paramref name="direction"/>.
    /// The stop's Enter runs first, and the part is looked for once the window has laid that out.
    /// </summary>
    private void Go(int index, int direction)
    {
        if (!IsOpen) return;
        if (index >= _stops.Count)
        {
            End(finished: true);
            return;
        }
        if (index < 0)
        {
            index = 0;
            direction = 1;
        }
        var stop = _stops[index];
        stop.Enter?.Invoke();
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsOpen) return;
            if (stop.Target() is { IsVisible: true, ActualWidth: >= 1 } target) Show(index, target);
            else Go(index + direction, direction);
        }, DispatcherPriority.Loaded);
    }

    private void Show(int index, FrameworkElement target)
    {
        _index = index;
        if (_first < 0) _first = index;
        _target = target;
        var stop = _stops[index];
        var last = index == _stops.Count - 1;
        CountText.Text = $"{index + 1} of {_stops.Count}";
        TitleText.Text = stop.Title;
        BodyText.Inlines.Clear();
        GuideDialog.AddText(BodyText.Inlines, stop.Text);
        BackButton.IsEnabled = index > _first;
        NextButton.Content = last ? "Finish" : "Next";
        NextButton.ToolTip = last ? "End the tour" : "The next part (→)";
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        _hole = Rect.Empty;
        Place();
        NextButton.Focus();
    }

    private void End(bool finished)
    {
        if (!IsOpen) return;
        LayoutUpdated -= OnLayoutUpdated;
        Visibility = Visibility.Collapsed;
        _target = null;
        _stops = [];
        Backdrop.Data = null;
        Ended?.Invoke(this, finished);
    }

    private void Next_Click(object sender, RoutedEventArgs e) => Go(_index + 1, 1);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_index > _first) Go(_index - 1, -1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => End(finished: false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                End(finished: false);
                break;
            case Key.Right:
                Go(_index + 1, 1);
                break;
            case Key.Left:
                Back_Click(this, e);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Place();

    // ─── Where the hole and the card go ─────────────────────────────────────

    /// <summary>
    /// The hole around the part and the card beside it - again only when the part moved or the window changed size, so the
    /// layout this causes doesn't place them once more.
    /// </summary>
    private void Place()
    {
        if (!IsOpen || _target is null || ActualWidth <= 0) return;
        var size = new Size(ActualWidth, ActualHeight);
        var hole = Rect.Empty;
        try
        {
            if (_target.IsVisible) hole = _target.TransformToVisual(this).TransformBounds(new Rect(_target.RenderSize));
        }
        catch (InvalidOperationException)
        {
            // No longer in the window (a page was replaced): the card in the middle, nothing ringed.
        }
        if (!hole.IsEmpty)
        {
            hole.Inflate(HolePadding, HolePadding);
            hole.Intersect(new Rect(size));
        }
        if (hole == _hole && size == _size) return;
        _hole = hole;
        _size = size;

        var window = new RectangleGeometry(new Rect(size));
        Backdrop.Data = hole.IsEmpty ? window : new CombinedGeometry(GeometryCombineMode.Exclude, window, new RectangleGeometry(hole, 6, 6));
        Ring.Visibility = hole.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!hole.IsEmpty)
        {
            Canvas.SetLeft(Ring, hole.X);
            Canvas.SetTop(Ring, hole.Y);
            Ring.Width = hole.Width;
            Ring.Height = hole.Height;
        }
        PlaceCallout(hole, size);
    }

    /// <summary>
    /// The card below the part, else above, right or left of it - wherever it fits whole; over the middle of a part too
    /// big for any of those (the Projects table). Always inside the window.
    /// </summary>
    private void PlaceCallout(Rect hole, Size size)
    {
        Callout.Width = Math.Max(200, Math.Min(340, size.Width - 2 * Edge));
        Callout.Measure(new Size(Callout.Width, double.PositiveInfinity));
        var card = new Size(Callout.Width, Math.Max(Callout.DesiredSize.Height, Callout.ActualHeight));
        double x, y;
        if (hole.IsEmpty)
            (x, y) = ((size.Width - card.Width) / 2, (size.Height - card.Height) / 2);
        else if (size.Height - hole.Bottom >= card.Height + Gap + Edge)
            (x, y) = (hole.Left, hole.Bottom + Gap);
        else if (hole.Top >= card.Height + Gap + Edge)
            (x, y) = (hole.Left, hole.Top - Gap - card.Height);
        else if (size.Width - hole.Right >= card.Width + Gap + Edge)
            (x, y) = (hole.Right + Gap, hole.Top);
        else if (hole.Left >= card.Width + Gap + Edge)
            (x, y) = (hole.Left - Gap - card.Width, hole.Top);
        else
            (x, y) = (hole.Left + (hole.Width - card.Width) / 2, hole.Top + (hole.Height - card.Height) / 2);
        // On whole pixels: between two, its text is drawn across both - blurred.
        Canvas.SetLeft(Callout, Math.Round(Math.Clamp(x, Edge, Math.Max(Edge, size.Width - card.Width - Edge))));
        Canvas.SetTop(Callout, Math.Round(Math.Clamp(y, Edge, Math.Max(Edge, size.Height - card.Height - Edge))));
    }
}
