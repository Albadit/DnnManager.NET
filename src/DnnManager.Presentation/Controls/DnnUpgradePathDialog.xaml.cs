using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DnnManager.Application.Configuration;
using DnnManager.Application.Upgrades;
using DnnManager.Infrastructure.Settings;
using DnnManager.Presentation.Services;
using Microsoft.Win32;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// Edits DNN's upgrade path (Settings → DNN releases): the whole path, a row per version - the version, what the step to
/// it needs, a note for the plan. A version DNN Manager knows can be changed or taken off, one DNN lists later added; the
/// path can be exported to a file and imported from one. Saved as its edits of the path DNN Manager knows
/// (<see cref="DnnUpgradePath.EditsFor"/>), so a later DNN Manager's new versions still come in.
/// </summary>
public partial class DnnUpgradePathDialog : Window
{
    private const int NoNewMinimum = 0;

    /// <summary>The columns of a row - the header in XAML has them too; the note takes the rest of the width.</summary>
    public static readonly GridLength VersionWidth = new(90), MinimumWidth = new(178), RemoveWidth = new(36),
        ScrollBarWidth = new(SystemParameters.VerticalScrollBarWidth);

    private readonly List<Row> _rows = [];
    private readonly IReadOnlyList<DnnUpgradeStopSetting> _original;
    private readonly IReadOnlyList<DnnPathVersion> _originalPath;

    internal DnnUpgradePathDialog(IReadOnlyList<DnnUpgradeStopSetting> stops)
    {
        _original = stops;
        _originalPath = DnnUpgradePath.PathWith(stops);
        InitializeComponent();
        ThemeManager.Track(this);
        Known.Text = $"DNN Manager knows DNN's path up to DNN {DnnUpgradeStep.Name(DnnUpgradePath.Versions[^1])}. What a version needs is checked " +
                     "before the step to it - and, past the newest version, before the steps after it.";
        Fill(_originalPath);
        Loaded += (_, _) => _rows.FirstOrDefault()?.NetFramework.Focus();
    }

    /// <summary>The edits of the path as they are to be - or null when cancelled.</summary>
    public static List<DnnUpgradeStopSetting>? Show(IReadOnlyList<DnnUpgradeStopSetting> stops)
    {
        var dialog = new DnnUpgradePathDialog(stops)
        {
            Owner = System.Windows.Application.Current.MainWindow is { IsVisible: true } w ? w : null
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? DnnUpgradePath.EditsFor(dialog.CurrentPath()!, stops) : null;
    }

    private sealed record Row(TextBox Version, ComboBox NetFramework, ComboBox SqlServer, TextBox Note, FrameworkElement View)
    {
        public bool IsEmpty => Version.Text.Trim().Length == 0;

        /// <summary>As the settings rules check it.</summary>
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

    /// <summary>The rows for <paramref name="path"/>, newest first.</summary>
    private void Fill(IEnumerable<DnnPathVersion> path)
    {
        _rows.Clear();
        RowsPanel.Children.Clear();
        foreach (var version in path.OrderByDescending(v => v.Version)) AddRow(version, atTop: false);
        Check();
    }

    private Row AddRow(DnnPathVersion? version, bool atTop)
    {
        var listed = version is not null && DnnUpgradePath.Versions.Contains(version.Version);
        var box = new TextBox
        {
            Text = version is null ? "" : listed ? DnnUpgradeStep.Name(version.Version) : version.Version.ToString(),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            // One of DNN's own: changed by what it needs, taken off by removing it - its number stays.
            IsReadOnly = listed,
            ToolTip = !listed ? "A version you added - three numbers, e.g. 10.4.2"
                : DnnUpgradePath.IsManual(version!.Version) ? "On DNN's path. Done by hand: GitHub has no packages of it."
                : "On DNN's path, as DNN Manager knows it"
        };
        // Read as text, not as a box to type in.
        if (listed)
        {
            box.Background = System.Windows.Media.Brushes.Transparent;
            box.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        var netFramework = Minimums(DnnUpgradePath.NetFrameworkChoices, version?.NetFrameworkRelease ?? NoNewMinimum, DnnSiteFacts.NetFrameworkName,
            Known(DnnRequirementKind.NetFramework));
        var sqlServer = Minimums(DnnUpgradePath.SqlServerChoices, version?.SqlServerMajor ?? NoNewMinimum, DnnSiteFacts.SqlServerName,
            Known(DnnRequirementKind.SqlServer));
        // As wide as the window leaves it.
        var note = new TextBox { Text = version?.Note ?? "", Margin = new Thickness(8, 0, 0, 0), MaxLength = SettingRules.MaxUpgradeStopNote };
        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = FindResource("GlyphDelete"),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = listed ? "Take this version off the path" : "Remove this version"
        };
        var name = listed ? DnnUpgradeStep.Name(version!.Version) : "the added version";
        AutomationProperties.SetName(box, "Version");
        AutomationProperties.SetName(netFramework, $"DNN {name} needs .NET Framework");
        AutomationProperties.SetName(sqlServer, $"DNN {name} needs SQL Server");
        AutomationProperties.SetName(note, $"Note in the upgrade plan for DNN {name}");
        AutomationProperties.SetName(remove, (string)remove.ToolTip);
        var view = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = VersionWidth }, new ColumnDefinition { Width = MinimumWidth }, new ColumnDefinition { Width = MinimumWidth },
                new ColumnDefinition(), new ColumnDefinition { Width = RemoveWidth }
            }
        };
        foreach (var (cell, column) in new FrameworkElement[] { box, netFramework, sqlServer, note, remove }.Select((cell, i) => (cell, i)))
        {
            Grid.SetColumn(cell, column);
            view.Children.Add(cell);
        }
        var row = new Row(box, netFramework, sqlServer, note, view);
        box.TextChanged += (_, _) => Check();
        note.TextChanged += (_, _) => Check();
        netFramework.SelectionChanged += (_, _) => Check();
        sqlServer.SelectionChanged += (_, _) => Check();
        remove.Click += (_, _) =>
        {
            var at = _rows.IndexOf(row);
            _rows.Remove(row);
            RowsPanel.Children.Remove(view);
            Check();
            // The keyboard goes on with the next row, not back to the top of the window.
            if (_rows.Count > 0) _rows[Math.Min(at, _rows.Count - 1)].NetFramework.Focus();
            else AddButton.Focus();
        };
        if (atTop)
        {
            _rows.Insert(0, row);
            RowsPanel.Children.Insert(0, view);
        }
        else
        {
            _rows.Add(row);
            RowsPanel.Children.Add(view);
        }
        return row;

