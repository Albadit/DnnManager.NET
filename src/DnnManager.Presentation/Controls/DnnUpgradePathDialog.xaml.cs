using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Edits the versions added to DNN's upgrade path (Settings → DNN releases): a row each - the version, what it needs, a
/// note for the plan - for a version DNN lists after this DNN Manager was made. The path DNN Manager knows is listed above
/// them, to read: it can't be changed here, only added to.
/// </summary>
public partial class DnnUpgradePathDialog : Window
{
    private const int NoNewMinimum = 0;

    private readonly List<Row> _rows = [];
    private readonly IReadOnlyList<DnnUpgradeStopSetting> _original;

    internal DnnUpgradePathDialog(IReadOnlyList<DnnUpgradeStopSetting> stops)
    {
        _original = stops;
        InitializeComponent();
        ThemeManager.Track(this);
        Known.Text = "DNN's path as DNN Manager came with it. It stays as it is: versions can only be added, and a minimum only raised " +
                     "for the version and the ones after it.";
        ShowKnownPath();
        foreach (var stop in stops) AddRow(stop);
        Check();
        Loaded += (_, _) => _rows.FirstOrDefault()?.Version.Focus();
    }

    /// <summary>The versions as they are to be, oldest first - or null when cancelled.</summary>
    public static List<DnnUpgradeStopSetting>? Show(IReadOnlyList<DnnUpgradeStopSetting> stops)
    {
        var dialog = new DnnUpgradePathDialog(stops)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Stops().OrderBy(s => SettingRules.UpgradeStopVersion(s.Version)).ToList() : null;
    }

    /// <summary>The path as DNN Manager knows it, newest first: each version with what the step to it needs.</summary>
    private void ShowKnownPath()
    {
        const string byHand = "Done by hand: no packages on GitHub";
        foreach (var step in DnnUpgradeKnowledge.Steps.Reverse())
            AddKnownRow(step.To, Needs(step, DnnRequirementKind.NetFramework), Needs(step, DnnRequirementKind.SqlServer),
                step.Method == DnnUpgradeMethod.Manual ? byHand : "");
        AddKnownRow(DnnUpgradePath.Versions[0], "-", "-", byHand);

        static string Needs(DnnUpgradeStep step, DnnRequirementKind kind) => step.Requirements.FirstOrDefault(r => r.Kind == kind)?.Text ?? "-";
    }

    private void AddKnownRow(Version version, string netFramework, string sqlServer, string note)
    {
        TextBlock Cell(string text, double width, double left) => new()
        {
            Style = (Style)FindResource("Hint"), Text = text, Width = width, Margin = new Thickness(left, 2, 0, 2), TextWrapping = TextWrapping.NoWrap
        };
        var name = Cell(DnnUpgradeStep.Name(version), 90, 0);
        name.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        KnownRows.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { name, Cell(netFramework, 170, 8), Cell(sqlServer, 170, 8), Cell(note, 200, 8) }
        });
    }

    private sealed record Row(TextBox Version, ComboBox NetFramework, ComboBox SqlServer, TextBox Note, FrameworkElement View)
    {
        public bool IsEmpty => Version.Text.Trim().Length == 0;

        public DnnUpgradeStopSetting Stop => new()
        {
            // As a plain number: DNN writes its versions as 10.04.02.
            Version = SettingRules.UpgradeStopVersion(Version.Text)?.ToString() ?? Version.Text.Trim(),
            NetFrameworkRelease = Minimum(NetFramework),
            SqlServerMajor = Minimum(SqlServer),
            Note = Note.Text.Trim()
        };

        private static int Minimum(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as int? ?? NoNewMinimum;
    }

    private Row AddRow(DnnUpgradeStopSetting stop)
    {
        var version = new TextBox
        {
            Text = stop.Version, Width = 90,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas")
        };
        var netFramework = Minimums(DnnUpgradePath.NetFrameworkChoices, stop.NetFrameworkRelease, DnnSiteFacts.NetFrameworkName);
        var sqlServer = Minimums(DnnUpgradePath.SqlServerChoices, stop.SqlServerMajor, DnnSiteFacts.SqlServerName);
        var note = new TextBox { Text = stop.Note, Width = 200, Margin = new Thickness(8, 0, 0, 0), MaxLength = SettingRules.MaxUpgradeStopNote };
        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = FindResource("GlyphDelete"),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Remove this version"
        };
        AutomationProperties.SetName(version, "Version");
        AutomationProperties.SetName(netFramework, "Needs .NET Framework");
        AutomationProperties.SetName(sqlServer, "Needs SQL Server");
        AutomationProperties.SetName(note, "Note in the upgrade plan");
        AutomationProperties.SetName(remove, "Remove this version");
        var view = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children = { version, netFramework, sqlServer, note, remove } };
        var row = new Row(version, netFramework, sqlServer, note, view);
        version.TextChanged += (_, _) => Check();
        note.TextChanged += (_, _) => Check();
        netFramework.SelectionChanged += (_, _) => Check();
        sqlServer.SelectionChanged += (_, _) => Check();
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

    /// <summary>"No new minimum" and <paramref name="choices"/> - and a saved value that isn't one of them, so it isn't lost.</summary>
    private static ComboBox Minimums(IReadOnlyList<int> choices, int saved, Func<int, string> name)
    {
        var box = new ComboBox { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
        box.Items.Add(new ComboBoxItem { Content = "No new minimum", Tag = NoNewMinimum });
        foreach (var choice in choices.Append(saved).Where(c => c != NoNewMinimum).Distinct().Order())
            box.Items.Add(new ComboBoxItem { Content = name(choice), Tag = choice });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == saved);
        return box;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = AddRow(new DnnUpgradeStopSetting());
        Check();
        row.Version.Focus();
        row.View.BringIntoView();
    }

    private void Source_Click(object sender, RoutedEventArgs e) => Shell.Open(DnnUpgradePath.Source);

    private List<DnnUpgradeStopSetting> Stops() => _rows.Select(r => r.Stop).ToList();

    private void Check()
    {
        Empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var problem = _rows.Any(r => r.IsEmpty) ? "Fill in the version, or remove the row." : SettingRules.UpgradeStopsProblem(Stops());
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem is null && Changed();
    }

    private bool Changed()
    {
        var stops = Stops();
        return stops.Count != _original.Count ||
               stops.Zip(_original).Any(p => p.First.Version != p.Second.Version || p.First.Note != p.Second.Note ||
                                             p.First.NetFrameworkRelease != p.Second.NetFrameworkRelease || p.First.SqlServerMajor != p.Second.SqlServerMajor);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
