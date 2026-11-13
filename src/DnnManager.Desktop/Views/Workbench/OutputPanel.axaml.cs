using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using DnnManager.Desktop.Services;
using DnnManager.Presentation.Controls;
using DnnManager.Presentation.Services;

namespace DnnManager.Desktop.Views.Workbench;

/// <summary>
/// The bottom panel (src/DnnManager.Presentation/Controls/TerminalPanel + PipelineView + PipelineLog): Output - the newest
/// operation as a pipeline (its state, stages, facts, warnings and errors) beside the log of every operation, stage by
/// stage, coloured as the Windows app's - and Logs, DNN Manager's own log file. Ctrl+F searches the tab shown.
/// </summary>
public partial class OutputPanel : UserControl
{
    private ActivityLog? _log;
    private string _logsDirectory = "";
    private OutputRun? _run;
    // The log is written again a moment after it changes (a burst of lines once), and each second while a run runs.
    private readonly DispatcherTimer _render, _clock;
    private string _plain = "";
    private int _match = -1;
    private List<(int Start, int Length)> _matches = [];
    private bool _maximized;

    public OutputPanel()
    {
        InitializeComponent();
        _render = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Render());
        _render.Stop();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clock.Stop();
        SearchBox.TextChanged += (_, _) => Search(reveal: true);
        Stages.ItemTemplate = new FuncDataTemplate<OutputStage>((stage, _) => stage is null ? new Panel() : new StageRow(stage, ShowStage));
        ActualThemeVariantChanged += (_, _) => RenderNow();
    }

    /// <summary>The panel's ✕.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Its maximize button.</summary>
    public event EventHandler? MaximizeToggled;

    /// <summary>Output or Logs - the workspace keeps it.</summary>
    public string Pane => LogsTab.IsChecked == true ? "Logs" : "Output";

    /// <summary>The plain text of the Output tab's log - what the tests and the search read.</summary>
    internal string PlainText => _plain;

    public bool IsMaximized
    {
        get => _maximized;
        set
        {
            _maximized = value;
            MaximizeIcon.Data = this.FindResource(value ? "PanelRestore" : "PanelMaximize") as Geometry;
            ToolTip.SetTip(MaximizeButton, value ? "Restore panel size" : "Maximize panel");
            AutomationProperties.SetName(MaximizeButton, value ? "Restore panel size" : "Maximize panel");
        }
    }

    public void Attach(ActivityLog log, string logsDirectory)
    {
        _log = log;
        _logsDirectory = logsDirectory;
        log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityLog.Latest)) Dispatcher.UIThread.Post(() => ShowRun(log.Latest));
        };
        log.Items.CollectionChanged += (_, _) => Soon();
        log.RunEnded += _ => Dispatcher.UIThread.Post(RenderNow);
        ShowRun(log.Latest);
        RenderNow();
    }

    /// <summary>Output, shown.</summary>
    public void ShowOutput() => OutputTab.IsChecked = true;

    public void ShowLogs() => LogsTab.IsChecked = true;

    // ─── The newest run: the header, the stages, the summary ────────────────

    private void ShowRun(OutputRun? run)
    {
        if (_run is not null)
        {
            _run.PropertyChanged -= OnRunChanged;
            _run.Stages.CollectionChanged -= OnStagesChanged;
        }
        _run = run;
        if (run is not null)
        {
            run.PropertyChanged += OnRunChanged;
            run.Stages.CollectionChanged += OnStagesChanged;
        }
        RunHeader.IsVisible = run is not null;
        NoRun.IsVisible = run is null;
        Stages.ItemsSource = run?.Stages;
        Facts.ItemsSource = run?.Facts;
        StagesPart.IsVisible = Summary.IsVisible = run is not null;
        Refresh();
        Soon();
    }

    private void OnStagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.UIThread.Post(() => { Refresh(); Soon(); });

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputRun.Duration) or nameof(OutputRun.DurationText)) return;
        Dispatcher.UIThread.Post(() => { Refresh(); Soon(); });
    }

    private void Refresh()
    {
        _clock.IsEnabled = _run is { IsRunning: true };
        OutputDot.IsVisible = false;
        if (_run is not { } run) return;
        var (pill, pillBg, pillFg, glyph) = run.Status switch
        {
            RunStatus.Running => ("Running", "OutPillRunningBg", "OutPillRunningFg", "StepSmallSpinner"),
            RunStatus.Finished => ("Finished", "OutPillOkBg", "OutPillOkFg", "StepCheck"),
            RunStatus.Failed => ("Failed", "OutPillFailBg", "OutPillFailFg", "StepCross"),
            _ => ("Cancelled", "OutPillNeutralBg", "OutPillNeutralFg", "StepCross")
        };
        PillText.Text = pill;
        Pill.Background = Brush(pillBg);
        PillText.Foreground = PillIcon.Stroke = Brush(pillFg);
        PillIcon.Data = this.FindResource(glyph) as Geometry;
        RunTitle.Text = run.Title;
        RunMeta.Text = run.Meta;
        RunTime.Text = run.TimeRange;
        RunDuration.Text = run.DurationText;

        Issues.IsVisible = run.IssueText is not null;
        IssueText.Text = run.IssueText;
        IssueText.Foreground = IssueIcon.Stroke = Brush(run.HasErrors ? "OutErrorSoft" : "OutWarn");
        IssueIcon.Data = this.FindResource(run.HasErrors ? "StepAlert" : "StepTriangle") as Geometry;
        StagesLabel.Text = $"STAGES · {run.Done}/{run.Total}";
        WarningCount.Text = run.Warnings.ToString(CultureInfo.InvariantCulture);
        WarningCount.Foreground = Brush(run.Warnings > 0 ? "OutWarn" : "OutText");
        ErrorCount.Text = run.Errors.ToString(CultureInfo.InvariantCulture);
        ErrorCount.Foreground = Brush(run.Errors > 0 ? "OutErrorSoft" : "OutText");
        // Something failed while Output wasn't looked at: the tab's dot says so.
        if (run.Status == RunStatus.Failed && LogsTab.IsChecked == true)
        {
            OutputDot.Fill = Brush("OutError");
            OutputDot.IsVisible = true;
        }
    }

    private void Tick()
    {
        if (_run is not { IsRunning: true } run)
        {
            _clock.Stop();
            return;
        }
        run.Tick();
        foreach (var stage in run.Stages.Where(s => s.IsRunning)) stage.Tick();
        RunDuration.Text = run.DurationText;
        RenderNow();
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    // ─── The log ────────────────────────────────────────────────────────────

    private void Soon()
    {
        if (!_render.IsEnabled) _render.Start();
    }

    private void RenderNow()
    {
        _render.Stop();
        Render();
    }

    private void Render()
    {
        _render.Stop();
        if (_log is null) return;
        var atEnd = LogScroller.Offset.Y + LogScroller.Viewport.Height >= LogScroller.Extent.Height - 24;
        var inlines = new InlineCollection();
        var plain = new System.Text.StringBuilder();
        void Add(string text, string brush, bool bold = false)
        {
            inlines.Add(new Run(text) { Foreground = Brush(brush), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal });
            plain.Append(text);
        }
        void Break()
        {
            inlines.Add(new LineBreak());
            plain.Append('\n');
        }

        foreach (var item in _log.Items.ToList())
        {
            switch (item)
            {
                case OutputRun run:
                    if (plain.Length > 0) Break();
                    Add(run.Title, "OutMuted", bold: true);
                    Add($"   {run.StartedAt:HH:mm:ss}", "OutTime");
                    Break();
                    foreach (var stage in run.Stages.Where(s => s.IsShownInLog).ToList())
                    {
                        var (title, time) = stage.Status switch
                        {
                            StageStatus.Warning => ("OutWarnText", "OutWarn"),
                            StageStatus.Failed => ("OutErrorText", "OutErrorSoft"),
                            StageStatus.Skipped or StageStatus.Cancelled => ("OutDim", "OutDim"),
                            _ => ("OutHeading", "OutHeadingTime")
                        };
                        Add($"▸ {stage.Title}", title, bold: true);
                        Add($"   {stage.DurationText}", time);
                        Break();
                        foreach (var line in stage.Lines.ToList()) Line(line);
                    }
                    foreach (var note in run.Notes.ToList()) Line(note);
                    if (!run.IsRunning) End(run);
                    break;
                case OutputLine line:
                    Line(line);
                    break;
            }
        }

        void Line(OutputLine line)
        {
            Add(Stamp(line.Time), "OutTime");
            switch (line.Level)
            {
                case LineLevel.Warn:
                    Add("WARN ", "OutWarn", bold: true);
                    Add(line.Text, "OutWarnText");
                    break;
                case LineLevel.Error:
                    Add("ERROR ", "OutError", bold: true);
                    Add(line.Text, "OutErrorText");
                    foreach (var detail in line.Details)
                    {
                        Break();
                        Add("          " + detail, "OutErrorDetail");
                    }
                    if (line.Hint is { } hint)
                    {
                        Break();
                        Add("          → ", "OutErrorSoft");
                        Add(hint, "OutHeading");
                    }
                    break;
                default:
                    Add(line.Text, line.Completes || line.Level == LineLevel.Success ? "OutSuccessText"
                        : line.Level == LineLevel.Progress ? "OutMuted" : "OutText");
                    break;
            }
            Break();
        }

        void End(OutputRun run)
        {
            var (badge, brush, text, textBrush) = run.Status switch
            {
                RunStatus.Finished => ("SUCCESS", "OutSuccessText", run.ClosingTitle ?? $"{run.Title} finished", "OutText"),
                RunStatus.Cancelled => ("CANCELLED", "OutMuted", run.Outcome ?? $"{run.Title} was cancelled", "OutText"),
                _ => ("ERROR", "OutError", run.StoppedAt is { } at ? $"{run.Title} stopped at {at.Name}" : $"{run.Title} failed", "OutErrorText")
            };
            Add($"{badge} ", brush, bold: true);
            Add(text, textBrush);
            Add($"  · {run.DurationText}", "OutMuted");
            if (run.Link is { } link) Add($"  · {link}", "OutValue");
            Break();
        }

        var text = plain.ToString();
        if (text == _plain) return;
        _plain = text;
        Log.Inlines = inlines;
        if (SearchBar.IsVisible && OutputTab.IsChecked == true) Search(reveal: false);
        if (atEnd) Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static string Stamp(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ";

    /// <summary>A stage chosen in the list: the log scrolls to its heading.</summary>
    private void ShowStage(OutputStage stage)
    {
        var at = _plain.LastIndexOf($"▸ {stage.Title}", StringComparison.Ordinal);
        if (at >= 0) Reveal(Log, LogScroller, at);
    }

    private static void Reveal(SelectableTextBlock text, ScrollViewer scroller, int index)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var rect = text.TextLayout.HitTestTextPosition(index);
            var y = rect.Y + text.Margin.Top;
            if (y < scroller.Offset.Y || y > scroller.Offset.Y + scroller.Viewport.Height - 40)
                scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, y - scroller.Viewport.Height / 3));
        }, DispatcherPriority.Background);
    }

    // ─── Logs: DNN Manager's own log ────────────────────────────────────────

    private void ShowAppLog()
    {
        var file = Directory.Exists(_logsDirectory)
            ? new DirectoryInfo(_logsDirectory).GetFiles("dnnmanager-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        LogFileName.Text = file?.FullName ?? $"Nothing written yet - DNN Manager's warnings and errors go to {_logsDirectory}.";
        string text;
        try
        {
            using var reader = file is null ? null : new StreamReader(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            text = reader?.ReadToEnd() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = $"The log can't be read: {ex.Message}";
        }
        // The newest part: a long day's log stays quick to show.
        const int Kept = 200_000;
        AppLog.Text = text.Length > Kept ? "…" + text[^Kept..] : text;
        Dispatcher.UIThread.Post(() => AppLogScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OpenLogs_Click(object? sender, RoutedEventArgs e) => Shell.Open(_logsDirectory);

    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        var logs = LogsTab.IsChecked == true;
        OutputPane.IsVisible = !logs;
        LogsPane.IsVisible = logs;
        ClearButton.IsVisible = !logs;
        if (logs) ShowAppLog();
        else OutputDot.IsVisible = false;
        if (SearchBar.IsVisible) Search(reveal: true);
    }

    // ─── Search ─────────────────────────────────────────────────────────────

    /// <summary>Ctrl+F: the search bar, with the keyboard in it - its text all selected.</summary>
    public void OpenSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        Search(reveal: true);
    }

    public void CloseSearch()
    {
        if (!SearchBar.IsVisible) return;
        SearchBar.IsVisible = false;
        _matches = [];
        _match = -1;
        Target.SelectionStart = Target.SelectionEnd = 0;
    }

    private SelectableTextBlock Target => LogsTab.IsChecked == true ? AppLog : Log;

    private ScrollViewer TargetScroller => LogsTab.IsChecked == true ? AppLogScroller : LogScroller;

    private void Search(bool reveal)
    {
        var query = new SearchQuery(SearchBox.Text ?? "", MatchCase.IsChecked == true, WholeWord.IsChecked == true, UseRegex.IsChecked == true);
        var text = LogsTab.IsChecked == true ? AppLog.Text ?? "" : _plain;
        _matches = [];
        if (query.Error is not null)
        {
            SearchCount.Text = "Invalid pattern";
            SearchCount.Foreground = Brush("ErrorText");
            return;
        }
        SearchCount.Foreground = Brush("TextPrimary");
        query.BeginPass();
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            foreach (var (start, length) in query.Matches(line)) _matches.Add((offset + start, length));
            offset += line.Length + 1;
        }
        if (_matches.Count == 0)
        {
            _match = -1;
            SearchCount.Text = query.IsEmpty ? "" : "No results";
            Target.SelectionStart = Target.SelectionEnd = 0;
            return;
        }
        _match = Math.Clamp(_match, 0, _matches.Count - 1);
        ShowMatch(reveal);
    }

    private void ShowMatch(bool reveal)
    {
        if (_match < 0 || _match >= _matches.Count) return;
        var (start, length) = _matches[_match];
        SearchCount.Text = $"{_match + 1} of {_matches.Count}";
        Target.SelectionStart = start;
        Target.SelectionEnd = start + length;
        if (reveal) Reveal(Target, TargetScroller, start);
    }

    private void Step(int by)
    {
        if (_matches.Count == 0) return;
        _match = ((_match + by) % _matches.Count + _matches.Count) % _matches.Count;
        ShowMatch(reveal: true);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Step(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        else if (e.Key == Key.Escape) CloseSearch();
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.C or Key.W or Key.R)
        {
            var option = e.Key switch { Key.C => MatchCase, Key.W => WholeWord, _ => UseRegex };
            option.IsChecked = option.IsChecked != true;
            Search(reveal: true);
        }
        else return;
        e.Handled = true;
    }

    private void SearchOption_Click(object? sender, RoutedEventArgs e) => Search(reveal: true);
    private void SearchNext_Click(object? sender, RoutedEventArgs e) => Step(1);
    private void SearchPrevious_Click(object? sender, RoutedEventArgs e) => Step(-1);
    private void SearchClose_Click(object? sender, RoutedEventArgs e) => CloseSearch();
    private void Find_Click(object? sender, RoutedEventArgs e) => OpenSearch();
    private void Clear_Click(object? sender, RoutedEventArgs e) => _log?.Clear();
    private void Maximize_Click(object? sender, RoutedEventArgs e) => MaximizeToggled?.Invoke(this, EventArgs.Empty);
    private void Hide_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>A stage in the list on the left: its state's icon, its name and how long it took - a click shows it in the log.</summary>
    private sealed class StageRow : Button
    {
        private readonly OutputStage _stage;
        private readonly Path _icon = new() { Width = 14, Height = 14, Stretch = Stretch.None, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        private readonly TextBlock _duration = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        private readonly Border _row;

        public StageRow(OutputStage stage, Action<OutputStage> show)
        {
            _stage = stage;
            Classes.Add("stage");
            var dock = new DockPanel();
            DockPanel.SetDock(_icon, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(_duration, Avalonia.Controls.Dock.Right);
            _icon.Margin = new Thickness(0, 0, 10, 0);
            dock.Children.Add(_icon);
            dock.Children.Add(_duration);
            dock.Children.Add(_name);
            _row = new Border { Padding = new Thickness(6, 4), Margin = new Thickness(4, 1), CornerRadius = new CornerRadius(4), Child = dock };
            Content = _row;
            ToolTip.SetTip(this, stage.Title);
            AutomationProperties.SetName(this, stage.Title);
            Click += (_, _) => show(stage);
            stage.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Update);
            AttachedToVisualTree += (_, _) => Update();
            ActualThemeVariantChanged += (_, _) => Update();
        }

        private void Update()
        {
            var s = _stage;
            _name.Text = s.Name;
            _duration.Text = s.DurationText;
            IsEnabled = s.IsShownInLog;
            var (glyph, stroke, thickness, background, name, time) = s.Status switch
            {
                StageStatus.Running => ("StepSmallSpinner", "OutActive", 2.0, "SelectionBg", "LogHeaderFg", "LogMuted"),
                StageStatus.Ok => ("StepCheck", "OutOk", 2.0, null, "LogFg", "LogMuted"),
                StageStatus.Warning => ("StepTriangle", "OutWarn", 1.6, "OutWarnRowBg", "OutWarnText", "OutWarn"),
                StageStatus.Failed => ("StepCross", "OutError", 2.0, "OutErrorBg", "OutErrorText", "OutErrorSoft"),
                StageStatus.Skipped => ("StepDash", "OutSkippedIcon", 2.0, null, "OutDim", "OutDim"),
                StageStatus.Cancelled => ("StepCross", "OutMuted", 2.0, null, "OutMuted", "LogMuted"),
                _ => ("StepCircle", "OutDim", 1.5, (string?)null, "OutDim", "OutDim")
            };
            _icon.Data = this.FindResource(glyph) as Geometry;
            _icon.Stroke = Find(stroke);
            _icon.StrokeThickness = thickness;
            _row.Background = background is null ? Brushes.Transparent : Find(background);
            _name.Foreground = Find(name);
            _duration.Foreground = Find(time);
        }

        private IBrush? Find(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
    }
}
