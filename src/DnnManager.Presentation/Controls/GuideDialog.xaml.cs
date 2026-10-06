using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>How the guide was left.</summary>
public enum GuideResult
{
    /// <summary>Skip guide, Esc or the window's ✕ - before its last page, or a help page closed.</summary>
    Skipped,
    /// <summary>Its last page's finish button.</summary>
    Finished,
    /// <summary>Take the tour, on the last page: the window's tour follows.</summary>
    Tour
}

/// <summary>
/// The getting started guide - and, with fewer pages, a page's help or what a new version added: one page at a time,
/// the pages listed on the left, Back and Next at the bottom (Enter is Next), Skip guide or Esc to leave at any time.
/// Each page is built from a <see cref="GuideStep"/> (Services/Onboarding).
/// </summary>
public partial class GuideDialog : Window
{
    private readonly IReadOnlyList<GuideStep> _steps;
    private readonly string _finishText;
    private readonly bool _offerTour;
    // The step list's number circles - a tick once a page was seen.
    private readonly List<(Border Circle, TextBlock Mark)> _marks = [];
    private readonly HashSet<int> _seen = [];
    private int _index = -1;
    private GuideResult _result = GuideResult.Skipped;

    private GuideDialog(string title, IReadOnlyList<GuideStep> steps, string skipText, string finishText, bool offerTour)
    {
        InitializeComponent();
        ThemeManager.Track(this);
        _steps = steps;
        _finishText = finishText;
        _offerTour = offerTour;
        Title = title;
        StepsTitle.Text = title;
        SkipButton.Content = skipText;
        // A short screen (a 768 px laptop) keeps the buttons on it.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        if (steps.Count < 2)
        {
            StepsPane.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            Width = 760;
        }
        for (var i = 0; i < steps.Count; i++)
        {
            var item = new ListBoxItem { Content = StepEntry(i, steps[i]) };
            System.Windows.Automation.AutomationProperties.SetName(item, $"Step {i + 1}: {steps[i].ShortTitle}");
            StepList.Items.Add(item);
        }
        ShowStep(0);
        Loaded += (_, _) => NextButton.Focus();
    }

