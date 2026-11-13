using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DnnManager.Presentation.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace DnnManager.Desktop.Views.Workbench;

/// <summary>How the getting started guide was left: finished, skipped (or closed), or for the tour.</summary>
public enum GuideResult { Finished, Skipped, Tour }

/// <summary>
/// The getting started guide, a page's help, or the pages a new version added (src/DnnManager.Presentation/Controls/
/// GuideDialog) - one page at a time, the steps on the left, Back and Next below; the texts are Services/Onboarding's.
/// </summary>
public sealed class GuideDialog : Window
{
    private readonly IReadOnlyList<GuideStep> _steps;
    private readonly string _closeText, _doneText;
    private readonly bool _offerTour;
    private readonly HashSet<int> _seen = [];
    private readonly StackPanel _body = new() { Margin = new Thickness(34, 28), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer _scroller;
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly Button _back = new() { Content = "Back", MinWidth = 84 };
    private readonly Button _next = new() { Content = "Next", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Classes = { "primary" } };
    private readonly Button _tour = new() { Content = "Take the tour", Margin = new Thickness(8, 0, 0, 0), IsVisible = false };
    private readonly TextBlock _progress = new() { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
    private int _index;
    private GuideResult _result = GuideResult.Skipped;

    private GuideDialog(string title, IReadOnlyList<GuideStep> steps, string closeText, string doneText, bool offerTour)
    {
        _steps = steps;
        _closeText = closeText;
        _doneText = doneText;
        _offerTour = offerTour;
        Title = title.StartsWith("Help", StringComparison.Ordinal) ? title : $"{title} with DNN Manager";
        Width = 900;
        Height = 660;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var skip = new Button { Content = closeText, Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(skip, "Close the guide (Esc) - the ? at the top right opens it again");
        skip.Click += (_, _) => Close();
        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) =>
        {
            if (_index < _steps.Count - 1) Go(_index + 1);
            else
            {
                _result = GuideResult.Finished;
                Close();
            }
        };
        _tour.Click += (_, _) =>
        {
            _result = GuideResult.Tour;
            Close();
        };
        ToolTip.SetTip(_tour, "Close this guide and point at each part of the window");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _back, _tour, _next } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new Border
        {
            Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel { Children = { buttons, new StackPanel { Orientation = Orientation.Horizontal, Children = { skip, _progress } } } }
        };
        footer.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBg"));
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CardBorder"));
        DockPanel.SetDock(footer, Dock.Bottom);

        _list.ItemsSource = steps.Select((s, i) => StepEntry(s, i)).ToList();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedIndex >= 0 && _list.SelectedIndex != _index) Go(_list.SelectedIndex); };
        var stepsTitle = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(8, 0, 0, 12) };
        DockPanel.SetDock(stepsTitle, Dock.Top);
        var stepsPane = new Border
        {
            Width = 230, BorderThickness = new Thickness(0, 0, 1, 0), IsVisible = steps.Count > 1,
            Child = new DockPanel { Margin = new Thickness(14, 20, 14, 14), Children = { stepsTitle, _list } }
        };
        stepsPane.Bind(Border.BackgroundProperty, this.GetResourceObservable("SidebarBg"));
        stepsPane.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CardBorder"));
        DockPanel.SetDock(stepsPane, Dock.Left);

        _scroller = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new DockPanel { Children = { footer, stepsPane, _scroller } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Go(0);
    }

    /// <summary>Shows the guide over the main window; how it was left.</summary>
    public static async Task<GuideResult> OpenAsync(Window owner, string title, IReadOnlyList<GuideStep> steps, string closeText = "Close",
        string doneText = "Done", bool offerTour = false)
    {
        if (steps.Count == 0) return GuideResult.Finished;
        var dialog = new GuideDialog(title, steps, closeText, doneText, offerTour);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void Go(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        _seen.Add(_index);
        var step = _steps[_index];
        var last = _index == _steps.Count - 1;
        _back.IsVisible = _index > 0;
        _next.Content = last ? _doneText : "Next";
        _tour.IsVisible = _offerTour && last;
        _progress.Text = _steps.Count > 1 ? $"{_index + 1} of {_steps.Count}" : "";
        _list.SelectedIndex = _index;
        if (_list.ItemsSource is List<Control> entries)
            for (var i = 0; i < entries.Count; i++) ((TextBlock)((StackPanel)entries[i]).Children[0]).Text = _seen.Contains(i) && i != _index ? "✓" : $"{i + 1}";

        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = step.Title, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _body.Children.Add(Rich(step.Intro, 14, new Thickness(0, 8, 0, 0)));
        foreach (var section in step.Sections)
        {
            var heading = new TextBlock { Text = section.Heading.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 24, 0, 4) };
            heading.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextMuted"));
            _body.Children.Add(heading);
            foreach (var item in section.Items) _body.Children.Add(Item(item));
        }
        if (step.Tip is { } tip)
        {
            var banner = new Border
            {
                Margin = new Thickness(0, 24, 0, 0), Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Child = Rich(tip, 13, default)
            };
            banner.Bind(Border.BackgroundProperty, this.GetResourceObservable("InfoBannerBg"));
            banner.Bind(Border.BorderBrushProperty, this.GetResourceObservable("InfoBannerBorder"));
            _body.Children.Add(banner);
        }
        _scroller.Offset = default;
    }

    private Control StepEntry(GuideStep step, int index)
    {
        var mark = new TextBlock { Text = $"{index + 1}", Width = 22, FontSize = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { mark, new TextBlock { Text = step.ShortTitle, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } }
        };
    }

    private Control Item(GuideItem item)
    {
        var box = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top, Child = ItemIcon(item.Icon) };
        box.Bind(Border.BackgroundProperty, this.GetResourceObservable("HoverBg"));
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold } } };
        if (Badge(item.Risk) is { } badge) head.Children.Add(badge);
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), Children = { head, Rich(item.Text, 13, new Thickness(0, 2, 0, 0)) } };
        Grid.SetColumn(text, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 7), Children = { box, text } };
    }

    private Control ItemIcon(string icon)
    {
        if (icon.StartsWith("Dot.", StringComparison.Ordinal) && icon != "Dot.None")
        {
            var (fill, stroke) = icon switch { "Dot.Running" => ("SuccessText", "SuccessText"), "Dot.Busy" => ("LogWarn", "LogWarn"), _ => ((string?)null, "TextMuted") };
            var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.StrokeProperty, this.GetResourceObservable(stroke));
            if (fill is not null) dot.Bind(Shape.FillProperty, this.GetResourceObservable(fill));
            return dot;
        }
        var glyph = new TextBlock { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (int.TryParse(icon, out _))
        {
            glyph.Text = icon;
            glyph.FontWeight = FontWeight.SemiBold;
            glyph.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Accent"));
            return glyph;
        }
        var key = icon == "Dot.None" ? "GlyphDash" : icon;
        if (this.TryFindResource(key, out var found) && found is string text && MainWindow.HasIconFont)
        {
            glyph.Text = text;
            glyph.Classes.Add("glyph");
            return glyph;
        }
        if (found is Geometry geometry)
        {
            var path = new Path { Data = geometry, StrokeThickness = 1.2, Width = 16, Height = 16, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            path.Bind(Shape.StrokeProperty, this.GetResourceObservable("TextPrimary"));
            return path;
        }
        glyph.Text = "•";
        return glyph;
    }

    private Border? Badge(GuideRisk risk)
    {
        var (text, colour) = risk switch
        {
            GuideRisk.Safe => ("Safe", "SuccessText"),
            GuideRisk.Careful => ("Careful", "LogWarn"),
            GuideRisk.Destructive => ("Can't be undone", "ErrorText"),
            _ => ((string?)null, "")
        };
        if (text is null) return null;
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(colour));
        var badge = new Border
        {
            Child = label, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        badge.Bind(Border.BorderBrushProperty, this.GetResourceObservable(colour));
        return badge;
    }

    /// <summary>A text with **bold** parts.</summary>
    internal static TextBlock Rich(string text, double size, Thickness margin)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = margin, LineHeight = size * 1.5 };
        var inlines = new InlineCollection();
        var bold = false;
        foreach (var part in text.Split("**"))
        {
            if (part.Length > 0) inlines.Add(new Run(part) { FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal });
            bold = !bold;
        }
        block.Inlines = inlines;
        return block;
    }
}