        // What DNN Manager knows the step to one of DNN's own versions needs: its minimum, and DNN's words for it
        // ("SQL Server 2008 R2" - a major version alone can't say R2).
        (int Minimum, string? Text)? Known(DnnRequirementKind kind) => !listed ? null
            : DnnUpgradeKnowledge.Steps.FirstOrDefault(s => s.To == version!.Version)?.Requirements.FirstOrDefault(r => r.Kind == kind) is { } r
                ? (r.Minimum, r.Text) : (NoNewMinimum, null);
    }

    /// <summary>
    /// "No new minimum" and <paramref name="choices"/> - and a saved value that isn't one of them, so it isn't lost. What
    /// DNN Manager knows for the version (<paramref name="known"/>) is said in DNN's words and in the tooltip, so a change
    /// of it can be seen.
    /// </summary>
    private static ComboBox Minimums(IReadOnlyList<int> choices, int saved, Func<int, string> name, (int Minimum, string? Text)? known)
    {
        var box = new ComboBox { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
        string Label(int value) => value == known?.Minimum && known?.Text is { } text ? text : value == NoNewMinimum ? "No new minimum" : name(value);
        if (known is { } dnns) box.ToolTip = $"DNN's path: {(dnns.Text is null ? "no new minimum" : Label(dnns.Minimum))}";
        box.Items.Add(new ComboBoxItem { Content = Label(NoNewMinimum), Tag = NoNewMinimum });
        foreach (var choice in choices.Append(saved).Append(known?.Minimum ?? NoNewMinimum).Where(c => c != NoNewMinimum).Distinct().Order())
            box.Items.Add(new ComboBoxItem { Content = Label(choice), Tag = choice });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag == saved);
        return box;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = AddRow(null, atTop: true);
        Check();
        row.Version.Focus();
        RowsScroller.ScrollToTop();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Fill(DnnUpgradePath.PathWith(null));

    private void Source_Click(object sender, RoutedEventArgs e) => Shell.Open(DnnUpgradePath.Source);

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import an upgrade path", Filter = UpgradePathFile.Filter };
        if (dialog.ShowDialog(this) != true) return;
        var read = UpgradePathFile.Read(dialog.FileName);
        if (!read.Success)
        {
            Dialogs.Error($"{Path.GetFileName(dialog.FileName)} can't be imported: {read.Error} The path stays as it was.");
            return;
        }
        Fill(read.Value!);
        Toast.Show($"Upgrade path imported from {Path.GetFileName(dialog.FileName)} - Save keeps it.", ToastKind.Success);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath() is not { } path) return;
        var dialog = new SaveFileDialog { Title = "Export the upgrade path", Filter = UpgradePathFile.Filter, FileName = "dnn-upgrade-path.json" };
        if (dialog.ShowDialog(this) != true) return;
        var written = UpgradePathFile.Write(dialog.FileName, path);
        if (!written.Success)
        {
            Dialogs.Error($"The upgrade path could not be exported: {written.Error}");
            return;
        }
        Toast.Show($"Upgrade path exported to {dialog.FileName}.", ToastKind.Success);
    }

    /// <summary>The path as the rows have it, oldest first - null while a row is wrong (<see cref="Check"/> says which).</summary>
    private List<DnnPathVersion>? CurrentPath()
    {
        if (Problem.Visibility == Visibility.Visible) return null;
        return _rows.Select(r => r.Stop).Select(s => new DnnPathVersion(SettingRules.UpgradeStopVersion(s.Version)!, s.NetFrameworkRelease, s.SqlServerMajor, s.Note))
            .OrderBy(v => v.Version).ToList();
    }

    private void Check()
    {
        var stops = _rows.Select(r => r.Stop).ToList();
        var problem = _rows.Count == 0 ? "The upgrade path needs at least one version - add one, or reset it."
            : _rows.Any(r => r.IsEmpty) ? "Fill in the version, or remove the row."
            : SettingRules.UpgradeStopsProblem(stops);
        Problem.Text = problem ?? "";
        Problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;

        var onPath = stops.Select(s => SettingRules.UpgradeStopVersion(s.Version)).OfType<Version>().ToHashSet();
        var off = DnnUpgradePath.Versions.Where(v => !onPath.Contains(v)).Select(DnnUpgradeStep.Name).ToList();
        Removed.Text = $"Taken off the path: DNN {string.Join(", ", off)}. Upgrades skip {(off.Count == 1 ? "it" : "them")} - DNN's path goes through {(off.Count == 1 ? "it" : "them")}.";
        Removed.Visibility = off.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ExportButton.IsEnabled = problem is null;
        OkButton.IsEnabled = problem is null && !CurrentPath()!.SequenceEqual(_originalPath);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
