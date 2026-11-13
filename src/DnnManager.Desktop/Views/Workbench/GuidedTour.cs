using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace DnnManager.Desktop.Views.Workbench;

/// <summary>One stop of the tour: what a part of the window is, and the part (null: not there now - the stop is skipped).</summary>
public sealed record TourStop(string Title, string Text, Func<Control?> Target, Action? Enter = null);

/// <summary>
/// The tour of the window (src/DnnManager.Presentation/Controls/GuidedTour): over the whole window, dimmed, with a hole and
/// a ring around the part a stop is about and a card beside it - Back, Next, Skip tour; ← → and Esc.
/// </summary>
public sealed class GuidedTour : UserControl
{
    private readonly Path _backdrop = new() { IsHitTestVisible = false };
    private readonly Border _ring = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
    private readonly Border _callout;
    private readonly Canvas _stage = new();
    private readonly TextBlock _count = new() { FontSize = 12 }, _title = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly ContentControl _text = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _back = new() { Content = "Back", MinWidth = 70 };
    private readonly Button _next = new() { Content = "Next", MinWidth = 70, Margin = new Thickness(8, 0, 0, 0), Classes = { "primary" } };
    private IReadOnlyList<TourStop> _stops = [];
    private int _index;

    public GuidedTour()
    {
        IsVisible = false;
        Focusable = true;
        Background = Brushes.Transparent;
        _backdrop.Bind(Shape.FillProperty, this.GetResourceObservable("TourBackdrop"));
        _ring.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Accent"));
        _count.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMuted"));
        var skip = new Button { Content = "Skip tour", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(skip, "End the tour (Esc) - Help opens it again");
        skip.Click += (_, _) => End(false);
        _back.Click += (_, _) => Go(_index - 1, -1);
        _next.Click += (_, _) => Next();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _back, _next } };
        DockPanel.SetDock(buttons, Dock.Right);
        _callout = new Border
        {
            Width = 340, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14),
            BoxShadow = BoxShadows.Parse("0 4 24 0 #59000000"),
            Child = new StackPanel { Children = { _count, _title, _text, new DockPanel { Margin = new Thickness(0, 14, 0, 0), Children = { buttons, skip } } } }
        };
        _callout.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBg"));
        _callout.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CardBorder"));
        _stage.Children.Add(_ring);
        _stage.Children.Add(_callout);
        Content = new Panel { Children = { _backdrop, _stage } };
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: End(false); break;
                case Key.Right or Key.Enter: Next(); break;
                case Key.Left: Go(_index - 1, -1); break;
                default: return;
            }
            e.Handled = true;
        };
        SizeChanged += (_, _) => { if (IsOpen) Place(); };
    }

    public bool IsOpen => IsVisible;

    /// <summary>The tour ended - finished (true) or skipped.</summary>
    public event EventHandler<bool>? Ended;

    public void Start(IReadOnlyList<TourStop> stops)
    {
        _stops = stops;
        if (stops.Count == 0) return;
        IsVisible = true;
        Focus();
        Go(0, 1);
    }

    private void Next()
    {
        if (_index >= _stops.Count - 1) End(true);
        else Go(_index + 1, 1);
    }

    private void Go(int index, int direction)
    {
        // A stop whose part isn't there now is passed over.
        while (index >= 0 && index < _stops.Count)
        {
            _stops[index].Enter?.Invoke();
            if (_stops[index].Target() is { IsEffectivelyVisible: true }) break;
            index += direction;
        }
        if (index < 0 || index >= _stops.Count)
        {
            if (direction > 0) End(true);
            return;
        }
        _index = index;
        var stop = _stops[index];
        _count.Text = $"{index + 1} of {_stops.Count}";
        _title.Text = stop.Title;
        _text.Content = GuideDialog.Rich(stop.Text, 13, default);
        _back.IsVisible = index > 0;
        _next.Content = index == _stops.Count - 1 ? "Done" : "Next";
        // Once the part the stop opened (the panel) is laid out.
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
    }

    private void Place()
    {
        var window = new Rect(Bounds.Size);
        var target = _stops.Count > 0 ? _stops[_index].Target() : null;
        var hole = target is not null && target.TranslatePoint(default, this) is { } at
            ? new Rect(at, target.Bounds.Size).Inflate(4).Intersect(window) : default;
        _backdrop.Data = hole.Width <= 0
            ? new RectangleGeometry(window)
            : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(window), new RectangleGeometry(hole, 6, 6));
        _ring.IsVisible = hole.Width > 0;
        Canvas.SetLeft(_ring, hole.X);
        Canvas.SetTop(_ring, hole.Y);
        _ring.Width = hole.Width;
        _ring.Height = hole.Height;

        // The card beside the part: right of it, else left, else below, else above - inside the window.
        _callout.Measure(Size.Infinity);
        var size = _callout.DesiredSize;
        const double gap = 12;
        Point place;
        if (hole.Width <= 0) place = new Point((window.Width - size.Width) / 2, (window.Height - size.Height) / 2);
        else if (hole.Right + gap + size.Width <= window.Width) place = new Point(hole.Right + gap, hole.Y);
        else if (hole.X - gap - size.Width >= 0) place = new Point(hole.X - gap - size.Width, hole.Y);
        else if (hole.Bottom + gap + size.Height <= window.Height) place = new Point(hole.X, hole.Bottom + gap);
        else place = new Point(hole.X, hole.Y - gap - size.Height);
        Canvas.SetLeft(_callout, Math.Clamp(place.X, 8, Math.Max(8, window.Width - size.Width - 8)));
        Canvas.SetTop(_callout, Math.Clamp(place.Y, 8, Math.Max(8, window.Height - size.Height - 8)));
    }

    private void End(bool finished)
    {
        if (!IsOpen) return;
        IsVisible = false;
        Ended?.Invoke(this, finished);
    }
}
