using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DnnManager.Desktop.Services;
using DnnManager.Infrastructure.SiteLogs;
using Microsoft.Extensions.DependencyInjection;

namespace DnnManager.Desktop.Views.Projects;

/// <summary>A line of a log, with how it is coloured (the Windows app's LogLine).</summary>
public sealed class LogLine
{
    public LogLine(string text)
    {
        text = text.Contains('\t') ? text.Replace("\t", "    ") : text;
        Text = text;
        Brush = text.StartsWith("---", StringComparison.Ordinal) ? "LogMuted"
            : text.Contains(" ERROR", StringComparison.Ordinal) || text.Contains(" FATAL", StringComparison.Ordinal) ||
              text.Contains("[ERROR", StringComparison.Ordinal) || text.Contains("[FATAL", StringComparison.Ordinal) ||
              text.Contains("[error]", StringComparison.Ordinal) || text.Contains("[critical]", StringComparison.Ordinal) ? "LogFail"
            : text.Contains(" WARN", StringComparison.Ordinal) || text.Contains("[WARN", StringComparison.Ordinal) ||
              text.Contains("[warning]", StringComparison.Ordinal) ? "LogWarn"
            : "LogFg";
    }

    public string Text { get; }
    public string Brush { get; }
}

/// <summary>
/// A website's logs (the Windows app's LogsView, on its panel's Logs tab - here a window of its own): which site and which
/// of its logs at the top - DNN's, IIS's, the Windows events about it, or DNN Manager's own - the log below: its newest
/// lines, then new ones as they are written. Its text can be selected and copied.
/// </summary>
internal sealed class LogsWindow : Window
{
    private const int InitialLines = 5000;
    private const int MaxLines = 50_000;
    private const string AppChoice = "DNN Manager";

    private static LogsWindow? _open;

    private readonly SiteLogCatalog _catalog = Program.Services.GetRequiredService<SiteLogCatalog>();
    private readonly ComboBox _siteBox = new() { Width = 190 };
    private readonly ComboBox _logBox = new() { Width = 300, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _description = new() { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _folder = new() { Classes = { "icon" }, IsEnabled = false };
    private readonly ListBox _lines = new();
    private readonly TextBlock _hint = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, MaxWidth = 520, Text = "Choose a website and one of its logs above."
    };
    private readonly ObservableCollection<LogLine> _shown = [];
    private ILogTail? _tail;
    private SiteLogSource? _source;
    private bool _filling;
    private int _listing;

    private LogsWindow()
    {
        Title = "Logs - DNN Manager";
        Width = 1000;
        Height = 620;
        Styles.Add(DialogKit.Styles());
        Bind(BackgroundProperty, this.GetResourceObservable("LogBg"));
        ToolTip.SetTip(_siteBox, "Website");
        ToolTip.SetTip(_logBox, "Log");
        Avalonia.Automation.AutomationProperties.SetName(_siteBox, "Website");
        Avalonia.Automation.AutomationProperties.SetName(_logBox, "Log");
        _description.Bind(TextBlock.ForegroundProperty, _description.GetResourceObservable("LogMuted"));
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable("LogMuted"));
        _folder.Content = BindingsDialog.Glyph("GlyphFolderOpen", "Folder");
        _folder.Bind(ForegroundProperty, _folder.GetResourceObservable("LinkFg"));
        ToolTip.SetTip(_folder, "Open folder");
        _folder.Click += (_, _) =>
        {
            if (_source?.FilePath is { } file) ProjectShell.Open(Path.GetDirectoryName(file)!);
        };

        _lines.ItemsSource = _shown;
        _lines.FontFamily = this.TryFindResource("CodeFont", out var font) && font is FontFamily code ? code : FontFamily.Default;
        _lines.FontSize = 12;
        _lines.ItemTemplate = new FuncDataTemplate<LogLine>((line, _) =>
        {
            var text = new SelectableTextBlock { Text = line?.Text, TextWrapping = TextWrapping.Wrap };
            if (line is not null) text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(line.Brush));
            return text;
        });

