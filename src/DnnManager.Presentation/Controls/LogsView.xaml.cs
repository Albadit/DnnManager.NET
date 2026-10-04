using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DnnManager.Infrastructure.SiteLogs;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

public enum LogLineLevel { Normal, Warning, Error, Note }

/// <summary>A line of a log, with how it is coloured.</summary>
public sealed class LogLine
{
    public LogLine(string text)
    {
        // Tabs as spaces: the text is drawn and selected in columns.
        text = text.Contains('\t') ? text.Replace("\t", "    ") : text;
        Text = text;
        Level = text.StartsWith("---", StringComparison.Ordinal) ? LogLineLevel.Note
            // DNN (log4net) writes "[ERROR]" / "[WARN]", the event logs "  ERROR  " / "  WARNING  ", DNN Manager
            // "[error]" / "[critical]" / "[warning]".
            : text.Contains(" ERROR", StringComparison.Ordinal) || text.Contains(" FATAL", StringComparison.Ordinal) ||
              text.Contains("[ERROR", StringComparison.Ordinal) || text.Contains("[FATAL", StringComparison.Ordinal) ||
              text.Contains("[error]", StringComparison.Ordinal) || text.Contains("[critical]", StringComparison.Ordinal) ? LogLineLevel.Error
            : text.Contains(" WARN", StringComparison.Ordinal) || text.Contains("[WARN", StringComparison.Ordinal) ||
              text.Contains("[warning]", StringComparison.Ordinal) ? LogLineLevel.Warning
            : LogLineLevel.Normal;
    }

    public string Text { get; }
    public LogLineLevel Level { get; }
}

/// <summary>DNN Manager itself in the Logs tab's list of websites - its own log files - above the sites.</summary>
public sealed class AppLogChoice
{
    public static readonly AppLogChoice Instance = new();

    private AppLogChoice() { }

    public string Name => SiteLogCatalog.AppGroup;
}

/// <summary>
/// The Logs tab: a website's logs - DNN's, IIS's, the Windows events about it - or DNN Manager's own, one at a time. Its newest lines are
/// read (never the whole of a large file), then new ones arrive as they are written; the view follows them while it
/// is at the bottom. Its text can be selected and copied (<see cref="LogView"/>). Searchable
/// (<see cref="ISearchTarget"/>): every match highlighted, the current one stronger. While the window is minimized
/// (<see cref="EfficiencyMode"/>) a file isn't looked at and lines Windows pushes are held; restored, the file is read
/// on from where it was at once, so no line is lost.
/// </summary>
public partial class LogsView : UserControl, ISearchTarget
{
    // Lines read at first, and the most kept - older ones drop off the top as new ones come.
    private const int InitialLines = 5000;
    private const int MaxLines = 50_000;

    private ServerStore _store = null!;
    private SiteLogCatalog _catalog = null!;
    private ILogTail? _tail;
    private SiteLogSource? _source;
    private bool _filling;
    // Lines that arrived while the window was minimized - shown when it is restored.
    private readonly List<string> _held = [];

    public LogsView()
    {
        InitializeComponent();
    }

    public event EventHandler? ContentChanged;

    /// <summary>The log shown: "ceesboer - DNN log - 2026.10.01"; null when none is.</summary>
    public string? Current { get; private set; }

    /// <summary>The site whose log is shown, and which log - kept for the next start.</summary>
    public string? CurrentSite { get; private set; }
    public SiteLogSource? CurrentSource => _source;

    internal void Attach(ServerStore store, SiteLogCatalog catalog)
    {
        _store = store; _catalog = catalog;
        // DNN Manager's own log first, then every IIS site, by name - kept current as sites come and go.
        var sites = new ListCollectionView(store.Projects);
        sites.SortDescriptions.Add(new SortDescription(nameof(ProjectRow.Name), ListSortDirection.Ascending));
        SiteBox.ItemsSource = new CompositeCollection { AppLogChoice.Instance, new CollectionContainer { Collection = sites } };
    }

    public void SetFont(FontFamily font, double size) => LogText.SetFont(font, size);

    // ─── Choosing a site and a log ────────────────────────────────────────

    /// <summary>Shows <paramref name="site"/>'s log <paramref name="source"/> - its newest one when null.</summary>
    internal void Show(ProjectRow site, SiteLogSource? source) =>
        Show(site, s => source is null || SameLog(s, source));

    /// <summary>
    /// Shows <paramref name="site"/>'s log of kind <paramref name="group"/> named <paramref name="title"/> - as it was
    /// before DNN Manager restarted; its newest log when that one isn't there (any more).
    /// </summary>
    internal void Show(ProjectRow site, string? group, string? title) =>
        Show(site, s => s.Group == group && s.Title == title);

    /// <summary>DNN Manager's own log named <paramref name="title"/> - its newest when null, or when that one is gone.</summary>
    internal void ShowApp(string? title) => Show(AppLogChoice.Instance, s => title is null || s.Title == title);