/// <summary>
/// What's new (src/DnnManager.Presentation/Controls/WhatsNewDialog): the release notes of the versions since the one before
/// an update, newest first - their Markdown shown as it reads (headings, lists, bold), with a link to every release.
/// </summary>
public sealed class WhatsNewDialog : Window
{
    private WhatsNewDialog(IReadOnlyList<ReleaseNote> notes)
    {
        Title = "What's new in DNN Manager";
        Width = 720;
        Height = 640;
        MinWidth = 440;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var close = new Button { Content = "Close", MinWidth = 80, IsDefault = true, IsCancel = true, Classes = { "primary" } };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        var all = new Button { Content = "All releases on GitHub", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
        all.Click += (_, _) => Services.Shell.Open($"{DnnManager.Infrastructure.Updates.AppReleaseFeed.Repository}/releases");
        var footer = new Border { Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = new DockPanel { Children = { close, all } } };
        footer.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBg"));
        footer.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CardBorder"));
        DockPanel.SetDock(footer, Dock.Bottom);

        var body = new StackPanel { Margin = new Thickness(34, 24), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        if (notes.Count == 0) body.Children.Add(new TextBlock { Text = "No release notes for this version.", Classes = { "muted" } });
        foreach (var note in notes) Markdown(body, note.Markdown);
        Content = new DockPanel { Children = { footer, new ScrollViewer { Content = body } } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public static Task ShowAsync(Window owner, IReadOnlyList<ReleaseNote> notes) => new WhatsNewDialog(notes).ShowDialog(owner);

    /// <summary>The Markdown of a release's notes, block by block: headings, list items, paragraphs - links as their text.</summary>
    private static void Markdown(StackPanel body, string markdown)
    {
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            body.Children.Add(GuideDialog.Rich(Plain(string.Join(' ', paragraph)), 13, new Thickness(0, 6, 0, 6)));
            paragraph.Clear();
        }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { Flush(); continue; }
            var level = line.TakeWhile(c => c == '#').Count();
            if (level is > 0 and <= 6 && line.Length > level && line[level] == ' ')
            {
                Flush();
                body.Children.Add(new TextBlock
                {
                    Text = Plain(line[(level + 1)..]).Replace("**", ""), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                    FontSize = level switch { 1 => 22, 2 => 17, _ => 14 }, Margin = new Thickness(0, level == 1 ? 4 : 16, 0, 4)
                });
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                var indent = (line.Length - trimmed.Length) / 2;
                var item = GuideDialog.Rich(Plain(trimmed[2..]), 13, default);
                var bullet = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0) };
                var row = new DockPanel { Margin = new Thickness(12 + indent * 16, 2, 0, 2), Children = { bullet, item } };
                DockPanel.SetDock(bullet, Dock.Left);
                body.Children.Add(row);
                continue;
            }
            paragraph.Add(line.Trim());
        }
        Flush();
    }

    // [text](link) as its text, `code` as it is.
    private static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("`", "");
}
