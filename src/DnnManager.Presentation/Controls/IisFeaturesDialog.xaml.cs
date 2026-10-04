using System.Windows;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Edits the IIS Windows features DNN Manager checks and enables (Settings → IIS): a row each - how it is shown and its
/// Windows feature name - added, removed, or the defaults again.
/// </summary>
public partial class IisFeaturesDialog : Window
{
    private readonly List<Row> _rows = [];
    private readonly IReadOnlyList<IisFeatureSetting> _original;

    private IisFeaturesDialog(IReadOnlyList<IisFeatureSetting> features)
    {
        _original = features;
        InitializeComponent();
        ThemeManager.Track(this);
        Fill(features);
        Loaded += (_, _) => _rows.FirstOrDefault()?.Label.Focus();
    }

    /// <summary>The features as they are to be, in order - or null when cancelled or nothing changed.</summary>
    public static List<IisFeatureSetting>? Show(IReadOnlyList<IisFeatureSetting> features)
    {
        var dialog = new IisFeaturesDialog(features)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Features() : null;
    }

    private sealed record Row(TextBox Label, TextBox Name, FrameworkElement View)
    {
        public string FeatureName => Name.Text.Trim();
        // Left empty: shown by its name.
        public string FeatureLabel => Label.Text.Trim() is { Length: > 0 } label ? label : FeatureName;
    }

    private void Fill(IEnumerable<IisFeatureSetting> features)
    {
        _rows.Clear();
        RowsPanel.Children.Clear();
        foreach (var feature in features) AddRow(feature.Label, feature.Name);
        Check();
    }

    private Row AddRow(string label, string name)
    {
        var labelBox = new TextBox { Text = label, Width = 230 };
        var nameBox = new TextBox
        {
            Text = name, Width = 230, Margin = new Thickness(8, 0, 0, 0),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas")
        };
        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = FindResource("GlyphDelete"),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Remove this feature"
        };
        var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { labelBox, nameBox, remove } };
        var row = new Row(labelBox, nameBox, view);
        labelBox.TextChanged += (_, _) => Check();
        nameBox.TextChanged += (_, _) => Check();
        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            RowsPanel.Children.Remove(view);
            Check();
        };
        _rows.Add(row);
        RowsPanel.Children.Add(view);
        return row;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = AddRow("", "");
        Check();
        row.Label.Focus();
        row.View.BringIntoView();
    }

    private void Defaults_Click(object sender, RoutedEventArgs e) => Fill(new IisSettings().RequiredFeatures);

    private List<IisFeatureSetting> Features() =>
        _rows.Select(r => new IisFeatureSetting { Name = r.FeatureName, Label = r.FeatureLabel }).ToList();

    private void Check()
    {
        var problem = FindProblem();
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null && Changed();
    }

    private bool Changed()
    {
        var features = Features();
        return features.Count != _original.Count
               || features.Zip(_original).Any(p => p.First.Name != p.Second.Name || p.First.Label != p.Second.Label);
    }

    private string? FindProblem()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var name = row.FeatureName;
            if (name.Length == 0)
                return row.Label.Text.Trim().Length == 0 ? "Fill in or remove the empty row." : $"'{row.FeatureLabel}' needs its Windows feature name.";
            // What dism and Enable-WindowsOptionalFeature know: letters, digits, '-', '_' and '.'.
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return $"'{name}' isn't a Windows feature name: letters, digits, '-', '_' and '.' only.";
            if (!seen.Add(name)) return $"{name} is there twice.";
        }
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