        var sites = new List<object> { AppChoice };
        sites.AddRange(ProjectsServices.Store.Projects.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
        _siteBox.ItemsSource = sites;
        _siteBox.ItemTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock { Text = item is ProjectRow row ? row.Name : item?.ToString() });
        _siteBox.SelectionChanged += async (_, _) =>
        {
            if (_filling || _siteBox.SelectedItem is not { } choice) return;
            if (!await FillLogsAsync(choice)) return;
            _logBox.SelectedItem = _logBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource { Whole: false })
                                   ?? _logBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource);
        };
        _logBox.SelectionChanged += (_, _) =>
        {
            if (_filling || (_logBox.SelectedItem as ComboBoxItem)?.Tag is not SiteLogSource source || _source is not null && SameLog(source, _source)) return;
            Open(source, _siteBox.SelectedItem is ProjectRow row ? row.Name : AppChoice);
        };

        var top = new DockPanel { Children = { _folder, _siteBox, _logBox, _description } };
        DockPanel.SetDock(_folder, Dock.Right);
        DockPanel.SetDock(_siteBox, Dock.Left);
        DockPanel.SetDock(_logBox, Dock.Left);
        var header = new Border { Padding = new Thickness(10, 5, 6, 5), BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable("CardBorder"));
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(new Panel { Children = { _lines, _hint } });
        Content = body;
        Closed += (_, _) =>
        {
            Stop();
            _open = null;
        };
    }

    /// <summary>Shows <paramref name="site"/>'s log <paramref name="source"/> - in the one Logs window, opened when it isn't.</summary>
    public static void Show(ProjectRow site, SiteLogSource source)
    {
        var window = _open ??= new LogsWindow();
        if (!window.IsVisible)
        {
            if (Dialogs.Owner is { } owner) window.Show(owner);
            else window.Show();
        }
        window.Activate();
        window.Choose(site, source);
    }

    private async void Choose(ProjectRow site, SiteLogSource source)
    {
        _filling = true;
        if (_siteBox.ItemsSource is List<object> sites && !sites.Contains(site))
        {
            sites.Add(site);
            _siteBox.ItemsSource = sites.ToList();
        }
        _siteBox.SelectedItem = site;
        _filling = false;
        if (!await FillLogsAsync(site)) return;
        _logBox.SelectedItem = _logBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource s && SameLog(s, source))
                               ?? _logBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SiteLogSource);
    }

    private static bool SameLog(SiteLogSource a, SiteLogSource b) =>
        a.Group == b.Group && a.Title == b.Title && a.FilePath == b.FilePath &&
        a.Events?.LogName == b.Events?.LogName && a.Events?.MessageContains == b.Events?.MessageContains;

    /// <summary>The site's logs, under a heading per kind - or DNN Manager's own - found off the UI thread.</summary>
    private async Task<bool> FillLogsAsync(object choice)
    {
        var listing = ++_listing;
        IReadOnlyList<SiteLogSource> sources;
        if (choice is ProjectRow site)
        {
            var (name, id, path, pool) = (site.Name, site.IisSite.Id, site.Path, site.IisSite.AppPool);
            sources = await Task.Run(() => _catalog.For(name, id, path, pool).ToList());
        }
        else sources = await Task.Run(() => _catalog.ForApp());
        if (listing != _listing) return false;
        _filling = true;
        _logBox.Items.Clear();
        string? group = null;
        foreach (var source in sources)
        {
            if (source.Group != group)
            {
                group = source.Group;
                _logBox.Items.Add(new ComboBoxItem { Content = group, IsEnabled = false, FontWeight = FontWeight.SemiBold });
            }
            var item = new ComboBoxItem { Content = source.Title, Tag = source, Padding = new Thickness(18, 3, 8, 3) };
            ToolTip.SetTip(item, source.Description);
            _logBox.Items.Add(item);
        }
        _filling = false;
        return true;
    }

    private void Open(SiteLogSource source, string site)
    {
        Stop();
        _source = source;
        Title = (SiteLogCatalog.IsAppLog(source.Group, source.Title) ? source.Title : $"{site} - {source.Title}") + " - DNN Manager";
        _description.Text = source.Description;
        ToolTip.SetTip(_description, source.Description);
        _folder.IsEnabled = source.FilePath is not null;
        _hint.IsVisible = false;
        _shown.Clear();
        var tail = source.Open();
        _tail = tail;
        // Lines arrive on a background thread; they are added on the UI thread in batches.
        tail.Lines += lines => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_tail, tail)) Append(lines); }, DispatcherPriority.Background);
        tail.Replaced += () => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_tail, tail)) _shown.Clear(); }, DispatcherPriority.Background);
        tail.Start(InitialLines);
        // Nothing came: say the log is empty rather than show nothing.
        DispatcherTimer.RunOnce(() =>
        {
            if (!ReferenceEquals(_tail, tail) || _shown.Count > 0) return;
            _hint.Text = source.FilePath is null
                ? "No events about this site yet - new ones show here as they are logged."
                : "This log is empty - new lines show here as they are written.";
            _hint.IsVisible = true;
        }, TimeSpan.FromMilliseconds(800));
    }

    private void Stop()
    {
        _tail?.Dispose();
        _tail = null;
        _source = null;
    }

    private void Append(IReadOnlyList<string> lines)
    {
        _hint.IsVisible = false;
        // Following the end: while the newest line is in view, the view stays on it.
        var atEnd = _shown.Count == 0 || _lines.Scroll is not { } scroll || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 4;
        foreach (var line in lines) _shown.Add(new LogLine(line));
        // The oldest go - a log followed for hours doesn't grow without end.
        while (_shown.Count > MaxLines) _shown.RemoveAt(0);
        if (atEnd && _shown.Count > 0) _lines.ScrollIntoView(_shown.Count - 1);
    }
}
