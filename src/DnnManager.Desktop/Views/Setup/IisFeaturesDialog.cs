using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using DnnManager.Application.Configuration;
using DnnManager.Desktop.Services;

namespace DnnManager.Desktop.Views.Setup;

/// <summary>
/// Edits the IIS Windows features DNN Manager checks and enables (Settings → IIS): a row each - how it is shown and its
/// Windows feature name - added, removed, or the defaults again. The Windows app's IisFeaturesDialog.
/// </summary>
public sealed class IisFeaturesDialog : Window
{
    private readonly List<Row> _rows = [];
    private readonly IReadOnlyList<IisFeatureSetting> _original;
    private readonly StackPanel _rowsPanel = new();
    private readonly TextBlock _problem = new() { Classes = { "errorLine" }, IsVisible = false };
    private readonly Button _ok;
    private bool _saved;

    private IisFeaturesDialog(IReadOnlyList<IisFeatureSetting> features)
    {
        _original = features;
        Title = "Edit Windows features";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Styles.Add(new StyleInclude(new Uri("avares://DnnManager.Desktop/")) { Source = new Uri("/Themes/SetupStyles.axaml", UriKind.Relative) });

        _ok = new Button { Content = "Save", Classes = { "primary" }, MinWidth = 80, IsDefault = true, IsEnabled = false };
        ToolTip.SetTip(_ok, "Save (Enter)");
        _ok.Click += (_, _) => { _saved = true; Close(); };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ToolTip.SetTip(cancel, "Cancel (Esc)");
        cancel.Click += (_, _) => Close();

        var add = new Button { Content = "Add feature", Classes = { "link" } };
        add.Click += (_, _) =>
        {
            var row = AddRow("", "");
            Check();
            row.Label.Focus();
            row.View.BringIntoView();
        };
        var defaults = new Button { Content = "Use the defaults", Classes = { "link" }, Margin = new Thickness(16, 0, 0, 0) };
        defaults.Click += (_, _) => Fill(new IisSettings().RequiredFeatures);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "The Windows features Test checks and Set up IIS enables. dism /online /get-features lists their names."
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
                    Children =
                    {
                        new TextBlock { Classes = { "fieldLabel" }, Text = "Shown as", Width = 230 },
                        new TextBlock { Classes = { "fieldLabel" }, Text = "Windows feature name", Margin = new Thickness(8, 8, 0, 4) }
                    }
                },
                new ScrollViewer { MaxHeight = 420, Content = _rowsPanel },
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { add, defaults } },
                _problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
                    Children = { _ok, cancel }
                }
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Fill(features);
        Opened += (_, _) => _rows.FirstOrDefault()?.Label.Focus();
    }

    /// <summary>The features as they are to be, in order - or null when cancelled or nothing changed.</summary>
    public static async Task<List<IisFeatureSetting>?> ShowAsync(IReadOnlyList<IisFeatureSetting> features)
    {
        var dialog = new IisFeaturesDialog(features);
        if (Dialogs.Owner is { IsVisible: true } owner) await dialog.ShowDialog(owner);
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return dialog._saved ? dialog.Features() : null;
    }

    private sealed record Row(TextBox Label, TextBox Name, Control View)
    {
        public string FeatureName => (Name.Text ?? "").Trim();
        // Left empty: shown by its name.
        public string FeatureLabel => (Label.Text ?? "").Trim() is { Length: > 0 } label ? label : FeatureName;
    }

    private void Fill(IEnumerable<IisFeatureSetting> features)
    {
        _rows.Clear();
        _rowsPanel.Children.Clear();
        foreach (var feature in features) AddRow(feature.Label, feature.Name);
        Check();
    }

    private Row AddRow(string label, string name)
    {
        var labelBox = new TextBox { Text = label, Width = 230 };
        var nameBox = new TextBox
        {
            Text = name, Width = 230, Margin = new Thickness(8, 0, 0, 0),
            FontFamily = Avalonia.Application.Current?.FindResource("CodeFont") as FontFamily ?? FontFamily.Default
        };
        Avalonia.Automation.AutomationProperties.SetName(labelBox, "Shown as");
        Avalonia.Automation.AutomationProperties.SetName(nameBox, "Windows feature name");
        var remove = new Button
        {
            Classes = { "icon" }, Margin = new Thickness(4, 0, 0, 0),
            Content = MainWindow.HasIconFont
                ? new TextBlock { Classes = { "glyph" }, Text = Avalonia.Application.Current?.FindResource("GlyphDelete") as string ?? "✕" }
                : new TextBlock { Text = "✕" }
        };
        ToolTip.SetTip(remove, "Remove this feature");
        Avalonia.Automation.AutomationProperties.SetName(remove, "Remove this feature");
        var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { labelBox, nameBox, remove } };
        var row = new Row(labelBox, nameBox, view);
        labelBox.TextChanged += (_, _) => Check();
        nameBox.TextChanged += (_, _) => Check();
        remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            _rowsPanel.Children.Remove(view);
            Check();
        };
        _rows.Add(row);
        _rowsPanel.Children.Add(view);
        return row;
    }

    private List<IisFeatureSetting> Features() =>
        _rows.Select(r => new IisFeatureSetting { Name = r.FeatureName, Label = r.FeatureLabel }).ToList();

    private void Check()
    {
        var problem = FindProblem(_rows.Select(r => ((r.Label.Text ?? "").Trim(), r.FeatureName)).ToList());
        SetupText.ShowError(_problem, _ok, problem ?? "");
        _ok.IsEnabled = problem is null && Changed();
    }

    private bool Changed()
    {
        var features = Features();
        return features.Count != _original.Count
               || features.Zip(_original).Any(p => p.First.Name != p.Second.Name || p.First.Label != p.Second.Label);
    }

    /// <summary>What is wrong with the rows (label as typed, Windows name) - or null when they can be saved.</summary>
    internal static string? FindProblem(IReadOnlyList<(string Label, string Name)> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, name) in rows)
        {
            if (name.Length == 0)
                return label.Length == 0 ? "Fill in or remove the empty row." : $"'{label}' needs its Windows feature name.";
            if (!SettingRules.IsIisFeatureName(name))
                return $"'{name}' isn't a Windows feature name: letters, digits, '-', '_' and '.' only.";
            if (!seen.Add(name)) return $"{name} is there twice.";
        }
        return null;
    }
}