    /// <summary>
    /// Shows <paramref name="steps"/> and returns how they were left. <paramref name="offerTour"/> adds Take the tour to
    /// the last page; <paramref name="finishText"/> is its finish button.
    /// </summary>
    public static GuideResult Open(string title, IReadOnlyList<GuideStep> steps, string skipText = "Close", string finishText = "Done",
        bool offerTour = false)
    {
        if (steps.Count == 0) return GuideResult.Finished;
        var dialog = new GuideDialog(title, steps, skipText, finishText, offerTour)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void ShowStep(int index)
    {
        if (index < 0 || index >= _steps.Count || index == _index) return;
        _index = index;
        _seen.Add(index);
        StepList.SelectedIndex = index;
        var last = index == _steps.Count - 1;
        BackButton.IsEnabled = index > 0;
        NextButton.Content = last ? _finishText : "Next";
        NextButton.ToolTip = last ? null : "The next page (Enter)";
        TourButton.Visibility = last && _offerTour ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = _steps.Count > 1 ? $"Step {index + 1} of {_steps.Count}" : "";
        for (var i = 0; i < _marks.Count; i++) Mark(i);

        Body.Children.Clear();
        Build(_steps[index]);
        Scroller.ScrollToTop();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _steps.Count - 1)
        {
            ShowStep(_index + 1);
            return;
        }
        _result = GuideResult.Finished;
        Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_index - 1);

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void Tour_Click(object sender, RoutedEventArgs e)
    {
        _result = GuideResult.Tour;
        Close();
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepList.SelectedIndex >= 0) ShowStep(StepList.SelectedIndex);
    }

    // ─── A page ─────────────────────────────────────────────────────────────

    private void Build(GuideStep step)
    {
        var title = Paragraph(step.Title);
        title.FontWeight = FontWeights.SemiBold;
        title.SetResourceReference(TextBlock.FontSizeProperty, "TextTitle");
        Body.Children.Add(title);

        var intro = Paragraph(step.Intro);
        intro.Margin = new Thickness(0, 8, 0, 0);
        intro.SetResourceReference(TextBlock.FontSizeProperty, "TextMedium");
        Body.Children.Add(intro);

        foreach (var section in step.Sections)
        {
            var heading = Paragraph(section.Heading);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 24, 0, 4);
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            Body.Children.Add(heading);
            foreach (var item in section.Items) Body.Children.Add(ItemRow(item));
        }

        if (step.Tip is { } tip)
        {
            var text = Paragraph(tip);
            var glyph = new TextBlock { Margin = new Thickness(0, 1, 10, 0), FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
            glyph.SetResourceReference(TextBlock.TextProperty, "GlyphInfo");
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var row = new DockPanel();
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(text);
            var banner = new Border { Child = row, Margin = new Thickness(0, 24, 0, 0) };
            banner.SetResourceReference(StyleProperty, "InfoBanner");
            Body.Children.Add(banner);
        }
    }

    /// <summary>An item: its icon in a small rounded box, its name (and how much it can change), what it means or does.</summary>
    private FrameworkElement ItemRow(GuideItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var box = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left, Child = IconFor(item.Icon)
        };
        box.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        grid.Children.Add(box);

        var name = new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var head = new WrapPanel();
        head.Children.Add(name);
        if (Badge(item.Risk) is { } badge) head.Children.Add(badge);

        var text = Paragraph(item.Text);
        text.Margin = new Thickness(0, 2, 0, 0);
        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(head);
        column.Children.Add(text);
        Grid.SetColumn(column, 1);
        grid.Children.Add(column);
        return grid;
    }

    /// <summary>The icon an item names (see <see cref="GuideItem"/>), drawn as the window draws it.</summary>
    private FrameworkElement IconFor(string icon)
    {
        Ellipse Dot(string? fill, string stroke)
        {
            var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.StrokeProperty, stroke);
            if (fill is not null) dot.SetResourceReference(Shape.FillProperty, fill);
            return dot;
        }

        switch (icon)
        {
            case "Dot.Running": return Dot("SuccessText", "SuccessText");
            case "Dot.Stopped": return Dot(null, "TextMuted");
            case "Dot.Busy": return Dot("LogWarn", "LogWarn");
        }

        var glyph = new TextBlock { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        if (icon.All(char.IsAsciiDigit))
        {
            // A step in a list.
            glyph.Text = icon;
            glyph.FontWeight = FontWeights.SemiBold;
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            return glyph;
        }
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        switch (TryFindResource(icon == "Dot.None" ? "GlyphDash" : icon))
        {
            case string text:
                glyph.Text = text;
                if (icon == "Dot.None") glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                return glyph;
            // The flame is filled, as in the Projects table; the layout icons are outlines.
            case Geometry flame when icon.StartsWith("Flame", StringComparison.Ordinal):
                var fill = new System.Windows.Shapes.Path { Data = flame, Stretch = Stretch.Uniform, Width = 14, Height = 14 };
                fill.SetResourceReference(Shape.FillProperty, "KeepWarmFg");
                return fill;
            case Geometry outline:
                var path = new System.Windows.Shapes.Path { Data = outline, Style = (Style)FindResource("IconStroke"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                path.SetResourceReference(Shape.StrokeProperty, "TextPrimary");
                return path;
            default:
                return glyph;
        }
    }

    /// <summary>How much an action can change, next to its name - nothing for a plain explanation.</summary>
    private static Border? Badge(GuideRisk risk)
    {
        var (text, colour) = risk switch
        {
            GuideRisk.Safe => ("Safe", "SuccessText"),
            GuideRisk.Careful => ("Careful", "LogWarn"),
            GuideRisk.Destructive => ("Can't be undone", "ErrorText"),
            _ => ("", "")
        };
        if (text.Length == 0) return null;
        var label = new TextBlock { Text = text };
        label.SetResourceReference(TextBlock.FontSizeProperty, "TextSmall");
        label.SetResourceReference(TextBlock.ForegroundProperty, colour);
        var badge = new Border
        {
            Child = label, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0, 7, 1),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        badge.SetResourceReference(Border.BorderBrushProperty, colour);
        return badge;
    }

    private static TextBlock Paragraph(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        AddText(block.Inlines, text);
        return block;
    }

    /// <summary><paramref name="text"/> with its <c>**bold**</c> parts bold - also the tour's (GuidedTour).</summary>
    internal static void AddText(InlineCollection inlines, string text)
    {
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) inlines.Add(i % 2 == 1 ? new Bold(new Run(parts[i])) : new Run(parts[i]));
    }

    // ─── The step list ──────────────────────────────────────────────────────

    private FrameworkElement StepEntry(int index, GuideStep step)
    {
        var mark = new TextBlock { Text = (index + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(TextBlock.FontSizeProperty, "TextCaption");
        var circle = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), Child = mark,
            Margin = new Thickness(0, 0, 10, 0)
        };
        _marks.Add((circle, mark));
        var name = new TextBlock { Text = step.ShortTitle, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var entry = new StackPanel { Orientation = Orientation.Horizontal };
        entry.Children.Add(circle);
        entry.Children.Add(name);
        return entry;
    }

    /// <summary>A step's circle: its number in blue while shown, a tick once seen, else its number in grey.</summary>
    private void Mark(int index)
    {
        var (circle, mark) = _marks[index];
        var current = index == _index;
        var seen = _seen.Contains(index) && !current;
        mark.Text = seen ? (string)FindResource("GlyphCheck") : (index + 1).ToString();
        if (seen) mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        else mark.ClearValue(TextBlock.FontFamilyProperty);
        var colour = current || seen ? "Accent" : "TextMuted";
        circle.SetResourceReference(Border.BorderBrushProperty, colour);
        mark.SetResourceReference(TextBlock.ForegroundProperty, colour);
    }
}
