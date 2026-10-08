using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Pages.Projects;
using DnnManager.Presentation.Services;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// The Output tab: the newest operation in the header strip and the stage rail - status, stages, summary - with the
/// sites kept warm under its stages; the log of every operation on the right (<see cref="PipelineLog"/>).
/// </summary>
public partial class PipelineView : UserControl
{
    private ActivityLog _log = null!;
    private AppOptions _options = null!;
    private OutputRun? _run;
    private ListCollectionView? _warm;
    // Ticks the running run's durations - only while one runs and the tab can be seen.
    private readonly DispatcherTimer _clock;

    public PipelineView()
    {
        InitializeComponent();
        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _clock.Stop();
        IsVisibleChanged += (_, _) => UpdateClock();
    }

    internal void Attach(ActivityLog log, ObservableCollection<ProjectRow> projects, AppOptions options)
    {
        _log = log;
        _options = options;
        Log.Attach(log, options.HostnameSuffix);
        log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivityLog.Latest)) Show(log.Latest);
        };

        // The sites kept warm, as they go on and off.
        _warm = new ListCollectionView(projects)
        {
            Filter = o => o is ProjectRow { KeepWarmOn: true },
            IsLiveFiltering = true,
            IsLiveSorting = true
        };
        _warm.LiveFilteringProperties.Add(nameof(ProjectRow.KeepWarmOn));
        _warm.SortDescriptions.Add(new SortDescription(nameof(ProjectRow.Name), ListSortDirection.Ascending));
        ((System.Collections.Specialized.INotifyCollectionChanged)_warm).CollectionChanged += (_, _) => UpdateBackground();
        BackgroundList.ItemsSource = _warm;
        options.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            Log.SetHostSuffix(options.HostnameSuffix);
            UpdateBackground();
        });
        UpdateBackground();
        Show(log.Latest);
    }

    /// <summary>The log's text size - the header and the rail keep the app's.</summary>
    internal void SetFontSize(double size)
    {
        // The terminal's size (13 by default) - the log a touch smaller, as monospaced text reads larger.
        Log.FontSize = Math.Max(8, size - 0.5);
    }

    // ─── The newest run ───────────────────────────────────────────────────

    private void Show(OutputRun? run)
    {
        if (_run is not null) _run.PropertyChanged -= OnRunChanged;
        _run = run;
        if (run is not null) run.PropertyChanged += OnRunChanged;

        RunHeader.DataContext = run;
        RunHeader.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
        NoRun.Visibility = run is null ? Visibility.Visible : Visibility.Collapsed;
        Stages.ItemsSource = run?.Stages;
        Facts.ItemsSource = run?.Facts;
        StagesPart.Visibility = Summary.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBackground();
        Refresh();
    }

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutputRun.Duration) or nameof(OutputRun.DurationText)) return;
        Refresh();
    }

    /// <summary>The pill, the issue count, the stage count and the summary - as the run stands now.</summary>
    private void Refresh()
    {
        UpdateClock();
        if (_run is not { } run) return;

        var (pill, pillBg, pillFg, glyph) = run.Status switch
        {
            RunStatus.Running => ("Running", "OutPillRunningBg", "OutPillRunningFg", null),
            RunStatus.Finished => ("Finished", "OutPillOkBg", "OutPillOkFg", "StepCheck"),
            RunStatus.Failed => ("Failed", "OutPillFailBg", "OutPillFailFg", "StepCross"),
            _ => ("Cancelled", "OutPillNeutralBg", "OutPillNeutralFg", "StepCross")
        };
        PillText.Text = pill;
        Pill.SetResourceReference(Border.BackgroundProperty, pillBg);
        PillText.SetResourceReference(TextBlock.ForegroundProperty, pillFg);
        PillIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, pillFg);
        PillSpin.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, pillFg);
        PillIcon.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        PillSpin.Visibility = glyph is null ? Visibility.Visible : Visibility.Collapsed;
        if (glyph is not null) PillIcon.Data = (System.Windows.Media.Geometry)FindResource(glyph);

        // Errors when there are any, else warnings - nothing when all went well.
        Issues.Visibility = run.IssueText is null ? Visibility.Collapsed : Visibility.Visible;
        var issueBrush = run.HasErrors ? "OutErrorSoft" : "OutWarn";
        IssueText.SetResourceReference(TextBlock.ForegroundProperty, issueBrush);
        IssueIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, issueBrush);
        IssueIcon.Data = (System.Windows.Media.Geometry)FindResource(run.HasErrors ? "StepAlert" : "StepTriangle");

        StagesLabel.Text = $"Stages · {run.Done}/{run.Total}";

        WarningCount.Text = run.Warnings.ToString();
        WarningCount.SetResourceReference(TextBlock.ForegroundProperty, run.Warnings > 0 ? "OutWarn" : "OutText");
        ErrorCount.Text = run.Errors.ToString();
        // Coloured only when there are some - a 0 is plain, like the other values.
        ErrorCount.SetResourceReference(TextBlock.ForegroundProperty, run.Errors > 0 ? "OutErrorSoft" : "OutText");
    }

    private void UpdateClock()
    {
        var run = _run is { IsRunning: true } && IsVisible;
        if (run && !_clock.IsEnabled) _clock.Start();
        else if (!run && _clock.IsEnabled) _clock.Stop();
    }

    private void Tick()
    {
        if (_run is not { IsRunning: true } run || !IsVisible)
        {
            _clock.Stop();
            return;
        }
        run.Tick();
        Log.Tick();
    }

    // A stage in the list: the log scrolls to it.
    private void Stage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OutputStage stage) Log.ShowStage(stage);
    }

    // ─── Background ───────────────────────────────────────────────────────

    private void UpdateBackground()
    {
        if (_warm is null) return;
        BackgroundPart.Visibility = _warm.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BackgroundPart.Margin = new Thickness(0, StagesPart.Visibility == Visibility.Visible ? 22 : 0, 0, 0);
        BackgroundNote.Text = $"ping every {_options.KeepWarm.PingMinutes} min · re-warm after recycle";
    }
}