    private async void Show(object choice, Func<SiteLogSource, bool> wanted)
    {
        _filling = true;
        SiteBox.SelectedItem = choice;
        _filling = false;
        if (!await FillLogsAsync(choice)) return;
        var item = LogBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource s && wanted(s))
                   ?? LogBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource);
        LogBox.SelectedItem = item;
    }


    // The catalog makes its sources anew each time it is asked (and an event log's filter holds a list): the same log
    // is the same kind and name.
    private static bool SameLog(SiteLogSource a, SiteLogSource b) =>
        a.Group == b.Group && a.Title == b.Title && a.FilePath == b.FilePath &&
        a.Events?.LogName == b.Events?.LogName && a.Events?.MessageContains == b.Events?.MessageContains;

    private async void Site_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || SiteBox.SelectedItem is not ({ } choice and (ProjectRow or AppLogChoice))) return;
        if (!await FillLogsAsync(choice)) return;
        LogBox.SelectedItem = LogBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource);
    }

    // Which listing the log list belongs to - a site chosen meanwhile makes an older one moot.
    private int _listing;

    /// <summary>
    /// The site's logs, under a heading per kind (DNN, IIS, Windows) - or DNN Manager's own - found off the UI thread
    /// (IIS's configuration and log folders of thousands of files are read). False when another one was chosen meanwhile.
    /// </summary>
    private async Task<bool> FillLogsAsync(object choice)
    {
        var listing = ++_listing;
        var catalog = _catalog!;
        IReadOnlyList<SiteLogSource> sources;
        if (choice is ProjectRow site)
        {
            var (name, id, path, pool) = (site.Name, site.IisSite.Id, site.Path, site.IisSite.AppPool);
            sources = await Task.Run(() => catalog.For(name, id, path, pool).ToList());
        }
        else
        {
            sources = await Task.Run(() => catalog.ForApp());
        }
        if (listing != _listing) return false;
        LogBox.Items.Clear();
        string? group = null;
        foreach (var source in sources)
        {
            if (source.Group != group)
            {
                group = source.Group;
                LogBox.Items.Add(new ComboBoxItem { Content = group, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            }
            LogBox.Items.Add(new ComboBoxItem { Content = source.Title, Tag = source, ToolTip = source.Description, Padding = new Thickness(18, 3, 8, 3) });
        }
        return true;
    }

    private void Log_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || (LogBox.SelectedItem as ComboBoxItem)?.Tag is not SiteLogSource source || _source is not null && SameLog(source, _source)) return;
        var site = SiteBox.SelectedItem switch { ProjectRow row => row.Name, AppLogChoice app => app.Name, _ => null };
        Open(source, site);
    }

    private void Open(SiteLogSource source, string? site)
    {
        Stop();
        _source = source;
        CurrentSite = site;
        // "DNN Manager log - 2026-10-04" names itself.
        Current = source.Group == SiteLogCatalog.AppGroup ? source.Title : $"{site} - {source.Title}";
        Description.Text = source.Description;
        Description.ToolTip = source.Description;
        FolderButton.IsEnabled = source.FilePath is not null;
        Hint.Visibility = Visibility.Collapsed;
        LogText.Clear();

        var tail = source.Open();
        _tail = tail;
        // Lines arrive on a background thread; they are added on the UI thread in batches.
        tail.Lines += lines => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_tail, tail)) Append(lines); }, DispatcherPriority.Background);
        tail.Paused = EfficiencyMode.GetIsSaving(this);
        tail.Start(InitialLines);
        // Nothing came: say the log is empty rather than show nothing.
        _ = Task.Delay(800).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_tail, tail) || LogText.Count > 0 || _held.Count > 0) return;
            Hint.Text = source.FilePath is null
                ? "No events about this site yet - new ones show here as they are logged."
                : "This log is empty - new lines show here as they are written.";
            Hint.Visibility = Visibility.Visible;
        }));
    }

    /// <summary>Stops following the log shown - when another is chosen, or the window closes.</summary>
    public void Stop()
    {
        _tail?.Dispose();
        _tail = null;
        _source = null;
        _held.Clear();
    }

    private void Append(IReadOnlyList<string> lines)
    {
        if (EfficiencyMode.GetIsSaving(this))
        {
            // Nobody sees them now. No more than the view would keep.
            _held.AddRange(lines);
            if (_held.Count > MaxLines) _held.RemoveRange(0, _held.Count - MaxLines);
            return;
        }
        Hint.Visibility = Visibility.Collapsed;
        // The oldest go - a log followed for hours doesn't grow without end.
        LogText.Append(lines.Select(l => new LogLine(l)), MaxLines);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    // Minimized: the file isn't looked at (nobody reads it). Restored: it is read on at once from where it was, after
    // the lines that were held.
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != EfficiencyMode.IsSavingProperty) return;
        var saving = (bool)e.NewValue;
        if (!saving && _held.Count > 0)
        {
            var held = _held.ToList();
            _held.Clear();
            Append(held);
        }
        if (_tail is not null) _tail.Paused = saving;
    }

    public void ScrollToEnd() => LogText.ScrollToEnd();

    /// <summary>Copies the selected text.</summary>
    public void Copy() => LogText.Copy();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_source?.FilePath is { } file) Shell.Open(Path.GetDirectoryName(file)!);
    }

    // ─── Search ───────────────────────────────────────────────────────────

    public int Find(SearchQuery query) => LogText.Find(query);

    public void ShowMatch(int index, bool reveal = true) => LogText.ShowMatch(index, reveal);

    public void ClearSearch() => LogText.ClearSearch();
}