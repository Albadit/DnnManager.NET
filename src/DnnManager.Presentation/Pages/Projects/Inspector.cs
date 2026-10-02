using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DnnManager.Presentation.Pages.Projects;

/// <summary>How a value or a row stands: nothing to say, as it should be, worth a look, or wrong.</summary>
public enum Health { None, Ok, Warning, Bad }

/// <summary>A label and what was detected for it - with, when there is more to say, a line under it.</summary>
public sealed record InspectorRow(string Label, string Value, Health Health = Health.None, string? Detail = null);

/// <summary>A row of a table; a cell that is an address (http…) is a link.</summary>
public sealed record InspectorTableRow(IReadOnlyList<string> Cells, Health Health = Health.None, string? Detail = null);

public sealed record InspectorTable(IReadOnlyList<string> Columns, IReadOnlyList<InspectorTableRow> Rows);

/// <summary>A group of what was detected - from one place (<see cref="Source"/>): rows, a table, or a note.</summary>
public sealed class InspectorSection(string title, string? source = null)
{
    public string Title { get; } = title;
    public string? Source { get; } = source;
    public List<InspectorRow> Rows { get; } = [];
    public InspectorTable? Table { get; set; }
    public string? Note { get; set; }

    public InspectorSection Add(string label, string? value, Health health = Health.None, string? detail = null)
    {
        if (value is not null) Rows.Add(new InspectorRow(label, value, health, detail));
        return this;
    }
}

/// <summary>
/// Draws <see cref="InspectorSection"/>s - an environment inspector: each section a card with its title and where its
/// values come from, then label and value rows (a dot for how they stand, the value selectable), compact tables, a note.
/// </summary>
public sealed class InspectorPanel : StackPanel
{
    private const double LabelWidth = 210;

    public void Show(IEnumerable<InspectorSection> sections)
    {
        Children.Clear();
        foreach (var section in sections) Children.Add(Card(section));
    }

    private UIElement Card(InspectorSection section)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "Card");
        var body = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (section.Source is { } source)
        {
            var from = Text(source, "TextMuted", "TextSmall");
            from.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(from, Dock.Right);
            head.Children.Add(from);
        }
        var title = new TextBlock { Text = section.Title };
        title.SetResourceReference(StyleProperty, "CardTitle");
        title.Margin = new Thickness(0);
        head.Children.Add(title);
        body.Children.Add(head);

        if (section.Rows.Count > 0) body.Children.Add(Rows(section.Rows));
        if (section.Table is { } table) body.Children.Add(Table(table));
        if (section.Note is { } note)
        {
            var hint = Text(note, "TextMuted", null);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, section.Rows.Count > 0 || section.Table is not null ? 8 : 0, 0, 0);
            body.Children.Add(hint);
        }
        card.Child = body;
        return card;
    }

    private static Grid Rows(IReadOnlyList<InspectorRow> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text(row.Label, "TextMuted", null);
            label.Margin = new Thickness(0, 3, 12, 3);
            label.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(label, i);
            grid.Children.Add(label);

            if (Dot(row.Health) is { } dot)
            {
                dot.Margin = new Thickness(0, 9, 0, 0);
                Grid.SetRow(dot, i);
                Grid.SetColumn(dot, 1);
                grid.Children.Add(dot);
            }

            var value = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            // An address opens in the browser; any other value can be selected and copied.
            value.Children.Add(IsAddress(row.Value) ? Cell(row.Value, row.Health) : Selectable(row.Value, row.Health));
            if (row.Detail is { } detail)
            {
                var more = Text(detail, "TextMuted", null);
                more.TextWrapping = TextWrapping.Wrap;
                more.Margin = new Thickness(0, 1, 0, 0);
                value.Children.Add(more);
            }
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);
        }
        return grid;
    }

    /// <summary>A compact table: muted headers, then the rows - a dot in front of a row that is worth a look.</summary>
    private static Grid Table(InspectorTable table)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        var hasDots = table.Rows.Any(r => r.Health != Health.None);
        if (hasDots) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        var offset = hasDots ? 1 : 0;
        for (var c = 0; c < table.Columns.Count; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == table.Columns.Count - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var header = Text(table.Columns[c], "TextMuted", "TextSmall");
            header.Margin = new Thickness(0, 0, 18, 4);
            Grid.SetColumn(header, c + offset);
            grid.Children.Add(header);
        }
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "CardBorder");
        Grid.SetColumnSpan(line, table.Columns.Count + offset);
        grid.Children.Add(line);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var at = r + 1;
            if (Dot(row.Health) is { } dot)
            {
                dot.Margin = new Thickness(0, 10, 0, 0);
                Grid.SetRow(dot, at);
                grid.Children.Add(dot);
            }
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var text = c < row.Cells.Count ? row.Cells[c] : "";
                var cell = Cell(text, c == 0 ? row.Health : Health.None);
                cell.Margin = new Thickness(0, 4, 18, 4);
                if (c == table.Columns.Count - 1 && row.Detail is { } detail) cell.ToolTip = detail;
                Grid.SetRow(cell, at);
                Grid.SetColumn(cell, c + offset);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    /// <summary>A cell: an address is a link that opens in the browser; anything else text that wraps.</summary>
    private static TextBlock Cell(string text, Health health)
    {
        // Left: narrower than its column (MaxWidth), it would be centred in it.
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        if (IsAddress(text))
        {
            var link = new Hyperlink(new Run(text)) { ToolTip = "Open in your browser" };
            link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            link.Click += (_, _) => Shell.Open(text);
            block.Inlines.Add(link);
            return block;
        }
        block.Text = text;
        block.SetResourceReference(TextBlock.ForegroundProperty, Brush(health));
        return block;
    }

    private static bool IsAddress(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A value that can be selected and copied (a path, a version…) - in the colour of how it stands.</summary>
    private static TextBox Selectable(string text, Health health)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(0), MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Top
        };
        box.SetResourceReference(Control.ForegroundProperty, Brush(health));
        if (health == Health.Bad) box.FontWeight = FontWeights.SemiBold;
        return box;
    }

    private static string Brush(Health health) => health switch
    {
        Health.Bad => "ErrorText",
        Health.Warning => "LogWarn",
        _ => "TextPrimary"
    };

    private static Ellipse? Dot(Health health)
    {
        if (health == Health.None) return null;
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        dot.SetResourceReference(Shape.FillProperty, health switch { Health.Ok => "SuccessText", Health.Warning => "LogWarn", _ => "ErrorText" });
        return dot;
    }

    // The size as a theme token (TextSmall), so it follows the font size setting; null for the text's own.
    private static TextBlock Text(string text, string brush, string? size)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (size is not null) block.SetResourceReference(TextBlock.FontSizeProperty, size);
        return block;
    }
}
